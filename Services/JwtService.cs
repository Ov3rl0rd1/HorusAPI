using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using HorusAPI.Models;

namespace HorusAPI.Services;

public interface IJwtService
{
    (string Token, DateTime ExpiresAt) Generate(User user);
}

public class JwtService(IConfiguration cfg) : IJwtService
{
    private readonly JwtSettings _s = cfg.GetSection("Jwt").Get<JwtSettings>()
        ?? throw new InvalidOperationException("Jwt settings missing");

    public (string Token, DateTime ExpiresAt) Generate(User user)
    {
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Secret));
        var creds   = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_s.ExpiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   user.id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.username),
            new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier,     user.id.ToString()),
        };

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
    public int    ExpiryMinutes { get; set; } = 60;
}
