using Damebooru.Core.DTOs;
using Damebooru.Processing.Services;
using Damebooru.Server.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly InboxSortService _inboxSortService;

    public InboxController(InboxSortService inboxSortService)
    {
        _inboxSortService = inboxSortService;
    }

    [HttpPost("sort")]
    public async Task<IActionResult> Sort([FromBody] SortInboxFilesDto dto, CancellationToken cancellationToken = default)
    {
        return await _inboxSortService.SortAsync(dto, cancellationToken).ToHttpResult();
    }
}
