SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Receipts', N'TerminalId') IS NULL
BEGIN
    ALTER TABLE dbo.Receipts ADD TerminalId INT NULL;
END;

IF COL_LENGTH(N'dbo.Receipts', N'ExternalTicketId') IS NULL
BEGIN
    ALTER TABLE dbo.Receipts ADD ExternalTicketId VARCHAR(100) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.Receipts')
      AND name = N'FK_Receipts_Terminals_TerminalId'
)
BEGIN
    ALTER TABLE dbo.Receipts WITH CHECK
        ADD CONSTRAINT FK_Receipts_Terminals_TerminalId
        FOREIGN KEY (TerminalId) REFERENCES dbo.Terminals(TerminalId);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Receipts')
      AND name = N'UX_Receipts_Terminal_ExternalTicketId'
)
BEGIN
    CREATE UNIQUE INDEX UX_Receipts_Terminal_ExternalTicketId
        ON dbo.Receipts (TerminalId, ExternalTicketId)
        WHERE TerminalId IS NOT NULL AND ExternalTicketId IS NOT NULL;
END;

COMMIT TRANSACTION;

-- Verification: expected result is zero rows.
SELECT TerminalId, ExternalTicketId, COUNT(*) AS ReceiptCount
FROM dbo.Receipts
WHERE ExternalTicketId IS NOT NULL
GROUP BY TerminalId, ExternalTicketId
HAVING COUNT(*) > 1;
