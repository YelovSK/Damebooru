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
    public int ThumbnailLibraryId { get; set; }
    public string ThumbnailContentHash { get; set; } = string.Empty;
    public List<TagDto> Tags { get; set; } = [];
    public List<SimilarPostDto> SimilarPosts { get; set; } = [];
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
}
