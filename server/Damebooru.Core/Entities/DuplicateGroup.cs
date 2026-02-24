namespace Damebooru.Core.Entities;

public class DuplicateGroup
{
    public int Id { get; set; }

    public DuplicateType Type { get; set; }

    /// <summary>Similarity percentage for perceptual matches (null for exact)</summary>
    public int? SimilarityPercent { get; set; }

    public bool IsResolved { get; set; }
    public DateTime DetectedDate { get; set; }

    public ICollection<DuplicateGroupEntry> Entries { get; set; } = new List<DuplicateGroupEntry>();
}

public class DuplicateGroupEntry
{
    public int Id { get; set; }

    public int DuplicateGroupId { get; set; }
    public DuplicateGroup DuplicateGroup { get; set; } = null!;

    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
}
