namespace Damebooru.Processing.Services.AutoTagging;

public sealed record AutoTagApplyResult(int AddedTags, int RemovedTags, int UpdatedTagCategories, int AddedSources);
