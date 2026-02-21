namespace Damebooru.Core.Interfaces;

public sealed record FileIdentity(string Device, string Value);

public interface IFileIdentityResolver
{
    FileIdentity? TryResolve(string filePath);
}
