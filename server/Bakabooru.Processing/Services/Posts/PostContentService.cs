using Bakabooru.Core.Results;
using Bakabooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Processing.Services;

public sealed class PostContentDescriptor
{
    public required string FullPath { get; init; }
    public required string ContentType { get; init; }
}

public class PostContentService
{
    private readonly BakabooruDbContext _context;

    public PostContentService(BakabooruDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PostContentDescriptor>> GetPostContentAsync(int id, CancellationToken cancellationToken = default)
    {
        var post = await _context.Posts
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.RelativePath,
                p.ContentType,
                LibraryPath = p.Library.Path
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (post == null)
        {
            return Result<PostContentDescriptor>.Failure(OperationError.NotFound, "Post not found.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(post.LibraryPath, post.RelativePath));
        var libraryRoot = Path.GetFullPath(post.LibraryPath + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Result<PostContentDescriptor>.Failure(OperationError.InvalidInput, "Invalid file path");
        }

        if (!File.Exists(fullPath))
        {
            return Result<PostContentDescriptor>.Failure(OperationError.NotFound, "File not found on disk");
        }

        return Result<PostContentDescriptor>.Success(new PostContentDescriptor
        {
            FullPath = fullPath,
            ContentType = post.ContentType
        });
    }
}
