namespace Damebooru.Core.External;

public sealed record PostDiscoveryContext(
    int PostId,
    string RelativePath,
    string ContentType,
    string FilePath,
    string ContentHash,
    string? Md5Hash);
