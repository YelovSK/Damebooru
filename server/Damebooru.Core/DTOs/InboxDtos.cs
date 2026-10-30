using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.DTOs;

public class SortInboxFilesDto
{
    [Required]
    [MinLength(1)]
    public List<SortInboxFileDto> Files { get; set; } = [];
}

public class SortInboxFileDto
{
    public int PostFileId { get; set; }

    /// <summary>
    /// The file is moved to the first destination and copied to the rest.
    /// </summary>
    [Required]
    [MinLength(1)]
    public List<SortDestinationDto> Destinations { get; set; } = [];
}

public class SortDestinationDto
{
    public int LibraryId { get; set; }

    /// <summary>
    /// Folder relative to the library root; empty for the root. Missing folders are created.
    /// </summary>
    public string Folder { get; set; } = string.Empty;
}

public class SortInboxFileResultDto
{
    public int PostFileId { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public List<SortedFileLocationDto> Locations { get; set; } = [];
}

public class SortedFileLocationDto
{
    public int LibraryId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
}

public class FolderLocationDto
{
    public int LibraryId { get; set; }
    public string Folder { get; set; } = string.Empty;
}

public class FolderSuggestionDto : FolderLocationDto
{
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public class PostFolderSuggestionsDto
{
    public int PostId { get; set; }
    public List<FolderSuggestionDto> Suggestions { get; set; } = [];
}

public class FolderSuggestionEvaluationDto
{
    public int Sampled { get; set; }
    public int Top1Hits { get; set; }
    public int Top3Hits { get; set; }
    public int WithoutSuggestions { get; set; }
    public List<FolderSuggestionMissDto> Misses { get; set; } = [];
}

public class FolderSuggestionMissDto
{
    public int PostId { get; set; }
    public List<FolderLocationDto> Actual { get; set; } = [];
    public List<FolderSuggestionDto> Suggested { get; set; } = [];
}
