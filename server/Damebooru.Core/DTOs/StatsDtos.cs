using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;

namespace Damebooru.Core.DTOs;

public enum StatsGrowthDateKind
{
    Imported = 0,
    FileModified = 1
}

public class StatsOverviewDto
{
    public int PostCount { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public int TagCount { get; set; }
    public int FavoritePostCount { get; set; }
    public int UntaggedPostCount { get; set; }
    public int SourceCount { get; set; }
    public int DuplicateGroupCount { get; set; }
    public int UnresolvedDuplicateGroupCount { get; set; }
    public int MissingMetadataFileCount { get; set; }
    public int MissingPerceptualHashFileCount { get; set; }
    public DateTime ServerTime { get; set; }
}

public class StatsGrowthDto
{
    public List<StatsSeriesPointDto> CumulativePosts { get; set; } = [];
    public List<StatsSeriesPointDto> CumulativeSizeBytes { get; set; } = [];
}

public class StatsStorageDto
{
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public long AverageFileSizeBytes { get; set; }
    public int ImageFileCount { get; set; }
    public int VideoFileCount { get; set; }
    public List<StatsStorageBreakdownDto> ContentTypes { get; set; } = [];
    public List<StatsStorageBreakdownDto> SizeBuckets { get; set; } = [];
}

public class StatsStorageBreakdownDto
{
    public string Label { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
}

public class StatsTagsDto
{
    public int TotalTags { get; set; }
    public int TaggedPostCount { get; set; }
    public int UntaggedPostCount { get; set; }
    public double AverageTagsPerPost { get; set; }
    public List<StatsTagCategoryDto> Categories { get; set; } = [];
    public List<StatsTagDensityBucketDto> DensityBuckets { get; set; } = [];
}

public class StatsTagCategoryDto
{
    public TagCategoryKind Category { get; set; }
    public int TagCount { get; set; }
    public int PostCount { get; set; }
}

public class StatsTagDensityBucketDto
{
    public string Label { get; set; } = string.Empty;
    public int PostCount { get; set; }
}

public class StatsMaintenanceDto
{
    public int MissingMetadataFileCount { get; set; }
    public int MissingPerceptualHashFileCount { get; set; }
    public int UnknownContentTypeFileCount { get; set; }
    public int UntaggedPostCount { get; set; }
    public int SourcelessPostCount { get; set; }
    public StatsDuplicateHealthDto Duplicates { get; set; } = new();
    public int FailedJobsLast7Days { get; set; }
    public List<StatsRecentFailedJobDto> RecentFailedJobs { get; set; } = [];
}

public class StatsDuplicateHealthDto
{
    public int TotalGroups { get; set; }
    public int UnresolvedGroups { get; set; }
    public int ExactResolvedGroups { get; set; }
    public int ExactUnresolvedGroups { get; set; }
    public int PerceptualResolvedGroups { get; set; }
    public int PerceptualUnresolvedGroups { get; set; }
    public int UnresolvedPostCount { get; set; }
}

public class StatsRecentFailedJobDto
{
    public int Id { get; set; }
    public string JobKey { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
}

public class StatsSeriesPointDto
{
    public DateTime PeriodStart { get; set; }
    public string Label { get; set; } = string.Empty;
    public long Value { get; set; }
}
