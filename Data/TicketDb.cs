using Microsoft.Data.SqlClient;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Services;

namespace VirtualTickets.Api.Data;

public sealed class TicketDb
{
    private readonly string? _connectionString;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TicketDb> _logger;

    public TicketDb(IHostEnvironment environment, ILogger<TicketDb> logger)
    {
        _connectionString = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_CONNECTION_STRING");
        _environment = environment;
        _logger = logger;
    }

    public bool HasConnectionString => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task<DatabaseConnectionResult> CanConnectAsync(CancellationToken cancellationToken)
    {
        if (!HasConnectionString)
        {
            return DatabaseConnectionResult.Failed(
                "connection_string_missing",
                "VIRTUAL_TICKETS_CONNECTION_STRING is not set.");
        }

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(TicketSql.HealthCheck, connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return DatabaseConnectionResult.Connected();
        }
        catch (SqlException exception)
        {
            _logger.LogError(exception, "Virtual tickets database connection failed.");
            return DatabaseConnectionResult.Failed(
                "database_unreachable",
                GetSafeDatabaseErrorMessage(exception));
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(exception, "Virtual tickets database connection failed because the connection could not be opened.");
            return DatabaseConnectionResult.Failed(
                "database_unreachable",
                GetSafeDatabaseErrorMessage(exception));
        }
    }

