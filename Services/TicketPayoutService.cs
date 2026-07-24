using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services;

public sealed class TicketPayoutService
{
    private const string VirtualDisplaySource = "VirtualDisplay";
    private readonly string? _connectionString;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TicketPayoutService> _logger;
    private readonly string _currency;

    public TicketPayoutService(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<TicketPayoutService> logger)
    {
        _connectionString = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_CONNECTION_STRING");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _currency = configuration["VirtualTickets:Currency"] ?? "UGX";
    }

    public async Task<TicketPayoutLookupResponse> LookupAsync(
        TicketPayoutLookupRequest request,
        CancellationToken cancellationToken)
    {
        var ticketNumber = ValidateTicketNumber(request.TicketNumber);
        var identity = ResolveTerminalIdentity();
        await using var connection = await OpenAsync(cancellationToken);
        var receipt = await FindReceiptAsync(connection, null, ticketNumber, false, cancellationToken)
            ?? throw Error(404, "TicketNotFound", "Ticket was not found.", ticketNumber);

        return ToLookup(receipt, identity.BranchId);
    }

    public async Task<TicketPayoutResponse> PayoutAsync(
        TicketPayoutRequest request,
        CancellationToken cancellationToken)
    {
        var ticketNumber = ValidateTicketNumber(request.TicketNumber);
        if (request.ConfirmationReference?.Length > 100)
        {
            throw Error(400, "InvalidTicket", "Confirmation reference cannot exceed 100 characters.", ticketNumber);
        }

        var identity = ResolveTerminalIdentity();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var receipt = await FindReceiptAsync(connection, transaction, ticketNumber, true, cancellationToken)
                ?? throw Error(404, "TicketNotFound", "Ticket was not found.", ticketNumber);
            var eligibility = ToLookup(receipt, identity.BranchId);
            if (!eligibility.CanPayout)
            {
                throw EligibilityError(eligibility);
            }

            var userId = await ResolveActiveShiftUserAsync(
                connection,
                transaction,
                identity.TerminalId,
                cancellationToken);
            if (userId is null)
            {
                throw Error(403, "PayoutNotAuthorized", "The terminal has no active user shift.", ticketNumber);
            }

            var payoutReference = CreatePayoutReference();
            var paidAt = DateTime.UtcNow;
            var clientReference = string.IsNullOrWhiteSpace(request.ConfirmationReference)
                ? null
                : request.ConfirmationReference.Trim();

            var branchUpdated = await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE dbo.Branches WITH (UPDLOCK)
                SET Balance = Balance - @amount,
                    ModifiedOn = GETDATE(),
                    ModifiedOnUtc = @paidAt
                WHERE BranchId = @branchId
                  AND Balance >= @amount
                  AND (MaxPayOut IS NULL OR MaxPayOut <= 0 OR @amount <= MaxPayOut);
                """,
                [
                    new("@amount", receipt.PayableAmount),
                    new("@paidAt", paidAt),
                    new("@branchId", identity.BranchId)
                ],
                cancellationToken);
            if (branchUpdated != 1)
            {
                throw Error(409, "Blocked", "Branch payout limit or cash balance does not allow this payout.", ticketNumber);
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                INSERT INTO dbo.VirtualTicketPayouts
                (
                    ReceiptId, TicketNumber, Amount, Currency, OriginalReceiptStatus,
                    ResultingReceiptStatus, TerminalId, TerminalCode, UserId, BranchId,
                    PayoutReference, ConfirmationReference, PaidAtUtc
                )
                VALUES
                (
                    @receiptId, @ticketNumber, @amount, @currency, @won,
                    @paid, @terminalId, @terminalCode, @userId, @branchId,
                    @payoutReference, @confirmationReference, @paidAt
                );
                """,
                [
                    new("@receiptId", receipt.ReceiptId),
                    new("@ticketNumber", ticketNumber),
                    new("@amount", receipt.PayableAmount),
                    new("@currency", _currency),
                    new("@won", (int)ReceiptStatus.Won),
                    new("@paid", (int)ReceiptStatus.Paid),
                    new("@terminalId", identity.TerminalId),
                    new("@terminalCode", identity.TerminalCode),
                    new("@userId", userId),
                    new("@branchId", identity.BranchId),
                    new("@payoutReference", payoutReference),
                    new("@confirmationReference", (object?)clientReference ?? DBNull.Value),
                    new("@paidAt", paidAt)
                ],
                cancellationToken);

