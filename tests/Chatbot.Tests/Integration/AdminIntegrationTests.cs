using System.Net;
using System.Text;
using System.Text.Json;
using Chatbot.Api.Security;
using Chatbot.Domain;
using Chatbot.Domain.Entities;
using Chatbot.Infrastructure.Admin;
using Chatbot.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Integration;

/// <summary>An admin signed in through the real HTTP pipeline: bearer client plus the refresh cookie, handled by hand.</summary>
internal sealed class AdminSession(HttpClient client, string accessToken, string cookie, TestAdmin admin)
{
    public HttpClient Client { get; } = client;

    public string AccessToken { get; } = accessToken;

    public string Cookie { get; set; } = cookie;

    public TestAdmin Admin { get; } = admin;
}

internal static class AdminHttp
{
    public static HttpClient Plain(this ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static async Task<HttpResponseMessage> LoginAsync(this ApiFactory factory, string username, string password) =>
        await factory.Plain().PostAsync("/api/admin/auth/login", Json(new { username, password }));

    public static async Task<string> MfaTokenAsync(this ApiFactory factory, TestAdmin admin)
    {
        var response = await factory.LoginAsync(admin.Username, admin.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("mfaToken").GetString()!;
    }

    /// <summary>The current step, or the step AFTER the one used last (at most one step ahead): two sign-ins of one test can follow each other, a third needs a recovery code.</summary>
    public static async Task<string> NextTotpAsync(TestAdmin admin)
    {
        await using var db = TestDatabase.NewContext();
        var last = (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == admin.Id)).TotpLastStep;
        var now = Totp.StepOf(DateTimeOffset.UtcNow);
        var step = Math.Max(now, last + 1); // the CURRENT step first: if the 30 s boundary passes while the request is on its way, it is still inside the window
        Assert.True(step <= now + Totp.Window, "no TOTP step is left for this test admin right now: use a recovery code");
        return Totp.Compute(admin.Secret, step);
    }

    public static async Task<AdminSession> SignInAsync(this ApiFactory factory, TestAdmin admin, bool recovery = false)
    {
        var mfa = await factory.MfaTokenAsync(admin);
        object body = recovery
            ? new { mfaToken = mfa, recoveryCode = RecoveryCodes.Display(admin.RecoveryCodes[RecoveryCursor.Next(admin)]) }
            : new { mfaToken = mfa, code = await NextTotpAsync(admin) };
        var response = await factory.Plain().PostAsync("/api/admin/auth/totp", Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.ReadJsonAsync();
        var token = json.GetProperty("accessToken").GetString()!;
        var cookie = CookieValue(response)!;
        var client = factory.Plain();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return new AdminSession(client, token, cookie, admin);
    }

    public static string? CookieValue(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var header = values.FirstOrDefault(v => v.StartsWith("lumind_admin_refresh=", StringComparison.Ordinal));
        var value = header?["lumind_admin_refresh=".Length..].Split(';')[0];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static Task<HttpResponseMessage> AdminRefreshAsync(this ApiFactory factory, string? cookie, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/refresh");
        if (header)
        {
            request.Headers.Add(AdminSecurity.RequestHeader, "1");
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"lumind_admin_refresh={cookie}");
        }

        return factory.Plain().SendAsync(request);
    }

    public static async Task<JsonElement> GetJsonAsync(this AdminSession session, string url)
    {
        var response = await session.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public static async Task<List<object?[]>> AuditAsync(Guid actorId) =>
        await RawSql.RowsAsync(
            "SELECT action, actor_type, target_type, target_id, to_jsonb(a)::text FROM public.audit_logs a WHERE actor_id = @p ORDER BY \"timestamp\"",
            ("p", actorId));
}

internal static class RecoveryCursor
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> Used = new();

    public static int Next(TestAdmin admin) => Used.AddOrUpdate(admin.Id, 0, (_, n) => n + 1);
}

/// <summary>
/// The admin panel against the real database (needs 003_admin.sql). Test admins are `itest-`; participants created through the admin API
/// are renamed to `itest-` at once and deleted at the end. Real admin accounts and the seed participants are never touched.
/// </summary>
public class AdminAuthIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    [PostgresFact]
    public async Task SignIn_NeedsThePasswordAndThenTotp_AndOpensThePanelApi()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();

        var session = await factory.SignInAsync(admin);

        var me = await session.GetJsonAsync("/api/admin/me");
        Assert.Equal(admin.Username, me.GetProperty("username").GetString());
        var audit = await AdminHttp.AuditAsync(admin.Id);
        Assert.Contains(audit, r => (string)r[0]! == AuditActions.AdminLoginSucceeded && (string)r[1]! == "Researcher");
        Assert.All(audit, r => Assert.Contains((string)r[0]!, AuditActions.All));
    }

