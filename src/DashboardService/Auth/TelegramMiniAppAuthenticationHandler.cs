using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GroupDashboard.Api.Auth;

/// <summary>
/// Authenticates Mini App requests using Telegram's signed initData.
/// REST requests send initData in X-Telegram-Init-Data.
/// SignalR sends it in the query string because browser WebSocket connections
/// cannot reliably attach arbitrary request headers.
/// </summary>
public sealed class TelegramMiniAppAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TelegramInitDataValidator validator,
    IWebHostEnvironment environment,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var isHubRequest = Request.Path.StartsWithSegments("/hubs");
        var initData = isHubRequest
            ? Request.Query["initData"].ToString()
            : Request.Headers["X-Telegram-Init-Data"].ToString();

        long managerTelegramId;

        if (validator.TryGetUserId(initData, out managerTelegramId))
        {
            return Task.FromResult(CreateSuccess(managerTelegramId));
        }

        // Local browser-only shortcut. It is disabled outside Development and
        // only applies when no initData was supplied (invalid initData is rejected).
        var developmentId = configuration.GetValue<long?>("MiniApp:DevelopmentManagerId");
        if (string.IsNullOrWhiteSpace(initData) && environment.IsDevelopment() && developmentId is > 0)
        {
            return Task.FromResult(CreateSuccess(developmentId.Value));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid or missing Telegram Mini App initData."));
    }

    private AuthenticateResult CreateSuccess(long managerTelegramId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, managerTelegramId.ToString()),
            new Claim(ClaimTypes.Name, $"telegram:{managerTelegramId}")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
