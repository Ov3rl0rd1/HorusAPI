using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using HorusAPI.Models;

namespace HorusAPI.Services;

public interface IJwtService
{
    (string Token, DateTime ExpiresAt) Generate(User user, string session);
}

public class JwtService(IConfiguration cfg) : IJwtService
{
    private readonly JwtSettings _s = cfg.GetSection("Jwt").Get<JwtSettings>()
        ?? throw new InvalidOperationException("Jwt settings missing");

    public (string Token, DateTime ExpiresAt) Generate(User user, string session)
    {
        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // expires_at nullable: null means subscription never expires
        bool subscriptionExpired = user.expires_at.HasValue && user.expires_at.Value <= DateTime.UtcNow;

        int tokenLifetimeMinutes;
        if (!user.expires_at.HasValue || subscriptionExpired)
            tokenLifetimeMinutes = _s.ExpiryMinutes;
        else
            tokenLifetimeMinutes = Math.Max(1, (int)(user.expires_at.Value - DateTime.UtcNow).TotalMinutes);

        var expires = DateTime.UtcNow.AddMinutes(tokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,        user.id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.username),
            new(ApiConsts.SESSION_ID,               session),
            new(JwtRegisteredClaimNames.Jti,        Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier,          user.id.ToString()),
        };

        if (user.is_admin)
            claims.Add(new Claim(ClaimTypes.Role, "admin"));

        if (user.expires_at.HasValue && !subscriptionExpired)
            claims.Add(new Claim(ApiConsts.SUBSCRIPTION_EXPIRES_AT, user.expires_at.Value.ToString("o")));

        var token = new JwtSecurityToken(
            issuer:             _s.Issuer,
            audience:           _s.Audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow,
            expires:            expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public class JwtSettings
{
    public string Secret        { get; set; } = string.Empty;
    public string Issuer        { get; set; } = string.Empty;
    public string Audience      { get; set; } = string.Empty;
    public int    ExpiryMinutes { get; set; } = 24 * 60;
}