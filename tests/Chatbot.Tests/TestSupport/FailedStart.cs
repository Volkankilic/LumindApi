namespace Chatbot.Tests.TestSupport;

/// <summary>
/// "The application must not start with this configuration" tests. The text of the failure is what they assert on.
///
/// Why a helper: when the app throws while it starts (that is the point of these tests), ASP.NET Core's own test harness
/// (<c>DeferredHostBuilder.DeferredHost.StartAsync</c>) sometimes reads from the service provider of the host that has just been
/// disposed, and throws an <see cref="ObjectDisposedException"/> INSTEAD of the real startup error. It is a race inside the
/// test harness (reproduced: 2-5 of 144 parallel failing starts, stack ends in <c>DeferredHost.StartAsync</c>); it cannot happen in
/// production, where there is no deferred host. A start that fails for that reason says nothing about our configuration check,
/// so it is repeated with a fresh factory. A host that STARTS is always a test failure, and the real error is returned as is.
/// </summary>
public static class FailedStart
{
    public static async Task<string> TextAsync(Func<ApiFactory> create, int attempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var factory = create();
            string? failure = null;
            var started = false;
            try
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/api/health");
                started = true;
            }
            catch (Exception ex)
            {
                if (IsHarnessRace(ex) && attempt < attempts)
                {
                    continue;
                }

                failure = ex.ToString();
            }

            if (started)
            {
                throw new Xunit.Sdk.XunitException("The host started although its configuration is invalid.");
            }

            return failure!;
        }
    }

    private static bool IsHarnessRace(Exception ex) =>
        ex is ObjectDisposedException && (ex.StackTrace ?? string.Empty).Contains("DeferredHostBuilder", StringComparison.Ordinal);
}