    [PostgresFact]
    public async Task APasswordWithoutTotp_NeverGivesAnAccessToken_AndTheMfaTokenOpensNothing()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var mfa = await factory.MfaTokenAsync(admin);

        var client = factory.Plain();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mfa);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/me")).StatusCode);

        var wrong = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = mfa, code = "000000" }));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Null(AdminHttp.CookieValue(wrong));
        Assert.DoesNotContain("accessToken", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var junk = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = "not-a-token", code = await AdminHttp.NextTotpAsync(admin) }));
        Assert.Equal(HttpStatusCode.Unauthorized, junk.StatusCode);
    }

    [PostgresFact]
    public async Task AWrongPassword_AnUnknownName_AndADisabledAccount_AllGetTheSameAnswer()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var disabled = await host.CreateAdminAsync();
        await using (var db = TestDatabase.NewContext())
        {
            (await db.AdminUsers.SingleAsync(a => a.Id == disabled.Id)).Status = AdminStatus.Disabled;
            await db.SaveChangesAsync();
        }

        var answers = new List<(HttpStatusCode, string)>();
        foreach (var (name, password) in new[] { (admin.Username, "wrong-password-xyz"), ("itest-yok-boyle-biri", "wrong-password-xyz"), (disabled.Username, disabled.Password) })
        {
            var response = await factory.LoginAsync(name, password);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a.Item1));
        Assert.Single(answers.Select(a => a.Item2).Distinct()); // identical bodies
        Assert.Contains("invalid-credentials", answers[0].Item2, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AfterFiveFailures_TheAccountIsLocked_EvenForTheRightPassword_AndAnUnknownNameBehavesTheSame()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var unknown = "itest-yok-" + Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.LoginAsync(admin.Username, "wrong-password-xyz")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.LoginAsync(unknown, "wrong-password-xyz")).StatusCode);
        }

        var lockedReal = await factory.LoginAsync(admin.Username, admin.Password); // the RIGHT password
        var lockedUnknown = await factory.LoginAsync(unknown, "wrong-password-xyz");
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedReal.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedUnknown.StatusCode);
        Assert.True(lockedReal.Headers.Contains("Retry-After") && lockedUnknown.Headers.Contains("Retry-After"));
        // Same answer for a real and an unknown name (the seconds left differ by a rounding step between the two requests).
        static string Normalised(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"retryAfterSeconds\":\\d+", "\"retryAfterSeconds\":N");
        Assert.Equal(Normalised(await lockedReal.Content.ReadAsStringAsync()), Normalised(await lockedUnknown.Content.ReadAsStringAsync()));

        var audit = await AdminHttp.AuditAsync(admin.Id);
        Assert.Contains(audit, r => (string)r[0]! == AuditActions.AdminAccountLocked);
        Assert.Equal(5, audit.Count(r => (string)r[0]! == AuditActions.AdminLoginFailed));
    }

    [PostgresFact]
    public async Task ATotpCodeWorksOnce_TheSameCodeAgainIsAReplay()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var code = await AdminHttp.NextTotpAsync(admin);

        var first = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), code }));
        var replay = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), code }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains(await AdminHttp.AuditAsync(admin.Id), r => (string)r[0]! == AuditActions.AdminTotpFailed);
    }

    [PostgresFact]
    public async Task ARecoveryCode_IsSingleUse()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var code = RecoveryCodes.Display(admin.RecoveryCodes[0]);

        var first = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), recoveryCode = code }));
        var second = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), recoveryCode = code.ToLowerInvariant() }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains(await AdminHttp.AuditAsync(admin.Id), r => (string)r[0]! == AuditActions.AdminRecoveryCodeUsed);
        var used = await RawSql.RowsAsync("SELECT count(*) FROM public.admin_recovery_codes WHERE admin_id = @a AND used_at IS NOT NULL", ("a", admin.Id));
        Assert.Equal(1L, used.Single()[0]);
    }

    [PostgresFact]
    public async Task RefreshRotatesTheCookie_APastCookieIsReuse_AndKillsTheWholeSession()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var session = await factory.SignInAsync(admin);

        var refreshed = await factory.AdminRefreshAsync(session.Cookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newCookie = AdminHttp.CookieValue(refreshed)!;
        Assert.NotEqual(session.Cookie, newCookie);
        var newToken = (await refreshed.ReadJsonAsync()).GetProperty("accessToken").GetString()!;

        var reuse = await factory.AdminRefreshAsync(session.Cookie); // the spent cookie comes back
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AdminRefreshAsync(newCookie)).StatusCode); // the family is revoked
        var client = factory.Plain();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/me")).StatusCode); // even the access token is dead
        Assert.Contains(await AdminHttp.AuditAsync(admin.Id), r => (string)r[0]! == AuditActions.AdminRefreshReuseDetected);
    }

    [PostgresFact]
    public async Task RefreshWithoutTheCustomHeader_IsRefused_AndTheSessionStaysIntact()
    {
        using var factory = host.NewFactory();
        var session = await factory.SignInAsync(await host.CreateAdminAsync());

        var withoutHeader = await factory.AdminRefreshAsync(session.Cookie, header: false);
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.AdminRefreshAsync(session.Cookie)).StatusCode); // the cookie was not spent by the refused call
    }

    [PostgresFact]
    public async Task Logout_EndsTheSessionAtOnce()
    {
        using var factory = host.NewFactory();
        var session = await factory.SignInAsync(await host.CreateAdminAsync());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/logout");
        request.Headers.Add(AdminSecurity.RequestHeader, "1");
        request.Headers.Add("Cookie", $"lumind_admin_refresh={session.Cookie}");

        Assert.Equal(HttpStatusCode.NoContent, (await factory.Plain().SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AdminRefreshAsync(session.Cookie)).StatusCode);
        Assert.Contains(await AdminHttp.AuditAsync(session.Admin.Id), r => (string)r[0]! == AuditActions.AdminLogout);
    }

    [PostgresFact]
    public async Task ADisabledAccount_LosesItsOpenSessionAtOnce_AndCannotSignInAgain()
    {
        using var factory = host.NewFactory();
        var session = await factory.SignInAsync(await host.CreateAdminAsync());
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/admin/me")).StatusCode);

        await using (var db = TestDatabase.NewContext())
        {
            (await db.AdminUsers.SingleAsync(a => a.Id == session.Admin.Id)).Status = AdminStatus.Disabled;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AdminRefreshAsync(session.Cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.LoginAsync(session.Admin.Username, session.Admin.Password)).StatusCode);
    }

    [PostgresFact]
    public async Task AnExpiredIdleSession_CannotBeRefreshed()
    {
        using var factory = host.NewFactory(new() { ["Admin:IdleMinutes"] = "1", ["Admin:AccessTokenMinutes"] = "1" });
        var session = await factory.SignInAsync(await host.CreateAdminAsync());
        await using (var db = TestDatabase.NewContext())
        {
            foreach (var token in await db.AdminRefreshTokens.Where(t => t.AdminId == session.Admin.Id).ToListAsync())
            {
                token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); // idle for longer than the limit
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AdminRefreshAsync(session.Cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/me")).StatusCode);
    }

    [PostgresFact]
    public async Task AConfirmedTotpReset_KillsTheOldSessionTheOldSecretAndTheOldRecoveryCodes_AndTheNewSecretSignsIn()
    {
        using var factory = host.NewFactory();
        var admin = await host.CreateAdminAsync();
        var session = await factory.SignInAsync(admin);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AdminAccountService>();
            var (problem, pending) = await accounts.PrepareTotpResetAsync(admin.Username, default);
            Assert.Equal(AdminAccountProblem.None, problem);
            var code = Totp.Compute(Base32.Decode(pending!.SecretBase32)!, Totp.StepOf(DateTimeOffset.UtcNow));
            Assert.Equal(AdminAccountProblem.None, await accounts.ConfirmTotpResetAsync(pending, code, default));

            // the old session is closed at once
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync("/api/admin/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.AdminRefreshAsync(session.Cookie)).StatusCode);

            // the old recovery codes and the old secret no longer sign in
            var oldRecovery = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), recoveryCode = RecoveryCodes.Display(admin.RecoveryCodes[4]) }));
            Assert.Equal(HttpStatusCode.Unauthorized, oldRecovery.StatusCode);
            var oldCode = Totp.Compute(admin.Secret, Totp.StepOf(DateTimeOffset.UtcNow));
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), code = oldCode }))).StatusCode);

            // a NEW recovery code signs in (the new secret's own next step would also do, but the confirming step is spent)
            var fresh = await factory.Plain().PostAsync("/api/admin/auth/totp", AdminHttp.Json(new { mfaToken = await factory.MfaTokenAsync(admin), recoveryCode = pending.RecoveryCodes[0] }));
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal(1L, (await RawSql.RowsAsync("SELECT count(*) FROM public.audit_logs WHERE action = @a AND target_id = @t", ("a", AuditActions.AdminTotpReset), ("t", admin.Id))).Single()[0]);
            Assert.Contains(await AdminHttp.AuditAsync(admin.Id), r => (string)r[0]! == AuditActions.AdminLoginSucceeded);
        }
    }

    [PostgresFact]
    public async Task TheStoredAdmin_HasOnlyAHashedPassword_AnEncryptedTotpSecret_AndHashedRecoveryCodes()
    {
        var admin = await host.CreateAdminAsync();
        var row = (await RawSql.RowsAsync("SELECT password_hash, totp_secret FROM public.admin_users WHERE id = @a", ("a", admin.Id))).Single();
        Assert.StartsWith("pbkdf2-sha512$", (string)row[0]!, StringComparison.Ordinal);
        Assert.DoesNotContain(admin.Password, (string)row[0]!, StringComparison.Ordinal);
        Assert.StartsWith("enc:v1:", (string)row[1]!, StringComparison.Ordinal);
        Assert.DoesNotContain(Base32.Encode(admin.Secret), (string)row[1]!, StringComparison.Ordinal);
        var hashes = (await RawSql.RowsAsync("SELECT code_hash FROM public.admin_recovery_codes WHERE admin_id = @a", ("a", admin.Id))).Select(r => (string)r[0]!).ToList();
        Assert.Equal(5, hashes.Count);
        Assert.All(admin.RecoveryCodes, c => Assert.DoesNotContain(hashes, h => h.Contains(c, StringComparison.Ordinal)));
    }
}

