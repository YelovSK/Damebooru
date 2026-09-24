using Damebooru.Core.Results;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services;

public sealed class PostContentDescriptor
{
    public required string FullPath { get; init; }
    public required string ContentType { get; init; }
}

public class PostContentService
{
    private readonly DamebooruDbContext _context;

    public PostContentService(DamebooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PostContentDescriptor>> GetPostContentAsync(int id, CancellationToken cancellationToken = default)
    {
        var post = await _context.Posts
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.ContentType,
                Files = p.PostFiles
                    .OrderBy(pf => pf.Id)
                    .Select(pf => new { LibraryPath = pf.Library.Path, pf.RelativePath })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (post == null)
        {
            return Result<PostContentDescriptor>.Failure(OperationError.NotFound, "Post not found.");
        }

        // Every file holds the same content, so any copy that still exists will do.
        foreach (var file in post.Files)
        {
            if (SafeSubpathResolver.TryResolve(file.LibraryPath, file.RelativePath, out var fullPath) && File.Exists(fullPath))
            {
                return Result<PostContentDescriptor>.Success(new PostContentDescriptor
                {
                    FullPath = fullPath,
                    ContentType = post.ContentType
                });
            }
        }

        return Result<PostContentDescriptor>.Failure(OperationError.NotFound, "File not found on disk");
    }
}