    public async Task<ActiveSetResult> GetActiveSetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1 SetNo
            FROM dbo.Sets
            WHERE Status = 1
            ORDER BY SetNo DESC
            """;

        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? ActiveSetResult.NotFound()
            : ActiveSetResult.Found(Convert.ToInt64(result));
    }

    public async Task<ReferenceDataStateResult> GetReferenceDataStateAsync(CancellationToken cancellationToken)
    {
        var missingTables = new List<string>();
        var tableNames = new List<string>();

        var setsTable = await FindDboTableAsync("Sets", cancellationToken);
        if (setsTable is null)
        {
            missingTables.Add("dbo.Sets");
        }
        else
        {
            var setsColumns = await GetColumnsAsync(setsTable, cancellationToken);
            if (!setsColumns.Contains("SetNo") || !setsColumns.Contains("Status"))
            {
                missingTables.Add("dbo.Sets(SetNo, Status)");
            }

            tableNames.Add(setsTable);
        }

        await AddReferenceTableAsync("accounts/users", TicketSql.AccountTables, tableNames, missingTables, cancellationToken);
        await AddReferenceTableAsync("branches/shops", TicketSql.ShopTables, tableNames, missingTables, cancellationToken);
        await AddReferenceTableAsync("odds", TicketSql.MatchOddTables, tableNames, missingTables, cancellationToken);

        if (missingTables.Count > 0)
        {
            return ReferenceDataStateResult.Invalid(missingTables);
        }

        foreach (var tableName in tableNames)
        {
            if (await HasAnyRowsAsync(tableName, cancellationToken))
            {
                return ReferenceDataStateResult.Available();
            }
        }

        return ReferenceDataStateResult.Empty();
    }

    public async Task<ProbeResult> AccountExistsAsync(string? userId, string? username, CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(TicketSql.AccountTables, cancellationToken);
        if (table is null)
        {
            return ProbeResult.Unknown("No recognized account or user table was found.");
        }

        var columns = await GetColumnsAsync(table, cancellationToken);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var idColumn = FirstExisting(columns, TicketSql.AccountIdColumns);
            if (idColumn is not null)
            {
                return await ExistsAsync(table, $"{Quote(idColumn)} = @value", [new("@value", userId)], cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            var usernameColumn = FirstExisting(columns, TicketSql.UsernameColumns);
            if (usernameColumn is not null)
            {
                return await ExistsAsync(table, $"{Quote(usernameColumn)} = @value", [new("@value", username)], cancellationToken);
            }
        }

        return ProbeResult.Unknown("A supplied user identifier could not be matched to recognized account columns.");
    }

    public async Task<ProbeResult> ShopExistsAsync(string shopCode, CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(TicketSql.ShopTables, cancellationToken);
        if (table is null)
        {
            return ProbeResult.Unknown("No recognized branch or shop table was found.");
        }

        var columns = await GetColumnsAsync(table, cancellationToken);
        var shopCodeColumn = FirstExisting(columns, TicketSql.ShopCodeColumns);
        if (shopCodeColumn is null)
        {
            return ProbeResult.Unknown("The branch or shop table did not expose a recognized code column.");
        }

        return await ExistsAsync(table, $"{Quote(shopCodeColumn)} = @shopCode", [new("@shopCode", shopCode)], cancellationToken);
    }

    public async Task<ProbeResult> MatchOddMatchesAsync(long matchOddId, long? matchId, decimal odd, CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(TicketSql.MatchOddTables, cancellationToken);
        if (table is null)
        {
            return ProbeResult.Unknown("No recognized odds table was found.");
        }

        var columns = await GetColumnsAsync(table, cancellationToken);
        var idColumn = FirstExisting(columns, TicketSql.MatchOddIdColumns);
        if (idColumn is null)
        {
            return ProbeResult.Unknown("The odds table did not expose a recognized match odd id column.");
        }

        var predicates = new List<string> { $"{Quote(idColumn)} = @matchOddId" };
        var parameters = new List<SqlParameter> { new("@matchOddId", matchOddId) };

        var oddColumn = FirstExisting(columns, TicketSql.OddValueColumns);
        if (oddColumn is not null)
        {
            predicates.Add($"ABS(CAST({Quote(oddColumn)} AS decimal(18, 6)) - @odd) < 0.000001");
            parameters.Add(new("@odd", odd));
        }

        var matchIdColumn = FirstExisting(columns, TicketSql.MatchIdColumns);
        if (matchIdColumn is not null && matchId.HasValue && matchId.Value > 0)
        {
            predicates.Add($"{Quote(matchIdColumn)} = @matchId");
            parameters.Add(new("@matchId", matchId.Value));
        }

        return await ExistsAsync(table, string.Join(" AND ", predicates), parameters, cancellationToken);
    }

    public async Task<TicketPlaceResult> PlaceTicketAsync(
        TicketValidateRequest request,
        long activeSetNo,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
            var errors = new List<TicketValidationError>();
            var branchId = await ResolveBranchIdAsync(connection, transaction, request, cancellationToken);
            if (!branchId.HasValue)
            {
                errors.Add(new TicketValidationError
                {
                    Code = "branch_required",
                    Field = "shopCode",
                    Message = "Ticket placement requires a valid branch or shop."
                });
            }

            var resolvedSelections = new List<ResolvedTicketSelection>();
            for (var index = 0; index < request.Selections.Count; index++)
            {
                var selection = request.Selections[index];
                var matchId = await ResolveMatchIdAsync(connection, transaction, selection, cancellationToken);
                if (!matchId.HasValue)
                {
                    errors.Add(new TicketValidationError
                    {
                        Code = "match_not_found",
                        Field = $"selections[{index}].matchId",
                        Message = "Selection could not be mapped to dbo.Matches.BetServiceMatchNo. Send a real matchId for placement."
                    });
                    continue;
                }

                resolvedSelections.Add(new ResolvedTicketSelection(selection, matchId.Value));
            }

            if (errors.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TicketPlaceResult.Failed(errors);
            }

            var serial = Guid.NewGuid();
            var ticketNumber = TicketNumber.Generate();
            var receiptId = await InsertReceiptAsync(
                connection,
                transaction,
                request,
                activeSetNo,
                branchId!.Value,
                serial,
                ticketNumber,
                cancellationToken);

            var placedBets = new List<PlacedBetResponse>();
            foreach (var resolvedSelection in resolvedSelections)
            {
                var betId = await InsertBetAsync(connection, transaction, receiptId, resolvedSelection, cancellationToken);
                placedBets.Add(new PlacedBetResponse
                {
                    BetId = betId,
                    MatchId = resolvedSelection.MatchId,
                    Odd = resolvedSelection.Selection.Odd
                });
            }

            await transaction.CommitAsync(cancellationToken);
                return TicketPlaceResult.Placed(receiptId, serial, ticketNumber, placedBets);
            }
            catch (SqlException exception) when (IsTicketNumberCollision(exception) && attempt < 5)
            {
                await RollbackQuietlyAsync(transaction, cancellationToken);
                _logger.LogWarning("Ticket-number collision on placement attempt {Attempt}; retrying.", attempt);
            }
            catch (SqlException exception)
            {
                await RollbackQuietlyAsync(transaction, cancellationToken);
                _logger.LogError(exception, "Ticket placement failed while writing receipt or bets.");
                return TicketPlaceResult.Failed(
            [
                new TicketValidationError
                {
                    Code = "ticket_place_failed",
                    Field = "database",
                    Message = GetSafeDatabaseErrorMessage(exception)
                }
            ]);
            }
        }

        return TicketPlaceResult.Failed(
        [
            new TicketValidationError
            {
                Code = "ticket_number_generation_failed",
                Field = "ticketNumber",
                Message = "A unique ticket number could not be generated."
            }
        ]);
    }

    private async Task<string?> FindTableAsync(IEnumerable<string> tableNames, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1) QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.name = @tableName
            ORDER BY CASE WHEN s.name = 'dbo' THEN 0 ELSE 1 END, s.name
            """;

