using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class TicketPlacementIdempotencyContractTests
{
    [Fact]
    public void Recovered_placement_preserves_original_public_result()
    {
        var bookedAtUtc = new DateTime(2026, 8, 9, 18, 48, 15, DateTimeKind.Utc);
        var serial = Guid.NewGuid();
        var bets = new List<PlacedBetResponse>
        {
            new() { BetId = 626, MatchId = 2138191216, Odd = 1.52m }
        };

        var result = TicketPlaceResult.Placed(
            281,
            serial,
            "121520564205",
            "test-DISPLAY-001",
            bookedAtUtc,
            bets,
            20260803);

        Assert.True(result.IsPlaced);
        Assert.Equal(281, result.ReceiptId);
        Assert.Equal(serial, result.Serial);
        Assert.Equal("121520564205", result.TicketNumber);
        Assert.Equal("test-DISPLAY-001", result.ShopDisplayName);
        Assert.Equal(bookedAtUtc, result.BookedAtUtc);
        Assert.Equal(20260803, result.ActiveSetNo);
        Assert.Same(bets, result.Bets);
    }

    [Fact]
    public void Failed_placement_contains_no_success_identity()
    {
        var result = TicketPlaceResult.Failed([]);

        Assert.False(result.IsPlaced);
        Assert.Null(result.ReceiptId);
        Assert.Null(result.TicketNumber);
        Assert.Null(result.ActiveSetNo);
        Assert.Empty(result.Bets);
    }
}
