using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;

namespace Damebooru.Processing.Services.Scanning;

internal sealed class LibraryWatchPendingState
{
    private sealed record PendingDeleteCandidate(
        string RelativePath,
        FileIdentity? Identity,
        bool IsDirectory,
        DateTime ExpiresAtUtc,
        long Sequence);

    private sealed record PendingDirectoryCreateCandidate(
        string RelativePath,
        DateTime ExpiresAtUtc,
        long Sequence);

    private readonly TimeSpan _gracePeriod;
    private readonly List<PendingDeleteCandidate> _pendingDeletes = [];
    private readonly List<PendingDirectoryCreateCandidate> _pendingDirectoryCreates = [];

    public LibraryWatchPendingState(TimeSpan gracePeriod)
    {
        _gracePeriod = gracePeriod;
    }

    public bool HasPendingExpirations => _pendingDeletes.Count > 0 || _pendingDirectoryCreates.Count > 0;

    public TimeSpan GetNextFlushDelay()
    {
        var now = DateTime.UtcNow;
        var nextExpiry = DateTime.MaxValue;
        if (_pendingDeletes.Count > 0)
        {
            nextExpiry = _pendingDeletes.Min(p => p.ExpiresAtUtc);
        }

        if (_pendingDirectoryCreates.Count > 0)
        {
            var nextCreateExpiry = _pendingDirectoryCreates.Min(p => p.ExpiresAtUtc);
            if (nextCreateExpiry < nextExpiry)
            {
                nextExpiry = nextCreateExpiry;
            }
        }

        var delay = nextExpiry - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    public void FlushExpiredDirectoryCreates()
    {
        if (_pendingDirectoryCreates.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _pendingDirectoryCreates.RemoveAll(p => p.ExpiresAtUtc <= now);
    }

    public IReadOnlyList<(string RelativePath, bool IsDirectory)> TakeExpiredDeleteEntries()
        => TakeExpiredDeleteCandidates().Select(p => (p.RelativePath, p.IsDirectory)).ToList();

    public IReadOnlyList<(string RelativePath, bool IsDirectory)> TakeAllDeletes()
    {
        var items = _pendingDeletes
            .OrderBy(p => p.Sequence)
            .Select(p => (p.RelativePath, p.IsDirectory))
            .ToList();
        _pendingDeletes.Clear();
        return items;
    }

    public void ClearDirectoryCreates() => _pendingDirectoryCreates.Clear();

    public void StageDelete(string relativePath, FileIdentity? identity, bool isDirectory, long sequence)
    {
        RemovePendingDelete(relativePath);
        _pendingDeletes.Add(new PendingDeleteCandidate(
            relativePath,
            identity,
            isDirectory,
            DateTime.UtcNow.Add(_gracePeriod),
            sequence));
    }

    public void StageDirectoryCreate(string relativePath, long sequence)
    {
        RemovePendingDirectoryCreate(relativePath);
        _pendingDirectoryCreates.Add(new PendingDirectoryCreateCandidate(
            relativePath,
            DateTime.UtcNow.Add(_gracePeriod),
            sequence));
    }

    public (string OldPath, string NewPath)? TryMatchDirectoryMoveFromCreate(string newRelativePath)
    {
        var match = TryMatchPendingDirectoryDelete(newRelativePath);
        return match == null ? null : (match.RelativePath, newRelativePath);
    }

    public (string OldPath, string NewPath)? TryMatchDirectoryMoveFromDelete(string oldRelativePath)
    {
        var match = TryMatchPendingDirectoryCreate(oldRelativePath);
        return match == null ? null : (oldRelativePath, match.RelativePath);
    }

    public string? TryMatchFileMove(string newRelativePath, FileIdentity? identity)
    {
        var match = TryMatchPendingDelete(identity);
        return match?.RelativePath;
    }

    public void RemovePendingDirectoryCreate(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        _pendingDirectoryCreates.RemoveAll(p => string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    public void RemovePendingDelete(string relativePath)
    {
        _pendingDeletes.RemoveAll(p => string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private PendingDeleteCandidate? TryMatchPendingDelete(FileIdentity? identity)
    {
        if (identity == null)
        {
            return null;
        }

        var match = _pendingDeletes
            .Where(p => !p.IsDirectory)
            .Where(p => IdentityEquals(p.Identity, identity))
            .OrderBy(p => p.Sequence)
            .FirstOrDefault();

        if (match != null)
        {
            _pendingDeletes.Remove(match);
        }

        return match;
    }

    private PendingDeleteCandidate? TryMatchPendingDirectoryDelete(string newRelativePath)
    {
        var normalizedLeaf = GetLeafName(newRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedLeaf))
        {
            return null;
        }

        var matches = _pendingDeletes
            .Where(p => p.IsDirectory)
            .Where(p => string.Equals(GetLeafName(p.RelativePath), normalizedLeaf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Sequence)
            .Take(2)
            .ToList();

        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        _pendingDeletes.Remove(match);
        return match;
    }

    private PendingDirectoryCreateCandidate? TryMatchPendingDirectoryCreate(string oldRelativePath)
    {
        var normalizedLeaf = GetLeafName(oldRelativePath);
        if (string.IsNullOrWhiteSpace(normalizedLeaf))
        {
            return null;
        }

        var matches = _pendingDirectoryCreates
            .Where(p => string.Equals(GetLeafName(p.RelativePath), normalizedLeaf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Sequence)
            .Take(2)
            .ToList();

        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        _pendingDirectoryCreates.Remove(match);
        return match;
    }

    private List<PendingDeleteCandidate> TakeExpiredDeleteCandidates()
    {
        if (_pendingDeletes.Count == 0)
        {
            return [];
        }

        var now = DateTime.UtcNow;
        var expired = _pendingDeletes
            .Where(p => p.ExpiresAtUtc <= now)
            .OrderBy(p => p.Sequence)
            .ToList();

        foreach (var item in expired)
        {
            _pendingDeletes.Remove(item);
        }

        return expired;
    }

    private static bool IdentityEquals(FileIdentity? left, FileIdentity? right)
        => left != null
            && right != null
            && string.Equals(left.Device, right.Device, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static string GetLeafName(string relativePath)
    {
        var normalizedPath = RelativePathMatcher.NormalizePath(relativePath);
        return Path.GetFileName(normalizedPath);
    }
}