        foreach (var tableName in tableNames)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add(new SqlParameter("@tableName", tableName));
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is string qualifiedName)
            {
                return qualifiedName;
            }
        }

        return null;
    }

    private async Task<HashSet<string>> GetColumnsAsync(string qualifiedTableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@qualifiedTableName)
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@qualifiedTableName", qualifiedTableName.Replace("[", string.Empty).Replace("]", string.Empty)));

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<ProbeResult> ExistsAsync(
        string qualifiedTableName,
        string whereClause,
        IReadOnlyCollection<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = $"SELECT TOP (1) 1 FROM {qualifiedTableName} WHERE {whereClause}";
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? ProbeResult.NotFound() : ProbeResult.Found();
    }

    private async Task<int?> ResolveBranchIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TicketValidateRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ShopCode))
        {
            var branchId = await ExecuteScalarAsync<int?>(
                connection,
                transaction,
                """
                SELECT TOP 1 BranchId
                FROM dbo.Branches
                WHERE BranchCode = @shopCode
                ORDER BY BranchId
                """,
                [new("@shopCode", request.ShopCode)],
                cancellationToken);

            if (branchId.HasValue)
            {
                return branchId;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            var branchId = await ExecuteScalarAsync<int?>(
                connection,
                transaction,
                """
                SELECT TOP 1 BranchId
                FROM dbo.Accounts
                WHERE UserId = @userId
                ORDER BY BranchId
                """,
                [new("@userId", request.UserId)],
                cancellationToken);

            if (branchId.HasValue)
            {
                return branchId;
            }
        }

        return await ExecuteScalarAsync<int?>(
            connection,
            transaction,
            """
            SELECT TOP 1 BranchId
            FROM dbo.Branches
            ORDER BY BranchId
            """,
            [],
            cancellationToken);
    }

    private async Task<long?> ResolveMatchIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TicketSelectionRequest selection,
        CancellationToken cancellationToken)
    {
        if (selection.MatchId.HasValue && selection.MatchId.Value > 0)
        {
            var matchId = await FindMatchByBetServiceMatchNoAsync(connection, transaction, selection.MatchId.Value, cancellationToken);
            if (matchId.HasValue)
            {
                return matchId;
            }
        }

        if (long.TryParse(selection.ProviderMatchId, out var providerMatchId) && providerMatchId > 0)
        {
            var matchId = await FindMatchByBetServiceMatchNoAsync(connection, transaction, providerMatchId, cancellationToken);
            if (matchId.HasValue)
            {
                return matchId;
            }
        }

        if (long.TryParse(selection.ShortCode, out var shortCode) && shortCode > 0)
        {
            var matchId = await FindMatchByBetServiceMatchNoAsync(connection, transaction, shortCode, cancellationToken);
            if (matchId.HasValue)
            {
                return matchId;
            }
        }

        if (!string.IsNullOrWhiteSpace(selection.HomeTeam) && !string.IsNullOrWhiteSpace(selection.AwayTeam))
        {
            return await ExecuteScalarAsync<long?>(
                connection,
                transaction,
                """
                SELECT TOP 1 BetServiceMatchNo
                FROM dbo.Matches
                WHERE UPPER(HomeTeam) = UPPER(@homeTeam)
                  AND UPPER(AwayTeam) = UPPER(@awayTeam)
                ORDER BY StartTime DESC, BetServiceMatchNo DESC
                """,
                [
                    new("@homeTeam", selection.HomeTeam),
                    new("@awayTeam", selection.AwayTeam)
                ],
                cancellationToken);
        }

        return null;
    }

    private async Task<long?> FindMatchByBetServiceMatchNoAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long betServiceMatchNo,
        CancellationToken cancellationToken)
    {
        return await ExecuteScalarAsync<long?>(
            connection,
            transaction,
            """
            SELECT TOP 1 BetServiceMatchNo
            FROM dbo.Matches
            WHERE BetServiceMatchNo = @matchId
            """,
            [new("@matchId", betServiceMatchNo)],
            cancellationToken);
    }

    private async Task<int> InsertReceiptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TicketValidateRequest request,
        long activeSetNo,
        int branchId,
        Guid serial,
        string ticketNumber,
        CancellationToken cancellationToken)
    {
        var totalOdds = request.Selections.Aggregate(1m, (current, selection) => current * selection.Odd);
        var now = DateTime.Now;
        var utcNow = DateTime.UtcNow;

        return await ExecuteScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO dbo.Receipts
            (
                UserId,
                ReceiptDate,
                Stake,
                TotalOdds,
                IsCanceled,
                SetNo,
                SetSize,
                SubmitedSize,
                WonSize,
                BranchId,
                Serial,
                SerialCode,
                ReceiptStatus,
                Bonus,
                Tax,
                HasPrinted,
                IsLive,
                CreatedOn,
                CreatedOnUtc,
                PaymentSource,
                ModifiedOn,
                ModifiedOnUtc
            )
            OUTPUT INSERTED.ReceiptId
            VALUES
            (
                @userId,
                @receiptDate,
                @stake,
                @totalOdds,
                0,
                @setNo,
                @setSize,
                @submittedSize,
                0,
                @branchId,
                @serial,
                @ticketNumber,
                @receiptStatus,
                0,
                0,
                0,
                0,
                @createdOn,
                @createdOnUtc,
                @paymentSource,
                @createdOn,
                @createdOnUtc
            )
            """,
            [
                new("@userId", (object?)request.UserId ?? DBNull.Value),
                new("@receiptDate", now),
                new("@stake", request.Stake),
                new("@totalOdds", totalOdds),
                new("@setNo", activeSetNo),
                new("@setSize", request.Selections.Count),
                new("@submittedSize", request.Selections.Count),
                new("@branchId", branchId),
                new("@serial", serial),
                new("@ticketNumber", ticketNumber),
                new("@receiptStatus", (int)ReceiptStatus.Pending),
                new("@createdOn", now),
                new("@createdOnUtc", utcNow),
                new("@paymentSource", (object?)request.Source ?? DBNull.Value)
            ],
            cancellationToken);
    }

    private async Task<int> InsertBetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int receiptId,
        ResolvedTicketSelection resolvedSelection,
        CancellationToken cancellationToken)
    {
        var selection = resolvedSelection.Selection;

        return await ExecuteScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO dbo.Bets
            (
                MatchId,
                RecieptId,
                GameBetStatus,
                BetOdd,
                ExtraValue,
                BetMinute,
                [Option],
                Line,
                Market,
                IsLive,
                HomeScore,
                AwayScore,
                MatchTimeStamp
            )
            OUTPUT INSERTED.BetId
            VALUES
            (
                @matchId,
                @receiptId,
                0,
                @betOdd,
                @extraValue,
                0,
                @option,
                @line,
                @market,
                0,
                0,
                0,
                @matchTimeStamp
            )
            """,
            [
                new("@matchId", resolvedSelection.MatchId),
                new("@receiptId", receiptId),
                new("@betOdd", selection.Odd),
                new("@extraValue", (object?)selection.Line ?? 0m),
                new("@option", (object?)selection.Option ?? DBNull.Value),
                new("@line", (object?)selection.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value),
                new("@market", (object?)selection.Market ?? DBNull.Value),
                new("@matchTimeStamp", new DateTime(1900, 1, 1))
            ],
            cancellationToken);
    }

    private static async Task<T?> ExecuteScalarAsync<T>(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        IReadOnlyCollection<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original placement failure.
        }
    }

    private static bool IsTicketNumberCollision(SqlException exception) =>
        exception.Number is 2601 or 2627
        && exception.Message.Contains("UX_Receipts_SerialCode", StringComparison.OrdinalIgnoreCase);

    private async Task AddReferenceTableAsync(
        string logicalName,
        IEnumerable<string> candidates,
        List<string> tableNames,
        List<string> missingTables,
        CancellationToken cancellationToken)
    {
        var table = await FindTableAsync(candidates, cancellationToken);
        if (table is null)
        {
            missingTables.Add(logicalName);
            return;
        }

        tableNames.Add(table);
    }

    private async Task<string?> FindDboTableAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'dbo' AND t.name = @tableName
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@tableName", tableName));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<bool> HasAnyRowsAsync(string qualifiedTableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = $"SELECT TOP (1) 1 FROM {qualifiedTableName}";
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private SqlConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("VIRTUAL_TICKETS_CONNECTION_STRING is not set.");
        }

        return new SqlConnection(_connectionString);
    }

    private static string? FirstExisting(HashSet<string> columns, IEnumerable<string> candidateColumns)
    {
        return candidateColumns.FirstOrDefault(columns.Contains);
    }

    private static string Quote(string identifier)
    {
        return $"[{identifier.Replace("]", "]]")}]";
    }

    private string GetSafeDatabaseErrorMessage(Exception exception)
    {
        return _environment.IsDevelopment()
            ? exception.Message
            : "The database could not be reached.";
    }
}

