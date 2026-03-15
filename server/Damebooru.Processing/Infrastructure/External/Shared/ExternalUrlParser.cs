using System.Text.RegularExpressions;

namespace Damebooru.Processing.Infrastructure.External.Shared;

internal static partial class ExternalUrlParser
{
    private static readonly string[] Separators = ["\r", "\n", " ", "\t"];

    public static IReadOnlyList<string> ParseMany(params string?[] rawValues)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawValue in rawValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            foreach (Match match in UrlRegex().Matches(rawValue))
            {
                AddUrl(urls, seen, match.Value);
            }

            foreach (var token in rawValue.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddUrl(urls, seen, token);
            }
        }

        return urls;
    }

    private static void AddUrl(List<string> urls, HashSet<string> seen, string rawValue)
    {
        var trimmed = rawValue.Trim().TrimEnd(',', ';');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalized = uri.ToString();
        if (seen.Add(normalized))
        {
            urls.Add(normalized);
        }
    }

    [GeneratedRegex("https?://[^\\s<>'\"]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
