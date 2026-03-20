using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cashlane.Api.Configuration;
using Cashlane.Api.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cashlane.Api.Infrastructure.Authentication;

public interface ITokenService
{
    string CreateAccessToken(User user);
    (string rawToken, string tokenHash, DateTime expiresAtUtc) CreateRefreshToken();
    string CreateRandomToken();
    string HashOpaqueToken(string token);
}

public sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _jwtOptions = options.Value;

    public string CreateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("display_name", user.DisplayName)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string rawToken, string tokenHash, DateTime expiresAtUtc) CreateRefreshToken()
    {
        var raw = CreateRandomToken();
        return (raw, HashOpaqueToken(raw), DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenDays));
    }

    public string CreateRandomToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    }

    public string HashOpaqueToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
