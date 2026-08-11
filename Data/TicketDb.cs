using Microsoft.Data.SqlClient;
using System.Data;
using VirtualTickets.Api.Contracts;
using VirtualTickets.Api.Services;

namespace VirtualTickets.Api.Data;

public sealed class TicketDb
{
    private const int ExternalTicketIdMaxLength = 100;
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
        TerminalTicketIdentity? terminalIdentity,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                terminalIdentity is null ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                cancellationToken);
            try
            {
            var errors = new List<TicketValidationError>();
            int? branchId;
            string? receiptUserId;
            string? shopDisplayName;
            if (terminalIdentity is not null)
            {
                var existing = await FindExistingTerminalPlacementAsync(
                    connection,
                    transaction,
                    terminalIdentity.TerminalId,
                    request.ExternalTicketId!,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }

                var authoritativeSelections = await ValidateVirtualBoardAsync(
                    connection, transaction, request, cancellationToken);
                if (authoritativeSelections.Errors.Count > 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return TicketPlaceResult.Failed(authoritativeSelections.Errors);
                }

                var ownership = await ResolveTerminalTicketOwnershipAsync(
                    connection, transaction, terminalIdentity, cancellationToken);
                if (!ownership.IsResolved)
                {
                    errors.Add(new TicketValidationError
                    {
                        Code = ownership.ErrorCode!,
                        Field = "branch",
                        Message = ownership.ErrorMessage!
                    });
                }

                branchId = ownership.BranchId;
                receiptUserId = ownership.UserId;
                shopDisplayName = ownership.ShopDisplayName;

                if (errors.Count == 0)
                {
                    var terminalSerial = Guid.NewGuid();
                    var terminalTicketNumber = TicketNumber.Generate();
                    var terminalReceipt = await InsertReceiptAsync(
                        connection, transaction, request, activeSetNo, branchId!.Value,
                        receiptUserId, "VirtualDisplay", terminalIdentity.TerminalId,
                        request.ExternalTicketId, terminalSerial, terminalTicketNumber, cancellationToken);
                    var terminalBets = new List<PlacedBetResponse>();
                    foreach (var selection in authoritativeSelections.Selections)
                    {
                        var betId = await InsertBetAsync(connection, transaction,
                            terminalReceipt.ReceiptId, selection, cancellationToken);
                        terminalBets.Add(new PlacedBetResponse
                        {
                            BetId = betId,
                            MatchId = selection.MatchId,
                            Odd = selection.Selection.Odd
                        });
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return TicketPlaceResult.Placed(
                        terminalReceipt.ReceiptId, terminalSerial, terminalTicketNumber, shopDisplayName,
                        terminalReceipt.BookedAtUtc, terminalBets, activeSetNo);
                }
            }
            else
            {
                branchId = await ResolveBranchIdAsync(connection, transaction, request, cancellationToken);
                receiptUserId = request.UserId;
                shopDisplayName = null;
            }

            if (!branchId.HasValue && terminalIdentity is null)
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
            var insertedReceipt = await InsertReceiptAsync(
                connection,
                transaction,
                request,
                activeSetNo,
                branchId!.Value,
                receiptUserId,
                terminalIdentity is null ? request.Source : "VirtualDisplay",
                terminalIdentity?.TerminalId,
                terminalIdentity is null ? null : request.ExternalTicketId,
                serial,
                ticketNumber,
                cancellationToken);
            var receiptId = insertedReceipt.ReceiptId;

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
                return TicketPlaceResult.Placed(
                    receiptId,
                    serial,
                    ticketNumber,
                    shopDisplayName,
                    insertedReceipt.BookedAtUtc,
                    placedBets,
                    activeSetNo);
            }
            catch (SqlException exception) when (
                terminalIdentity is not null &&
                IsExternalTicketIdCollision(exception))
            {
                await RollbackQuietlyAsync(transaction, cancellationToken);
                var existing = await FindExistingTerminalPlacementAsync(
                    terminalIdentity, request.ExternalTicketId!, cancellationToken);
                if (existing is not null)
                {
                    _logger.LogInformation(
                        "Resolved concurrent ticket placement retry for TerminalId {TerminalId} and ExternalTicketId {ExternalTicketId}.",
                        terminalIdentity.TerminalId,
                        request.ExternalTicketId);
                    return existing;
                }

                _logger.LogError(exception, "Idempotent ticket collision occurred but the original receipt could not be loaded.");
                return TicketPlaceResult.Failed([new TicketValidationError
                {
                    Code = "ticket_retry_resolution_failed",
                    Field = "externalTicketId",
                    Message = "The original ticket placement could not be resolved."
                }]);
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

    public async Task<TicketPlaceResult?> FindExistingTerminalPlacementAsync(
        TerminalTicketIdentity identity,
        string externalTicketId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var result = await FindExistingTerminalPlacementAsync(
            connection, transaction, identity.TerminalId, externalTicketId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<TicketPlaceResult?> FindExistingTerminalPlacementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int terminalId,
        string externalTicketId,
        CancellationToken cancellationToken)
    {
        const string receiptSql = """
            SELECT r.ReceiptId, r.Serial, r.SerialCode, r.CreatedOnUtc, r.SetNo,
                   CASE WHEN b.BranchName IS NULL OR t.TerminalCode IS NULL THEN NULL
                        ELSE b.BranchName + N'-' + t.TerminalCode END AS ShopDisplayName
            FROM dbo.Receipts r
            LEFT JOIN dbo.Branches b ON b.BranchId = r.BranchId
            LEFT JOIN dbo.Terminals t ON t.TerminalId = r.TerminalId
            WHERE r.TerminalId = @terminalId AND r.ExternalTicketId = @externalTicketId
            """;
        await using var receiptCommand = new SqlCommand(receiptSql, connection, transaction);
        receiptCommand.Parameters.Add(new SqlParameter("@terminalId", terminalId));
        receiptCommand.Parameters.Add(new SqlParameter("@externalTicketId", System.Data.SqlDbType.VarChar, ExternalTicketIdMaxLength)
        {
            Value = externalTicketId
        });

        int receiptId;
        Guid serial;
        string ticketNumber;
        DateTime bookedAtUtc;
        long? activeSetNo;
        string? shopDisplayName;
        await using (var reader = await receiptCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            receiptId = reader.GetInt32(reader.GetOrdinal("ReceiptId"));
            serial = reader.GetGuid(reader.GetOrdinal("Serial"));
            ticketNumber = reader.GetString(reader.GetOrdinal("SerialCode"));
            bookedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedOnUtc")), DateTimeKind.Utc);
            activeSetNo = reader.IsDBNull(reader.GetOrdinal("SetNo")) ? null : Convert.ToInt64(reader["SetNo"]);
            shopDisplayName = reader.IsDBNull(reader.GetOrdinal("ShopDisplayName"))
                ? null : reader.GetString(reader.GetOrdinal("ShopDisplayName"));
        }

        const string betsSql = """
            SELECT BetId, MatchId, BetOdd
            FROM dbo.Bets
            WHERE RecieptId = @receiptId
            ORDER BY BetId
            """;
        await using var betsCommand = new SqlCommand(betsSql, connection, transaction);
        betsCommand.Parameters.Add(new SqlParameter("@receiptId", receiptId));
        var bets = new List<PlacedBetResponse>();
        await using var betsReader = await betsCommand.ExecuteReaderAsync(cancellationToken);
        while (await betsReader.ReadAsync(cancellationToken))
        {
            bets.Add(new PlacedBetResponse
            {
                BetId = betsReader.GetInt32(betsReader.GetOrdinal("BetId")),
                MatchId = Convert.ToInt64(betsReader["MatchId"]),
                Odd = Convert.ToDecimal(betsReader["BetOdd"])
            });
        }

        return TicketPlaceResult.Placed(
            receiptId, serial, ticketNumber, shopDisplayName, bookedAtUtc, bets, activeSetNo);
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

    private static async Task<TerminalTicketOwnership> ResolveTerminalTicketOwnershipAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TerminalTicketIdentity identity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                t.BranchId,
                b.BranchName,
                t.TerminalCode,
                b.TicketAccountUserId,
                a.UserId AS VerifiedAccountUserId
            FROM dbo.Terminals t
            INNER JOIN dbo.Branches b ON b.BranchId = t.BranchId
            LEFT JOIN dbo.Accounts a
                ON a.UserId = b.TicketAccountUserId
               AND a.BranchId = t.BranchId
            WHERE t.TerminalId = @terminalId
              AND UPPER(LTRIM(RTRIM(t.TerminalCode))) = UPPER(@terminalCode)
              AND t.IsActive = 1
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@terminalId", identity.TerminalId));
        command.Parameters.Add(new SqlParameter("@terminalCode", identity.TerminalCode));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return TerminalTicketOwnership.InvalidTerminal();
        }

        var databaseBranchId = reader.GetInt32(reader.GetOrdinal("BranchId"));
        var branchName = reader.GetString(reader.GetOrdinal("BranchName")).Trim();
        var terminalCode = reader.GetString(reader.GetOrdinal("TerminalCode")).Trim();
        var configuredUserId = reader.IsDBNull(reader.GetOrdinal("TicketAccountUserId"))
            ? null
            : reader.GetString(reader.GetOrdinal("TicketAccountUserId"));
        var verifiedUserId = reader.IsDBNull(reader.GetOrdinal("VerifiedAccountUserId"))
            ? null
            : reader.GetString(reader.GetOrdinal("VerifiedAccountUserId"));

        return TicketOwnershipPolicy.Resolve(identity.BranchId, databaseBranchId, configuredUserId, verifiedUserId)
            .WithShopDisplayName($"{branchName}-{terminalCode}");
    }

    private static async Task<AuthoritativeSelectionValidation> ValidateVirtualBoardAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TicketValidateRequest request,
        CancellationToken cancellationToken)
    {
        var requestedProviderEventId = request.ProviderEventId?.Trim();
        if (!string.Equals(request.Provider?.Trim(), "VirtualHorizon", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(requestedProviderEventId))
        {
            return AuthoritativeSelectionValidation.Failed(BoardChanged());
        }

        const string boardSql = """
            SELECT b.Id AS VirtualBoardId, b.ProviderEventId,
                   CASE WHEN b.Status <> 0 OR b.HasResults = 1
                                  OR (b.EndAtUtc IS NOT NULL AND b.EndAtUtc <= SYSUTCDATETIME())
                        THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsExpired
            FROM dbo.VirtualBoards b WITH (UPDLOCK, HOLDLOCK)
            WHERE b.Provider = N'VirtualHorizon'
              AND b.ProviderEventId = @providerEventId
            """;

        long virtualBoardId;
        string submittedProviderEventId;
        bool isExpired;
        await using (var boardCommand = new SqlCommand(boardSql, connection, transaction))
        {
            boardCommand.Parameters.Add(new SqlParameter("@providerEventId", requestedProviderEventId));
            await using var reader = await boardCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return AuthoritativeSelectionValidation.Failed(BoardChanged());
            }

            virtualBoardId = reader.GetInt64(reader.GetOrdinal("VirtualBoardId"));
            submittedProviderEventId = reader.GetString(reader.GetOrdinal("ProviderEventId"));
            isExpired = reader.GetBoolean(reader.GetOrdinal("IsExpired"));
        }

        if (isExpired)
        {
            return AuthoritativeSelectionValidation.Failed(new TicketValidationError
            {
                Code = "board_expired",
                Field = "providerEventId",
                Message = "The current VirtualHorizon board is closed for ticket placement."
            });
        }

        var resolved = new List<ResolvedTicketSelection>(request.Selections.Count);
        var errors = new List<TicketValidationError>();
        for (var index = 0; index < request.Selections.Count; index++)
        {
            var selection = request.Selections[index];
            const string selectionSql = """
                SELECT TOP (1)
                    boardMatch.BetServiceMatchNo,
                    snapshot.MatchOddId,
                    COALESCE(currentOdd.Odd, snapshot.Odd) AS Odd
                FROM dbo.VirtualBoardMatchesMap boardMatch WITH (HOLDLOCK)
                INNER JOIN dbo.VirtualBoardSelections snapshot WITH (HOLDLOCK)
                    ON snapshot.VirtualBoardId = boardMatch.VirtualBoardId
                   AND snapshot.ProviderMatchId = boardMatch.ProviderMatchId
                   AND snapshot.BetServiceMatchNo = boardMatch.BetServiceMatchNo
                LEFT JOIN dbo.MatchOdds currentOdd WITH (UPDLOCK, HOLDLOCK)
                    ON currentOdd.BetServiceMatchNo = boardMatch.BetServiceMatchNo
                   AND currentOdd.MatchOddId = snapshot.MatchOddId
                WHERE boardMatch.VirtualBoardId = @virtualBoardId
                  AND boardMatch.ProviderMatchId = @providerMatchId
                  AND snapshot.ProviderEventId = @providerEventId
                  AND snapshot.Market = @market
                  AND snapshot.[Option] = @option
                  AND ((@lineValue IS NULL AND NULLIF(LTRIM(RTRIM(snapshot.Line)), N'') IS NULL)
                       OR TRY_CONVERT(decimal(18, 6), snapshot.Line) = @lineValue)
                  AND snapshot.IsActive = 1
                  AND (currentOdd.MatchOddId IS NULL OR ISNULL(currentOdd.IsLocked, 0) = 0)
                  AND (@matchOddId IS NULL OR snapshot.MatchOddId = @matchOddId)
                """;
            await using var command = new SqlCommand(selectionSql, connection, transaction);
            command.Parameters.Add(new SqlParameter("@virtualBoardId", virtualBoardId));
            command.Parameters.Add(new SqlParameter("@providerEventId", submittedProviderEventId));
            command.Parameters.Add(new SqlParameter("@providerMatchId", (object?)selection.ProviderMatchId?.Trim() ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@market", (object?)selection.Market?.Trim() ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@option", (object?)selection.Option?.Trim() ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@lineValue", (object?)selection.Line ?? DBNull.Value));
            command.Parameters.Add(new SqlParameter("@matchOddId", (object?)selection.MatchOddId ?? DBNull.Value));

            long matchId;
            decimal currentOdd;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    errors.Add(new TicketValidationError
                    {
                        Code = "selection_not_available",
                        Field = $"selections[{index}]",
                        SelectionIndex = index,
                        Message = "The requested selection is not available on the submitted VirtualHorizon board."
                    });
                    continue;
                }

                matchId = Convert.ToInt64(reader["BetServiceMatchNo"]);
                currentOdd = Convert.ToDecimal(reader["Odd"]);
            }

            if (!VirtualBoardSelectionPolicy.OddsMatch(selection.Odd, currentOdd))
            {
                errors.Add(new TicketValidationError
                {
                    Code = "odds_changed",
                    Field = $"selections[{index}].odd",
                    SelectionIndex = index,
                    CurrentOdd = currentOdd,
                    Message = "The selection odd has changed. Refresh the betslip before placing."
                });
                continue;
            }

            resolved.Add(new ResolvedTicketSelection(selection, matchId));
        }

        return errors.Count == 0
            ? AuthoritativeSelectionValidation.Succeeded(resolved)
            : new AuthoritativeSelectionValidation([], errors);

        static TicketValidationError BoardChanged() => new()
        {
            Code = "board_changed",
            Field = "providerEventId",
            Message = "The VirtualHorizon board has changed. Refresh before placing."
        };
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

    private async Task<InsertedReceipt> InsertReceiptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TicketValidateRequest request,
        long activeSetNo,
        int branchId,
        string? receiptUserId,
        string? paymentSource,
        int? terminalId,
        string? externalTicketId,
        Guid serial,
        string ticketNumber,
        CancellationToken cancellationToken)
    {
        var totalOdds = request.Selections.Aggregate(1m, (current, selection) => current * selection.Odd);
        var now = DateTime.Now;
        var utcNow = DateTime.UtcNow;

        return await ExecuteInsertedReceiptAsync(
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
                TerminalId,
                ExternalTicketId,
                ModifiedOn,
                ModifiedOnUtc
            )
            OUTPUT INSERTED.ReceiptId, INSERTED.CreatedOnUtc
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
                @terminalId,
                @externalTicketId,
                @createdOn,
                @createdOnUtc
            )
            """,
            [
                new("@userId", (object?)receiptUserId ?? DBNull.Value),
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
                new("@paymentSource", (object?)paymentSource ?? DBNull.Value),
                new("@terminalId", (object?)terminalId ?? DBNull.Value),
                new SqlParameter("@externalTicketId", System.Data.SqlDbType.VarChar, ExternalTicketIdMaxLength)
                {
                    Value = (object?)externalTicketId ?? DBNull.Value
                }
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

    private static async Task<InsertedReceipt> ExecuteInsertedReceiptAsync(
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Receipt insert did not return the persisted receipt timestamp.");
        }

        return new InsertedReceipt(
            reader.GetInt32(reader.GetOrdinal("ReceiptId")),
            DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedOnUtc")), DateTimeKind.Utc));
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

    private static bool IsExternalTicketIdCollision(SqlException exception) =>
        exception.Number is 2601 or 2627
        && exception.Message.Contains("UX_Receipts_Terminal_ExternalTicketId", StringComparison.OrdinalIgnoreCase);

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

public sealed record AuthoritativeSelectionValidation(
    List<ResolvedTicketSelection> Selections,
    List<TicketValidationError> Errors)
{
    public static AuthoritativeSelectionValidation Succeeded(List<ResolvedTicketSelection> selections) =>
        new(selections, []);

    public static AuthoritativeSelectionValidation Failed(TicketValidationError error) =>
        new([], [error]);
}

public sealed record TicketPlaceResult(
    bool IsPlaced,
    int? ReceiptId,
    Guid? Serial,
    string? TicketNumber,
    string? ShopDisplayName,
    DateTime? BookedAtUtc,
    List<PlacedBetResponse> Bets,
    List<TicketValidationError> Errors,
    long? ActiveSetNo)
{
    public static TicketPlaceResult Placed(
        int receiptId,
        Guid serial,
        string ticketNumber,
        string? shopDisplayName,
        DateTime bookedAtUtc,
        List<PlacedBetResponse> bets,
        long? activeSetNo) =>
        new(true, receiptId, serial, ticketNumber, shopDisplayName, bookedAtUtc, bets, [], activeSetNo);

    public static TicketPlaceResult Failed(List<TicketValidationError> errors) =>
        new(false, null, null, null, null, null, [], errors, null);
}

public sealed record InsertedReceipt(int ReceiptId, DateTime BookedAtUtc);
