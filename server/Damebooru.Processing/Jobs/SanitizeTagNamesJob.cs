using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Jobs;

public class SanitizeTagNamesJob : IJob
{
    public static readonly JobKey JobKey = JobKeys.SanitizeTagNames;
    public const string JobName = "Sanitize Tag Names";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SanitizeTagNamesJob> _logger;

    public SanitizeTagNamesJob(IServiceScopeFactory scopeFactory, ILogger<SanitizeTagNamesJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public int DisplayOrder => 80;
    public JobKey Key => JobKey;
    public string Name => JobName;
    public string Description => "Retroactively sanitizes all tag names (lowercase, replace colons with underscores, collapse duplicates).";
    public bool SupportsAllMode => false;

    public async Task ExecuteAsync(JobContext context)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();

        var allTags = await db.Tags.ToListAsync(context.CancellationToken);
        if (allTags.Count == 0)
        {
            context.Reporter.Update(new JobState
            {
                ActivityText = "Completed",
                ProgressCurrent = 0,
                ProgressTotal = 0,
                FinalText = "No tags to process."
            });
            return;
        }

        var processed = 0;
        var renamed = 0;
        var merged = 0;
        var failed = 0;

        // Build a lookup of sanitized name → existing tags with that sanitized name
        var sanitizedGroups = allTags
            .GroupBy(t => TagService.SanitizeTagName(t.Name))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (sanitizedName, group) in sanitizedGroups)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                // Tags that sanitize to empty — skip
                processed += group.Count;
                continue;
            }

            try
            {
                if (group.Count == 1)
                {
                    // Single tag — just rename if needed
                    var tag = group[0];
                    if (tag.Name != sanitizedName)
                    {
                        tag.Name = sanitizedName;
                        renamed++;
                    }
                    processed++;
                }
                else
                {
                    // Multiple tags that sanitize to the same name — merge them
                    // Pick the tag with the most usages as the survivor
                    var survivor = group.OrderByDescending(t => t.PostCount).First();
                    survivor.Name = sanitizedName;

                    // Transfer the best category if survivor has none
                    if (survivor.TagCategoryId == null)
                    {
                        var donor = group.FirstOrDefault(t => t.TagCategoryId != null);
                        if (donor != null)
                        {
                            survivor.TagCategoryId = donor.TagCategoryId;
                        }
                    }

                    var victims = group.Where(t => t.Id != survivor.Id).ToList();

                    foreach (var victim in victims)
                    {
                        // Move PostTag links from victim to survivor (skip duplicates)
                        var survivorPostIds = await db.PostTags
                            .Where(pt => pt.TagId == survivor.Id)
                            .Select(pt => new { pt.PostId, pt.Source })
                            .ToHashSetAsync(context.CancellationToken);

                        var victimLinks = await db.PostTags
                            .Where(pt => pt.TagId == victim.Id)
                            .ToListAsync(context.CancellationToken);

                        foreach (var link in victimLinks)
                        {
                            var survivorKey = new { link.PostId, link.Source };
                            if (!survivorPostIds.Contains(survivorKey))
                            {
                                db.PostTags.Add(new Core.Entities.PostTag
                                {
                                    PostId = link.PostId,
                                    TagId = survivor.Id,
                                    Source = link.Source
                                });
                            }
                        }

                        db.PostTags.RemoveRange(victimLinks);
                        db.Tags.Remove(victim);
                        merged++;
                    }

                    processed += group.Count;
                }
            }
            catch (Exception ex)
            {
                failed += group.Count;
                processed += group.Count;
                _logger.LogWarning(ex, "Failed to sanitize tag group '{SanitizedName}'", sanitizedName);
            }

            if (processed % 100 == 0)
            {
                context.Reporter.Update(new JobState
                {
                    ActivityText = $"Sanitizing tags... ({processed}/{allTags.Count})",
                    ProgressCurrent = processed,
                    ProgressTotal = allTags.Count
                });
            }
        }

        await db.SaveChangesAsync(context.CancellationToken);

        context.Reporter.Update(new JobState
        {
            ActivityText = "Completed",
            ProgressCurrent = processed,
            ProgressTotal = allTags.Count,
            FinalText = $"Renamed {renamed} tags, merged {merged} duplicates, failed {failed}."
        });
    }
}
