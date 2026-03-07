using Damebooru.Core;
using Damebooru.Core.DTOs;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Results;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Damebooru.Processing.Services.Duplicates;

public class DuplicateLookupService
{
    private const int MaxPerceptualMatches = 50;

    private readonly DamebooruDbContext _context;
    private readonly IHasherService _hasherService;
    private readonly ISimilarityService _similarityService;
    private readonly ILogger<DuplicateLookupService> _logger;

    public DuplicateLookupService(
        DamebooruDbContext context,
        IHasherService hasherService,
        ISimilarityService similarityService,
        ILogger<DuplicateLookupService> logger)
    {
        _context = context;
        _hasherService = hasherService;
        _similarityService = similarityService;
        _logger = logger;
    }

    public async Task<Result<DuplicateLookupResponseDto>> LookupAsync(
        Func<Stream> openReadStream,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openReadStream);

        await using var hashStream = openReadStream();
        var contentHash = await _hasherService.ComputeContentHashAsync(hashStream, cancellationToken);
        var normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? GetFallbackContentType(fileName)
            : contentType.Trim();

        var response = await CreateExactLookupResponseAsync(
            fileName,
            normalizedContentType,
            TryResolveLength(hashStream),
            contentHash,
            cancellationToken);

        if (!IsImageLike(fileName, normalizedContentType))
        {
            response.PerceptualUnavailableReason = "Perceptual matching is only available for image uploads.";
            return Result<DuplicateLookupResponseDto>.Success(response);
        }

        PdqHashWords uploadHashWords;
        try
        {
            await using var similarityStream = openReadStream();
            var hashes = await _similarityService.ComputeHashesAsync(similarityStream, cancellationToken);
            if (!PdqHashMatchHelper.TryParseHex256(hashes.PdqHash256, out uploadHashWords))
            {
                response.PerceptualUnavailableReason = "Could not parse the uploaded image hash.";
                return Result<DuplicateLookupResponseDto>.Success(response);
            }

            response.PerceptualHashComputed = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute perceptual hash for uploaded file {FileName}", fileName);
            response.PerceptualUnavailableReason = "Could not compute a perceptual hash for this upload.";
            return Result<DuplicateLookupResponseDto>.Success(response);
        }

        var exactMatchIds = response.ExactMatches
            .Select(match => match.Id)
            .ToHashSet();

        var candidates = await _context.Posts
            .AsNoTracking()
            .Where(p => p.ContentType.StartsWith("image/") && !string.IsNullOrEmpty(p.PdqHash256))
            .Where(p => p.ContentHash != contentHash)
            .Select(p => new
            {
                p.Id,
                p.LibraryId,
                LibraryName = p.Library.Name,
                p.RelativePath,
                p.ContentHash,
                p.Width,
                p.Height,
                p.ContentType,
                p.SizeBytes,
                p.ImportDate,
                p.FileModifiedDate,
                p.PdqHash256,
            })
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            response.PerceptualUnavailableReason = "No stored perceptual hashes are available. Run the Compute Similarity job first.";
            return Result<DuplicateLookupResponseDto>.Success(response);
        }

        response.PerceptualMatches = candidates
            .Where(candidate => !exactMatchIds.Contains(candidate.Id))
            .Select(candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate.PdqHash256)
                    || !PdqHashMatchHelper.TryParseHex256(candidate.PdqHash256, out var candidateHash))
                {
                    return null;
                }

                if (!PdqHashMatchHelper.TryComputeSimilarity(
                    uploadHashWords,
                    normalizedContentType,
                    candidateHash,
                    candidate.ContentType,
                    PdqHashMatchHelper.DefaultSimilarityThresholdPercent,
                    out var similarityPercent))
                {
                    return null;
                }

                return new DuplicateLookupMatchDto
                {
                    Id = candidate.Id,
                    LibraryId = candidate.LibraryId,
                    LibraryName = candidate.LibraryName,
                    RelativePath = candidate.RelativePath,
                    ContentHash = candidate.ContentHash,
                    Width = candidate.Width,
                    Height = candidate.Height,
                    ContentType = candidate.ContentType,
                    SizeBytes = candidate.SizeBytes,
                    ImportDate = candidate.ImportDate,
                    FileModifiedDate = candidate.FileModifiedDate,
                    ThumbnailLibraryId = candidate.LibraryId,
                    ThumbnailContentHash = candidate.ContentHash,
                    SimilarityPercent = similarityPercent,
                };
            })
            .Where(match => match != null)
            .Select(match => match!)
            .OrderByDescending(match => match.SimilarityPercent)
            .ThenByDescending(match => match.Width * match.Height)
            .ThenByDescending(match => match.SizeBytes)
            .ThenByDescending(match => match.FileModifiedDate)
            .ThenByDescending(match => match.Id)
            .Take(MaxPerceptualMatches)
            .ToList();

        return Result<DuplicateLookupResponseDto>.Success(response);
    }

    public async Task<Result<DuplicateLookupResponseDto>> LookupExactByHashAsync(
        DuplicateHashLookupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ContentHash))
        {
            return Result<DuplicateLookupResponseDto>.Failure(OperationError.InvalidInput, "Content hash is required.");
        }

        var normalizedContentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? GetFallbackContentType(request.FileName)
            : request.ContentType.Trim();

        var response = await CreateExactLookupResponseAsync(
            request.FileName,
            normalizedContentType,
            request.SizeBytes,
            request.ContentHash.Trim(),
            cancellationToken);
        response.PerceptualUnavailableReason = "Perceptual matching is only available for image uploads.";
        return Result<DuplicateLookupResponseDto>.Success(response);
    }

    private async Task<DuplicateLookupResponseDto> CreateExactLookupResponseAsync(
        string fileName,
        string contentType,
        long sizeBytes,
        string contentHash,
        CancellationToken cancellationToken)
    {
        return new DuplicateLookupResponseDto
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            ExactMatches = await _context.Posts
                .AsNoTracking()
                .Where(p => p.ContentHash == contentHash)
                .OrderBy(p => p.Library.Name)
                .ThenBy(p => p.RelativePath)
                .ThenBy(p => p.Id)
                .Select(p => new DuplicateLookupMatchDto
                {
                    Id = p.Id,
                    LibraryId = p.LibraryId,
                    LibraryName = p.Library.Name,
                    RelativePath = p.RelativePath,
                    ContentHash = p.ContentHash,
                    Width = p.Width,
                    Height = p.Height,
                    ContentType = p.ContentType,
                    SizeBytes = p.SizeBytes,
                    ImportDate = p.ImportDate,
                    FileModifiedDate = p.FileModifiedDate,
                    ThumbnailLibraryId = p.LibraryId,
                    ThumbnailContentHash = p.ContentHash,
                    SimilarityPercent = null,
                })
                .ToListAsync(cancellationToken),
        };
    }

    private static bool IsImageLike(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName);
        return SupportedMedia.IsImage(extension)
            || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFallbackContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension)
            ? "application/octet-stream"
            : SupportedMedia.GetMimeType(extension);
    }

    private static long TryResolveLength(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return 0;
        }

        return stream.Length;
    }
}
