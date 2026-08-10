using System.Text.Json;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Services;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class VirtualBoardPlacementProtectionTests
{
    [Fact]
    public void Odds_policy_requires_exact_authoritative_odd()
    {
        Assert.True(VirtualBoardSelectionPolicy.OddsMatch(1.85m, 1.850000m));
        Assert.False(VirtualBoardSelectionPolicy.OddsMatch(1.86m, 1.85m));
        Assert.False(VirtualBoardSelectionPolicy.OddsMatch(1.850001m, 1.85m));
    }

    [Theory]
    [InlineData("board_changed")]
    [InlineData("board_expired")]
    [InlineData("selection_not_available")]
    [InlineData("odds_changed")]
    public void Stale_state_errors_are_conflicts(string code)
    {
        Assert.True(TicketApplicationService.IsConflictError(code));
    }

    [Fact]
    public void Odds_changed_exposes_only_affected_selection_and_current_odd()
    {
        var error = new TicketValidationError
        {
            Code = "odds_changed",
            Field = "selections[2].odd",
            SelectionIndex = 2,
            CurrentOdd = 1.72m,
            Message = "Refresh."
        };

        var json = JsonSerializer.Serialize(error, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"selectionIndex\":2", json);
        Assert.Contains("\"currentOdd\":1.72", json);
    }

    [Fact]
    public void Placement_source_enforces_required_transaction_order_and_scope()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        var idempotency = source.IndexOf("FindExistingTerminalPlacementAsync(\n                    connection", StringComparison.Ordinal);
        var boardValidation = source.IndexOf("ValidateVirtualBoardAsync(\n                    connection", StringComparison.Ordinal);
        var ownership = source.IndexOf("ResolveTerminalTicketOwnershipAsync(\n                    connection", StringComparison.Ordinal);
        var receipt = source.IndexOf("var terminalReceipt = await InsertReceiptAsync", StringComparison.Ordinal);

        Assert.True(idempotency >= 0 && idempotency < boardValidation);
        Assert.True(boardValidation < ownership && ownership < receipt);
        Assert.Contains("IsolationLevel.Serializable", source);
        Assert.Contains("VirtualCurrentBoard currentBoard WITH (UPDLOCK, HOLDLOCK)", source);
        Assert.Contains("dbo.VirtualBoardSelections WITH (HOLDLOCK)", source);
        Assert.Contains("await transaction.CommitAsync", source);
        Assert.DoesNotContain("MatchOdds.LastUpdateTime", source);
    }

    [Fact]
    public void Snapshot_query_cannot_be_bypassed_by_crafted_match_identity()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("VirtualBoardId = @virtualBoardId", source);
        Assert.Contains("ProviderEventId = @providerEventId", source);
        Assert.Contains("ProviderMatchId = @providerMatchId", source);
        Assert.Contains("Market = @market", source);
        Assert.Contains("[Option] = @option", source);
        Assert.Contains("TRY_CONVERT(decimal(18, 6), Line) = @lineValue", source);
        Assert.Contains("IsActive = 1", source);
        Assert.Contains("BetServiceMatchNo = @matchId", source);
        Assert.Contains("MatchOddId = @matchOddId", source);
    }

    [Fact]
    public void Every_stale_state_failure_is_structured_before_receipt_insertion()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        var receipt = source.IndexOf("var terminalReceipt = await InsertReceiptAsync", StringComparison.Ordinal);
        foreach (var code in new[] { "board_changed", "board_expired", "selection_not_available", "odds_changed" })
        {
            var error = source.IndexOf($"Code = \"{code}\"", StringComparison.Ordinal);
            Assert.True(error >= 0, $"Missing {code}");
        }

        Assert.True(source.IndexOf("ValidateVirtualBoardAsync", StringComparison.Ordinal) < receipt);
    }

    [Fact]
    public void Non_terminal_legacy_odds_validation_remains_enabled()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "TicketApplicationService.cs"));
        Assert.Contains("if (terminalIdentity is null)", source);
        Assert.Contains("await _oddsValidator.ValidateAsync", source);
    }

    private static string ProjectRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
