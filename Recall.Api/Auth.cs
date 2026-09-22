using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Recall.Api.Auth;

public class JwtTokenService(string secret, string issuer)
{
    public TimeSpan AccessTokenLifetime { get; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; } = TimeSpan.FromDays(7);

    public string CreateAccessToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("role", user.Role),
            new("email", user.Email),
        };
        var key = new SymmetricSecurityKey(Convert.FromBase64String(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: issuer,
            claims: claims,
            expires: DateTime.UtcNow.Add(AccessTokenLifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    public static string HashRefreshToken(string token)
    {
        var bytes = SHA256.HashData(Convert.FromBase64String(token));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Safe decode: untrusted input may not be valid Base64 or 64 bytes.</summary>
    public static bool TryHashRefreshToken(string? token, out string hash)
    {
        hash = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length != 64) return false;
            hash = Convert.ToBase64String(SHA256.HashData(bytes));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; } // one per login; rotation keeps the same family
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}