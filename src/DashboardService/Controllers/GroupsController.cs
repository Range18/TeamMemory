using System.Security.Claims;
using GroupDashboard.Api.Contracts;
using GroupDashboard.Api.Data;
using GroupDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GroupDashboard.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public sealed class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TelegramChatRequestService _telegramChatRequestService;
    private readonly ILogger<GroupsController> _logger;

    public GroupsController(
        AppDbContext db,
        TelegramChatRequestService telegramChatRequestService,
        ILogger<GroupsController> logger)
    {
        _db = db;
        _telegramChatRequestService = telegramChatRequestService;
        _logger = logger;
    }

    // GET /api/groups — return only groups linked to the current manager.
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(IReadOnlyList<GroupDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GroupDto>>> GetMyGroups(CancellationToken cancellationToken)
    {
        var managerTelegramId = GetManagerTelegramId();

        var groups = await _db.Groups
            .AsNoTracking()
            .Where(group => group.Managers.Any(link => link.ManagerTelegramId == managerTelegramId))
            .OrderBy(group => group.Title)
            .Select(group => new GroupDto(
                group.ChatId.ToString(),
                group.Title,
                group.Username,
                group.MemberCount,
                group.Status,
                group.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(groups);
    }

    // GET /api/groups/{chatId} — return a group only if the current manager has access.
    [HttpGet("{chatId:long}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(GroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GroupDto>> GetGroupByChatId(
        [FromRoute] long chatId,
        CancellationToken cancellationToken)
    {
        var managerTelegramId = GetManagerTelegramId();

        var group = await _db.Groups
            .AsNoTracking()
            .Where(item => item.ChatId == chatId)
            .Where(item => item.Managers.Any(link => link.ManagerTelegramId == managerTelegramId))
            .Select(item => new GroupDto(
                item.ChatId.ToString(),
                item.Title,
                item.Username,
                item.MemberCount,
                item.Status,
                item.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        if (group is null)
            return NotFound();

        return Ok(group);
    }

    // POST /api/groups/connect-request — ask Telegram to prepare its native chat picker.
    // The actual group selection and chat_shared update are handled by the external Collector.
    [HttpPost("connect-request")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ConnectGroupRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ConnectGroupRequestDto>> CreateConnectRequest(CancellationToken cancellationToken)
    {
        try
        {
            var managerTelegramId = GetManagerTelegramId();
            var preparedButtonId = await _telegramChatRequestService
                .CreatePreparedButtonAsync(managerTelegramId, cancellationToken);

            return Ok(new ConnectGroupRequestDto(preparedButtonId));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not prepare Telegram group selection button.");
            return Problem(
                detail: "Не удалось подготовить выбор группы в Telegram.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private long GetManagerTelegramId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(value, out var managerTelegramId) && managerTelegramId > 0)
            return managerTelegramId;

        throw new InvalidOperationException("Authenticated Telegram user id is missing.");
    }
}

public sealed record ConnectGroupRequestDto(string PreparedButtonId);
