namespace Damebooru.Core.Interfaces;

public static class JobKeys
{
    public static readonly JobKey ScanAllLibraries = JobKey.Parse("scan-all-libraries");
    public static readonly JobKey ExtractMetadata = JobKey.Parse("extract-metadata");
    public static readonly JobKey ComputeSimilarity = JobKey.Parse("compute-similarity");
    public static readonly JobKey FindDuplicates = JobKey.Parse("find-duplicates");
    public static readonly JobKey GenerateThumbnails = JobKey.Parse("generate-thumbnails");
    public static readonly JobKey CleanupOrphanedThumbnails = JobKey.Parse("cleanup-orphaned-thumbnails");
    public static readonly JobKey ApplyFolderTags = JobKey.Parse("apply-folder-tags");
    public static readonly JobKey SanitizeTagNames = JobKey.Parse("sanitize-tag-names");
}
