using System.Security.Cryptography;
using System.Text;
using Chatbot.Application.Abstractions;
using Chatbot.Application.Options;

namespace Chatbot.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with a random 12 byte nonce per value. Stored as <c>enc:v1:&lt;keyId&gt;:&lt;base64(nonce | tag | ciphertext)&gt;</c>.
/// The additional authenticated data is <c>table|column|rowId</c>. Keys come only from configuration (user-secrets /
/// environment) and never appear in a message or a log.
/// </summary>
public sealed class AesGcmFieldProtector : IFieldProtector
{
    public const string Prefix = "enc:v1:";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _currentId;

    public AesGcmFieldProtector(PrivacyOptions privacy)
    {
        var problems = OptionsValidator.ValidateEncryption(privacy);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", problems));
        }

        foreach (var (id, value) in privacy.DataEncryptionKeys)
        {
            OptionsValidator.TryDecodeKey(value, out var key);
            _keys[id] = key;
        }

        _currentId = privacy.CurrentDataEncryptionKeyId;
    }

    public static bool LooksEncrypted(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string table, string column, Guid rowId, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceBytes + TagBytes + plain.Length];
        var nonce = blob.AsSpan(0, NonceBytes);
        var tag = blob.AsSpan(NonceBytes, TagBytes);
        var cipher = blob.AsSpan(NonceBytes + TagBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_keys[_currentId], TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(table, column, rowId));
        CryptographicOperations.ZeroMemory(plain);
        return $"{Prefix}{_currentId}:{Convert.ToBase64String(blob)}";
    }

    public string Unprotect(string table, string column, Guid rowId, string stored)
    {
        if (!LooksEncrypted(stored))
        {
            throw new PlaintextFieldException(table, column, rowId);
        }

        var rest = stored.AsSpan(Prefix.Length);
        var colon = rest.IndexOf(':');
        if (colon <= 0 || !_keys.TryGetValue(rest[..colon].ToString(), out var key))
        {
            throw new FieldProtectionException($"Field {table}.{column} of row {rowId} uses an unknown key id.");
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(rest[(colon + 1)..].ToString());
        }
        catch (FormatException)
        {
            throw new FieldProtectionException($"Field {table}.{column} of row {rowId} is malformed.");
        }

        if (blob.Length < NonceBytes + TagBytes)
        {
            throw new FieldProtectionException($"Field {table}.{column} of row {rowId} is malformed.");
        }

        var plain = new byte[blob.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(blob.AsSpan(0, NonceBytes), blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), plain, Aad(table, column, rowId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Wrong key, tampered value, or a value moved to another row/column: the same answer, no detail.
            throw new FieldProtectionException($"Field {table}.{column} of row {rowId} failed authentication.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Aad(string table, string column, Guid rowId) =>
        Encoding.UTF8.GetBytes($"{table}|{column}|{rowId:D}");
}
