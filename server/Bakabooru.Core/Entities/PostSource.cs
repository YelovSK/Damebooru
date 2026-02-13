using System.ComponentModel.DataAnnotations;

namespace Bakabooru.Core.Entities;

public class PostSource
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    public int Order { get; set; }
}
