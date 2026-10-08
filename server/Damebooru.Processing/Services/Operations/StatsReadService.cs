using System.Globalization;
using Damebooru.Core.DTOs;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public class StatsReadService
{
    private readonly DamebooruDbContext _dbContext;

    public StatsReadService(DamebooruDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<StatsOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var postCount = await _dbContext.Posts.AsNoTracking().CountAsync(cancellationToken);
        var fileCount = await _dbContext.PostFiles.AsNoTracking().CountAsync(cancellationToken);
        var totalSizeBytes = await _dbContext.PostFiles.AsNoTracking().SumAsync(pf => (long?)pf.Post.SizeBytes, cancellationToken) ?? 0;
        var tagCount = await _dbContext.Tags.AsNoTracking().CountAsync(cancellationToken);
        var favoritePostCount = await _dbContext.Posts.AsNoTracking().CountAsync(p => p.IsFavorite, cancellationToken);
        var untaggedPostCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => !p.PostTags.Any(), cancellationToken);
        var sourceCount = await _dbContext.PostSources.AsNoTracking().CountAsync(cancellationToken);
        var duplicateGroupCount = await _dbContext.DuplicateGroups.AsNoTracking().CountAsync(cancellationToken);
        var unresolvedDuplicateGroupCount = await _dbContext.DuplicateGroups
            .AsNoTracking()
            .CountAsync(g => !g.IsResolved, cancellationToken);
        var missingMetadataFileCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => p.Width == 0 || p.Height == 0, cancellationToken);
        var missingPerceptualHashFileCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => p.ContentType.StartsWith("image/") && string.IsNullOrEmpty(p.PdqHash256), cancellationToken);

        return new StatsOverviewDto
        {
            PostCount = postCount,
            FileCount = fileCount,
            TotalSizeBytes = totalSizeBytes,
            TagCount = tagCount,
            FavoritePostCount = favoritePostCount,
            UntaggedPostCount = untaggedPostCount,
            SourceCount = sourceCount,
            DuplicateGroupCount = duplicateGroupCount,
            UnresolvedDuplicateGroupCount = unresolvedDuplicateGroupCount,
            MissingMetadataFileCount = missingMetadataFileCount,
            MissingPerceptualHashFileCount = missingPerceptualHashFileCount,
            ServerTime = DateTime.UtcNow
        };
    }

    public async Task<StatsGrowthDto> GetGrowthAsync(
        StatsGrowthDateKind dateKind = StatsGrowthDateKind.Imported,
        CancellationToken cancellationToken = default)
    {
        var monthly = await GetMonthlyTotalsAsync(dateKind, cancellationToken);
        var monthlyPosts = monthly.Select(m => new MonthlyValue(m.Year, m.Month, m.PostCount)).ToList();
        var monthlySizeBytes = monthly.Select(m => new MonthlyValue(m.Year, m.Month, m.SizeBytes)).ToList();

        var months = BuildMonthRange(monthlyPosts);
        var cumulativePosts = BuildSeries(months, monthlyPosts, cumulative: true);
        var cumulativeSizeBytes = BuildSeries(months, monthlySizeBytes, cumulative: true);

        return new StatsGrowthDto
        {
            CumulativePosts = cumulativePosts,
            CumulativeSizeBytes = cumulativeSizeBytes
        };
    }

    public async Task<StatsStorageDto> GetStorageAsync(CancellationToken cancellationToken = default)
    {
        var posts = _dbContext.Posts.AsNoTracking();
        // Grouped over Posts, weighting by file count: grouping PostFiles through the Post navigation
        // makes EF emit a full-table subquery per group.
        var contentTypes = await posts
            .GroupBy(p => string.IsNullOrEmpty(p.ContentType) ? "Unknown" : p.ContentType)
            .Select(g => new StatsStorageBreakdownDto
            {
                Label = g.Key,
                FileCount = g.Sum(p => p.PostFiles.Count),
                SizeBytes = g.Sum(p => p.SizeBytes * p.PostFiles.Count)
            })
            .OrderByDescending(item => item.SizeBytes)
            .ThenByDescending(item => item.FileCount)
            .ThenBy(item => item.Label)
            .ToListAsync(cancellationToken);
        var sizeBuckets = await posts
            .GroupBy(p => p.SizeBytes < 1_048_576 ? 0
                : p.SizeBytes < 5_242_880 ? 1
                : p.SizeBytes < 20_971_520 ? 2
                : p.SizeBytes < 104_857_600 ? 3
                : 4)
            .Select(g => new
            {
                Index = g.Key,
                FileCount = g.Sum(p => p.PostFiles.Count),
                SizeBytes = g.Sum(p => p.SizeBytes * p.PostFiles.Count)
            })
            .ToDictionaryAsync(b => b.Index, cancellationToken);

        var fileCount = contentTypes.Sum(c => c.FileCount);
        var totalSizeBytes = contentTypes.Sum(c => c.SizeBytes);

        return new StatsStorageDto
        {
            FileCount = fileCount,
            TotalSizeBytes = totalSizeBytes,
            AverageFileSizeBytes = fileCount == 0 ? 0 : totalSizeBytes / fileCount,
            ImageFileCount = contentTypes.Where(c => c.Label.StartsWith("image/")).Sum(c => c.FileCount),
            VideoFileCount = contentTypes.Where(c => c.Label.StartsWith("video/")).Sum(c => c.FileCount),
            ContentTypes = contentTypes,
            SizeBuckets = SizeBucketLabels
                .Select((label, index) => new StatsStorageBreakdownDto
                {
                    Label = label,
                    FileCount = sizeBuckets.GetValueOrDefault(index)?.FileCount ?? 0,
                    SizeBytes = sizeBuckets.GetValueOrDefault(index)?.SizeBytes ?? 0
                })
                .ToList()
        };
    }

    private static readonly string[] SizeBucketLabels = ["< 1 MB", "1-5 MB", "5-20 MB", "20-100 MB", "100 MB+"];

    public async Task<StatsTagsDto> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var postCount = await _dbContext.Posts.AsNoTracking().CountAsync(cancellationToken);
        var totalTags = await _dbContext.Tags.AsNoTracking().CountAsync(cancellationToken);
        var tagCountHistogram = await _dbContext.PostTags
            .AsNoTracking()
            .GroupBy(pt => pt.PostId)
            .Select(g => g.Select(pt => pt.TagId).Distinct().Count())
            .GroupBy(tagCount => tagCount)
            .Select(g => new TagCountFrequency(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
        var taggedPostCount = tagCountHistogram.Sum(item => item.PostCount);
        var totalPostTagCount = tagCountHistogram.Sum(item => (long)item.TagCount * item.PostCount);
        var categories = await _dbContext.Tags
            .AsNoTracking()
            .GroupBy(tag => tag.Category)
            .Select(g => new StatsTagCategoryDto
            {
                Category = g.Key,
                TagCount = g.Count(),
                PostCount = g.Sum(tag => tag.PostCount)
            })
            .ToListAsync(cancellationToken);

        return new StatsTagsDto
        {
            TotalTags = totalTags,
            TaggedPostCount = taggedPostCount,
            UntaggedPostCount = postCount - taggedPostCount,
            AverageTagsPerPost = postCount == 0 ? 0 : (double)totalPostTagCount / postCount,
            Categories = BuildCategoryBreakdown(categories),
            DensityBuckets = BuildDensityBuckets(postCount - taggedPostCount, tagCountHistogram)
        };
    }

    private static List<StatsTagCategoryDto> BuildCategoryBreakdown(IReadOnlyCollection<StatsTagCategoryDto> categories)
    {
        var byCategory = categories.ToDictionary(item => item.Category);

        return Enum.GetValues<TagCategoryKind>()
            .Select(category => byCategory.TryGetValue(category, out var item)
                ? item
                : new StatsTagCategoryDto { Category = category })
            .ToList();
    }

    private static List<StatsTagDensityBucketDto> BuildDensityBuckets(
        int untaggedPostCount,
        IReadOnlyCollection<TagCountFrequency> histogram)
    {
        return [
            new StatsTagDensityBucketDto { Label = "0", PostCount = untaggedPostCount },
            new StatsTagDensityBucketDto { Label = "1-5", PostCount = CountPosts(1, 5) },
            new StatsTagDensityBucketDto { Label = "6-15", PostCount = CountPosts(6, 15) },
            new StatsTagDensityBucketDto { Label = "16-30", PostCount = CountPosts(16, 30) },
            new StatsTagDensityBucketDto { Label = "30+", PostCount = CountPosts(31, int.MaxValue) }
        ];

        int CountPosts(int min, int max)
            => histogram.Where(item => item.TagCount >= min && item.TagCount <= max).Sum(item => item.PostCount);
    }

    public async Task<StatsMaintenanceDto> GetMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
        var missingMetadataFileCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => p.Width == 0 || p.Height == 0, cancellationToken);
        var missingPerceptualHashFileCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => p.ContentType.StartsWith("image/") && string.IsNullOrEmpty(p.PdqHash256), cancellationToken);
        var unknownContentTypeFileCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => string.IsNullOrEmpty(p.ContentType) || p.ContentType == "application/octet-stream", cancellationToken);
        var untaggedPostCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => !p.PostTags.Any(), cancellationToken);
        var sourcelessPostCount = await _dbContext.Posts
            .AsNoTracking()
            .CountAsync(p => !p.Sources.Any(), cancellationToken);
        var failedJobsLast7Days = await _dbContext.JobExecutions
            .AsNoTracking()
            .CountAsync(job => job.Status == JobStatus.Failed && job.StartTime >= sevenDaysAgo, cancellationToken);
        var recentFailedJobs = await _dbContext.JobExecutions
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Failed)
            .OrderByDescending(job => job.StartTime)
            .Take(5)
            .Select(job => new StatsRecentFailedJobDto
            {
                Id = job.Id,
                JobKey = job.JobKey,
                JobName = job.JobName,
                Status = job.Status,
                StartTime = job.StartTime,
                EndTime = job.EndTime,
                ErrorMessage = job.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        return new StatsMaintenanceDto
        {
            MissingMetadataFileCount = missingMetadataFileCount,
            MissingPerceptualHashFileCount = missingPerceptualHashFileCount,
            UnknownContentTypeFileCount = unknownContentTypeFileCount,
            UntaggedPostCount = untaggedPostCount,
            SourcelessPostCount = sourcelessPostCount,
            Duplicates = await GetDuplicateHealthAsync(cancellationToken),
            FailedJobsLast7Days = failedJobsLast7Days,
            RecentFailedJobs = recentFailedJobs
        };
    }

    private async Task<StatsDuplicateHealthDto> GetDuplicateHealthAsync(CancellationToken cancellationToken)
    {
        var totalGroups = await _dbContext.DuplicateGroups.CountAsync(cancellationToken);
        var unresolvedGroups = await _dbContext.DuplicateGroups.CountAsync(group => !group.IsResolved, cancellationToken);
        var unresolvedPostCount = await _dbContext.DuplicateGroupEntries
            .AsNoTracking()
            .Where(entry => !entry.DuplicateGroup.IsResolved)
            .Select(entry => entry.PostId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new StatsDuplicateHealthDto
        {
            TotalGroups = totalGroups,
            UnresolvedGroups = unresolvedGroups,
            UnresolvedPostCount = unresolvedPostCount
        };
    }

    private async Task<List<MonthlyTotals>> GetMonthlyTotalsAsync(StatsGrowthDateKind dateKind, CancellationToken cancellationToken)
    {
        var posts = _dbContext.Posts.AsNoTracking();
        var byMonth = dateKind == StatsGrowthDateKind.FileModified
            ? posts.GroupBy(p => new { p.FileModifiedDate.Year, p.FileModifiedDate.Month })
            : posts.GroupBy(p => new { p.ImportDate.Year, p.ImportDate.Month });

        return await byMonth
            .Select(g => new MonthlyTotals(
                g.Key.Year,
                g.Key.Month,
                g.LongCount(),
                g.Sum(p => p.SizeBytes * p.PostFiles.Count)))
            .ToListAsync(cancellationToken);
    }

    private static List<DateTime> BuildMonthRange(params List<MonthlyValue>[] values)
    {
        var populatedMonths = values
            .SelectMany(value => value)
            .Select(value => new DateTime(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (populatedMonths.Count == 0)
        {
            return [];
        }

        var months = new List<DateTime>();
        var cursor = populatedMonths[0];
        var end = populatedMonths[^1];

        while (cursor <= end)
        {
            months.Add(cursor);
            cursor = cursor.AddMonths(1);
        }

        return months;
    }

    private static List<StatsSeriesPointDto> BuildSeries(
        IReadOnlyList<DateTime> months,
        IReadOnlyCollection<MonthlyValue> values,
        bool cumulative)
    {
        var valuesByMonth = values.ToDictionary(value => (value.Year, value.Month), value => value.Value);
        var runningTotal = 0L;
        var series = new List<StatsSeriesPointDto>(months.Count);

        foreach (var month in months)
        {
            valuesByMonth.TryGetValue((month.Year, month.Month), out var value);
            if (cumulative)
            {
                runningTotal += value;
                value = runningTotal;
            }

            series.Add(new StatsSeriesPointDto
            {
                PeriodStart = month,
                Label = month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                Value = value
            });
        }

        return series;
    }

    private sealed record MonthlyValue(int Year, int Month, long Value);
    private sealed record MonthlyTotals(int Year, int Month, long PostCount, long SizeBytes);
    private sealed record TagCountFrequency(int TagCount, int PostCount);
}
