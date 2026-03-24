using Damebooru.Core.Entities;

namespace Damebooru.Processing.Services.AutoTagging;

internal static class AutoTagDiscoveryPlan
{
    public static readonly AutoTagProvider[] OrderedDiscoveryProviders =
    [
        AutoTagProvider.SauceNao,
        AutoTagProvider.Iqdb,
        AutoTagProvider.Danbooru,
        AutoTagProvider.Gelbooru,
    ];

    public static readonly AutoTagProvider[] MetadataProviders =
    [
        AutoTagProvider.Danbooru,
        AutoTagProvider.Gelbooru,
    ];
}