public sealed record DatabaseConnectionResult(bool CanConnect, string? Code, string? Message)
{
    public static DatabaseConnectionResult Connected() => new(true, null, null);

    public static DatabaseConnectionResult Failed(string code, string message) => new(false, code, message);
}

public sealed record ActiveSetResult(bool IsFound, long? SetNo)
{
    public static ActiveSetResult Found(long setNo) => new(true, setNo);

    public static ActiveSetResult NotFound() => new(false, null);
}

public sealed record ReferenceDataStateResult(bool SchemaValid, bool IsEmpty, IReadOnlyCollection<string> MissingTables)
{
    public static ReferenceDataStateResult Available() => new(true, false, []);

    public static ReferenceDataStateResult Empty() => new(true, true, []);

    public static ReferenceDataStateResult Invalid(IReadOnlyCollection<string> missingTables) => new(false, false, missingTables);
}

public sealed record ProbeResult(bool IsFound, bool IsUnknown, string? Detail)
{
    public static ProbeResult Found() => new(true, false, null);

    public static ProbeResult NotFound() => new(false, false, null);

    public static ProbeResult Unknown(string detail) => new(false, true, detail);
}

public sealed record ResolvedTicketSelection(TicketSelectionRequest Selection, long MatchId);

public sealed record TicketPlaceResult(
    bool IsPlaced,
    int? ReceiptId,
    Guid? Serial,
    string? TicketNumber,
    List<PlacedBetResponse> Bets,
    List<TicketValidationError> Errors)
{
    public static TicketPlaceResult Placed(int receiptId, Guid serial, string ticketNumber, List<PlacedBetResponse> bets) =>
        new(true, receiptId, serial, ticketNumber, bets, []);

    public static TicketPlaceResult Failed(List<TicketValidationError> errors) =>
        new(false, null, null, null, [], errors);
}
