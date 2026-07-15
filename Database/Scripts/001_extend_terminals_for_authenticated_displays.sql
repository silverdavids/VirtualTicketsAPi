/*
    Extends dbo.Terminals so existing terminal identity records can be reused
    for authenticated virtual display terminals.

    Secrets must be hashed before they are stored in SecretHash. Do not store
    plaintext display or terminal secrets in this table.
*/

IF OBJECT_ID(N'dbo.Terminals', N'U') IS NULL
BEGIN
    THROW 50001, 'dbo.Terminals does not exist.', 1;
END;

IF COL_LENGTH(N'dbo.Terminals', N'TerminalCode') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD TerminalCode NVARCHAR(50) NULL;
END;

IF COL_LENGTH(N'dbo.Terminals', N'TerminalType') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD TerminalType TINYINT NOT NULL
        CONSTRAINT DF_Terminals_TerminalType DEFAULT (1) WITH VALUES;
END;

IF COL_LENGTH(N'dbo.Terminals', N'SecretHash') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD SecretHash NVARCHAR(256) NULL;
END;

IF COL_LENGTH(N'dbo.Terminals', N'LastSeenAt') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD LastSeenAt DATETIME2 NULL;
END;

IF COL_LENGTH(N'dbo.Terminals', N'LastVersion') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD LastVersion NVARCHAR(30) NULL;
END;

IF COL_LENGTH(N'dbo.Terminals', N'SecretRotatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD SecretRotatedAt DATETIME2 NULL;
END;

IF COL_LENGTH(N'dbo.Terminals', N'UpdatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Terminals
    ADD UpdatedAt DATETIME2 NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_Terminals_TerminalCode'
      AND object_id = OBJECT_ID(N'dbo.Terminals', N'U')
)
BEGIN
    CREATE UNIQUE INDEX IX_Terminals_TerminalCode
    ON dbo.Terminals(TerminalCode)
    WHERE TerminalCode IS NOT NULL;
END;
