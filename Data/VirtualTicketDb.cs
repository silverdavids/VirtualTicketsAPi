using System.Data;
using Microsoft.Data.SqlClient;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Services;

namespace VirtualTickets.Api.Data;

public sealed class VirtualTicketDb
{
    private const string PaymentSource = "VirtualDisplay";
    private readonly string? _connectionString;

    public VirtualTicketDb()
    {
        _connectionString = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_CONNECTION_STRING");
    }

    public async Task<IReadOnlyCollection<VirtualTicketListItem>> GetTicketsAsync(
        DateTime? from,
        DateTime? to,
        int? status,
        string? userId,
        int? branchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP 200
                r.ReceiptId,
                r.SerialCode,
                r.ReceiptDate,
                r.UserId,
                r.BranchId,
                r.Stake,
                r.TotalOdds,
                ROUND(r.Stake * r.TotalOdds, 2) AS PossibleWin,
                r.ReceiptStatus,
                r.SetSize,
                r.SubmitedSize,
                r.WonSize,
                r.IsCanceled,
                r.IsLive,
                r.PaymentSource
            FROM dbo.Receipts r
            WHERE r.PaymentSource = @PaymentSource
              AND (@From IS NULL OR r.ReceiptDate >= @From)
              AND (@To IS NULL OR r.ReceiptDate < DATEADD(day, 1, @To))
              AND (@Status IS NULL OR r.ReceiptStatus = @Status)
              AND (@UserId IS NULL OR r.UserId = @UserId)
              AND (@BranchId IS NULL OR r.BranchId = @BranchId)
            ORDER BY r.ReceiptDate DESC;
            """,
            connection);

        AddParameter(command, "@PaymentSource", SqlDbType.NVarChar, PaymentSource);
        AddParameter(command, "@From", SqlDbType.DateTime2, from);
        AddParameter(command, "@To", SqlDbType.DateTime2, to);
        AddParameter(command, "@Status", SqlDbType.Int, status);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, string.IsNullOrWhiteSpace(userId) ? null : userId);
        AddParameter(command, "@BranchId", SqlDbType.Int, branchId);

        var tickets = new List<VirtualTicketListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(new VirtualTicketListItem
            {
                ReceiptId = GetRequiredInt64(reader, "ReceiptId"),
                SerialCode = GetNullableString(reader, "SerialCode"),
                ReceiptDate = GetNullableDateTime(reader, "ReceiptDate"),
                UserId = GetNullableString(reader, "UserId"),
                BranchId = GetNullableInt32(reader, "BranchId"),
                Stake = GetNullableDecimal(reader, "Stake"),
                TotalOdds = GetNullableDecimal(reader, "TotalOdds"),
                PossibleWin = GetNullableDecimal(reader, "PossibleWin"),
                ReceiptStatus = GetNullableInt32(reader, "ReceiptStatus"),
                SetSize = GetNullableInt32(reader, "SetSize"),
                SubmitedSize = GetNullableInt32(reader, "SubmitedSize"),
                WonSize = GetNullableInt32(reader, "WonSize"),
                IsCanceled = GetNullableBoolean(reader, "IsCanceled"),
                IsLive = GetNullableBoolean(reader, "IsLive"),
                PaymentSource = GetNullableString(reader, "PaymentSource")
            });
        }

        return tickets;
    }

    public async Task<TerminalVirtualTicketScope?> ResolveTerminalScopeAsync(
        TerminalTicketIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT t.BranchId, b.TicketAccountUserId, a.UserId AS VerifiedUserId
            FROM dbo.Terminals t
            INNER JOIN dbo.Branches b ON b.BranchId = t.BranchId
            LEFT JOIN dbo.Accounts a
              ON a.UserId = b.TicketAccountUserId
             AND a.BranchId = t.BranchId
            WHERE t.TerminalId = @TerminalId
              AND UPPER(LTRIM(RTRIM(t.TerminalCode))) = UPPER(@TerminalCode)
              AND t.IsActive = 1;
            """;
        await using var command = new SqlCommand(sql, connection);
        AddParameter(command, "@TerminalId", SqlDbType.Int, identity.TerminalId);
        AddParameter(command, "@TerminalCode", SqlDbType.NVarChar, identity.TerminalCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var databaseBranchId = GetNullableInt32(reader, "BranchId");
        var configuredUserId = GetNullableString(reader, "TicketAccountUserId");
        var verifiedUserId = GetNullableString(reader, "VerifiedUserId");
        if (!databaseBranchId.HasValue
            || databaseBranchId.Value != identity.BranchId
            || string.IsNullOrWhiteSpace(configuredUserId)
            || !string.Equals(configuredUserId, verifiedUserId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new TerminalVirtualTicketScope(databaseBranchId.Value, verifiedUserId!);
    }

    public async Task<VirtualTicketDetailsResponse?> GetTicketDetailsAsync(
        long receiptId,
        string? userId,
        int? branchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT
                r.ReceiptId,
                r.SerialCode,
                r.ReceiptDate,
                r.CreatedOnUtc AS BookedAtUtc,
                r.Stake,
                r.TotalOdds,
                ROUND(r.Stake * r.TotalOdds, 2) AS PossibleWin,
                r.ReceiptStatus,
                r.SetSize,
                r.SubmitedSize,
                r.WonSize,
                r.AmountPaid,
                r.DateSettled,
                r.PaymentSource
            FROM dbo.Receipts r
            WHERE r.ReceiptId = @ReceiptId
              AND r.PaymentSource = @PaymentSource
              AND (@UserId IS NULL OR r.UserId = @UserId)
              AND (@BranchId IS NULL OR r.BranchId = @BranchId);

            SELECT
                b.BetId,
                b.MatchId,
                m.HomeTeam,
                m.AwayTeam,
                m.League,
                m.StartTime,
                b.Market,
                b.[Option],
                b.Line,
                b.BetOdd,
                b.GameBetStatus,
                b.HomeScore,
                b.AwayScore,
                m.GameStatus AS MatchStatus
            FROM dbo.Bets b
            LEFT JOIN dbo.Matches m
                ON m.BetServiceMatchNo = b.MatchId
            WHERE b.RecieptId = @ReceiptId
              AND EXISTS (
                SELECT 1 FROM dbo.Receipts scopedReceipt
                WHERE scopedReceipt.ReceiptId = b.RecieptId
                  AND scopedReceipt.PaymentSource = @PaymentSource
                  AND (@UserId IS NULL OR scopedReceipt.UserId = @UserId)
                  AND (@BranchId IS NULL OR scopedReceipt.BranchId = @BranchId)
              )
            ORDER BY b.BetId;
            """,
            connection);

        AddParameter(command, "@ReceiptId", SqlDbType.BigInt, receiptId);
        AddParameter(command, "@PaymentSource", SqlDbType.NVarChar, PaymentSource);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, string.IsNullOrWhiteSpace(userId) ? null : userId);
        AddParameter(command, "@BranchId", SqlDbType.Int, branchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var receipt = new VirtualTicketReceiptDetail
        {
            ReceiptId = GetRequiredInt64(reader, "ReceiptId"),
            SerialCode = GetNullableString(reader, "SerialCode"),
            ReceiptDate = GetNullableDateTime(reader, "ReceiptDate"),
            BookedAtUtc = GetNullableDateTime(reader, "BookedAtUtc") is { } bookedAtUtc
                ? DateTime.SpecifyKind(bookedAtUtc, DateTimeKind.Utc)
                : null,
            Stake = GetNullableDecimal(reader, "Stake"),
            TotalOdds = GetNullableDecimal(reader, "TotalOdds"),
            PossibleWin = GetNullableDecimal(reader, "PossibleWin"),
            ReceiptStatus = GetNullableInt32(reader, "ReceiptStatus"),
            SetSize = GetNullableInt32(reader, "SetSize"),
            SubmitedSize = GetNullableInt32(reader, "SubmitedSize"),
            WonSize = GetNullableInt32(reader, "WonSize"),
            AmountPaid = GetNullableDecimal(reader, "AmountPaid"),
            DateSettled = GetNullableDateTime(reader, "DateSettled"),
            PaymentSource = GetNullableString(reader, "PaymentSource")
        };

        var bets = new List<VirtualTicketBetDetail>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                bets.Add(new VirtualTicketBetDetail
                {
                    BetId = GetRequiredInt64(reader, "BetId"),
                    MatchId = GetNullableInt64(reader, "MatchId"),
                    HomeTeam = GetNullableString(reader, "HomeTeam"),
                    AwayTeam = GetNullableString(reader, "AwayTeam"),
                    League = GetNullableString(reader, "League"),
                    StartTime = GetNullableDateTime(reader, "StartTime"),
                    Market = GetNullableString(reader, "Market"),
                    Option = GetNullableString(reader, "Option"),
                    Line = GetValueAsString(reader, "Line"),
                    BetOdd = GetNullableDecimal(reader, "BetOdd"),
                    GameBetStatus = GetNullableInt32(reader, "GameBetStatus"),
                    HomeScore = GetNullableInt32(reader, "HomeScore"),
                    AwayScore = GetNullableInt32(reader, "AwayScore"),
                    MatchStatus = GetNullableString(reader, "MatchStatus")
                });
            }
        }

        return new VirtualTicketDetailsResponse(receipt, bets);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("VIRTUAL_TICKETS_CONNECTION_STRING is not set.");
        }

        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddParameter(SqlCommand command, string name, SqlDbType type, object? value)
    {
        command.Parameters.Add(new SqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });
    }

    private static long GetRequiredInt64(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return Convert.ToInt64(value);
    }

    private static string? GetNullableString(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToString(value);
    }

    private static string? GetValueAsString(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int? GetNullableInt32(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToInt32(value);
    }

    private static long? GetNullableInt64(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToInt64(value);
    }

    private static decimal? GetNullableDecimal(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToDecimal(value);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToDateTime(value);
    }

    private static bool? GetNullableBoolean(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value is DBNull ? null : Convert.ToBoolean(value);
    }
}

public sealed record TerminalVirtualTicketScope(int BranchId, string UserId);
