using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace VirtualTickets.Api.Data;

public sealed class TerminalAuthDb
{
    private const byte VirtualDisplayTerminalType = 1;
    private readonly string? _connectionString;
    private readonly PasswordHasher<TerminalAuthRecord> _passwordHasher;
    private readonly PasswordHasherOptions _passwordHasherOptions;
    private readonly ILogger<TerminalAuthDb> _logger;

    public TerminalAuthDb(
        ILogger<TerminalAuthDb> logger,
        IOptions<PasswordHasherOptions> passwordHasherOptions)
    {
        _connectionString = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_CONNECTION_STRING");
        _logger = logger;
        _passwordHasherOptions = passwordHasherOptions.Value;
        _passwordHasher = new PasswordHasher<TerminalAuthRecord>(passwordHasherOptions);
    }

    public async Task<TerminalAuthenticationResult> AuthenticateAsync(
        string terminalCode,
        string secret,
        string? version,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var normalizedTerminalCode = terminalCode.Trim();
        _logger.LogInformation(
            "[terminal-auth] Authentication requested for terminalCode '{TerminalCode}'.",
            normalizedTerminalCode);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var terminal = await FindTerminalAsync(connection, normalizedTerminalCode, cancellationToken);
        if (terminal is null)
        {
            _logger.LogWarning(
                "[terminal-auth] Terminal lookup failed. terminalCode '{TerminalCode}' was not found.",
                normalizedTerminalCode);
            return TerminalAuthenticationResult.Failed(TerminalAuthenticationFailureCode.TerminalNotFound, "Terminal not found.");
        }

        _logger.LogInformation(
            "[terminal-auth] Terminal row found. RequestedCode='{RequestedTerminalCode}', SqlTerminalCode='{SqlTerminalCode}', TerminalId={TerminalId}, IsActive={IsActive}, TerminalType={TerminalType}, SecretHashPresent={SecretHashPresent}.",
            normalizedTerminalCode,
            terminal.TerminalCode,
            terminal.TerminalId,
            terminal.IsActive,
            terminal.TerminalType,
            !string.IsNullOrWhiteSpace(terminal.SecretHash));

        if (!terminal.IsActive)
        {
            _logger.LogWarning("[terminal-auth] Terminal {TerminalId} rejected because it is inactive.", terminal.TerminalId);
            return TerminalAuthenticationResult.Failed(TerminalAuthenticationFailureCode.TerminalInactive, "Terminal inactive.");
        }

        if (terminal.TerminalType != VirtualDisplayTerminalType)
        {
            _logger.LogWarning(
                "[terminal-auth] Terminal {TerminalId} rejected because TerminalType {TerminalType} is not Virtual Display ({VirtualDisplayTerminalType}).",
                terminal.TerminalId,
                terminal.TerminalType,
                VirtualDisplayTerminalType);
            return TerminalAuthenticationResult.Failed(TerminalAuthenticationFailureCode.InvalidTerminalType, "Terminal is not a virtual display.");
        }

        if (string.IsNullOrWhiteSpace(terminal.SecretHash))
        {
            _logger.LogWarning("[terminal-auth] Terminal {TerminalId} rejected because SecretHash is missing.", terminal.TerminalId);
            return TerminalAuthenticationResult.Failed(TerminalAuthenticationFailureCode.SecretHashMissing, "Terminal secret hash is missing.");
        }

        LogPasswordHasherDetails(terminal.TerminalId);
        var verificationResult = _passwordHasher.VerifyHashedPassword(terminal, terminal.SecretHash, secret);
        _logger.LogInformation(
            "[terminal-auth] Password verification returned {PasswordVerificationResult} for TerminalId {TerminalId}.",
            verificationResult,
            terminal.TerminalId);

        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return TerminalAuthenticationResult.Failed(TerminalAuthenticationFailureCode.InvalidSecret, "Invalid secret.");
        }

