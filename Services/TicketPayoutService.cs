using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Data;

namespace VirtualTickets.Api.Services;

public sealed class TicketPayoutService
{
    private readonly string? _connectionString;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TicketPayoutService> _logger;
    private readonly string _currency;
    private readonly Guid? _configuredPayoutUserId;

    public TicketPayoutService(
        IHttpContextAccessor httpContextAccessor,
        IOptions<VirtualTicketPayoutOptions> options,
        ILogger<TicketPayoutService> logger)
    {
        _connectionString = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_CONNECTION_STRING");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _currency = string.IsNullOrWhiteSpace(options.Value.Currency)
            ? "UGX"
            : options.Value.Currency.Trim().ToUpperInvariant();
        _configuredPayoutUserId = options.Value.PayoutUserId;
    }

    public async Task<TicketPayoutLookupResponse> LookupAsync(
        TicketPayoutLookupRequest request,
        CancellationToken cancellationToken)
    {
        var ticketNumber = ValidateTicketNumber(request.TicketNumber);
        var identity = ResolveTerminalIdentity();
        await using var connection = await OpenAsync(cancellationToken);
        await ValidateActiveTerminalAsync(connection, null, identity, cancellationToken);
        await ResolvePayoutUserAsync(connection, null, identity.BranchId, false, cancellationToken);
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
            await ValidateActiveTerminalAsync(connection, transaction, identity, cancellationToken);
            var receipt = await FindReceiptAsync(connection, transaction, ticketNumber, true, cancellationToken)
                ?? throw Error(404, "TicketNotFound", "Ticket was not found.", ticketNumber);
            var eligibility = ToLookup(receipt, identity.BranchId);
            if (!eligibility.CanPayout)
            {
                throw EligibilityError(eligibility);
            }

            var payoutUserId = await ResolvePayoutUserAsync(
                connection,
                transaction,
                identity.BranchId,
                true,
                cancellationToken);

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
                    @paid, @terminalId, @terminalCode, @payoutUserId, @branchId,
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
                    new("@payoutUserId", payoutUserId),
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
                    PaidBy = @payoutUserId,
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
                    new("@payoutUserId", payoutUserId),
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
                "Virtual ticket payout completed. ReceiptId={ReceiptId} TicketNumber={TicketNumber} Amount={Amount} Currency={Currency} OriginalStatus={OriginalStatus} ResultingStatus={ResultingStatus} TerminalId={TerminalId} TerminalCode={TerminalCode} PayoutUserId={PayoutUserId} BranchId={BranchId} PayoutReference={PayoutReference} ConfirmationReference={ConfirmationReference} PaidAtUtc={PaidAtUtc}",
                receipt.ReceiptId, ticketNumber, receipt.PayableAmount, _currency,
                ReceiptStatus.Won, ReceiptStatus.Paid, identity.TerminalId, identity.TerminalCode,
                payoutUserId, identity.BranchId, payoutReference, clientReference, paidAt);

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

