using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Cashlane.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Cashlane.Api.Infrastructure.Authentication;

public sealed record PendingRegistration(string Email, string DisplayName, string PasswordHash);

public interface IRegistrationVerificationTokenService
{
    string CreateToken(PendingRegistration pendingRegistration);
    PendingRegistration? ReadToken(string token);
}

internal sealed record ProtectedPendingRegistration(string Email, string DisplayName, string PasswordHash, DateTime ExpiresAtUtc);

public sealed class RegistrationVerificationTokenService(IOptions<JwtOptions> options)
    : IRegistrationVerificationTokenService
{
    private readonly byte[] _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public string CreateToken(PendingRegistration pendingRegistration)
    {
        var payload = new ProtectedPendingRegistration(
            pendingRegistration.Email,
            pendingRegistration.DisplayName,
            pendingRegistration.PasswordHash,
            DateTime.UtcNow.AddHours(1));
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherBytes = new byte[payloadBytes.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(_encryptionKey, tag.Length);
        aesGcm.Encrypt(nonce, payloadBytes, cipherBytes, tag);

        var tokenBytes = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, tokenBytes, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, tokenBytes, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, tokenBytes, nonce.Length + tag.Length, cipherBytes.Length);

        return WebEncoders.Base64UrlEncode(tokenBytes);
    }

    public PendingRegistration? ReadToken(string token)
    {
        try
        {
            var tokenBytes = WebEncoders.Base64UrlDecode(token);
            if (tokenBytes.Length <= 28)
            {
                return null;
            }

            var nonce = tokenBytes[..12];
            var tag = tokenBytes[12..28];
            var cipherBytes = tokenBytes[28..];
            var payloadBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(_encryptionKey, tag.Length);
            aesGcm.Decrypt(nonce, cipherBytes, tag, payloadBytes);

            var payload = JsonSerializer.Deserialize<ProtectedPendingRegistration>(payloadBytes);

            if (payload is null ||
                payload.ExpiresAtUtc < DateTime.UtcNow ||
                string.IsNullOrWhiteSpace(payload.Email) ||
                string.IsNullOrWhiteSpace(payload.DisplayName) ||
                string.IsNullOrWhiteSpace(payload.PasswordHash))
            {
                return null;
            }

            return new PendingRegistration(payload.Email, payload.DisplayName, payload.PasswordHash);
        }
        catch
        {
            return null;
        }
    }
}
