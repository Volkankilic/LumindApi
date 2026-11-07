using System.Text;
using Chatbot.Api.Security;
using Chatbot.Api.Startup;
using Chatbot.Application;
using Chatbot.Application.Admin;
using Chatbot.Application.Options;
using Chatbot.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Chatbot.Api.Controllers;

// Wire models of the admin panel. camelCase JSON, enums as camelCase strings. None of them has a text, nickname or code
// field except `IssuedCodeResponse.Code`, which exists only in the answer that created or reset the code.

public sealed record AdminLoginRequest(string? Username, string? Password);

public sealed record AdminLoginResponse(string Status, string MfaToken, int ExpiresInSeconds);

public sealed record AdminTotpRequest(string? MfaToken, string? Code, string? RecoveryCode);

public sealed record AdminAuthResponse(string AccessToken, int ExpiresInSeconds, string Username);

public sealed record AdminMeResponse(string Username);

public sealed record CreateParticipantsRequest(string? Label, string? LabelPrefix, int? Count);

public sealed record IssuedCodeResponse(Guid Id, string Label, string Code);

public sealed record CreatedParticipantsResponse(IReadOnlyList<IssuedCodeResponse> Participants);

public sealed record CrisisActionResponse(int Changed);

/// <summary>Admin sign-in: password, then TOTP or a recovery code. The refresh cookie lives only under /api/admin/auth.</summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/admin/auth")]
public class AdminAuthController(IAdminAuthService auth, AuthOptions authOptions) : ControllerBase
{
    public const string RefreshCookie = "lumind_admin_refresh";
    public const string CookiePath = "/api/admin/auth";

    /// <summary>Step one. Always the same answer shape; every failure is the same 401 `invalid-credentials`, a lock is a 429.</summary>
    [HttpPost("login")]
    [EnableRateLimiting(ApiServices.AdminLoginLimiter)]
    public async Task<AdminLoginResponse> Login(AdminLoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Username, request.Password, HttpContext.Client(), ct);
        return new AdminLoginResponse("totp-required", result.MfaToken, result.ExpiresInSeconds);
    }

    /// <summary>Step two. Only here the access token and the refresh cookie are issued.</summary>
    [HttpPost("totp")]
    [EnableRateLimiting(ApiServices.AdminTotpLimiter)]
    public async Task<AdminAuthResponse> Totp(AdminTotpRequest request, CancellationToken ct)
    {
        var result = await auth.CompleteAsync(request.MfaToken, request.Code, request.RecoveryCode, HttpContext.Client(), ct);
        SetRefreshCookie(result.Refresh);
        return new AdminAuthResponse(result.AccessToken, result.ExpiresInSeconds, result.Username);
    }

    /// <summary>Rotates the refresh token. Needs the custom header (CSRF defence in depth) and the cookie.</summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(ApiServices.AdminRefreshLimiter)]
    public async Task<AdminAuthResponse> Refresh(CancellationToken ct)
    {
        AdminRequestHeader.Require(Request);
        try
        {
            var result = await auth.RefreshAsync(Request.Cookies[RefreshCookie], HttpContext.Client(), ct);
            SetRefreshCookie(result.Refresh);
            return new AdminAuthResponse(result.AccessToken, result.ExpiresInSeconds, result.Username);
        }
        catch (AppException)
        {
            DeleteRefreshCookie();
            throw;
        }
    }

    [HttpPost("logout")]
    public async Task<NoContentResult> Logout(CancellationToken ct)
    {
        AdminRequestHeader.Require(Request);
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], HttpContext.Client(), ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    private void SetRefreshCookie(Application.Abstractions.IssuedRefreshToken refresh) =>
        Response.Cookies.Append(RefreshCookie, refresh.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = authOptions.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = refresh.ExpiresAt,
            IsEssential = true,
        });

    internal static CookieOptions DeleteOptions(AuthOptions options) => new()
    {
        HttpOnly = true,
        Secure = options.CookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
    };

    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookie, DeleteOptions(authOptions));
}

