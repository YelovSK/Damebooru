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
/// A post is one piece of content, so a file whose content changes moves to the post holding that content.
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

        var post = await FindPostByHashAsync(dbContext, snapshot.Hash, cancellationToken) ?? AddPost(dbContext, snapshot);
        post.PostFiles.Add(postFile);
        return postFile;
    }

    /// <returns>Whether the content changed, which invalidates the post's derived media data.</returns>
    public static async Task<bool> ApplyAsync(
        DamebooruDbContext dbContext,
        PostFile postFile,
        FileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await dbContext.Entry(postFile).Reference(pf => pf.Post).LoadAsync(cancellationToken);

        var contentChanged = !string.Equals(postFile.Post.ContentHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase);
        if (contentChanged)
        {
            var matchingPost = await FindPostByHashAsync(dbContext, snapshot.Hash, cancellationToken);
            if (matchingPost != null)
            {
                postFile.Post = matchingPost;
            }
            else if (await HasSiblingFilesAsync(dbContext, postFile, cancellationToken))
            {
                postFile.Post = AddPost(dbContext, snapshot);
            }
            else
            {
                // A post's only file keeps the post when edited, so its tags survive.
                SetContent(postFile.Post, snapshot);
            }
        }

        CopySnapshot(postFile, snapshot);
        return contentChanged;
    }

    public static Task<int> DeleteEmptyPostsAsync(DamebooruDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.Posts
            .Where(p => !p.PostFiles.Any())
            .ExecuteDeleteAsync(cancellationToken);

    private static void CopySnapshot(PostFile postFile, FileSnapshot snapshot)
    {
        postFile.RelativePath = snapshot.RelativePath;
        postFile.FileModifiedDate = snapshot.ModifiedUtc;
        postFile.FileIdentityDevice = snapshot.Identity?.Device ?? postFile.FileIdentityDevice;
        postFile.FileIdentityValue = snapshot.Identity?.Value ?? postFile.FileIdentityValue;
    }

    private static void SetContent(Post post, FileSnapshot snapshot)
    {
        post.ContentHash = snapshot.Hash;
        post.SizeBytes = snapshot.SizeBytes;
        post.ContentType = SupportedMedia.GetMimeType(Path.GetExtension(snapshot.RelativePath));
        post.Width = 0;
        post.Height = 0;
        post.PdqHash256 = null;
    }

    private static Post AddPost(DamebooruDbContext dbContext, FileSnapshot snapshot)
    {
        var post = new Post
        {
            ImportDate = DateTime.UtcNow,
            FileModifiedDate = snapshot.ModifiedUtc,
        };
        SetContent(post, snapshot);
        dbContext.Posts.Add(post);
        return post;
    }

    private static async Task<bool> HasSiblingFilesAsync(
        DamebooruDbContext dbContext,
        PostFile postFile,
        CancellationToken cancellationToken)
        => dbContext.PostFiles.Local.Any(pf => pf != postFile && pf.PostId == postFile.PostId)
            || await dbContext.PostFiles.AnyAsync(pf => pf.PostId == postFile.PostId && pf.Id != postFile.Id, cancellationToken);

    private static async Task<Post?> FindPostByHashAsync(
        DamebooruDbContext dbContext,
        string hash,
        CancellationToken cancellationToken)
    {
        // Unsaved changes in the current batch are only visible locally: new posts, and posts whose content was replaced.
        var tracked = dbContext.Posts.Local.FirstOrDefault(p => string.Equals(p.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
        if (tracked != null)
        {
            return tracked;
        }

        var stored = await dbContext.Posts.FirstOrDefaultAsync(p => p.ContentHash == hash, cancellationToken);
        return stored != null && string.Equals(stored.ContentHash, hash, StringComparison.OrdinalIgnoreCase) ? stored : null;
    }
}
