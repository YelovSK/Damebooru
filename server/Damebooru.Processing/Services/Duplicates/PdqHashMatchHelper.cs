using System.Globalization;
using System.Numerics;

namespace Damebooru.Processing.Services.Duplicates;

internal readonly record struct PdqHashWords(ulong W0, ulong W1, ulong W2, ulong W3);

internal static class PdqHashMatchHelper
{
    internal const int DefaultSimilarityThresholdPercent = 68;

    internal static bool TryParseHex256(string hex, out PdqHashWords words)
    {
        words = default;

        var trimmed = hex.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        if (!ulong.TryParse(trimmed.AsSpan(0, 16), NumberStyles.HexNumber, null, out var w0)
            || !ulong.TryParse(trimmed.AsSpan(16, 16), NumberStyles.HexNumber, null, out var w1)
            || !ulong.TryParse(trimmed.AsSpan(32, 16), NumberStyles.HexNumber, null, out var w2)
            || !ulong.TryParse(trimmed.AsSpan(48, 16), NumberStyles.HexNumber, null, out var w3))
        {
            return false;
        }

        words = new PdqHashWords(w0, w1, w2, w3);
        return true;
    }

    internal static bool TryComputeSimilarity(
        PdqHashWords left,
        string leftContentType,
        PdqHashWords right,
        string rightContentType,
        int similarityThresholdPercent,
        out int similarityPercent)
    {
        var distance = BitOperations.PopCount(left.W0 ^ right.W0)
                     + BitOperations.PopCount(left.W1 ^ right.W1)
                     + BitOperations.PopCount(left.W2 ^ right.W2)
                     + BitOperations.PopCount(left.W3 ^ right.W3);

        var similarity = 1.0 - (double)distance / 256;
        var threshold = similarityThresholdPercent / 100.0;

        var isLeftImage = leftContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isRightImage = rightContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        if (!isLeftImage || !isRightImage)
        {
            threshold = Math.Max(threshold, 0.90);
        }

        if (similarity < threshold)
        {
            similarityPercent = 0;
            return false;
        }

        similarityPercent = (int)Math.Round(similarity * 100);
        return true;
    }
}
