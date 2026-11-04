using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Chatbot.Api.Http;
using Chatbot.Api.Security;
using Chatbot.Application;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chatbot.Api.Startup;

public static class ApiServices
{
    public const string AuthCodeLimiter = "auth-code";
    public const string AuthRefreshLimiter = "auth-refresh";
    public const string SendMessageLimiter = "send-message";
    public const string CorsPolicy = "client";

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();

        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)))
            .ConfigureApiBehaviorOptions(o =>
            {
                // Wrong shape, bad JSON, unknown enum value: one answer, and nothing the client sent is echoed back.
                o.InvalidModelStateResponseFactory = context =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    return new ProblemResult(StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationFailed);
                };
            });

        services.AddOpenApi();

        // --- authentication: access JWT in the Authorization header
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AuthOptions>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false; // keep the JWT names (`sub`)
                jwt.RequireHttpsMetadata = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth.Issuer,
                    ValidateAudience = true,
                    ValidAudience = auth.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                };
            });

        // --- authorization: both policies are checked against the database on every request
        services.AddScoped<IAuthorizationHandler, ParticipantAccessHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p
                .RequireAuthenticatedUser()
                .RequireRole(Roles.Participant)
                .AddRequirements(new ParticipantAccessRequirement(RequireOnboarded: false)))
            .AddPolicy(Policies.OnboardedParticipant, p => p
                .RequireAuthenticatedUser()
                .RequireRole(Roles.Participant)
                .AddRequirements(new ParticipantAccessRequirement(RequireOnboarded: true)));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, ct) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
                    ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
                    : 30;
                await Problems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, ProblemCodes.RateLimited, retry);
            };

            // Code entry: very tight, per client IP (a code is the only credential).
            limiter.AddPolicy(AuthCodeLimiter, http =>
            {
                var auth = http.RequestServices.GetRequiredService<AuthOptions>();
                return RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = auth.CodeAttempts,
                    Window = TimeSpan.FromSeconds(auth.CodeWindowSeconds),
                    QueueLimit = 0,
                });
            });

            limiter.AddPolicy(AuthRefreshLimiter, http =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            // Sending messages: per participant.
            limiter.AddPolicy(SendMessageLimiter, http =>
            {
                var chat = http.RequestServices.GetRequiredService<ChatOptions>();
                var key = http.User.Identity?.IsAuthenticated == true ? "p:" + http.User.ParticipantId() : ClientKey(http);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = chat.MessagesPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        // CORS: only the origins named in configuration (Cors:AllowedOrigins), with credentials for the refresh cookie.
        // No wildcard (startup validation rejects one). With no origin configured, no cross-origin call is allowed.
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IConfiguration>((cors, configuration) =>
            {
                var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                cors.AddPolicy(CorsPolicy, policy =>
                {
                    if (origins.Length > 0)
                    {
                        policy.WithOrigins(origins).AllowCredentials().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Retry-After");
                    }
                });
            });

        services.AddHostedService<ConfigurationValidationService>();
        services.AddHostedService<SchemaCheckService>();
        return services;
    }

    /// <summary>The client key of the rate limiters. Behind a proxy it is the real IP only when forwarded headers are configured.</summary>
    public static string ClientKey(HttpContext http) =>
        "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>
    /// Trusts forwarded headers ONLY from proxies named in configuration (ForwardedHeaders:KnownProxies / KnownNetworks).
    /// Without any, nothing is trusted: a client could otherwise fake its IP (and dodge the rate limits).
    /// </summary>
    public static ForwardedHeadersOptions? BuildForwardedHeaders(IConfiguration configuration)
    {
        var proxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        var networks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        if (proxies.Length == 0 && networks.Length == 0)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }

        foreach (var network in networks)
        {
            if (System.Net.IPNetwork.TryParse(network, out var parsed))
            {
                options.KnownIPNetworks.Add(parsed);
            }
        }

        return options;
    }
}

/// <summary>A ProblemDetails result for the model-state factory (no echo of the request).</summary>
public sealed class ProblemResult(int status, string code) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context) => Problems.WriteAsync(context.HttpContext, status, code);
}