    public async Task<TicketCancelResponse> CancelAsync(
        TicketCancelRequest request,
        CancellationToken cancellationToken)
    {
        var ticketNumber = ValidateTicketNumber(request.TicketNumber);
        ValidateOptionalText(request.ConfirmationReference, 100, "Confirmation reference", ticketNumber);
        ValidateOptionalText(request.Reason, 500, "Cancellation reason", ticketNumber);

        var identity = ResolveTerminalIdentity();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await ValidateActiveTerminalAsync(connection, transaction, identity, cancellationToken);
            var receipt = await FindReceiptAsync(connection, transaction, ticketNumber, true, cancellationToken)
                ?? throw Error(404, "TicketNotFound", "Ticket was not found.", ticketNumber);
            var cannotCancelReason = GetCannotCancelReason(receipt, identity.BranchId);
            if (cannotCancelReason is not null)
                throw CancellationEligibilityError(cannotCancelReason, ticketNumber);

            var userId = await ResolvePayoutUserAsync(
                connection,
                transaction,
                identity.BranchId,
                true,
                cancellationToken);
            var cancelledAt = DateTime.UtcNow;
            var cancelReference = CreateCancelReference();
            var confirmationReference = NormalizeOptionalText(request.ConfirmationReference);
            var reason = NormalizeOptionalText(request.Reason) ?? "CustomerRequested";

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                INSERT INTO dbo.VirtualTicketCancellations
                (
                    ReceiptId, TicketNumber, OriginalReceiptStatus, ResultingReceiptStatus,
                    TerminalId, TerminalCode, UserId, BranchId, CancelReference,
                    ConfirmationReference, CancelledAtUtc, Reason
                )
                VALUES
                (
                    @receiptId, @ticketNumber, @pending, @cancelled,
                    @terminalId, @terminalCode, @userId, @branchId, @cancelReference,
                    @confirmationReference, @cancelledAt, @reason
                );
                """,
                [
                    new("@receiptId", receipt.ReceiptId),
                    new("@ticketNumber", ticketNumber),
                    new("@pending", (int)ReceiptStatus.Pending),
                    new("@cancelled", (int)ReceiptStatus.Cancelled),
                    new("@terminalId", identity.TerminalId),
                    new("@terminalCode", identity.TerminalCode),
                    new("@userId", userId),
                    new("@branchId", identity.BranchId),
                    new("@cancelReference", cancelReference),
                    new("@confirmationReference", (object?)confirmationReference ?? DBNull.Value),
                    new("@cancelledAt", cancelledAt),
                    new("@reason", reason)
                ],
                cancellationToken);

            var receiptUpdated = await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE dbo.Receipts
                SET ReceiptStatus = @cancelled,
                    IsCanceled = 1,
                    DateSettled = @cancelledAt,
                    ModifiedOn = GETDATE(),
                    ModifiedOnUtc = @cancelledAt
                WHERE ReceiptId = @receiptId
                  AND ReceiptStatus = @pending
                  AND IsCanceled = 0;
                """,
                [
                    new("@cancelled", (int)ReceiptStatus.Cancelled),
                    new("@cancelledAt", cancelledAt),
                    new("@receiptId", receipt.ReceiptId),
                    new("@pending", (int)ReceiptStatus.Pending)
                ],
                cancellationToken);
            if (receiptUpdated != 1)
                throw Error(409, "AlreadyCancelled", "This ticket has already been cancelled.", ticketNumber);

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Virtual ticket cancellation completed. ReceiptId={ReceiptId} TicketNumber={TicketNumber} OriginalStatus={OriginalStatus} ResultingStatus={ResultingStatus} TerminalId={TerminalId} TerminalCode={TerminalCode} UserId={UserId} BranchId={BranchId} CancelReference={CancelReference} ConfirmationReference={ConfirmationReference} CancelledAtUtc={CancelledAtUtc} Reason={Reason}",
                receipt.ReceiptId, ticketNumber, ReceiptStatus.Pending, ReceiptStatus.Cancelled,
                identity.TerminalId, identity.TerminalCode, userId, identity.BranchId,
                cancelReference, confirmationReference, cancelledAt, reason);

            return new(
                receipt.ReceiptId,
                ticketNumber,
                "Canceled",
                AsUtcOffset(cancelledAt),
                cancelReference);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw Error(409, "AlreadyCancelled", "This ticket has already been cancelled.", ticketNumber);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, cancellationToken);
            throw;
        }
    }

    private TicketPayoutLookupResponse ToLookup(PayoutReceipt receipt, int terminalBranchId)
    {
        var reason = TicketPayoutEligibility.GetCannotPayoutReason(
            receipt.PaymentSource,
            receipt.BranchId,
            terminalBranchId,
            receipt.IsCanceled,
            receipt.Status,
            receipt.AllSelectionsSettled,
            receipt.AllSelectionsWon);
        var cannotCancelReason = GetCannotCancelReason(receipt, terminalBranchId);

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
            receipt.PayoutReference,
            cannotCancelReason is null,
            cannotCancelReason);
    }

    private static string? GetCannotCancelReason(PayoutReceipt receipt, int terminalBranchId) =>
        TicketCancellationEligibility.GetCannotCancelReason(
            receipt.PaymentSource,
            receipt.BranchId,
            terminalBranchId,
            receipt.IsCanceled,
            receipt.Status,
            receipt.NoSelectionsSettled,
            receipt.EventStarted);

    private static TicketPayoutException CancellationEligibilityError(string code, string ticketNumber)
    {
        var message = code switch
        {
            "AlreadyCancelled" => "This ticket has already been cancelled.",
            "AlreadyPaid" => "This ticket has already been paid.",
            "CannotCancelWonTicket" => "Winning tickets cannot be cancelled.",
            "CannotCancelSettledTicket" => "Settled tickets cannot be cancelled.",
            "EventStarted" => "The first event has already started.",
            "WrongBranch" => "This ticket cannot be cancelled by this branch.",
            "NotVirtualTicket" => "This is not a virtual-display ticket.",
            _ => "This ticket cannot be cancelled."
        };
        return Error(409, code, message, ticketNumber);
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
                     THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS AllSelectionsSettled,
                CASE WHEN COUNT(b.BetId) > 0
                       AND SUM(CASE WHEN b.GameBetStatus = 2 THEN 1 ELSE 0 END) = COUNT(b.BetId)
                     THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS AllSelectionsWon,
                CASE WHEN COUNT(b.BetId) > 0
                       AND SUM(CASE WHEN b.GameBetStatus = 0 THEN 1 ELSE 0 END) = COUNT(b.BetId)
                     THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS NoSelectionsSettled,
                CASE WHEN COUNT(b.BetId) = 0
                       OR COUNT(m.BetServiceMatchNo) <> COUNT(b.BetId)
                       OR MIN(m.StartTime) <= GETDATE()
                     THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS EventStarted
            FROM dbo.Receipts r{lockHint}
            LEFT JOIN dbo.Bets b ON b.RecieptId = r.ReceiptId
            LEFT JOIN dbo.Matches m ON m.BetServiceMatchNo = b.MatchId
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
            Convert.ToBoolean(reader["AllSelectionsWon"]),
            Convert.ToBoolean(reader["NoSelectionsSettled"]),
            Convert.ToBoolean(reader["EventStarted"]),
            reader["TimePaid"] is DBNull ? null : Convert.ToDateTime(reader["TimePaid"]),
            reader["PaymentReference"] is DBNull ? null : Convert.ToString(reader["PaymentReference"]));
    }

    private async Task<string> ResolvePayoutUserAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int terminalBranchId,
        bool lockUser,
        CancellationToken cancellationToken)
    {
        var payoutUserId = PayoutUserAuthorization.RequireConfigured(_configuredPayoutUserId);
        var lockHint = lockUser ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        await using var command = new SqlCommand(
            $"""
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM dbo.AspNetUsers u{lockHint}
                INNER JOIN dbo.Accounts a ON a.UserId = u.Id
                WHERE u.Id = @payoutUserId
                  AND u.IsActivated = 1
                  AND a.BranchId = @branchId
            )
            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """,
            connection,
            transaction);
        command.Parameters.Add(new("@payoutUserId", payoutUserId));
        command.Parameters.Add(new("@branchId", terminalBranchId));
        var isValid = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
        return PayoutUserAuthorization.RequireActive(payoutUserId, isValid);
    }

    private static async Task ValidateActiveTerminalAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        TerminalClaims identity,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM dbo.Terminals
                WHERE TerminalId = @terminalId
                  AND IsActive = 1
                  AND TerminalType = 1
                  AND BranchId = @branchId
                  AND UPPER(LTRIM(RTRIM(TerminalCode))) = UPPER(@terminalCode)
            )
            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;
            """,
            connection,
            transaction);
        command.Parameters.Add(new("@terminalId", identity.TerminalId));
        command.Parameters.Add(new("@branchId", identity.BranchId));
        command.Parameters.Add(new("@terminalCode", identity.TerminalCode));
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
        {
            throw Error(403, "PayoutNotAuthorized", "The virtual-display terminal is not active.");
        }
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

    private static string CreateCancelReference() =>
        $"VTC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant();

    private static void ValidateOptionalText(string? value, int maxLength, string fieldName, string ticketNumber)
    {
        if (value?.Trim().Length > maxLength)
            throw Error(400, "InvalidTicket", $"{fieldName} cannot exceed {maxLength} characters.", ticketNumber);
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        bool AllSelectionsWon, bool NoSelectionsSettled, bool EventStarted,
        DateTime? PaidAt, string? PayoutReference);
}
