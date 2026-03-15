using System.Text.Json.Serialization;

namespace Damebooru.Processing.Infrastructure.External.Danbooru;

internal sealed class DanbooruPostDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("tag_string_general")]
    public string? TagStringGeneral { get; set; }

    [JsonPropertyName("tag_string_artist")]
    public string? TagStringArtist { get; set; }

    [JsonPropertyName("tag_string_character")]
    public string? TagStringCharacter { get; set; }

    [JsonPropertyName("tag_string_copyright")]
    public string? TagStringCopyright { get; set; }

    [JsonPropertyName("tag_string_meta")]
    public string? TagStringMeta { get; set; }
}
