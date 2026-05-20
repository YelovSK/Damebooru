using System;
namespace Damebooru.Core.Entities;

public class Post
{
    public int Id { get; set; }

    /// <summary>When this post was first imported into Damebooru.</summary>
    public DateTime ImportDate { get; set; }

    public bool IsFavorite { get; set; }

    /// <summary>
    /// DB-maintained cached primary PostFile id. Source of truth is PostFiles; triggers refresh this value.
    /// </summary>
    public int? PrimaryPostFileId { get; set; }
    public PostFile? PrimaryPostFile { get; set; }

    /// <summary>
    /// DB-maintained cached modified date for the primary PostFile. Used for fast post-list sorting.
    /// </summary>
    public DateTime? PrimaryFileModifiedDate { get; set; }

    public ICollection<PostFile> PostFiles { get; set; } = new List<PostFile>();
    public ICollection<PostTag> PostTags { get; set; } = new List<PostTag>();
    public ICollection<PostSource> Sources { get; set; } = new List<PostSource>();
    public ICollection<DuplicateGroupEntry> DuplicateGroupEntries { get; set; } = new List<DuplicateGroupEntry>();
}
