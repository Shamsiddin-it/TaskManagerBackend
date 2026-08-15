using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MentorTaskFlow.Api.Authentication;
using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Telegram;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// Telegram account binding and the bot webhook (Приложение D.8, TZ 19).
/// </summary>
/// <remarks>
/// The bot performs no business actions: work cannot be approved or returned by a chat command
/// (<c>TG-004</c>). It carries notifications and binds accounts, and an unrecognised command gets a
/// help reply — a chat is not an authenticated context, and treating it as one would put the whole
/// authorisation model behind a message anyone can send.
/// </remarks>
[ApiController]
[Route("api/v1/telegram")]
[Produces("application/json")]
public sealed class TelegramController(
    ITelegramService telegram,
    IOptions<TelegramOptions> options) : ControllerBase
{
    private readonly TelegramOptions _options = options.Value;

    /// <summary>
    /// <c>POST /telegram/bind-token</c> — a one-time token and its deep link.
    /// </summary>
    /// <remarks>
    /// The response is the only place the plain token exists. Issuing a new one retires the previous
    /// (<c>TG-006</c>), and the endpoint is limited to five requests an hour per user (<c>TG-014</c>).
    /// </remarks>
    [HttpPost("bind-token")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<TelegramBindTokenDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TelegramBindTokenDto>> IssueBindTokenAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();

        var token = await telegram.IssueBindTokenAsync(cancellationToken);

        // The token is a bearer secret for the next fifteen minutes; no cache may keep it (TG-013).
        Response.Headers.CacheControl = "no-store";

        return StatusCode(StatusCodes.Status201Created, token);
    }

    [HttpGet("status")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<TelegramStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelegramStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();

        return Ok(await telegram.GetStatusAsync(cancellationToken));
    }

    /// <summary>
    /// <c>DELETE /telegram/binding</c> — 204, and notifications fall back to email (<c>NTF-002</c>).
    /// </summary>
    [HttpDelete("binding")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnbindAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();

        await telegram.UnbindAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// <c>POST /telegram/webhook</c> — anonymous to the outside world, guarded by a shared secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The secret header is the only thing separating Telegram from anyone who has guessed the URL, so
    /// it is compared in constant time and a mismatch answers 401 with no body at all (<c>TG-002</c>):
    /// a body would tell a prober that the endpoint exists and cares about the header.
    /// </para>
    /// <para>
    /// The reply is always 200. Telegram retries anything else, and a retry of a message we have
    /// already understood — a wrong token, say — would achieve nothing but load.
    /// </para>
    /// </remarks>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.TelegramWebhook)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> WebhookAsync(
        [FromBody] JsonElement update,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !HasValidSecret())
        {
            // The status is written directly and an empty result returned: every ordinary way of
            // producing a 401 in an [ApiController] attaches a ProblemDetails body, and TG-002
            // requires none — a body tells a prober that the endpoint exists and cares about the
            // header it just failed to present.
            Response.StatusCode = StatusCodes.Status401Unauthorized;

            return new EmptyResult();
        }

        var (chatId, text) = ReadMessage(update);

        if (chatId is null)
        {
            // An update we do not handle — an edited message, a channel post. Acknowledged so Telegram
            // stops resending it.
            return Ok();
        }

        var reply = ParseStartCommand(text) is { } token
            ? await telegram.RedeemBindTokenAsync(chatId, token, cancellationToken)
            : HelpReply;

        return Ok(new { method = "sendMessage", chat_id = chatId, text = reply });
    }

    private const string HelpReply =
        "Этот бот доставляет уведомления MentorTaskFlow. "
        + "Чтобы привязать аккаунт, откройте ссылку из личного кабинета. "
        + "Действия по задачам выполняются в приложении.";

    /// <summary>
    /// <c>TG-002</c>: constant-time comparison. Ordinary equality returns at the first differing byte,
    /// which turns the check into an oracle for recovering the secret.
    /// </summary>
    private bool HasValidSecret()
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return false;
        }

        var presented = Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(_options.WebhookSecret));
    }

    private static (string? ChatId, string? Text) ReadMessage(JsonElement update)
    {
        if (!update.TryGetProperty("message", out var message))
        {
            return (null, null);
        }

        var chatId = message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id)
            ? id.ValueKind is JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString()
            : null;

        var text = message.TryGetProperty("text", out var value) ? value.GetString() : null;

        return (chatId, text);
    }

    /// <summary>Extracts the token from <c>/start &lt;token&gt;</c>.</summary>
    private static string? ParseStartCommand(string? text)
    {
        if (text is null || !text.StartsWith("/start", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length is 2 ? parts[1] : null;
    }

    /// <summary>
    /// With the feature off the endpoints answer 404 rather than 403 (4.1).
    /// </summary>
    /// <remarks>
    /// A capability the installation does not have should be indistinguishable from one that does not
    /// exist; 403 would advertise that the feature is there and merely closed.
    /// </remarks>
    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new NotFoundException();
        }
    }
}
