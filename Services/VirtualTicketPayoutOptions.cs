namespace VirtualTickets.Api.Services;

public sealed class VirtualTicketPayoutOptions
{
    public const string SectionName = "VirtualTicketPayout";

    public string Currency { get; set; } = "UGX";
    public Guid? PayoutUserId { get; set; }

    public static Guid? ParsePayoutUserId(string? value) =>
        Guid.TryParse(value, out var userId) && userId != Guid.Empty
            ? userId
            : null;
}
