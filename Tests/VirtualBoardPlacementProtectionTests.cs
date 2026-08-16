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
        Assert.Contains("dbo.VirtualBoards b WITH (UPDLOCK, HOLDLOCK)", source);
        Assert.Contains("b.ProviderEventId = @providerEventId", source);
        Assert.Contains("b.Status <> 0 OR b.HasResults = 1", source);
        Assert.Contains("b.EndAtUtc IS NOT NULL AND b.EndAtUtc <= SYSUTCDATETIME()", source);
        Assert.DoesNotContain("FROM dbo.VirtualCurrentBoard", source);
        Assert.Contains("dbo.VirtualBoardSelections snapshot WITH (HOLDLOCK)", source);
        Assert.Contains("await transaction.CommitAsync", source);
        Assert.DoesNotContain("MatchOdds.LastUpdateTime", source);
    }

    [Fact]
    public void Boards_without_an_end_time_are_not_automatically_expired()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.DoesNotContain("b.EndAtUtc IS NULL OR b.EndAtUtc <= SYSUTCDATETIME()", source);
    }

    [Fact]
    public void Snapshot_query_cannot_be_bypassed_by_crafted_match_identity()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("boardMatch.VirtualBoardId = @virtualBoardId", source);
        Assert.Contains("snapshot.ProviderEventId = @providerEventId", source);
        Assert.Contains("boardMatch.ProviderMatchId = @providerMatchId", source);
        Assert.Contains("snapshot.IsActive = 1", source);
        Assert.Contains("snapshot.MatchOddId = @matchOddId", source);
        Assert.Contains("VirtualBoardSelectionPolicy.IsSameSelection", source);
        Assert.DoesNotContain("snapshot.Market = @market", source);
        Assert.DoesNotContain("snapshot.[Option] = @option", source);
        Assert.DoesNotContain("TRY_CONVERT(decimal(18, 6), snapshot.Line) = @lineValue", source);
    }

    [Fact]
    public void Board_a_selection_does_not_follow_board_b_current_pointer()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("FROM dbo.VirtualBoardMatchesMap boardMatch WITH (HOLDLOCK)", source);
        Assert.Contains("boardMatch.VirtualBoardId = @virtualBoardId", source);
        Assert.Contains("snapshot.ProviderEventId = @providerEventId", source);
        Assert.DoesNotContain("FROM dbo.VirtualCurrentBoard", source);
    }

    [Fact]
    public void Selection_on_submitted_board_uses_board_mapping_not_client_match_id()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        var selectionLookupStart = source.IndexOf("const string selectionSql", StringComparison.Ordinal);
        var selectionLookupEnd = source.IndexOf("var matched = candidates", selectionLookupStart, StringComparison.Ordinal);
        var selectionLookup = source[selectionLookupStart..selectionLookupEnd];
        Assert.Contains("snapshot.BetServiceMatchNo = boardMatch.BetServiceMatchNo", selectionLookup);
        Assert.DoesNotContain("BetServiceMatchNo = @matchId", selectionLookup);
        Assert.DoesNotContain("new SqlParameter(\"@matchId\"", selectionLookup);
    }

    [Fact]
    public void Selection_genuinely_absent_from_submitted_board_has_precise_error()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("Code = \"selection_not_available\"", source);
        Assert.Contains("The requested selection is not available on the submitted VirtualHorizon board.", source);
    }

    [Fact]
    public void Selection_odd_changed_uses_unlocked_authoritative_match_odd()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("LEFT JOIN dbo.MatchOdds currentOdd WITH (UPDLOCK, HOLDLOCK)", source);
        Assert.Contains("currentOdd.MatchOddId = snapshot.MatchOddId", source);
        Assert.Contains("currentOdd.MatchOddId IS NULL OR ISNULL(currentOdd.IsLocked, 0) = 0", source);
        Assert.Contains("COALESCE(currentOdd.Odd, snapshot.Odd) AS Odd", source);
        Assert.Contains("Code = \"odds_changed\"", source);
    }

    [Fact]
    public void Existing_1x2_selection_matches_board_snapshot()
    {
        var submitted = Selection("1X2", "1", null);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "1X2", "1", ""));
    }

    [Fact]
    public void Ou_over_1_5_with_null_match_odd_id_matches_board_snapshot_alias()
    {
        var submitted = Selection("OU", "OV1.5", 1.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "OVER_1.5", ""));
    }

    [Fact]
    public void Ou_under_selection_matches_board_snapshot_alias()
    {
        var submitted = Selection("OU", "UN1.5", 1.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "UNDER_1.5", ""));
    }

    [Theory]
    [InlineData("OV0.5", "OVER_0.5_HOME")]
    [InlineData("UN0.5", "UNDER_0.5_HOME")]
    public void Home_team_total_matches_scoped_team_goals_snapshot(string submittedOption, string snapshotOption)
    {
        var submitted = Selection("HOME_OU", submittedOption, 0.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", snapshotOption, ""));
    }

    [Fact]
    public void Away_team_total_matches_scoped_team_goals_snapshot()
    {
        var submitted = Selection("AWAY_OU", "OV0.5", 0.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", "OVER_0.5_AWAY", ""));
    }

    [Fact]
    public void Home_team_total_does_not_match_a_different_line()
    {
        var submitted = Selection("HOME_OU", "OV1.5", 1.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", "OVER_0.5_HOME", ""));
    }

    [Theory]
    [InlineData("OU", "OVER_0.5")]
    [InlineData("TEAM_GOALS_HOME_AWAY", "OVER_0.5_AWAY")]
    public void Home_team_total_does_not_cross_market_scope(string snapshotMarket, string snapshotOption)
    {
        var submitted = Selection("HOME_OU", "OVER 0.5", 0.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, snapshotMarket, snapshotOption, ""));
    }

    [Fact]
    public void Invalid_client_created_team_total_option_is_rejected()
    {
        var submitted = Selection("HOME_OU", "YES0.5", 0.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", "OVER_0.5_HOME", ""));
    }

    [Fact]
    public void Team_total_snapshot_requires_an_exact_known_scope_encoding()
    {
        var submitted = Selection("HOME_OU", "OV0.5", 0.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", "OVER_0.5", ""));
        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "TEAM_GOALS_HOME_AWAY", "OVER_0.5_PLAYER", ""));
    }

    [Fact]
    public void Combined_result_and_total_market_preserves_all_components()
    {
        var submitted = Selection("1X2_OU_1.5", "1+OV1.5", 1.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", "OVER_1.5_HOME", ""));
        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", "OVER_1.5_AWAY", ""));
    }

    [Theory]
    [InlineData("X+OV1.5", "OVER_1.5_DRAW")]
    [InlineData("2+OV1.5", "OVER_1.5_AWAY")]
    [InlineData("1+UN1.5", "UNDER_1.5_HOME")]
    [InlineData("X+UN2.5", "UNDER_2.5_DRAW")]
    public void Combined_market_normalizes_verified_variants(string submittedOption, string snapshotOption)
    {
        var line = submittedOption.Contains("2.5", StringComparison.Ordinal) ? 2.5m : 1.5m;
        var submitted = Selection($"1X2_OU_{line}", submittedOption, line);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", snapshotOption, ""));
    }

    [Theory]
    [InlineData("OVER_1.5_DRAW")]
    [InlineData("UNDER_1.5_HOME")]
    [InlineData("OVER_2.5_HOME")]
    public void Combined_market_rejects_component_mismatches(string snapshotOption)
    {
        var submitted = Selection("1X2_OU_1.5", "1+OV1.5", 1.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", snapshotOption, ""));
    }

    [Theory]
    [InlineData("1X2_OU_1.5", "1+OV2.5", 1.5)]
    [InlineData("1X2_OU_1.5", "1+OV1.5", 2.5)]
    public void Combined_market_rejects_contradictory_submitted_lines(string market, string option, double line)
    {
        var submitted = Selection(market, option, (decimal)line);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", "OVER_1.5_HOME", ""));
    }

    [Fact]
    public void Combined_market_rejects_contradictory_snapshot_line()
    {
        var submitted = Selection("1X2_OU_1.5", "1+OV1.5", 1.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            submitted, "OVER_UNDER_1X2", "OVER_2.5_HOME", "1.5"));
    }

    [Fact]
    public void Plain_markets_cannot_match_combined_market()
    {
        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            Selection("1X2", "1", null), "OVER_UNDER_1X2", "OVER_1.5_HOME", ""));
        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(
            Selection("OU", "OV1.5", 1.5m), "OVER_UNDER_1X2", "OVER_1.5_HOME", ""));
    }

    [Fact]
    public void Line_based_market_distinguishes_two_lines_for_same_match()
    {
        var submitted = Selection("OU", "OV1.5", 1.5m);

        Assert.True(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "OVER_1.5", ""));
        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "OVER_2.5", ""));
    }

    [Fact]
    public void Incorrect_line_fails()
    {
        var submitted = Selection("OU", "OV1.5", 2.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "OVER_1.5", ""));
    }

    [Fact]
    public void Incorrect_option_fails()
    {
        var submitted = Selection("OU", "OV1.5", 1.5m);

        Assert.False(VirtualBoardSelectionPolicy.IsSameSelection(submitted, "OU", "UNDER_1.5", ""));
    }

    [Fact]
    public void Selection_from_another_board_still_fails_before_policy_matching()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("boardMatch.VirtualBoardId = @virtualBoardId", source);
        Assert.Contains("snapshot.VirtualBoardId = boardMatch.VirtualBoardId", source);
        Assert.DoesNotContain("FROM dbo.VirtualCurrentBoard", source);
    }

    [Fact]
    public void Provider_match_id_resolves_through_board_match_map()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        var selectionLookupStart = source.IndexOf("const string selectionSql", StringComparison.Ordinal);
        var selectionLookupEnd = source.IndexOf("var matched = candidates", selectionLookupStart, StringComparison.Ordinal);
        var selectionLookup = source[selectionLookupStart..selectionLookupEnd];
        Assert.Contains("FROM dbo.VirtualBoardMatchesMap boardMatch WITH (HOLDLOCK)", selectionLookup);
        Assert.Contains("boardMatch.ProviderMatchId = @providerMatchId", selectionLookup);
        Assert.Contains("snapshot.BetServiceMatchNo = boardMatch.BetServiceMatchNo", selectionLookup);
        Assert.DoesNotContain("BetServiceMatchNo = @matchId", selectionLookup);
    }

    [Fact]
    public void Board_snapshot_fallback_works_when_match_odds_is_absent()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("LEFT JOIN dbo.MatchOdds currentOdd WITH (UPDLOCK, HOLDLOCK)", source);
        Assert.Contains("COALESCE(currentOdd.Odd, snapshot.Odd) AS Odd", source);
        Assert.Contains("currentOdd.MatchOddId IS NULL OR ISNULL(currentOdd.IsLocked, 0) = 0", source);
    }

    [Fact]
    public void Valid_match_odds_can_still_supply_current_odd_when_available()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        Assert.Contains("currentOdd.BetServiceMatchNo = boardMatch.BetServiceMatchNo", source);
        Assert.Contains("currentOdd.MatchOddId = snapshot.MatchOddId", source);
        Assert.Contains("COALESCE(currentOdd.Odd, snapshot.Odd) AS Odd", source);
    }

    [Fact]
    public void Completed_or_resulted_board_is_rejected_before_selection_lookup()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Data", "TicketDb.cs"));
        var completedCheck = source.IndexOf("b.Status <> 0 OR b.HasResults = 1", StringComparison.Ordinal);
        var expiredReturn = source.IndexOf("Code = \"board_expired\"", StringComparison.Ordinal);
        var selectionLookup = source.IndexOf("FROM dbo.VirtualBoardMatchesMap", StringComparison.Ordinal);
        Assert.True(completedCheck >= 0 && completedCheck < expiredReturn && expiredReturn < selectionLookup);
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

    private static TicketSelectionRequest Selection(string market, string option, decimal? line) => new()
    {
        Market = market,
        Option = option,
        Line = line,
        MatchOddId = null,
        Odd = 1.55m
    };
}
