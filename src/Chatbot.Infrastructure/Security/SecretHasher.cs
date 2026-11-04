using System.Security.Cryptography;
using System.Text;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;

namespace Chatbot.Infrastructure.Security;

public sealed class SecretHasher(AuthOptions auth, PrivacyOptions privacy) : ISecretHasher
{
    private readonly byte[] _codePepper = Encoding.UTF8.GetBytes(auth.CodePepper);
    private readonly byte[] _ipKey = Encoding.UTF8.GetBytes(privacy.IpHashKey);

    public string HashCode(string normalizedCode) =>
        Hex(HMACSHA256.HashData(_codePepper, Encoding.UTF8.GetBytes(normalizedCode)));

    public string HashRefreshToken(string token) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string? HashIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : Hex(HMACSHA256.HashData(_ipKey, Encoding.UTF8.GetBytes(ip)));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
