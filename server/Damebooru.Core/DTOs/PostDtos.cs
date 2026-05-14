using Damebooru.Core.Entities;

namespace Damebooru.Core.DTOs;

public class PostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public DateTime ImportDate { get; set; }
    public DateTime FileModifiedDate { get; set; }
    public bool IsFavorite { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<PostFileDto> PostFiles { get; set; } = [];
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
    public List<TagDto> Tags { get; set; } = [];
    public List<SimilarPostDto> SimilarPosts { get; set; } = [];
}

public class PostFileDto
{
    public int LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public DateTime FileModifiedDate { get; set; }
}

public class PostListDto
{
    public IReadOnlyList<PostDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class PostsAroundDto
{
    public PostDto? Prev { get; set; }
    public PostDto? Next { get; set; }
    public IReadOnlyList<PostDto> PrevItems { get; set; } = [];
    public IReadOnlyList<PostDto> NextItems { get; set; } = [];
    public IReadOnlyList<PostDto> Items { get; set; } = [];
    public int AnchorIndex { get; set; }
    public bool HasPrevious { get; set; }
    public bool HasNext { get; set; }
}

public class UpdatePostMetadataDto
{
    public List<UpdatePostTagDto>? TagsWithSources { get; set; }
    public List<string>? Sources { get; set; }
}

public class UpdatePostTagDto
{
    public int? TagId { get; set; }
    public string Name { get; set; } = string.Empty;
    public PostTagSource Source { get; set; }
    public TagCategoryKind Category { get; set; } = TagCategoryKind.General;
}

public class AutoTagPostResultDto
{
    public AutoTagScanStatus ScanStatus { get; set; }
    public int AddedTags { get; set; }
    public int RemovedTags { get; set; }
    public int UpdatedTagCategories { get; set; }
    public int AddedSources { get; set; }
    public PostDto Post { get; set; } = null!;
}

public sealed class PostAutoTagStatusDto
{
    public bool HasScan { get; set; }
    public AutoTagScanStatus? ScanStatus { get; set; }
    public DateTime? LastStartedAtUtc { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
    public List<PostAutoTagProviderStatusDto> DiscoveryProviders { get; set; } = [];
    public List<PostAutoTagProviderStatusDto> MetadataProviders { get; set; } = [];
    public List<PostAutoTagCandidateDto> Candidates { get; set; } = [];
}

public sealed class PostAutoTagProviderStatusDto
{
    public AutoTagProvider Provider { get; set; }
    public AutoTagScanStepKind Kind { get; set; }
    public AutoTagScanStepStatus? Status { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public string? LastError { get; set; }
    public long? ExternalPostId { get; set; }
    public int TagCount { get; set; }
    public int SourceCount { get; set; }
}

public sealed class PostAutoTagCandidateDto
{
    public AutoTagProvider DiscoveryProvider { get; set; }
    public AutoTagProvider Provider { get; set; }
    public long ExternalPostId { get; set; }
    public decimal Similarity { get; set; }
    public string CanonicalUrl { get; set; } = string.Empty;
}

public sealed class AiTagPreviewDto
{
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public decimal Threshold { get; set; }
    public decimal ApplyThreshold { get; set; }
    public decimal MinConfidence { get; set; }
    public decimal ElapsedMilliseconds { get; set; }
    public List<AiTagSuggestionDto> Tags { get; set; } = [];
}

public sealed class AiTagPostResultDto
{
    public int AddedTags { get; set; }
    public int RemovedTags { get; set; }
    public int UpdatedTagCategories { get; set; }
    public AiTagPreviewDto Preview { get; set; } = null!;
    public PostDto Post { get; set; } = null!;
}

public sealed class AiTagSuggestionDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Score { get; set; }
    public TagCategoryKind Category { get; set; }
    public string RawCategory { get; set; } = string.Empty;
    public bool MeetsApplyThreshold { get; set; }
}

public class PostAuditEntryDto
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Entity { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public class PostAuditListDto
{
    public List<PostAuditEntryDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
}
