using System.Collections.Concurrent;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services.Scanning;

internal sealed record TrackedFileChange(int PostFileId, string OldRelativePath, FileSnapshot Snapshot);

internal sealed record LibraryScanChanges(
    int Scanned,
    List<TrackedFileChange> Updates,
    List<TrackedFileChange> Moves,
    List<FileSnapshot> NewFiles,
    List<int> OrphanPostFileIds);

/// <summary>
/// Compares a library's files on disk with its tracked post files. Files may be inspected concurrently.
/// A new path whose file identity matches a tracked file that disappeared is a move, not a new file.
/// </summary>
internal sealed class LibraryScanDiff
{
    private sealed record TrackedFile(
        int PostFileId,
        string RelativePath,
        string Hash,
        long SizeBytes,
        DateTime ModifiedUtc,
        FileIdentity? Identity);

    private readonly IHasherService _hasher;
    private readonly IFileIdentityResolver _fileIdentityResolver;
    private readonly ILogger _logger;
    private readonly LibraryScanRules _rules;
    private readonly Dictionary<string, TrackedFile> _trackedByPath;
    private readonly Dictionary<FileIdentity, List<TrackedFile>> _trackedByIdentity;

    private readonly ConcurrentDictionary<string, byte> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<TrackedFileChange> _updates = [];
    private readonly ConcurrentBag<FileSnapshot> _moveCandidates = [];
    private readonly ConcurrentBag<FileSnapshot> _newFiles = [];
    private int _scanned;

    private LibraryScanDiff(
        IHasherService hasher,
        IFileIdentityResolver fileIdentityResolver,
        ILogger logger,
        LibraryScanRules rules,
        List<TrackedFile> trackedFiles)
    {
        _hasher = hasher;
        _fileIdentityResolver = fileIdentityResolver;
        _logger = logger;
        _rules = rules;
        _trackedByPath = trackedFiles
            .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _trackedByIdentity = trackedFiles
            .Where(f => f.Identity != null)
            .GroupBy(f => f.Identity!)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public int TrackedCount => _trackedByPath.Count;

    public static async Task<LibraryScanDiff> LoadAsync(
        DamebooruDbContext dbContext,
        int libraryId,
        IHasherService hasher,
        IFileIdentityResolver fileIdentityResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var rules = await LibraryScanRules.LoadAsync(dbContext, libraryId, cancellationToken);
        var trackedFiles = (await dbContext.PostFiles
            .AsNoTracking()
            .Where(pf => pf.LibraryId == libraryId)
            .Select(pf => new
            {
                pf.Id,
                pf.RelativePath,
                pf.Post.ContentHash,
                pf.Post.SizeBytes,
                pf.FileModifiedDate,
                pf.FileIdentityDevice,
                pf.FileIdentityValue
            })
            .ToListAsync(cancellationToken))
            .Select(pf => new TrackedFile(
                pf.Id,
                pf.RelativePath,
                pf.ContentHash,
                pf.SizeBytes,
                pf.FileModifiedDate,
                ToIdentity(pf.FileIdentityDevice, pf.FileIdentityValue)))
            .ToList();

        return new LibraryScanDiff(hasher, fileIdentityResolver, logger, rules, trackedFiles);
    }

    public async Task InspectAsync(MediaSourceItem item, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scanned);

        var relativePath = item.RelativePath;
        if (_rules.IsIgnored(relativePath))
        {
            return;
        }

        _seenPaths.TryAdd(relativePath, 0);

        string? hash = null;
        if (_rules.HasExclusion(relativePath))
        {
            hash = await _hasher.ComputeContentHashAsync(item.FullPath, cancellationToken);
            if (string.IsNullOrEmpty(hash) || _rules.IsExcluded(relativePath, hash))
            {
                return;
            }

            _logger.LogInformation("Exclusion mismatch for {Path}: path matched but hash changed, allowing ingest", relativePath);
        }

        if (_trackedByPath.TryGetValue(relativePath, out var tracked))
        {
            await InspectTrackedAsync(item, tracked, hash, cancellationToken);
            return;
        }

        hash ??= await _hasher.ComputeContentHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        var snapshot = new FileSnapshot(relativePath, hash, item.SizeBytes, item.LastModifiedUtc, identity);
        if (identity != null && _trackedByIdentity.ContainsKey(identity))
        {
            _moveCandidates.Add(snapshot);
        }
        else
        {
            _newFiles.Add(snapshot);
        }
    }

    /// <summary>Call once all files are inspected.</summary>
    public LibraryScanChanges Build()
    {
        var moves = new List<TrackedFileChange>();
        var newFiles = _newFiles.ToList();
        var movedPostFileIds = new HashSet<int>();

        foreach (var candidate in _moveCandidates)
        {
            var source = _trackedByIdentity[candidate.Identity!].FirstOrDefault(tracked =>
                !_seenPaths.ContainsKey(tracked.RelativePath)
                && !movedPostFileIds.Contains(tracked.PostFileId));

            if (source == null)
            {
                newFiles.Add(candidate);
                continue;
            }

            movedPostFileIds.Add(source.PostFileId);
            moves.Add(new TrackedFileChange(source.PostFileId, source.RelativePath, candidate));
        }

        var orphanPostFileIds = _trackedByPath.Values
            .Where(tracked => !_seenPaths.ContainsKey(tracked.RelativePath) && !movedPostFileIds.Contains(tracked.PostFileId))
            .Select(tracked => tracked.PostFileId)
            .ToList();

        return new LibraryScanChanges(_scanned, _updates.ToList(), moves, newFiles, orphanPostFileIds);
    }

    private async Task InspectTrackedAsync(MediaSourceItem item, TrackedFile tracked, string? hash, CancellationToken cancellationToken)
    {
        var fileChanged = item.SizeBytes != tracked.SizeBytes
            || Math.Abs((item.LastModifiedUtc - tracked.ModifiedUtc).TotalSeconds) > 1;

        if (!fileChanged)
        {
            // Files indexed before identity tracking existed get their identity backfilled.
            if (tracked.Identity != null)
            {
                return;
            }

            var resolvedIdentity = _fileIdentityResolver.TryResolve(item.FullPath);
            if (resolvedIdentity != null)
            {
                _updates.Add(new TrackedFileChange(
                    tracked.PostFileId,
                    tracked.RelativePath,
                    new FileSnapshot(tracked.RelativePath, tracked.Hash, tracked.SizeBytes, tracked.ModifiedUtc, resolvedIdentity)));
            }

            return;
        }

        hash ??= await _hasher.ComputeContentHashAsync(item.FullPath, cancellationToken);
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        if (!string.Equals(hash, tracked.Hash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("File changed: {Path} (size: {OldSize}->{NewSize})", tracked.RelativePath, tracked.SizeBytes, item.SizeBytes);
        }

        var identity = _fileIdentityResolver.TryResolve(item.FullPath);
        _updates.Add(new TrackedFileChange(
            tracked.PostFileId,
            tracked.RelativePath,
            new FileSnapshot(tracked.RelativePath, hash, item.SizeBytes, item.LastModifiedUtc, identity)));
    }

    private static FileIdentity? ToIdentity(string? device, string? value)
        => string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(value)
            ? null
            : new FileIdentity(device.Trim(), value.Trim());
}
