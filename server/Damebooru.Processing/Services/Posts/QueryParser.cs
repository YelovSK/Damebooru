namespace Damebooru.Processing.Pipeline;

public enum PostMediaType
{
    Image,
    Animation,
    Video
}

public enum SearchSortField
{
    FileModifiedDate,
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
    public List<string> IncludedFilenames { get; set; } = new();
    public List<string> ExcludedFilenames { get; set; } = new();
    public HashSet<PostMediaType> IncludedMediaTypes { get; set; } = [];
    public HashSet<PostMediaType> ExcludedMediaTypes { get; set; } = [];
    public NumericFilter? TagCountFilter { get; set; }
    public bool? FavoriteFilter { get; set; }
    public SearchSortField SortField { get; set; } = SearchSortField.FileModifiedDate;
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

            var unescapedToken = UnescapeToken(token);
            if (isNegated)
            {
                result.ExcludedTags.Add(unescapedToken);
            }
            else
            {
                result.IncludedTags.Add(unescapedToken);
            }
        }

        return result;
    }

    private static bool TryParseDirective(SearchQuery result, string token, bool isNegated)
    {
        var separatorIndex = IndexOfUnescapedColon(token);
        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
        {
            return false;
        }

        var key = UnescapeToken(token[..separatorIndex]).Trim().ToLowerInvariant();
        var value = UnescapeToken(token[(separatorIndex + 1)..]).Trim();
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

        if (key is "favorite" or "fav")
        {
            if (!TryParseBoolean(value, out var favoriteValue))
            {
                return true;
            }

            result.FavoriteFilter = isNegated ? !favoriteValue : favoriteValue;
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

        if (key is "filename" or "file")
        {
            var parsedAny = false;
            foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                parsedAny = true;
                if (isNegated)
                {
                    result.ExcludedFilenames.Add(raw);
                }
                else
                {
                    result.IncludedFilenames.Add(raw);
                }
            }

            return parsedAny;
        }

        return false;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
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
        if (string.IsNullOrEmpty(normalized))
        {
            field = default;
            direction = default;
            return false;
        }

        var explicitDirection = (SearchSortDirection?)null;

        // Preferred syntax: sort:<field>[:asc|desc]
        var fieldToken = normalized;
        var separatorIndex = normalized.IndexOf(':');
        if (separatorIndex >= 0)
        {
            fieldToken = normalized[..separatorIndex].Trim();
            var directionToken = normalized[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrEmpty(fieldToken) || string.IsNullOrEmpty(directionToken) || directionToken.Contains(':'))
            {
                field = default;
                direction = default;
                return false;
            }

            if (!TryParseSortDirection(directionToken, out var parsedDirection))
            {
                field = default;
                direction = default;
                return false;
            }

            explicitDirection = parsedDirection;
        }
        else
        {
            // Backward compatibility: sort:+id / sort:-id / sort:id_asc / sort:id_desc
            if (fieldToken.StartsWith('+'))
            {
                explicitDirection = SearchSortDirection.Asc;
                fieldToken = fieldToken[1..];
            }
            else if (fieldToken.StartsWith('-'))
            {
                explicitDirection = SearchSortDirection.Desc;
                fieldToken = fieldToken[1..];
            }

            if (fieldToken.EndsWith("_asc", StringComparison.Ordinal))
            {
                explicitDirection ??= SearchSortDirection.Asc;
                fieldToken = fieldToken[..^4];
            }
            else if (fieldToken.EndsWith("_desc", StringComparison.Ordinal))
            {
                explicitDirection ??= SearchSortDirection.Desc;
                fieldToken = fieldToken[..^5];
            }
        }

        switch (fieldToken)
        {
            case "new":
            case "newest":
                field = SearchSortField.FileModifiedDate;
                direction = explicitDirection ?? SearchSortDirection.Desc;
                return true;
            case "old":
            case "oldest":
                field = SearchSortField.FileModifiedDate;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "date":
            case "modified-date":
            case "file-date":
            case "file-modified-date":
                field = SearchSortField.FileModifiedDate;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "import-date":
            case "imported-date":
                field = SearchSortField.ImportDate;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "tag-count":
            case "tagcount":
            case "tags":
                field = SearchSortField.TagCount;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "id":
                field = SearchSortField.Id;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "width":
                field = SearchSortField.Width;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "height":
                field = SearchSortField.Height;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            case "size":
            case "size-bytes":
            case "filesize":
                field = SearchSortField.SizeBytes;
                direction = explicitDirection ?? SearchSortDirection.Asc;
                return true;
            default:
                field = default;
                direction = default;
                return false;
        }
    }

    private static bool TryParseSortDirection(string value, out SearchSortDirection direction)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "asc":
                direction = SearchSortDirection.Asc;
                return true;
            case "desc":
                direction = SearchSortDirection.Desc;
                return true;
            default:
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

    private static int IndexOfUnescapedColon(string value)
    {
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == ':')
            {
                return i;
            }
        }

        return -1;
    }

    private static string UnescapeToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.IndexOf('\\') < 0)
        {
            return token;
        }

        Span<char> buffer = stackalloc char[token.Length];
        var writeIndex = 0;
        var escaped = false;

        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (escaped)
            {
                buffer[writeIndex++] = ch;
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            buffer[writeIndex++] = ch;
        }

        if (escaped)
        {
            buffer[writeIndex++] = '\\';
        }

        return new string(buffer[..writeIndex]);
    }
}
