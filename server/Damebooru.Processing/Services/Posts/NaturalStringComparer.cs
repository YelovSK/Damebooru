namespace Damebooru.Processing.Services;

/// <summary>Case-insensitive order that compares digit runs by value, so "page 9" sorts before "page 10".</summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var xStart = i;
                var yStart = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var xDigits = x.AsSpan(xStart, i - xStart).TrimStart('0');
                var yDigits = y.AsSpan(yStart, j - yStart).TrimStart('0');
                var numberResult = xDigits.Length != yDigits.Length
                    ? xDigits.Length.CompareTo(yDigits.Length)
                    : xDigits.SequenceCompareTo(yDigits);
                if (numberResult != 0) return numberResult;
                continue;
            }

            var charResult = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (charResult != 0) return charResult;
            i++;
            j++;
        }

        var remainingResult = (x.Length - i).CompareTo(y.Length - j);
        return remainingResult != 0 ? remainingResult : string.CompareOrdinal(x, y);
    }
}
