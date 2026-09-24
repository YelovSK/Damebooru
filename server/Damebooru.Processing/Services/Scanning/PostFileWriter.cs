using Damebooru.Core;
using Damebooru.Core.Entities;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Processing.Services.Scanning;

internal sealed record FileSnapshot(
    string RelativePath,
    string Hash,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileIdentity? Identity);

/// <summary>
/// Tracked-entity mutations shared by full scans and watcher events. Callers save.
/// A post groups every file with the same content, so content changes move files between posts.
/// </summary>
internal static class PostFileWriter
{
    public static async Task<PostFile> AddAsync(
        DamebooruDbContext dbContext,
        int libraryId,
        FileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var postFile = new PostFile { LibraryId = libraryId };
        CopySnapshot(postFile, snapshot);

        var post = await FindPostByHashAsync(dbContext, snapshot.Hash, cancellationToken) ?? AddPost(dbContext);
        post.PostFiles.Add(postFile);
        return postFile;
    }

    /// <returns>Whether the content changed, which invalidates the file's derived media data.</returns>
    public static async Task<bool> ApplyAsync(
        DamebooruDbContext dbContext,
        PostFile postFile,
        FileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var contentChanged = !string.Equals(postFile.ContentHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase);
        if (contentChanged)
        {
            // A post's only file keeps the post when edited, so its tags survive.
            var matchingPost = await FindPostByHashAsync(dbContext, snapshot.Hash, cancellationToken);
            if (matchingPost != null)
            {
                postFile.Post = matchingPost;
            }
            else if (await HasSiblingFilesAsync(dbContext, postFile, cancellationToken))
            {
                postFile.Post = AddPost(dbContext);
            }

            postFile.Width = 0;
            postFile.Height = 0;
            postFile.PdqHash256 = null;
        }

        CopySnapshot(postFile, snapshot);
        return contentChanged;
    }

    public static void SetPath(PostFile postFile, string relativePath)
    {
        postFile.RelativePath = relativePath;
        postFile.ContentType = SupportedMedia.GetMimeType(Path.GetExtension(relativePath));
    }

    public static Task<int> DeleteEmptyPostsAsync(DamebooruDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.Posts
            .Where(p => !p.PostFiles.Any())
            .ExecuteDeleteAsync(cancellationToken);

    private static void CopySnapshot(PostFile postFile, FileSnapshot snapshot)
    {
        SetPath(postFile, snapshot.RelativePath);
        postFile.ContentHash = snapshot.Hash;
        postFile.SizeBytes = snapshot.SizeBytes;
        postFile.FileModifiedDate = snapshot.ModifiedUtc;
        postFile.FileIdentityDevice = snapshot.Identity?.Device ?? postFile.FileIdentityDevice;
        postFile.FileIdentityValue = snapshot.Identity?.Value ?? postFile.FileIdentityValue;
    }

    private static async Task<bool> HasSiblingFilesAsync(
        DamebooruDbContext dbContext,
        PostFile postFile,
        CancellationToken cancellationToken)
        => dbContext.PostFiles.Local.Any(pf => pf != postFile && pf.PostId == postFile.PostId)
            || await dbContext.PostFiles.AnyAsync(pf => pf.PostId == postFile.PostId && pf.Id != postFile.Id, cancellationToken);

    private static Post AddPost(DamebooruDbContext dbContext)
    {
        var post = new Post { ImportDate = DateTime.UtcNow };
        dbContext.Posts.Add(post);
        return post;
    }

    private static async Task<Post?> FindPostByHashAsync(
        DamebooruDbContext dbContext,
        string hash,
        CancellationToken cancellationToken)
    {
        // Posts created earlier in the same unsaved batch are only visible locally.
        var tracked = dbContext.Posts.Local
            .Where(p => p.PostFiles.Any(pf => string.Equals(pf.ContentHash, hash, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.ImportDate)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

        return tracked ?? await dbContext.Posts
            .Where(p => p.PostFiles.Any(pf => pf.ContentHash == hash))
            .OrderBy(p => p.ImportDate)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
