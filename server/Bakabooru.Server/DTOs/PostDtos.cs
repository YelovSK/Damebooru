namespace Bakabooru.Server.DTOs;

public class PostDto
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Md5Hash { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public DateTime ImportDate { get; set; }
    public List<TagDto> Tags { get; set; } = new();
}

public class PostListDto
{
    public IEnumerable<PostDto> Items { get; set; } = new List<PostDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
