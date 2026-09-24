using Damebooru.Core.Entities;

namespace Damebooru.Core.DTOs;

public class PostDto
{
    public int Id { get; set; }

    // The location displayed for the post: its first file. PostFiles lists all copies.
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
    public List<TagDto> Tags { get; set; } = [];
    public List<SimilarPostDto> SimilarPosts { get; set; } = [];

    public static PostDto FromPost(Post post)
    {
        var displayFile = GetDisplayFile(post);

        return new PostDto
        {
            Id = post.Id,
            LibraryId = displayFile?.LibraryId ?? 0,
            LibraryName = displayFile?.Library?.Name ?? string.Empty,
            RelativePath = displayFile?.RelativePath ?? string.Empty,
            ContentHash = post.ContentHash,
            SizeBytes = post.SizeBytes,
            Width = post.Width,
            Height = post.Height,
            ContentType = post.ContentType,
            ImportDate = post.ImportDate,
            FileModifiedDate = post.FileModifiedDate,
            IsFavorite = post.IsFavorite,
            Sources = post.Sources.OrderBy(s => s.Order).Select(s => s.Url).ToList(),
            PostFiles = post.PostFiles
                .OrderBy(pf => pf.Id)
                .Select(PostFileDto.FromPostFile)
                .ToList(),
            Tags = TagDto.FromPostTags(post.PostTags),
            SimilarPosts = post.DuplicateGroupEntries
                .Select(dge => dge.DuplicateGroup)
                .SelectMany(g => g.Entries)
                .Where(e => e.PostId != post.Id)
                .Select(SimilarPostDto.FromDuplicateGroupEntry)
                .ToList()
        };
    }

    public static PostFile? GetDisplayFile(Post post)
        => post.PostFiles.MinBy(pf => pf.Id);
}

public class PostFileDto
{
    public int LibraryId { get; set; }
    public string? LibraryName { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public DateTime FileModifiedDate { get; set; }

    public static PostFileDto FromPostFile(PostFile postFile)
        => new()
        {
            LibraryId = postFile.LibraryId,
            LibraryName = postFile.Library?.Name,
            RelativePath = postFile.RelativePath,
            FileModifiedDate = postFile.FileModifiedDate,
        };
}

public class PostListDto
{
    public IReadOnlyList<PostDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
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
