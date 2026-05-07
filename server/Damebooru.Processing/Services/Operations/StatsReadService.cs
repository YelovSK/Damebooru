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
        var totalSizeBytes = await _dbContext.PostFiles.AsNoTracking().SumAsync(pf => (long?)pf.SizeBytes, cancellationToken) ?? 0;
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
        var missingMetadataFileCount = await _dbContext.PostFiles
            .AsNoTracking()
            .CountAsync(pf => pf.Width == 0 || pf.Height == 0 || string.IsNullOrEmpty(pf.ContentType), cancellationToken);
        var missingPerceptualHashFileCount = await _dbContext.PostFiles
            .AsNoTracking()
            .CountAsync(pf => string.IsNullOrEmpty(pf.PdqHash256), cancellationToken);

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
        var monthlyPosts = dateKind == StatsGrowthDateKind.FileModified
            ? await GetMonthlyPostsByFileModifiedDateAsync(cancellationToken)
            : await GetMonthlyPostsByImportDateAsync(cancellationToken);
        var monthlySizeBytes = dateKind == StatsGrowthDateKind.FileModified
            ? await GetMonthlySizeByFileModifiedDateAsync(cancellationToken)
            : await GetMonthlySizeByImportDateAsync(cancellationToken);

        var months = BuildMonthRange(monthlyPosts, monthlySizeBytes);
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
        var files = _dbContext.PostFiles.AsNoTracking();
        var fileCount = await files.CountAsync(cancellationToken);
        var totalSizeBytes = await files.SumAsync(pf => (long?)pf.SizeBytes, cancellationToken) ?? 0;
        var imageFileCount = await files.CountAsync(pf => pf.ContentType.StartsWith("image/"), cancellationToken);
        var videoFileCount = await files.CountAsync(pf => pf.ContentType.StartsWith("video/"), cancellationToken);
        var contentTypes = await files
            .GroupBy(pf => string.IsNullOrEmpty(pf.ContentType) ? "Unknown" : pf.ContentType)
            .Select(g => new StatsStorageBreakdownDto
            {
                Label = g.Key,
                FileCount = g.Count(),
                SizeBytes = g.Sum(pf => pf.SizeBytes)
            })
            .OrderByDescending(item => item.SizeBytes)
            .ThenByDescending(item => item.FileCount)
            .ThenBy(item => item.Label)
            .ToListAsync(cancellationToken);

        return new StatsStorageDto
        {
            FileCount = fileCount,
            TotalSizeBytes = totalSizeBytes,
            AverageFileSizeBytes = fileCount == 0 ? 0 : totalSizeBytes / fileCount,
            ImageFileCount = imageFileCount,
            VideoFileCount = videoFileCount,
            ContentTypes = contentTypes,
            SizeBuckets = await GetSizeBucketsAsync(cancellationToken)
        };
    }

    private async Task<List<StatsStorageBreakdownDto>> GetSizeBucketsAsync(CancellationToken cancellationToken)
    {
        var files = _dbContext.PostFiles.AsNoTracking();

        return [
            await BuildSizeBucketAsync("< 1 MB", pf => pf.SizeBytes < 1_048_576, cancellationToken),
            await BuildSizeBucketAsync("1-5 MB", pf => pf.SizeBytes >= 1_048_576 && pf.SizeBytes < 5_242_880, cancellationToken),
            await BuildSizeBucketAsync("5-20 MB", pf => pf.SizeBytes >= 5_242_880 && pf.SizeBytes < 20_971_520, cancellationToken),
            await BuildSizeBucketAsync("20-100 MB", pf => pf.SizeBytes >= 20_971_520 && pf.SizeBytes < 104_857_600, cancellationToken),
            await BuildSizeBucketAsync("100 MB+", pf => pf.SizeBytes >= 104_857_600, cancellationToken)
        ];

        async Task<StatsStorageBreakdownDto> BuildSizeBucketAsync(
            string label,
            System.Linq.Expressions.Expression<Func<Damebooru.Core.Entities.PostFile, bool>> predicate,
            CancellationToken token)
        {
            var bucket = files.Where(predicate);
            return new StatsStorageBreakdownDto
            {
                Label = label,
                FileCount = await bucket.CountAsync(token),
                SizeBytes = await bucket.SumAsync(pf => (long?)pf.SizeBytes, token) ?? 0
            };
        }
    }

    public async Task<StatsTagsDto> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var postCount = await _dbContext.Posts.AsNoTracking().CountAsync(cancellationToken);
        var totalTags = await _dbContext.Tags.AsNoTracking().CountAsync(cancellationToken);
        var distinctPostTagCounts = await _dbContext.PostTags
            .AsNoTracking()
            .GroupBy(pt => pt.PostId)
            .Select(g => new PostTagCount(g.Key, g.Select(pt => pt.TagId).Distinct().Count()))
            .ToListAsync(cancellationToken);
        var taggedPostCount = distinctPostTagCounts.Count;
        var totalPostTagCount = distinctPostTagCounts.Sum(item => item.TagCount);
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
            DensityBuckets = BuildDensityBuckets(postCount, distinctPostTagCounts)
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
        int postCount,
        IReadOnlyCollection<PostTagCount> taggedCounts)
    {
        var zeroCount = postCount - taggedCounts.Count;

        return [
            new StatsTagDensityBucketDto { Label = "0", PostCount = zeroCount },
            new StatsTagDensityBucketDto { Label = "1-5", PostCount = taggedCounts.Count(item => item.TagCount >= 1 && item.TagCount <= 5) },
            new StatsTagDensityBucketDto { Label = "6-15", PostCount = taggedCounts.Count(item => item.TagCount >= 6 && item.TagCount <= 15) },
            new StatsTagDensityBucketDto { Label = "16-30", PostCount = taggedCounts.Count(item => item.TagCount >= 16 && item.TagCount <= 30) },
            new StatsTagDensityBucketDto { Label = "30+", PostCount = taggedCounts.Count(item => item.TagCount > 30) }
        ];
    }

    public async Task<StatsMaintenanceDto> GetMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
        var missingMetadataFileCount = await _dbContext.PostFiles
            .AsNoTracking()
            .CountAsync(pf => pf.Width == 0 || pf.Height == 0 || string.IsNullOrEmpty(pf.ContentType), cancellationToken);
        var missingPerceptualHashFileCount = await _dbContext.PostFiles
            .AsNoTracking()
            .CountAsync(pf => string.IsNullOrEmpty(pf.PdqHash256), cancellationToken);
        var unknownContentTypeFileCount = await _dbContext.PostFiles
            .AsNoTracking()
            .CountAsync(pf => string.IsNullOrEmpty(pf.ContentType) || pf.ContentType == "application/octet-stream", cancellationToken);
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
        var groups = await _dbContext.DuplicateGroups
            .AsNoTracking()
            .GroupBy(group => new { group.Type, group.IsResolved })
            .Select(g => new DuplicateGroupCount(g.Key.Type, g.Key.IsResolved, g.Count()))
            .ToListAsync(cancellationToken);
        var unresolvedPostCount = await _dbContext.DuplicateGroupEntries
            .AsNoTracking()
            .Where(entry => !entry.DuplicateGroup.IsResolved)
            .Select(entry => entry.PostId)
            .Distinct()
            .CountAsync(cancellationToken);

        var exactResolved = GetDuplicateGroupCount(groups, DuplicateType.Exact, isResolved: true);
        var exactUnresolved = GetDuplicateGroupCount(groups, DuplicateType.Exact, isResolved: false);
        var perceptualResolved = GetDuplicateGroupCount(groups, DuplicateType.Perceptual, isResolved: true);
        var perceptualUnresolved = GetDuplicateGroupCount(groups, DuplicateType.Perceptual, isResolved: false);

        return new StatsDuplicateHealthDto
        {
            TotalGroups = groups.Sum(group => group.Count),
            UnresolvedGroups = exactUnresolved + perceptualUnresolved,
            ExactResolvedGroups = exactResolved,
            ExactUnresolvedGroups = exactUnresolved,
            PerceptualResolvedGroups = perceptualResolved,
            PerceptualUnresolvedGroups = perceptualUnresolved,
            UnresolvedPostCount = unresolvedPostCount
        };
    }

    private static int GetDuplicateGroupCount(
        IReadOnlyCollection<DuplicateGroupCount> groups,
        DuplicateType type,
        bool isResolved)
        => groups.FirstOrDefault(group => group.Type == type && group.IsResolved == isResolved)?.Count ?? 0;

    private async Task<List<MonthlyValue>> GetMonthlyPostsByImportDateAsync(CancellationToken cancellationToken)
        => await _dbContext.Posts
            .AsNoTracking()
            .GroupBy(p => new { p.ImportDate.Year, p.ImportDate.Month })
            .Select(g => new MonthlyValue(g.Key.Year, g.Key.Month, g.LongCount()))
            .ToListAsync(cancellationToken);

    private async Task<List<MonthlyValue>> GetMonthlyPostsByFileModifiedDateAsync(CancellationToken cancellationToken)
        => await _dbContext.PostFiles
            .AsNoTracking()
            .GroupBy(pf => pf.PostId)
            .Select(g => g.Min(pf => pf.FileModifiedDate))
            .GroupBy(date => new { date.Year, date.Month })
            .Select(g => new MonthlyValue(g.Key.Year, g.Key.Month, g.LongCount()))
            .ToListAsync(cancellationToken);

    private async Task<List<MonthlyValue>> GetMonthlySizeByImportDateAsync(CancellationToken cancellationToken)
        => await _dbContext.PostFiles
            .AsNoTracking()
            .GroupBy(pf => new { pf.Post.ImportDate.Year, pf.Post.ImportDate.Month })
            .Select(g => new MonthlyValue(g.Key.Year, g.Key.Month, g.Sum(pf => pf.SizeBytes)))
            .ToListAsync(cancellationToken);

    private async Task<List<MonthlyValue>> GetMonthlySizeByFileModifiedDateAsync(CancellationToken cancellationToken)
        => await _dbContext.PostFiles
            .AsNoTracking()
            .GroupBy(pf => new { pf.FileModifiedDate.Year, pf.FileModifiedDate.Month })
            .Select(g => new MonthlyValue(g.Key.Year, g.Key.Month, g.Sum(pf => pf.SizeBytes)))
            .ToListAsync(cancellationToken);

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
    private sealed record PostTagCount(int PostId, int TagCount);
    private sealed record DuplicateGroupCount(DuplicateType Type, bool IsResolved, int Count);
}
