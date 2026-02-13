namespace Bakabooru.Core.DTOs;

public class PostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public DateTime ImportDate { get; set; }
    public bool IsFavorite { get; set; }
    public List<string> Sources { get; set; } = [];
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ContentUrl { get; set; } = string.Empty;
    public List<TagDto> Tags { get; set; } = [];
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
