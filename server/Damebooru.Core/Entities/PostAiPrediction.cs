using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.Entities;

/// <summary>
/// The latest AI tagger scores for a post's character, copyright and artist tags, including ones below the apply threshold.
/// Folder suggestions match them against folder names; inbox posts get predictions without having AI tags applied.
/// </summary>
public class PostAiPrediction
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    [MaxLength(100)]
    public string TagName { get; set; } = string.Empty;

    public TagCategoryKind Category { get; set; }
    public decimal Score { get; set; }
}
