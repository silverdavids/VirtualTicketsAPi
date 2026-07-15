using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Auth;

public sealed class JwtTokenService
{
    private readonly VirtualTicketsJwtOptions _options;

    public JwtTokenService(IOptions<VirtualTicketsJwtOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured => _options.IsConfigured;

    public IssuedJwt Issue(TerminalIdentity terminal)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Virtual tickets JWT signing key is not configured.");
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(_options.TokenLifetime);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey!));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, terminal.TerminalId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("terminal_id", terminal.TerminalId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("terminal_code", terminal.TerminalCode),
            new("branch_id", terminal.BranchId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("terminal_type", terminal.TerminalType.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("name", terminal.TerminalName),
            new("auth_type", "display_terminal")
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return new IssuedJwt(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

public sealed record IssuedJwt(string AccessToken, DateTime ExpiresAt);
