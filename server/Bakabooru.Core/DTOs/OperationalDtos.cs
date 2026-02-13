using Bakabooru.Core.Entities;
using Bakabooru.Core.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Core.DTOs;

public class JobViewDto
{
    public string Name { get; set; } = string.Empty;
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
    public string JobName { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
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

public class DuplicatePostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ImportDate { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ContentUrl { get; set; } = string.Empty;
}

public class DuplicateGroupDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public int? SimilarityPercent { get; set; }
    public DateTime DetectedDate { get; set; }
    public List<DuplicatePostDto> Posts { get; set; } = [];
}

public class ResolveAllExactResponseDto
{
    public int Resolved { get; set; }
}

public class SystemInfoDto
{
    public int PostCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public int TagCount { get; set; }
    public int LibraryCount { get; set; }
    public DateTime ServerTime { get; set; }
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
}