        await UpdateTerminalMetadataAsync(connection, terminal.TerminalId, version, ipAddress, cancellationToken);
        _logger.LogInformation(
            "[terminal-auth] Terminal {TerminalId} authenticated successfully. Metadata updated.",
            terminal.TerminalId);

        return TerminalAuthenticationResult.Authenticated(terminal.ToIdentity());
    }

    private async Task<TerminalAuthRecord?> FindTerminalAsync(
        SqlConnection connection,
        string terminalCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                TerminalId,
                TerminalCode,
                TerminalName,
                BranchId,
                TerminalType,
                IsActive,
                SecretHash
            FROM dbo.Terminals
            WHERE UPPER(LTRIM(RTRIM(TerminalCode))) = UPPER(@terminalCode)
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@terminalCode", terminalCode));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TerminalAuthRecord(
            reader.GetInt32(reader.GetOrdinal("TerminalId")),
            reader.GetString(reader.GetOrdinal("TerminalCode")),
            reader.IsDBNull(reader.GetOrdinal("TerminalName")) ? string.Empty : reader.GetString(reader.GetOrdinal("TerminalName")),
            reader.GetInt32(reader.GetOrdinal("BranchId")),
            reader.GetByte(reader.GetOrdinal("TerminalType")),
            reader.GetBoolean(reader.GetOrdinal("IsActive")),
            reader.IsDBNull(reader.GetOrdinal("SecretHash")) ? null : reader.GetString(reader.GetOrdinal("SecretHash")));
    }

    private static async Task UpdateTerminalMetadataAsync(
        SqlConnection connection,
        int terminalId,
        string? version,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Terminals
            SET LastSeenAt = SYSUTCDATETIME(),
                LastVersion = @version,
                IpAddress = COALESCE(@ipAddress, IpAddress),
                UpdatedAt = SYSUTCDATETIME()
            WHERE TerminalId = @terminalId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@terminalId", terminalId));
        command.Parameters.Add(new SqlParameter("@version", (object?)version ?? DBNull.Value));
        command.Parameters.Add(new SqlParameter("@ipAddress", (object?)ipAddress ?? DBNull.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void LogPasswordHasherDetails(int terminalId)
    {
        var assembly = typeof(PasswordHasher<TerminalAuthRecord>).GetTypeInfo().Assembly.GetName();
        _logger.LogInformation(
            "[terminal-auth] PasswordHasher details for TerminalId {TerminalId}: Assembly={AssemblyName}, Version={AssemblyVersion}, CompatibilityMode={CompatibilityMode}, IterationCount={IterationCount}.",
            terminalId,
            assembly.Name,
            assembly.Version?.ToString() ?? "unknown",
            _passwordHasherOptions.CompatibilityMode,
            _passwordHasherOptions.IterationCount);
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
}

public sealed record TerminalAuthRecord(
    int TerminalId,
    string TerminalCode,
    string TerminalName,
    int BranchId,
    byte TerminalType,
    bool IsActive,
    string? SecretHash)
{
    public TerminalIdentity ToIdentity() => new(TerminalId, TerminalCode, TerminalName, BranchId, TerminalType);
}

public sealed record TerminalIdentity(
    int TerminalId,
    string TerminalCode,
    string TerminalName,
    int BranchId,
    byte TerminalType);

public enum TerminalAuthenticationFailureCode
{
    None,
    TerminalNotFound,
    TerminalInactive,
    InvalidTerminalType,
    SecretHashMissing,
    InvalidSecret
}

public sealed record TerminalAuthenticationResult(
    bool IsAuthenticated,
    TerminalIdentity? Terminal,
    TerminalAuthenticationFailureCode FailureCode,
    string? FailureMessage)
{
    public static TerminalAuthenticationResult Authenticated(TerminalIdentity terminal) =>
        new(true, terminal, TerminalAuthenticationFailureCode.None, null);

    public static TerminalAuthenticationResult Failed(
        TerminalAuthenticationFailureCode failureCode,
        string failureMessage) =>
        new(false, null, failureCode, failureMessage);
}
