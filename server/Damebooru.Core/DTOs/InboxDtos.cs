using System.ComponentModel.DataAnnotations;

namespace Damebooru.Core.DTOs;

public class SortInboxPostsDto
{
    [Required]
    [MinLength(1)]
    public List<SortInboxPostDto> Posts { get; set; } = [];
}

public class SortInboxPostDto
{
    public int PostId { get; set; }

    /// <summary>
    /// The post's inbox file is moved to the first destination and copied to the rest.
    /// Other inbox copies of the post are deleted.
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

public class DiscardInboxPostsDto
{
    [Required]
    [MinLength(1)]
    public List<int> PostIds { get; set; } = [];
}

public class InboxPostResultDto
{
    public int PostId { get; set; }
    public string? Error { get; set; }
}

public class SortInboxPostResultDto
{
    public int PostId { get; set; }
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

public class DestinationFolderDto : FolderLocationDto
{
    public string LibraryName { get; set; } = string.Empty;
    public int PostCount { get; set; }
}

public class FolderSuggestionDto : FolderLocationDto
{
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public class PostFolderSuggestionsDto
{
    public int PostId { get; set; }

    /// <summary>The post is an image the AI tagger hasn't seen yet; predicting may bring suggestions.</summary>
    public bool NeedsPrediction { get; set; }

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