public class AdminPanelIntegrationTests(IntegrationHost host) : IClassFixture<IntegrationHost>
{
    private const string Marker = "GIZLI-PANEL-METNI-4711";

    private static async Task<List<(Guid Id, string Label, string Code)>> CreateAsync(AdminSession session, object body)
    {
        var response = await session.Client.PostAsync("/api/admin/participants", AdminHttp.Json(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return [.. (await response.ReadJsonAsync()).GetProperty("participants").EnumerateArray()
            .Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("label").GetString()!, p.GetProperty("code").GetString()!))];
    }

    private async Task<AdminSession> SignedInAsync(ApiFactory factory) => await factory.SignInAsync(await host.CreateAdminAsync());

    [PostgresFact]
    public async Task ACreatedParticipant_CanSignInWithTheCode_ButTheCodeIsShownOnlyOnce()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        var prefix = "Zt" + Guid.NewGuid().ToString("N")[..6] + "-";

        var created = await CreateAsync(session, new { labelPrefix = prefix, count = 3 });

        Assert.Equal([$"{prefix}001", $"{prefix}002", $"{prefix}003"], created.Select(c => c.Label));
        foreach (var c in created)
        {
            await host.AdoptAsync(c.Id);
        }

        var signedIn = await factory.PostCodeAsync(created[0].Code);
        Assert.Equal(HttpStatusCode.OK, signedIn.Response.StatusCode);

