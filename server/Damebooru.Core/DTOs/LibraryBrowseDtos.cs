namespace Damebooru.Core.DTOs;

public class LibraryBrowseBreadcrumbDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public class LibraryFolderNodeDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int RecursivePostCount { get; set; }
    public bool HasChildren { get; set; }
    public List<LibraryFolderNodeDto> Children { get; set; } = [];
}

public class LibraryBrowseResponseDto
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public bool Recursive { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public List<LibraryBrowseBreadcrumbDto> Breadcrumbs { get; set; } = [];
    public List<LibraryFolderNodeDto> ChildFolders { get; set; } = [];
    public IReadOnlyList<PostDto> Posts { get; set; } = [];
}
