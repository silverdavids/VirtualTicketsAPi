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

    [Fact]
    public void Placement_contract_names_internal_guid_and_canonical_ticket_number_explicitly()
    {
        var internalSerial = Guid.NewGuid();
        var response = new TicketPlaceResponse
        {
            ReceiptId = 277,
            InternalSerial = internalSerial,
            TicketNumber = "642094279825"
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains($"\"internalSerial\":\"{internalSerial}\"", json);
        Assert.Contains("\"ticketNumber\":\"642094279825\"", json);
        Assert.DoesNotContain("\"serial\":", json);
    }
}
