using System.Text.Json;
using VirtualTickets.Api.Contracts;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class TicketPlaceResponseTests
{
    [Fact]
    public void Booked_at_utc_serializes_as_iso_8601_utc_with_z_suffix()
    {
        var response = new TicketPlaceResponse
        {
            IsPlaced = true,
            ReceiptId = 277,
            TicketNumber = "642094279825",
            BookedAtUtc = new DateTime(2026, 8, 9, 15, 35, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"bookedAtUtc\":\"2026-08-09T15:35:00Z\"", json);
    }
}
