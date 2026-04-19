using System;
namespace Damebooru.Core.Entities;

public class Post
{
    public int Id { get; set; }

    /// <summary>When this post was first imported into Damebooru.</summary>
    public DateTime ImportDate { get; set; }

    public bool IsFavorite { get; set; }

    public ICollection<PostFile> PostFiles { get; set; } = new List<PostFile>();
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
    public ICollection<PostSource> Sources { get; set; } = new List<PostSource>();
    public ICollection<DuplicateGroupEntry> DuplicateGroupEntries { get; set; } = new List<DuplicateGroupEntry>();
}