            var receiptUpdated = await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE dbo.Receipts
                SET ReceiptStatus = @paid,
                    AmountPaid = @amount,
                    PaidBy = @userId,
                    PayingBranchId = @branchId,
                    TimePaid = @paidAt,
                    PaymentReference = @payoutReference,
                    ModifiedOn = GETDATE(),
                    ModifiedOnUtc = @paidAt
                WHERE ReceiptId = @receiptId
                  AND ReceiptStatus = @won
                  AND IsCanceled = 0;
                """,
                [
                    new("@paid", (int)ReceiptStatus.Paid),
                    new("@amount", receipt.PayableAmount),
                    new("@userId", userId),
                    new("@branchId", identity.BranchId),
                    new("@paidAt", paidAt),
                    new("@payoutReference", payoutReference),
                    new("@receiptId", receipt.ReceiptId),
                    new("@won", (int)ReceiptStatus.Won)
                ],
                cancellationToken);
            if (receiptUpdated != 1)
            {
                throw Error(409, "AlreadyPaid", "This ticket has already been paid.", ticketNumber);
            }

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Virtual ticket payout completed. ReceiptId={ReceiptId} TicketNumber={TicketNumber} Amount={Amount} Currency={Currency} OriginalStatus={OriginalStatus} ResultingStatus={ResultingStatus} TerminalId={TerminalId} TerminalCode={TerminalCode} UserId={UserId} BranchId={BranchId} PayoutReference={PayoutReference} ConfirmationReference={ConfirmationReference} PaidAtUtc={PaidAtUtc}",
                receipt.ReceiptId, ticketNumber, receipt.PayableAmount, _currency,
                ReceiptStatus.Won, ReceiptStatus.Paid, identity.TerminalId, identity.TerminalCode,
                userId, identity.BranchId, payoutReference, clientReference, paidAt);

            return new(
                receipt.ReceiptId,
                ticketNumber,
                receipt.PayableAmount,
                _currency,
                new DateTimeOffset(DateTime.SpecifyKind(paidAt, DateTimeKind.Utc)),
                payoutReference,
                ReceiptStatus.Paid.ToString());
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw Error(409, "AlreadyPaid", "This ticket has already been paid.", ticketNumber);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw;
        }
    }

    private TicketPayoutLookupResponse ToLookup(PayoutReceipt receipt, int terminalBranchId)
    {
        string? reason = null;
        if (!string.Equals(receipt.PaymentSource, VirtualDisplaySource, StringComparison.OrdinalIgnoreCase))
            reason = "NotVirtualTicket";
        else if (receipt.BranchId != terminalBranchId)
            reason = "WrongBranch";
        else if (receipt.IsCanceled || receipt.Status == ReceiptStatus.Cancelled)
            reason = "Cancelled";
        else if (receipt.Status == ReceiptStatus.Blocked)
            reason = "Blocked";
        else if (receipt.Status == ReceiptStatus.Paid)
            reason = "AlreadyPaid";
        else if (!receipt.AllSelectionsSettled || receipt.Status is ReceiptStatus.Pending or ReceiptStatus.Inactive)
            reason = "Pending";
        else if (receipt.Status == ReceiptStatus.Lost)
            reason = "Lost";
        else if (receipt.Status != ReceiptStatus.Won)
            reason = "Blocked";

        return new(
            receipt.ReceiptId,
            receipt.TicketNumber,
            AsUtcOffset(receipt.PlacedAt),
            receipt.Stake,
            receipt.TotalOdds,
            receipt.PossibleWin,
            receipt.PayableAmount,
            _currency,
            receipt.Status.ToString(),
            reason is null,
            reason,
            receipt.PaidAt.HasValue ? AsUtcOffset(receipt.PaidAt.Value) : null,
            receipt.PayoutReference);
    }

    private static TicketPayoutException EligibilityError(TicketPayoutLookupResponse response)
    {
        var code = response.CannotPayoutReason ?? "Blocked";
        var message = code switch
        {
            "Pending" => "This ticket has not been fully settled.",
            "Lost" => "This ticket did not win.",
            "Cancelled" => "This ticket was cancelled.",
            "AlreadyPaid" => "This ticket has already been paid.",
            "WrongBranch" => "This ticket cannot be paid by this branch.",
            "NotVirtualTicket" => "This is not a virtual-display ticket.",
            _ => "This ticket is not eligible for payout."
        };
        return Error(409, code, message, response.TicketNumber);
    }

    private TerminalClaims ResolveTerminalIdentity()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true
            || !int.TryParse(user.FindFirstValue("terminal_id"), out var terminalId)
            || !int.TryParse(user.FindFirstValue("branch_id"), out var branchId)
            || user.FindFirstValue("terminal_code") is not { Length: > 0 } terminalCode
            || user.FindFirstValue("terminal_type") != "1")
        {
            throw Error(403, "PayoutNotAuthorized", "Authenticated virtual-display terminal identity is required.");
        }

        return new(terminalId, terminalCode, branchId);
    }

    private static string ValidateTicketNumber(string? value)
    {
        var normalized = TicketNumber.Normalize(value);
        if (!TicketNumber.IsValid(normalized))
        {
            throw Error(400, "InvalidTicket", "A valid ticket number is required.", normalized);
        }
        return normalized!;
    }

    private async Task<PayoutReceipt?> FindReceiptAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string ticketNumber,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        var lockHint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var command = new SqlCommand(
            $"""
            SELECT
                r.ReceiptId, r.SerialCode, COALESCE(r.CreatedOnUtc, r.ReceiptDate) AS PlacedAtUtc, r.Stake, r.TotalOdds,
                ROUND(r.Stake * r.TotalOdds, 2) AS PossibleWin,
                COALESCE(r.AmountPaid, ROUND(r.Stake * r.TotalOdds, 2)) AS PayableAmount,
                r.ReceiptStatus, r.IsCanceled, r.BranchId, r.PaymentSource,
                r.TimePaid, r.PaymentReference,
                CASE WHEN COUNT(b.BetId) > 0
                       AND SUM(CASE WHEN b.GameBetStatus IN (1, 2) THEN 1 ELSE 0 END) = COUNT(b.BetId)
                     THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS AllSelectionsSettled
            FROM dbo.Receipts r{lockHint}
            LEFT JOIN dbo.Bets b ON b.RecieptId = r.ReceiptId
            WHERE UPPER(LTRIM(RTRIM(r.SerialCode))) = @ticketNumber
            GROUP BY
                r.ReceiptId, r.SerialCode, r.CreatedOnUtc, r.ReceiptDate, r.Stake, r.TotalOdds,
                r.AmountPaid, r.ReceiptStatus, r.IsCanceled, r.BranchId,
                r.PaymentSource, r.TimePaid, r.PaymentReference;
            """,
            connection,
            transaction);
        command.Parameters.Add(new("@ticketNumber", ticketNumber));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new(
            Convert.ToInt64(reader["ReceiptId"]),
            Convert.ToString(reader["SerialCode"])!,
            Convert.ToDateTime(reader["PlacedAtUtc"]),
            Convert.ToDecimal(reader["Stake"]),
            Convert.ToDecimal(reader["TotalOdds"]),
            Convert.ToDecimal(reader["PossibleWin"]),
            Convert.ToDecimal(reader["PayableAmount"]),
            (ReceiptStatus)Convert.ToInt32(reader["ReceiptStatus"]),
            Convert.ToBoolean(reader["IsCanceled"]),
            Convert.ToInt32(reader["BranchId"]),
            reader["PaymentSource"] is DBNull ? null : Convert.ToString(reader["PaymentSource"]),
            Convert.ToBoolean(reader["AllSelectionsSettled"]),
            reader["TimePaid"] is DBNull ? null : Convert.ToDateTime(reader["TimePaid"]),
            reader["PaymentReference"] is DBNull ? null : Convert.ToString(reader["PaymentReference"]));
    }

    private static async Task<string?> ResolveActiveShiftUserAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int terminalId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) UserId
            FROM dbo.Shifts WITH (UPDLOCK, HOLDLOCK)
            WHERE TerminalId = @terminalId
              AND ISNULL(IsClosed, 0) = 0
              AND UserId IS NOT NULL
            ORDER BY StartTime DESC;
            """,
            connection,
            transaction);
        command.Parameters.Add(new("@terminalId", terminalId));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IEnumerable<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("VIRTUAL_TICKETS_CONNECTION_STRING is not set.");
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string CreatePayoutReference() =>
        $"VPO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant();

    private static TicketPayoutException Error(int status, string code, string message, string? ticket = null) =>
        new(status, code, message, ticket);

    private static async Task RollbackQuietlyAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        try { await transaction.RollbackAsync(cancellationToken); }
        catch { }
    }

    private sealed record TerminalClaims(int TerminalId, string TerminalCode, int BranchId);
    private sealed record PayoutReceipt(
        long ReceiptId, string TicketNumber, DateTime PlacedAt, decimal Stake,
        decimal TotalOdds, decimal PossibleWin, decimal PayableAmount, ReceiptStatus Status,
        bool IsCanceled, int BranchId, string? PaymentSource, bool AllSelectionsSettled,
        DateTime? PaidAt, string? PayoutReference);
}