        // Nothing shows the code again: not the list, not the detail, not the export, not the database, not the audit trail.
        var everywhere = new StringBuilder();
        everywhere.Append(await (await session.Client.GetAsync("/api/admin/participants?pageSize=100")).Content.ReadAsStringAsync());
        everywhere.Append(await (await session.Client.GetAsync($"/api/admin/participants/{created[0].Id}")).Content.ReadAsStringAsync());
        everywhere.Append(await (await session.Client.GetAsync("/api/admin/stats/export")).Content.ReadAsStringAsync());
        everywhere.Append(string.Join(' ', (await AdminHttp.AuditAsync(session.Admin.Id)).Select(r => (string)r[4]!)));
        everywhere.Append(string.Join(' ', (await RawSql.RowsAsync("SELECT to_jsonb(p)::text FROM public.participants p WHERE id = @i", ("i", created[0].Id))).Select(r => (string)r[0]!)));
        foreach (var c in created)
        {
            Assert.DoesNotContain(c.Code, everywhere.ToString(), StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task ResetRevokeAndReactivate_ChangeWhoCanSignIn_ForReal()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        var created = (await CreateAsync(session, new { label = "Zt" + Guid.NewGuid().ToString("N")[..8] })).Single();
        await host.AdoptAsync(created.Id);

        var participant = await factory.SignInAsync(new TestParticipant(created.Id, created.Code)); // signs in: Active, has a refresh cookie
        Assert.Equal(HttpStatusCode.OK, (await participant.Client.GetAsync("/api/me")).StatusCode);

        var reset = await session.Client.PostAsync($"/api/admin/participants/{created.Id}/reset-code", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var newCode = (await reset.ReadJsonAsync()).GetProperty("code").GetString()!;
        Assert.NotEqual(created.Code, newCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.PostCodeAsync(created.Code)).Response.StatusCode); // the old code is dead
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.RefreshAsync(participant.RefreshCookie)).StatusCode); // and so is the old sign-in
        Assert.Equal(HttpStatusCode.OK, (await factory.PostCodeAsync(newCode)).Response.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.PostCodeAsync(newCode)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/revoke", null)).StatusCode); // already revoked
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/reset-code", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.PostCodeAsync(newCode)).Response.StatusCode);

        var actions = (await AdminHttp.AuditAsync(session.Admin.Id)).Select(r => (string)r[0]!).ToList();
        foreach (var expected in new[] { AuditActions.AdminParticipantCreated, AuditActions.AdminParticipantCodeReset, AuditActions.AdminParticipantRevoked, AuditActions.AdminParticipantReactivated })
        {
            Assert.Contains(expected, actions);
        }
    }

