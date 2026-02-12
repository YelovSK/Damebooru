namespace Bakabooru.Server.DTOs;

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
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ContentUrl { get; set; } = string.Empty;
    public List<TagDto> Tags { get; set; } = new();
}

public class PostListDto
{
    public IEnumerable<PostDto> Items { get; set; } = new List<PostDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class MicroPostDto
{
    public int Id { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
}

public class PostsAroundDto
{
    public MicroPostDto? Prev { get; set; }
    public MicroPostDto? Next { get; set; }
}
