using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GiftCardPlatform.Modules.Identity.Application;

internal interface ITokenGenerator
{
    (string Token, DateTimeOffset ExpiresAtUtc) CreateAccessToken(
        User user,
        UserSession session,
        DateTimeOffset now);

    (string Plaintext, string Hash) CreateRefreshToken();

    string HashRefreshToken(string plaintext);
}

internal sealed class TokenGenerator(
    IOptions<IdentityTokenOptions> options) : ITokenGenerator
{
    private readonly IdentityTokenOptions options = options.Value;

    public (string Token, DateTimeOffset ExpiresAtUtc) CreateAccessToken(
        User user,
        UserSession session,
        DateTimeOffset now)
    {
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Sid, session.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };

        var descriptor = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(descriptor), expires);
    }

    public (string Plaintext, string Hash) CreateRefreshToken()
    {
        var plaintext = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        return (plaintext, HashRefreshToken(plaintext));
    }

    public string HashRefreshToken(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