    [PostgresFact]
    public async Task AnErasedParticipant_CannotBeReactivated()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        var created = (await CreateAsync(session, new { label = "Zt" + Guid.NewGuid().ToString("N")[..8] })).Single();
        await host.AdoptAsync(created.Id);
        await using (var db = TestDatabase.NewContext())
        {
            (await db.Participants.SingleAsync(p => p.Id == created.Id)).Status = ParticipantStatus.Deleted;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync($"/api/admin/participants/{created.Id}/reset-code", null)).StatusCode);
    }

    [PostgresFact]
    public async Task TheReservedPrefixes_AndDuplicateLabels_AreRefused()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        Assert.Equal(HttpStatusCode.UnprocessableContent, (await session.Client.PostAsync("/api/admin/participants", AdminHttp.Json(new { labelPrefix = "itest-", count = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableContent, (await session.Client.PostAsync("/api/admin/participants", AdminHttp.Json(new { label = "seed-1" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableContent, (await session.Client.PostAsync("/api/admin/participants", AdminHttp.Json(new { labelPrefix = "K-", count = 101 }))).StatusCode);

        var label = "Zt" + Guid.NewGuid().ToString("N")[..8];
        var created = (await CreateAsync(session, new { label })).Single();
        await host.AdoptAsync(created.Id);
        await using (var db = TestDatabase.NewContext())
        {
            var row = await db.Participants.SingleAsync(p => p.Id == created.Id);
            row.Label = label; // give it back its admin label to provoke the duplicate
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync("/api/admin/participants", AdminHttp.Json(new { label }))).StatusCode);
        await using (var db = TestDatabase.NewContext())
        {
            (await db.Participants.SingleAsync(p => p.Id == created.Id)).Label = TestDataCleanup.NewLabel();
            await db.SaveChangesAsync();
        }
    }

    [PostgresFact]
    public async Task TheStatistics_AreRightAndCarryNoContent()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        var participant = await host.CreateParticipantAsync();
        var user = await factory.SignInAsync(participant);
        await user.OnboardAsync("PanelAd4711");
        var conversation = await user.StartConversationAsync();
        await user.SendAsync(conversation, $"{Marker} birinci mesaj");
        await user.SendAsync(conversation, $"{Marker} ikinci mesaj");
        await user.Client.PostAsync($"/api/conversations/{conversation}/end", null);

        var detail = await session.GetJsonAsync($"/api/admin/participants/{participant.Id}");
        var stats = detail.GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("conversationCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("completedConversations").GetInt32());
        Assert.Equal(2, stats.GetProperty("userMessages").GetInt32());
        Assert.Equal(2, stats.GetProperty("assistantMessages").GetInt32());
        Assert.True(stats.GetProperty("loginCount").GetInt32() >= 1);
        Assert.Equal("completed", detail.GetProperty("conversations")[0].GetProperty("state").GetString());

        var everything = new StringBuilder(detail.GetRawText());
        everything.Append(await (await session.Client.GetAsync("/api/admin/participants?pageSize=100")).Content.ReadAsStringAsync());
        everything.Append((await session.GetJsonAsync("/api/admin/stats/summary")).GetRawText());
        var csv = await session.Client.GetAsync("/api/admin/stats/export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        everything.Append(await csv.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Marker, everything.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PanelAd4711", everything.ToString(), StringComparison.Ordinal);
        Assert.Contains(await AdminHttp.AuditAsync(session.Admin.Id), r => (string)r[0]! == AuditActions.AdminStatsExported);
    }

    [PostgresFact]
    public async Task ACrisisFlag_AppearsGroupedByConversation_CanBeAcknowledgedAndReopened_WithoutAnyText()
    {
        using var factory = host.NewFactoryWithTheRealCrisisDetector();
        var session = await SignedInAsync(factory);
        var participant = await host.CreateParticipantAsync();
        var user = await factory.SignInAsync(participant);
        await user.OnboardAsync("KrizAd4711");
        var conversation = await user.StartConversationAsync();
        await user.SendAsync(conversation, $"{Marker} Artık ölmek istiyorum.");
        await user.SendAsync(conversation, $"{Marker} Yaşamak istemiyorum artık.");
        await user.SendAsync(conversation, $"{Marker} bugün hava güzel");

        var overview = await session.GetJsonAsync("/api/admin/crisis");
        var group = overview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("conversationId").GetGuid() == conversation);
        Assert.Equal(2, group.GetProperty("flaggedCount").GetInt32());
        Assert.Equal(2, group.GetProperty("openCount").GetInt32());
        Assert.True(overview.GetProperty("openConversations").GetInt32() >= 1);
        Assert.DoesNotContain(Marker, overview.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("ölmek", overview.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var ack = await session.Client.PostAsync($"/api/admin/crisis/{conversation}/acknowledge", null);
        Assert.Equal(2, (await ack.ReadJsonAsync()).GetProperty("changed").GetInt32());
        var after = (await session.GetJsonAsync("/api/admin/crisis")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("conversationId").GetGuid() == conversation);
        Assert.Equal(0, after.GetProperty("openCount").GetInt32());
        Assert.Equal(session.Admin.Username, after.GetProperty("acknowledgedBy").GetString());

        var reopened = await session.Client.PostAsync($"/api/admin/crisis/{conversation}/reopen", null);
        Assert.Equal(2, (await reopened.ReadJsonAsync()).GetProperty("changed").GetInt32());

        var actions = (await AdminHttp.AuditAsync(session.Admin.Id)).Select(r => (string)r[0]!).ToList();
        Assert.Equal(2, actions.Count(a => a == AuditActions.AdminCrisisAcknowledged)); // one entry per message
        Assert.Equal(2, actions.Count(a => a == AuditActions.AdminCrisisReopened));
        Assert.Contains(AuditActions.AdminCrisisViewed, actions);
        Assert.Equal(HttpStatusCode.NotFound, (await session.Client.PostAsync($"/api/admin/crisis/{Guid.NewGuid()}/acknowledge", null)).StatusCode);
    }

    [PostgresFact]
    public async Task EveryAdminAuditRow_IsResearcherOrSystem_NamedInTheList_AndHasNoContent()
    {
        using var factory = host.NewFactory();
        var session = await SignedInAsync(factory);
        var created = (await CreateAsync(session, new { label = "Zt" + Guid.NewGuid().ToString("N")[..8] })).Single();
        await host.AdoptAsync(created.Id);
        await session.Client.GetAsync($"/api/admin/participants/{created.Id}");

        var rows = await AdminHttp.AuditAsync(session.Admin.Id);
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Equal("Researcher", r[1]);
            Assert.Contains((string)r[0]!, AuditActions.All);
            Assert.DoesNotContain(created.Code, (string)r[4]!, StringComparison.Ordinal);
        });
    }
}
