using System.Security.Claims;
using GroupDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GroupDashboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/groups/{chatId:long}/digest")]
public sealed class DigestsController(GroupDigestService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(long chatId, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var managerId) || managerId <= 0)
            return Unauthorized();
        if (date is { Year: < 1900 or > 9998 }) return BadRequest("Date is outside the supported range.");
        var digest = await service.Get(chatId, managerId, date, ct);
        return digest is null ? NotFound() : Ok(digest);
    }
}