/// <summary>The panel API. Every action needs an active admin with a live session; none of them returns message text, nickname or category.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = AdminSecurity.Scheme, Policy = AdminSecurity.Policy)]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/admin")]
public class AdminController(
    IAdminAccess access,
    IAdminParticipantService participants,
    IAdminStatsService stats,
    IAdminCrisisService crisis) : ControllerBase
{
    private Guid AdminId => User.AdminId();

    [HttpGet("me")]
    public async Task<AdminMeResponse> Me(CancellationToken ct) =>
        new(await access.UsernameAsync(AdminId, ct) ?? throw AppException.Unauthorized());

    // ---- participants

    [HttpGet("participants")]
    public Task<PagedResult<ParticipantStats>> List(
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? sort = null,
        [FromQuery] bool desc = false,
        CancellationToken ct = default)
    {
        ParticipantStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ParticipantStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
            {
                throw AppException.ValidationFailed();
            }

            parsedStatus = s;
        }

        var parsedSort = ParticipantSort.Label;
        if (!string.IsNullOrWhiteSpace(sort) && (!Enum.TryParse(sort, ignoreCase: true, out parsedSort) || !Enum.IsDefined(parsedSort)))
        {
            throw AppException.ValidationFailed();
        }

        return participants.ListAsync(new ParticipantListQuery(q, parsedStatus, page, pageSize, parsedSort, desc), ct);
    }

    /// <summary>Single (`label`) or bulk (`labelPrefix` + `count`). The codes are in THIS answer and nowhere else, ever.</summary>
    [HttpPost("participants")]
    public async Task<ActionResult<CreatedParticipantsResponse>> Create(CreateParticipantsRequest request, CancellationToken ct)
    {
        var created = await participants.CreateAsync(AdminId, new CreateParticipantsInput(request.Label, request.LabelPrefix, request.Count), HttpContext.Client(), ct);
        return StatusCode(StatusCodes.Status201Created, new CreatedParticipantsResponse([.. created.Select(c => new IssuedCodeResponse(c.Id, c.Label, c.Code))]));
    }

    [HttpGet("participants/{id:guid}")]
    public Task<ParticipantDetail> Get(Guid id, CancellationToken ct) => participants.GetAsync(AdminId, id, HttpContext.Client(), ct);

    [HttpPost("participants/{id:guid}/reset-code")]
    public async Task<IssuedCodeResponse> ResetCode(Guid id, CancellationToken ct)
    {
        var issued = await participants.ResetCodeAsync(AdminId, id, HttpContext.Client(), ct);
        return new IssuedCodeResponse(issued.Id, issued.Label, issued.Code);
    }

    [HttpPost("participants/{id:guid}/revoke")]
    public async Task<NoContentResult> Revoke(Guid id, CancellationToken ct)
    {
        await participants.RevokeAsync(AdminId, id, HttpContext.Client(), ct);
        return NoContent();
    }

    [HttpPost("participants/{id:guid}/reactivate")]
    public async Task<NoContentResult> Reactivate(Guid id, CancellationToken ct)
    {
        await participants.ReactivateAsync(AdminId, id, HttpContext.Client(), ct);
        return NoContent();
    }

    // ---- statistics

    [HttpGet("stats/summary")]
    public Task<StatsSummary> Summary(CancellationToken ct) => stats.SummaryAsync(ct);

    [HttpGet("stats/export")]
    public async Task<FileContentResult> Export(CancellationToken ct)
    {
        var csv = await stats.ExportCsvAsync(AdminId, HttpContext.Client(), ct);
        return File(new UTF8Encoding(false).GetBytes(csv), "text/csv; charset=utf-8", "katilimci-istatistikleri.csv");
    }

    // ---- crisis flags

    [HttpGet("crisis")]
    public Task<CrisisOverview> CrisisList([FromQuery] bool openOnly = false, CancellationToken ct = default) =>
        crisis.ListAsync(AdminId, openOnly, HttpContext.Client(), ct);

    [HttpPost("crisis/{conversationId:guid}/acknowledge")]
    public async Task<CrisisActionResponse> Acknowledge(Guid conversationId, CancellationToken ct) =>
        new(await crisis.AcknowledgeAsync(AdminId, conversationId, HttpContext.Client(), ct));

    [HttpPost("crisis/{conversationId:guid}/reopen")]
    public async Task<CrisisActionResponse> Reopen(Guid conversationId, CancellationToken ct) =>
        new(await crisis.ReopenAsync(AdminId, conversationId, HttpContext.Client(), ct));
}
