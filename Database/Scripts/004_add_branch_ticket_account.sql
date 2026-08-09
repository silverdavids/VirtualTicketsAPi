/*
    Adds the explicitly designated receipt owner for VirtualDisplay sales.
    Existing branches intentionally remain unconfigured.
*/

IF COL_LENGTH(N'dbo.Branches', N'TicketAccountUserId') IS NULL
BEGIN
    ALTER TABLE dbo.Branches
    ADD TicketAccountUserId VARCHAR(36) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_Branches_TicketAccount_Accounts'
      AND parent_object_id = OBJECT_ID(N'dbo.Branches')
)
BEGIN
    ALTER TABLE dbo.Branches WITH CHECK
    ADD CONSTRAINT FK_Branches_TicketAccount_Accounts
        FOREIGN KEY (TicketAccountUserId) REFERENCES dbo.Accounts(UserId);

    ALTER TABLE dbo.Branches
    CHECK CONSTRAINT FK_Branches_TicketAccount_Accounts;
END;

/*
    Configure known branches only after verifying account ownership, for example:

    UPDATE b
    SET TicketAccountUserId = a.UserId
    FROM dbo.Branches b
    INNER JOIN dbo.Accounts a ON a.BranchId = b.BranchId
    WHERE b.BranchId = 2
      AND a.UserId = '<shop2test Accounts.UserId>';
*/
