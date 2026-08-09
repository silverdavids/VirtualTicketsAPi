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
        Assert.Equal(12, value.Length);
        Assert.All(value, character => Assert.InRange(character, '0', '9'));
    }

    [Fact]
    public void Normalize_removes_spaces_from_numeric_ticket()
    {
        var value = TicketNumber.Normalize(" 2126 9700 7925 ");

        Assert.Equal("212697007925", value);
        Assert.True(TicketNumber.IsValid(value));
    }

    [Fact]
    public void Existing_legacy_ticket_numbers_remain_valid()
    {
        Assert.True(TicketNumber.IsValid("VT-23456789ABCDEFGH"));
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
    [InlineData("21269700792A")]
    [InlineData("VT-OOOOOOOOOOOOOOOO")]
    [InlineData("VT-23456789ABCDEFGI")]
    public void IsValid_rejects_malformed_values(string? value)
    {
        Assert.False(TicketNumber.IsValid(value));
    }
}
