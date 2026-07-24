/*
    Adds non-sequential printable ticket identification and an auditable,
    one-payment-per-receipt ledger. Historical NULL SerialCode values remain
    valid; all new virtual tickets are populated by the API.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_Receipts_SerialCode'
      AND object_id = OBJECT_ID(N'dbo.Receipts', N'U')
)
BEGIN
    CREATE UNIQUE INDEX UX_Receipts_SerialCode
        ON dbo.Receipts(SerialCode)
        WHERE SerialCode IS NOT NULL;
END;

IF OBJECT_ID(N'dbo.VirtualTicketPayouts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VirtualTicketPayouts
    (
        VirtualTicketPayoutId BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_VirtualTicketPayouts PRIMARY KEY,
        ReceiptId INT NOT NULL,
        TicketNumber VARCHAR(24) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        Currency VARCHAR(3) NOT NULL,
        OriginalReceiptStatus INT NOT NULL,
        ResultingReceiptStatus INT NOT NULL,
        TerminalId INT NOT NULL,
        TerminalCode NVARCHAR(100) NOT NULL,
        PaidByUserId VARCHAR(36) NOT NULL,
        BranchId INT NOT NULL,
        PayoutReference VARCHAR(30) NOT NULL,
        ConfirmationReference VARCHAR(100) NULL,
        PaidAtUtc DATETIME2(7) NOT NULL,
        CONSTRAINT FK_VirtualTicketPayouts_Receipts
            FOREIGN KEY (ReceiptId) REFERENCES dbo.Receipts(ReceiptId),
        CONSTRAINT FK_VirtualTicketPayouts_Terminals
            FOREIGN KEY (TerminalId) REFERENCES dbo.Terminals(TerminalId),
        CONSTRAINT FK_VirtualTicketPayouts_Branches
            FOREIGN KEY (BranchId) REFERENCES dbo.Branches(BranchId),
        CONSTRAINT CK_VirtualTicketPayouts_Amount_Positive CHECK (Amount > 0)
    );

    CREATE UNIQUE INDEX UX_VirtualTicketPayouts_ReceiptId
        ON dbo.VirtualTicketPayouts(ReceiptId);
    CREATE UNIQUE INDEX UX_VirtualTicketPayouts_PayoutReference
        ON dbo.VirtualTicketPayouts(PayoutReference);
END;

IF COL_LENGTH(N'dbo.VirtualTicketPayouts', N'PaidByUserId') IS NULL
   AND COL_LENGTH(N'dbo.VirtualTicketPayouts', N'UserId') IS NOT NULL
BEGIN
    EXEC sys.sp_rename
        N'dbo.VirtualTicketPayouts.UserId',
        N'PaidByUserId',
        N'COLUMN';
END;

COMMIT TRANSACTION;
