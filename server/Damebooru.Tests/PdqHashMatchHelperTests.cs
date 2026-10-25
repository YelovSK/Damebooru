using Damebooru.Processing.Services.Duplicates;

namespace Damebooru.Tests;

public class PdqHashMatchHelperTests
{
    [Fact]
    public void TryParseHex256_ValidHash_ReturnsWords()
    {
        var success = PdqHashMatchHelper.TryParseHex256(
            "0123456789abcdef111111111111111122222222222222223333333333333333",
            out var words);

        Assert.True(success);
        Assert.Equal(0x0123456789abcdefUL, words.W0);
        Assert.Equal(0x1111111111111111UL, words.W1);
        Assert.Equal(0x2222222222222222UL, words.W2);
        Assert.Equal(0x3333333333333333UL, words.W3);
    }

    [Fact]
    public void TryParseHex256_InvalidHash_ReturnsFalse()
    {
        var success = PdqHashMatchHelper.TryParseHex256("not-a-valid-hash", out _);

        Assert.False(success);
    }

    [Fact]
    public void TryComputeSimilarity_IdenticalHashes_ReturnsHundredPercent()
    {
        var words = new PdqHashWords(1, 2, 3, 4);

        var success = PdqHashMatchHelper.TryComputeSimilarity(
            words,
            words,
            similarityThresholdPercent: 1,
            out var similarityPercent);

        Assert.True(success);
        Assert.Equal(100, similarityPercent);
    }

    [Theory]
    [InlineData(0UL, 100)]
    [InlineData(0xFFFFUL, 75)]
    [InlineData(0xFFFFFFFFUL, 50)]
    public void TryComputeSimilarity_ScalesSoUnrelatedImagesScoreZero(ulong differingBitsInEachWord, int expectedPercent)
    {
        // Differing bits across the first two words: 0, 32 and 64 of 256.
        var left = new PdqHashWords(0, 0, 0, 0);
        var right = new PdqHashWords(differingBitsInEachWord, differingBitsInEachWord, 0, 0);

        PdqHashMatchHelper.TryComputeSimilarity(left, right, similarityThresholdPercent: 1, out var similarityPercent);

        Assert.Equal(expectedPercent, similarityPercent);
    }

    [Fact]
    public void OrderBySimilarity_PutsMostSimilarPairFirstThenClosestMatches()
    {
        var zero = new string('0', 64);
        var items = new (string Name, string? Hash)[]
        {
            ("far", new string('f', 16) + new string('0', 48)),
            ("noHash", null),
            ("a", zero),
            ("near", "0000000000000fff" + new string('0', 48)),
            ("b", zero),
        };

        var ordered = PdqHashMatchHelper.OrderBySimilarity(items, item => item.Hash);

        Assert.Equal(["a", "b", "near", "far", "noHash"], ordered.Select(item => item.Name));
    }

    [Fact]
    public void TryComputeSimilarity_HalfTheBitsDifferent_IsNoMatch()
    {
        var left = new PdqHashWords(0, 0, 0, 0);
        var right = new PdqHashWords(ulong.MaxValue, ulong.MaxValue, 0, 0);

        var success = PdqHashMatchHelper.TryComputeSimilarity(left, right, similarityThresholdPercent: 1, out var similarityPercent);

        Assert.False(success);
        Assert.Equal(0, similarityPercent);
    }
}
