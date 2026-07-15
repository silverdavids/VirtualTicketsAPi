namespace VirtualTickets.Api.Contracts;

public sealed class DisplayAuthResponse
{
    public required string AccessToken { get; set; }

    public required DateTime ExpiresAt { get; set; }

    public required AuthenticatedTerminalResponse Terminal { get; set; }
}

public sealed class AuthenticatedTerminalResponse
{
    public required int TerminalId { get; set; }

    public required string TerminalCode { get; set; }

    public required string TerminalName { get; set; }

    public required int BranchId { get; set; }

    public required byte TerminalType { get; set; }
}
