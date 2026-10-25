using System.Globalization;
using System.Numerics;

namespace Damebooru.Processing.Services.Duplicates;

internal readonly record struct PdqHashWords(ulong W0, ulong W1, ulong W2, ulong W3);

internal static class PdqHashMatchHelper
{
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
        PdqHashWords right,
        int similarityThresholdPercent,
        out int similarityPercent)
    {
        var distance = Distance(left, right);

        // Unrelated images differ in about half of the 256 bits, so 128 differing bits maps to 0%, not 50%.
        var similarity = Math.Max(0, 1.0 - (double)distance / 128);
        var threshold = similarityThresholdPercent / 100.0;

        if (similarity < threshold)
        {
            similarityPercent = 0;
            return false;
        }

        similarityPercent = (int)Math.Round(similarity * 100);
        return true;
    }

    internal static int Distance(PdqHashWords left, PdqHashWords right)
        => BitOperations.PopCount(left.W0 ^ right.W0)
         + BitOperations.PopCount(left.W1 ^ right.W1)
         + BitOperations.PopCount(left.W2 ^ right.W2)
         + BitOperations.PopCount(left.W3 ^ right.W3);

    /// <summary>
    /// Orders items so the most similar pair comes first, followed by the rest by their closest match among the
    /// already placed items. Items without a valid hash go last. Ties keep the input order.
    /// </summary>
    internal static List<T> OrderBySimilarity<T>(IReadOnlyList<T> items, Func<T, string?> getHash)
    {
        if (items.Count < 3)
        {
            return items.ToList();
        }

        var hashes = items
            .Select(item => getHash(item) is { } hex && TryParseHex256(hex, out var words) ? words : (PdqHashWords?)null)
            .ToArray();

        int GetDistance(int a, int b)
            => hashes[a] is { } left && hashes[b] is { } right ? Distance(left, right) : int.MaxValue;

        var (first, second) = (0, 1);
        var bestPairDistance = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                var distance = GetDistance(i, j);
                if (distance < bestPairDistance)
                {
                    bestPairDistance = distance;
                    (first, second) = (i, j);
                }
            }
        }

        var ordered = new List<T> { items[first], items[second] };
        var remaining = Enumerable.Range(0, items.Count).Where(i => i != first && i != second).ToList();
        var closest = remaining.ToDictionary(i => i, i => Math.Min(GetDistance(i, first), GetDistance(i, second)));

        while (remaining.Count > 0)
        {
            var next = remaining.MinBy(i => closest[i]);
            remaining.Remove(next);
            ordered.Add(items[next]);

            foreach (var i in remaining)
            {
                closest[i] = Math.Min(closest[i], GetDistance(i, next));
            }
        }

        return ordered;
    }
}
