using Bakabooru.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Bakabooru.Scanner;

public class ImageHashService : ISimilarityService
{
    private readonly ILogger<ImageHashService> _logger;

    public ImageHashService(ILogger<ImageHashService> logger)
    {
        _logger = logger;
    }

    public async Task<ulong> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Difference Hash implementation
            // 1. Resize to 9x8 (72 pixels). 
            //    We need 9 columns to produce 8 differences per row. 8 rows * 8 diffs = 64 bits.
            using var image = await Image.LoadAsync<L8>(filePath, cancellationToken);
            
            image.Mutate(x => x
                .Resize(new ResizeOptions { Size = new Size(9, 8), Mode = ResizeMode.Stretch })
                .Grayscale());

            ulong hash = 0;
            
            // Process pixel data
            // Since it's small, we can just process it row by row.
            // ImageSharp accessing pixels via indexer is fast enough for 9x8
            
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < 8; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < 8; x++)
                    {
                        if (row[x].PackedValue > row[x + 1].PackedValue)
                        {
                            hash |= 1UL << ((y * 8) + x);
                        }
                    }
                }
            });

            return hash;
        }
        catch (Exception ex)
        {
             _logger.LogWarning(ex, "Failed to compute perceptual hash for {Path}", filePath);
             return 0;
        }
    }
}
