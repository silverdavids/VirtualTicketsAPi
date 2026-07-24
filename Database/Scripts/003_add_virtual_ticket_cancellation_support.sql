/*
    Adds an auditable, one-cancellation-per-receipt ledger for virtual tickets.
    Receipt cancellation and audit insertion are performed in one serializable
    API transaction.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.VirtualTicketCancellations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.VirtualTicketCancellations
    (
        VirtualTicketCancellationId BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_VirtualTicketCancellations PRIMARY KEY,
        ReceiptId INT NOT NULL,
        TicketNumber VARCHAR(24) NOT NULL,
        OriginalReceiptStatus INT NOT NULL,
        ResultingReceiptStatus INT NOT NULL,
        TerminalId INT NOT NULL,
        TerminalCode NVARCHAR(100) NOT NULL,
        UserId VARCHAR(36) NOT NULL,
        BranchId INT NOT NULL,
        CancelReference VARCHAR(30) NOT NULL,
        ConfirmationReference VARCHAR(100) NULL,
        CancelledAtUtc DATETIME2(7) NOT NULL,
        Reason NVARCHAR(500) NOT NULL,
        CONSTRAINT FK_VirtualTicketCancellations_Receipts
            FOREIGN KEY (ReceiptId) REFERENCES dbo.Receipts(ReceiptId),
        CONSTRAINT FK_VirtualTicketCancellations_Terminals
            FOREIGN KEY (TerminalId) REFERENCES dbo.Terminals(TerminalId),
        CONSTRAINT FK_VirtualTicketCancellations_Branches
            FOREIGN KEY (BranchId) REFERENCES dbo.Branches(BranchId)
    );

    CREATE UNIQUE INDEX UX_VirtualTicketCancellations_ReceiptId
        ON dbo.VirtualTicketCancellations(ReceiptId);
    CREATE UNIQUE INDEX UX_VirtualTicketCancellations_CancelReference
        ON dbo.VirtualTicketCancellations(CancelReference);
END;

COMMIT TRANSACTION;
