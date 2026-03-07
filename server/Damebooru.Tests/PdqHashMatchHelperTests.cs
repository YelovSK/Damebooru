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
            "image/png",
            words,
            "image/jpeg",
            PdqHashMatchHelper.DefaultSimilarityThresholdPercent,
            out var similarityPercent);

        Assert.True(success);
        Assert.Equal(100, similarityPercent);
    }
}
