namespace Bakabooru.Processing.Pipeline;

public enum PostMediaType
{
    Image,
    Animation,
    Video
}

public enum SearchSortField
{
    ImportDate,
    TagCount,
    Width,
    Height,
    SizeBytes,
    Id
}

public enum SearchSortDirection
{
    Asc,
    Desc
}

public enum NumericComparisonOperator
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

public sealed record NumericFilter(NumericComparisonOperator Operator, int Value);

public class SearchQuery
{
    public List<string> IncludedTags { get; set; } = new();
    public List<string> ExcludedTags { get; set; } = new();
    public HashSet<PostMediaType> IncludedMediaTypes { get; set; } = [];
    public HashSet<PostMediaType> ExcludedMediaTypes { get; set; } = [];
    public NumericFilter? TagCountFilter { get; set; }
    public SearchSortField SortField { get; set; } = SearchSortField.ImportDate;
    public SearchSortDirection SortDirection { get; set; } = SearchSortDirection.Desc;
}

public static class QueryParser
{
    public static SearchQuery Parse(string query)
    {
        var result = new SearchQuery();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var isNegated = part.StartsWith('-');
            var token = isNegated ? part[1..] : part;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (TryParseDirective(result, token, isNegated))
            {
                continue;
            }

            if (isNegated)
            {
                result.ExcludedTags.Add(token);
            }
            else
            {
                result.IncludedTags.Add(token);
            }
        }

        return result;
    }

    private static bool TryParseDirective(SearchQuery result, string token, bool isNegated)
    {
        var separatorIndex = token.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
        {
            return false;
        }

        var key = token[..separatorIndex].Trim().ToLowerInvariant();
        var value = token[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (key is "type")
        {
            var parsedAny = false;
            foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseMediaType(raw, out var mediaType))
                {
                    continue;
                }

                parsedAny = true;
                if (isNegated)
                {
                    result.ExcludedMediaTypes.Add(mediaType);
                }
                else
                {
                    result.IncludedMediaTypes.Add(mediaType);
                }
            }

            return parsedAny;
        }

        if (key is "tag-count" or "tagcount")
        {
            if (!TryParseNumericFilter(value, out var numericFilter))
            {
                return true;
            }

            // Negative tag-count is uncommon and ambiguous; ignore it instead of turning it into a tag token.
            if (!isNegated)
            {
                result.TagCountFilter = numericFilter;
            }

            return true;
        }

        if (key is "sort" or "order")
        {
            if (TryParseSort(value, out var sortField, out var sortDirection))
            {
                result.SortField = sortField;
                result.SortDirection = sortDirection;
            }

            return true;
        }

        return false;
    }

    private static bool TryParseMediaType(string value, out PostMediaType mediaType)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "image":
            case "img":
                mediaType = PostMediaType.Image;
                return true;
            case "gif":
            case "animation":
            case "animated":
                mediaType = PostMediaType.Animation;
                return true;
            case "video":
            case "mp4":
            case "webm":
                mediaType = PostMediaType.Video;
                return true;
            default:
                mediaType = default;
                return false;
        }
    }

    private static bool TryParseSort(string value, out SearchSortField field, out SearchSortDirection direction)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var explicitDirection = (SearchSortDirection?)null;

        if (normalized.StartsWith('+'))
        {
            explicitDirection = SearchSortDirection.Asc;
            normalized = normalized[1..];
        }
        else if (normalized.StartsWith('-'))
        {
            explicitDirection = SearchSortDirection.Desc;
            normalized = normalized[1..];
        }

        if (normalized.EndsWith("_asc", StringComparison.Ordinal))
        {
            explicitDirection ??= SearchSortDirection.Asc;
            normalized = normalized[..^4];
        }
        else if (normalized.EndsWith("_desc", StringComparison.Ordinal))
        {
            explicitDirection ??= SearchSortDirection.Desc;
            normalized = normalized[..^5];
        }

        switch (normalized)
        {
            case "new":
            case "newest":
            case "date":
            case "import-date":
                field = SearchSortField.ImportDate;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "old":
            case "oldest":
                field = SearchSortField.ImportDate;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "tag-count":
            case "tagcount":
            case "tags":
                field = SearchSortField.TagCount;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "id":
                field = SearchSortField.Id;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "width":
                field = SearchSortField.Width;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "height":
                field = SearchSortField.Height;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "size":
            case "size-bytes":
            case "filesize":
                field = SearchSortField.SizeBytes;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            default:
                field = default;
                direction = default;
                return false;
        }
    }

    private static bool TryParseNumericFilter(string value, out NumericFilter filter)
    {
        var text = value.Trim();
        if (string.IsNullOrEmpty(text))
        {
            filter = default!;
            return false;
        }

        NumericComparisonOperator @operator;
        string numberPart;
        if (text.StartsWith(">="))
        {
            @operator = NumericComparisonOperator.GreaterThanOrEqual;
            numberPart = text[2..];
        }
        else if (text.StartsWith("<="))
        {
            @operator = NumericComparisonOperator.LessThanOrEqual;
            numberPart = text[2..];
        }
        else if (text.StartsWith('>'))
        {
            @operator = NumericComparisonOperator.GreaterThan;
            numberPart = text[1..];
        }
        else if (text.StartsWith('<'))
        {
            @operator = NumericComparisonOperator.LessThan;
            numberPart = text[1..];
        }
        else if (text.StartsWith('='))
        {
            @operator = NumericComparisonOperator.Equal;
            numberPart = text[1..];
        }
        else
        {
            @operator = NumericComparisonOperator.Equal;
            numberPart = text;
        }

        if (!int.TryParse(numberPart.Trim(), out var valueNumber))
        {
            filter = default!;
            return false;
        }

        filter = new NumericFilter(@operator, valueNumber);
        return true;
    }
}
