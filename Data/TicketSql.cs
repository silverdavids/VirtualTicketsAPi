namespace VirtualTickets.Api.Data;

public static class TicketSql
{
    public const string HealthCheck = "SELECT 1";

    public static readonly string[] SetTables =
    [
        "BetSet",
        "BetSets",
        "Sets",
        "TicketSet",
        "TicketSets"
    ];

    public static readonly string[] ActiveColumns =
    [
        "IsActive",
        "Active"
    ];

    public static readonly string[] StatusColumns =
    [
        "Status",
        "State"
    ];

    public static readonly string[] AccountTables =
    [
        "Accounts",
        "Account",
        "Users",
        "User"
    ];

    public static readonly string[] AccountIdColumns =
    [
        "UserId",
        "AccountId",
        "Id"
    ];

    public static readonly string[] UsernameColumns =
    [
        "Username",
        "UserName",
        "LoginName",
        "Name"
    ];

    public static readonly string[] ShopTables =
    [
        "Branches",
        "Branch",
        "Shops",
        "Shop"
    ];

    public static readonly string[] ShopCodeColumns =
    [
        "ShopCode",
        "BranchCode",
        "Code"
    ];

    public static readonly string[] MatchOddTables =
    [
        "MatchOdds",
        "MatchOdd",
        "Odds",
        "Odd"
    ];

    public static readonly string[] MatchOddIdColumns =
    [
        "MatchOddId",
        "OddId",
        "Id"
    ];

    public static readonly string[] MatchIdColumns =
    [
        "MatchId",
        "EventId"
    ];

    public static readonly string[] OddValueColumns =
    [
        "Odd",
        "Odds",
        "Value"
    ];
}
