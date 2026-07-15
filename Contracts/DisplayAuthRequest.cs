using System.ComponentModel.DataAnnotations;

namespace VirtualTickets.Api.Contracts;

public sealed class DisplayAuthRequest
{
    [Required]
    public string? TerminalCode { get; set; }

    [Required]
    public string? Secret { get; set; }

    public string? Version { get; set; }
}
