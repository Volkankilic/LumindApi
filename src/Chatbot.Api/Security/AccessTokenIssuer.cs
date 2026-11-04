using System.Security.Claims;
using System.Text;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Chatbot.Api.Security;

public static class Roles
{
    public const string Participant = "Participant";
}

/// <summary>
/// Short lived access token (HS256). Claims: `sub` (participant id) and the role. The participant code and the nickname
/// are never put into a token.
/// </summary>
public sealed class AccessTokenIssuer(AuthOptions options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid participantId)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(options.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, participantId.ToString()),
                new Claim(ClaimTypes.Role, Roles.Participant),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSigningKey)),
                SecurityAlgorithms.HmacSha256),
        };
        return (_handler.CreateToken(descriptor), expiresAt);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The participant id from the `sub` claim (inbound claim mapping is off, so the name is the JWT one).</summary>
    public static Guid ParticipantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;
}
