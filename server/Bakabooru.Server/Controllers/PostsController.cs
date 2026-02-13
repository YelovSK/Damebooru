using Bakabooru.Core.DTOs;
using Bakabooru.Data;
using Bakabooru.Processing.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly BakabooruDbContext _context;
    private readonly PostReadService _postReadService;
    private readonly PostWriteService _postWriteService;

    public PostsController(BakabooruDbContext context, PostReadService postReadService, PostWriteService postWriteService)
    {
        _context = context;
        _postReadService = postReadService;
        _postWriteService = postWriteService;
    }

    [HttpGet]
    public async Task<ActionResult<PostListDto>> GetPosts(
        [FromQuery] string? tags = null,
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        return await _postReadService.GetPostsAsync(tags, page, pageSize, cancellationToken);
    }

    [HttpGet("{id}/around")]
    public async Task<ActionResult<PostsAroundDto>> GetPostsAround(int id, [FromQuery] string? tags = null, CancellationToken cancellationToken = default)
    {
        var around = await _postReadService.GetPostsAroundAsync(id, tags, cancellationToken);
        if (around == null) return NotFound();
        return Ok(around);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PostDto>> GetPost(int id, CancellationToken cancellationToken = default)
    {
        var post = await _postReadService.GetPostAsync(id, cancellationToken);
        if (post == null) return NotFound();
        return post;
    }

    [HttpPost("{id}/tags")]
    public async Task<IActionResult> AddTag(int id, [FromBody] string tagName)
    {
        var result = await _postWriteService.AddTagAsync(id, tagName);
        return result.Error switch
        {
            AddTagError.None => NoContent(),
            AddTagError.PostNotFound => NotFound("Post not found"),
            AddTagError.EmptyTagName => BadRequest("Tag name cannot be empty"),
            AddTagError.TagAlreadyAssigned => Conflict("Tag already assigned"),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
    
    [HttpDelete("{id}/tags/{tagName}")]
    public async Task<IActionResult> RemoveTag(int id, string tagName)
    {
        var result = await _postWriteService.RemoveTagAsync(id, tagName);
        return result.Error switch
        {
            RemoveTagError.None => NoContent(),
            RemoveTagError.PostNotFound => NotFound("Post not found"),
            RemoveTagError.TagNotFoundOnPost => NotFound("Tag not found on post"),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetPostContent(int id, CancellationToken cancellationToken = default)
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

        if (post == null) return NotFound();

        var fullPath = Path.GetFullPath(Path.Combine(post.LibraryPath, post.RelativePath));
        var libraryRoot = Path.GetFullPath(post.LibraryPath + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid file path");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound("File not found on disk");
        }

        return PhysicalFile(fullPath, post.ContentType, enableRangeProcessing: true);
    }
}
