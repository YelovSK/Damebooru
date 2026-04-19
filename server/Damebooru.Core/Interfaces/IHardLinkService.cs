namespace Damebooru.Core.Interfaces;

public sealed record HardLinkResult(bool Success, string? FailureReason = null)
{
    public static HardLinkResult Ok() => new(true);
    public static HardLinkResult Fail(string reason) => new(false, reason);
}

public interface IHardLinkService
{
    HardLinkResult ReplaceWithHardLink(string existingFilePath, string canonicalFilePath);
}
