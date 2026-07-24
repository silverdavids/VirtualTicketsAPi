using VirtualTickets.Api.Services;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class TicketNumberTests
{
    [Fact]
    public void Generate_returns_printable_valid_ticket_number()
    {
        var value = TicketNumber.Generate();

        Assert.True(TicketNumber.IsValid(value));
        Assert.StartsWith("VT-", value);
        Assert.Equal(19, value.Length);
    }

    [Fact]
    public void Normalize_removes_spaces_and_uppercases()
    {
        var value = TicketNumber.Normalize(" vt-2345 6789 abcd efgh ");

        Assert.Equal("VT-23456789ABCDEFGH", value);
        Assert.True(TicketNumber.IsValid(value));
    }

    [Fact]
    public void Generate_does_not_repeat_in_large_sample()
    {
        var values = Enumerable.Range(0, 10_000)
            .Select(_ => TicketNumber.Generate())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(10_000, values.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("VT-OOOOOOOOOOOOOOOO")]
    [InlineData("VT-23456789ABCDEFGI")]
    public void IsValid_rejects_malformed_values(string? value)
    {
        Assert.False(TicketNumber.IsValid(value));
    }
}
