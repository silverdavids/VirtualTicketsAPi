namespace VirtualTickets.Api.Services;

public static class VirtualBoardSelectionPolicy
{
    public static bool OddsMatch(decimal submittedOdd, decimal authoritativeOdd) =>
        submittedOdd == authoritativeOdd;
}
