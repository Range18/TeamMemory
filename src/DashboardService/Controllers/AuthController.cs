using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GroupDashboard.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public sealed class AuthController(IWebHostEnvironment environment) : ControllerBase
{
    // GET /api/auth/me — useful for checking which Telegram account is authenticated.
    [HttpGet("me")]
    [Produces("application/json")]
    public ActionResult<CurrentMiniAppUserDto> GetCurrentUser()
    {
        var telegramId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Ok(new CurrentMiniAppUserDto(telegramId!, environment.IsDevelopment()));
    }
}

public sealed record CurrentMiniAppUserDto(string TelegramId, bool IsDevelopment);
