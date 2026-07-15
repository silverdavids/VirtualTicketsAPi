namespace VirtualTickets.Api.Auth;

public sealed class VirtualTicketsJwtOptions
{
    public const string SectionName = "VirtualTicketsJwt";

    public string Issuer { get; set; } = "VirtualTickets.Api";

    public string Audience { get; set; } = "VirtualDisplay";

    public string? SigningKey { get; set; }

    public int Minutes { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey);

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(Math.Clamp(Minutes, 30, 60));
}
