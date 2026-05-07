using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.DTOs;

public class JobViewDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool SupportsAllMode { get; set; }
    public bool IsRunning { get; set; }
    public JobInfo? ActiveJobInfo { get; set; }
}

public class StartJobResponseDto
{
    public string JobId { get; set; } = string.Empty;
}

public class JobExecutionDto
{
    public int Id { get; set; }
    public string JobKey { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    public JobState? State { get; set; }
}

public class JobHistoryResponseDto
{
    public List<JobExecutionDto> Items { get; set; } = [];
    public int Total { get; set; }
}

public class ScheduledJobDto
{
    public int Id { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
}

public class ScheduledJobUpdateDto
{
    [Required]
    [MinLength(1)]
    public string CronExpression { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

public class CronPreviewDto
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public List<DateTime> NextRuns { get; set; } = [];
}

public class DuplicatePostFileDto
{
    public int PostFileId { get; set; }
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}

public class DuplicatePostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ImportDate { get; set; }
    public DateTime FileModifiedDate { get; set; }
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
    public List<DuplicatePostFileDto> Files { get; set; } = [];
}

public class DuplicateGroupDto
{
    public int Id { get; set; }
    public DuplicateType Type { get; set; }
    public int? SimilarityPercent { get; set; }
    public DateTime DetectedDate { get; set; }
    public List<DuplicatePostDto> Posts { get; set; } = [];
}

public class ExactDuplicateFileDto
{
    public int PostId { get; set; }
    public int PostFileId { get; set; }
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime FileModifiedDate { get; set; }
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
}

public class ExactDuplicateFolderBucketDto
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public List<ExactDuplicateFileDto> Files { get; set; } = [];
}

public class ExactDuplicateClusterDto
{
    public string ContentHash { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public bool HasSameFolderDuplicates { get; set; }
    public bool HasCrossFolderDuplicates { get; set; }
    public List<ExactDuplicateFolderBucketDto> Folders { get; set; } = [];
}

public class MarkAllUnresolvedResponseDto
{
    public int Unresolved { get; set; }
}

public class SimilarPostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
    public DuplicateType DuplicateType { get; set; }
    public int? SimilarityPercent { get; set; }
    public bool GroupIsResolved { get; set; }
}

public class DuplicateLookupMatchDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ImportDate { get; set; }
    public DateTime FileModifiedDate { get; set; }
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
    public int? SimilarityPercent { get; set; }
}

public class DuplicateLookupResponseDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public bool PerceptualHashComputed { get; set; }
    public string? PerceptualUnavailableReason { get; set; }
    public List<DuplicateLookupMatchDto> ExactMatches { get; set; } = [];
    public List<DuplicateLookupMatchDto> PerceptualMatches { get; set; } = [];
}

public class DuplicateHashLookupRequestDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
}

public class LoginRequestDto
{
    [Required]
    [MinLength(1)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string Password { get; set; } = string.Empty;
}

public class AuthSessionDto
{
    public string Username { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public bool AuthEnabled { get; set; } = true;
}
