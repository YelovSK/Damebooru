using Damebooru.Core.DTOs;
using Damebooru.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly DamebooruDbContext _context;

    public LogsController(DamebooruDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<AppLogListDto>> GetLogs(
        [FromQuery] string? level = null,
        [FromQuery] string? category = null,
        [FromQuery] string? contains = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] long? beforeId = null,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 500);

        var query = _context.AppLogEntries.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalizedLevel = level.Trim().ToLower();
            query = query.Where(e => e.Level.ToLower() == normalizedLevel);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalizedCategory = category.Trim().ToLower();
            query = query.Where(e => e.Category.ToLower().Contains(normalizedCategory));
        }

        if (!string.IsNullOrWhiteSpace(contains))
        {
            var term = contains.Trim().ToLower();
            query = query.Where(e => e.Message.ToLower().Contains(term)
                || (e.Exception != null && e.Exception.ToLower().Contains(term))
                || (e.PropertiesJson != null && e.PropertiesJson.ToLower().Contains(term)));
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(e => e.TimestampUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(e => e.TimestampUtc <= toUtc.Value);
        }

        if (beforeId.HasValue)
        {
            query = query.Where(e => e.Id < beforeId.Value);
        }

        var entries = await query
            .OrderByDescending(e => e.Id)
            .Take(take + 1)
            .Select(e => new AppLogEntryDto
            {
                Id = e.Id,
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                Category = e.Category,
                Message = e.Message,
                Exception = e.Exception,
                MessageTemplate = e.MessageTemplate,
                PropertiesJson = e.PropertiesJson,
            })
            .ToListAsync(cancellationToken);

        var hasMore = entries.Count > take;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return Ok(new AppLogListDto
        {
            Items = entries,
            HasMore = hasMore,
        });
    }
}
