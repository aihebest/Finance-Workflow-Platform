IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [Requests] (
        [RequestId] uniqueidentifier NOT NULL,
        [RequestNumber] nvarchar(30) NOT NULL,
        [ModuleKey] nvarchar(50) NOT NULL,
        [FormCode] nvarchar(20) NOT NULL,
        [FormRevision] nvarchar(10) NOT NULL,
        [TreasuryNumber] nvarchar(50) NULL,
        [JournalVoucherNumber] nvarchar(50) NULL,
        [CurrentState] nvarchar(50) NOT NULL,
        [CurrentActorId] uniqueidentifier NULL,
        [StateEnteredAt] datetime2 NOT NULL,
        [SlaDueAt] datetime2 NULL,
        [EscalationCount] int NOT NULL,
        [ReminderCount] int NOT NULL,
        [RevisionNumber] int NOT NULL,
        [RequesterId] uniqueidentifier NOT NULL,
        [DepartmentId] int NOT NULL,
        [TotalAmountNgn] decimal(18,2) NOT NULL,
        [SubmittedAt] datetime2 NULL,
        [ClosedAt] datetime2 NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Requests] PRIMARY KEY ([RequestId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [AuditEventId] bigint NOT NULL IDENTITY,
        [RequestId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(50) NOT NULL,
        [FromState] nvarchar(50) NULL,
        [ToState] nvarchar(50) NULL,
        [ActorId] uniqueidentifier NOT NULL,
        [ActorRole] nvarchar(50) NOT NULL,
        [OnBehalfOfUserId] uniqueidentifier NULL,
        [Reason] nvarchar(1000) NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [ClientIpAddress] nvarchar(45) NULL,
        [CorrelationId] nvarchar(100) NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [PreviousHash] varbinary(32) NULL,
        [EventHash] varbinary(32) NOT NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([AuditEventId]),
        CONSTRAINT [FK_AuditEvents_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [CashAdvanceRequests] (
        [RequestId] uniqueidentifier NOT NULL,
        [Purpose] nvarchar(500) NOT NULL,
        [AllocationType] nvarchar(20) NOT NULL,
        [ProjectCode] nvarchar(30) NULL,
        [CostCentreCode] nvarchar(30) NULL,
        [StationScope] nvarchar(20) NOT NULL,
        [HasSupportingDocuments] bit NOT NULL,
        [AmountInWords] nvarchar(max) NOT NULL,
        [CashReleasedAt] datetime2 NULL,
        [AcknowledgedAt] datetime2 NULL,
        [AcknowledgedByUserId] uniqueidentifier NULL,
        [RetirementDueDate] datetime2 NULL,
        [RetirementStatus] nvarchar(20) NOT NULL,
        [PostedByUserId] uniqueidentifier NULL,
        [AuthorisedByUserId] uniqueidentifier NULL,
        [RetiredAmountNgn] decimal(18,2) NOT NULL,
        CONSTRAINT [PK_CashAdvanceRequests] PRIMARY KEY ([RequestId]),
        CONSTRAINT [CK_Advance_Allocation] CHECK ((([AllocationType] = 'Project' AND [ProjectCode] IS NOT NULL) OR ([AllocationType] = 'CostCentre' AND [CostCentreCode] IS NOT NULL))),
        CONSTRAINT [CK_MakerChecker_Advance] CHECK (([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR [PostedByUserId] <> [AuthorisedByUserId])),
        CONSTRAINT [CK_RetirementDue] CHECK (([RetirementDueDate] IS NULL OR ([CashReleasedAt] IS NOT NULL AND [RetirementDueDate] >= [CashReleasedAt]))),
        CONSTRAINT [FK_CashAdvanceRequests_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [GlPostingLines] (
        [PostingLineId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [Side] nvarchar(10) NOT NULL,
        [AccountNumber] nvarchar(20) NOT NULL,
        [Narration] nvarchar(500) NULL,
        [AmountNgn] decimal(18,2) NOT NULL,
        CONSTRAINT [PK_GlPostingLines] PRIMARY KEY ([PostingLineId]),
        CONSTRAINT [FK_GlPostingLines_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [AdvanceLines] (
        [LineId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [CurrencyCode] char(3) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [FxRate] decimal(18,6) NOT NULL,
        [FxRateDate] date NOT NULL,
        [AmountNgn] decimal(18,2) NOT NULL,
        CONSTRAINT [PK_AdvanceLines] PRIMARY KEY ([LineId]),
        CONSTRAINT [CK_Currency_AdvanceLine] CHECK (([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)),
        CONSTRAINT [FK_AdvanceLines_CashAdvanceRequests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [CashAdvanceRequests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [ExpenseRequests] (
        [RequestId] uniqueidentifier NOT NULL,
        [BeneficiaryId] uniqueidentifier NOT NULL,
        [RetiresAdvanceId] uniqueidentifier NULL,
        [AdvanceAmountNgn] decimal(18,2) NOT NULL,
        [ReceiptStatus] nvarchar(20) NOT NULL,
        [PaymentMethod] nvarchar(20) NOT NULL,
        [AmountInWords] nvarchar(200) NOT NULL,
        [PostedByUserId] uniqueidentifier NULL,
        [AuthorisedByUserId] uniqueidentifier NULL,
        [PostedAt] datetime2 NULL,
        [PaymentReference] nvarchar(50) NULL,
        [PaymentDate] datetime2 NULL,
        [AcknowledgedByUserId] uniqueidentifier NULL,
        [AcknowledgedAt] datetime2 NULL,
        [RefundReceivedAmountNgn] decimal(18,2) NULL,
        CONSTRAINT [PK_ExpenseRequests] PRIMARY KEY ([RequestId]),
        CONSTRAINT [CK_MakerChecker_Expense] CHECK (([PostedByUserId] IS NULL OR [AuthorisedByUserId] IS NULL OR [PostedByUserId] <> [AuthorisedByUserId])),
        CONSTRAINT [FK_ExpenseRequests_CashAdvanceRequests_RetiresAdvanceId] FOREIGN KEY ([RetiresAdvanceId]) REFERENCES [CashAdvanceRequests] ([RequestId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ExpenseRequests_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE TABLE [ExpenseLines] (
        [LineId] uniqueidentifier NOT NULL,
        [RequestId] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [ExpenseDate] date NOT NULL,
        [ExpenseCategoryId] int NULL,
        [ProjectCode] nvarchar(30) NULL,
        [CostCentreCode] nvarchar(30) NULL,
        [CurrencyCode] char(3) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [FxRate] decimal(18,6) NOT NULL,
        [FxRateDate] date NOT NULL,
        [AmountNgn] decimal(18,2) NOT NULL,
        CONSTRAINT [PK_ExpenseLines] PRIMARY KEY ([LineId]),
        CONSTRAINT [CK_Currency_ExpenseLine] CHECK (([CurrencyCode] <> 'NGN' OR [FxRate] = 1.0)),
        CONSTRAINT [CK_ExpenseLine_Allocation] CHECK ((([ProjectCode] IS NOT NULL AND [CostCentreCode] IS NULL) OR ([ProjectCode] IS NULL AND [CostCentreCode] IS NOT NULL))),
        CONSTRAINT [FK_ExpenseLines_ExpenseRequests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [ExpenseRequests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AdvanceLines_RequestId] ON [AdvanceLines] ([RequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_RequestId] ON [AuditEvents] ([RequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Advance_Overdue] ON [CashAdvanceRequests] ([RetirementStatus], [RetirementDueDate]) INCLUDE ([RetiredAmountNgn]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ExpenseLines_RequestId] ON [ExpenseLines] ([RequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ExpenseRequests_RetiresAdvanceId] ON [ExpenseRequests] ([RetiresAdvanceId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_GlPostingLines_RequestId] ON [GlPostingLines] ([RequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Request_Actor_State] ON [Requests] ([CurrentActorId], [CurrentState]) INCLUDE ([RequestNumber], [SlaDueAt], [TotalAmountNgn]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Request_Ageing] ON [Requests] ([ModuleKey], [CurrentState], [SubmittedAt]) INCLUDE ([TotalAmountNgn], [DepartmentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Request_Sla_Breach] ON [Requests] ([SlaDueAt]) WHERE [ClosedAt] IS NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_RequestNumber] ON [Requests] ([RequestNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260731213220_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260731213220_InitialCreate', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    DROP INDEX [IX_AuditEvents_RequestId] ON [AuditEvents];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    ALTER TABLE [AuditEvents] ADD [IdempotencyKey] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [OutboxMessageId] bigint NOT NULL IDENTITY,
        [RequestId] uniqueidentifier NOT NULL,
        [Template] nvarchar(100) NOT NULL,
        [RecipientRolesJson] nvarchar(max) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [DispatchedAt] datetime2 NULL,
        [AttemptCount] int NOT NULL,
        [LastError] nvarchar(2000) NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([OutboxMessageId]),
        CONSTRAINT [FK_OutboxMessages_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UQ_AuditEvent_Idempotency] ON [AuditEvents] ([RequestId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    CREATE INDEX [IX_Outbox_Pending] ON [OutboxMessages] ([Status], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_RequestId] ON [OutboxMessages] ([RequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801030808_AddOutboxAndIdempotency'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801030808_AddOutboxAndIdempotency', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE TABLE [Departments] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(20) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [DepartmentHeadId] uniqueidentifier NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Departments] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE TABLE [SecurityEvents] (
        [SecurityEventId] bigint NOT NULL IDENTITY,
        [RequestId] uniqueidentifier NOT NULL,
        [ModuleKey] nvarchar(50) NOT NULL,
        [Action] nvarchar(50) NOT NULL,
        [FromState] nvarchar(50) NULL,
        [Reason] nvarchar(50) NOT NULL,
        [Detail] nvarchar(1000) NULL,
        [AttemptedByUserId] uniqueidentifier NOT NULL,
        [OnBehalfOfUserId] uniqueidentifier NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_SecurityEvents] PRIMARY KEY ([SecurityEventId]),
        CONSTRAINT [FK_SecurityEvents_Requests_RequestId] FOREIGN KEY ([RequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE TABLE [Employees] (
        [Id] uniqueidentifier NOT NULL,
        [EntraObjectId] nvarchar(100) NOT NULL,
        [StaffNumber] nvarchar(30) NOT NULL,
        [FullName] nvarchar(200) NOT NULL,
        [Email] nvarchar(200) NOT NULL,
        [Grade] nvarchar(50) NULL,
        [DepartmentId] int NOT NULL,
        [LineManagerId] uniqueidentifier NULL,
        [DefaultCostCentreCode] nvarchar(30) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Employees] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Employees_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Employees_Employees_LineManagerId] FOREIGN KEY ([LineManagerId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE TABLE [Beneficiaries] (
        [Id] uniqueidentifier NOT NULL,
        [Type] nvarchar(20) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [BankName] nvarchar(100) NOT NULL,
        [BankAccountNumber] nvarchar(30) NOT NULL,
        [EmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_Beneficiaries] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Beneficiary_EmployeeLink] CHECK (([Type] <> 'Employee' OR [EmployeeId] IS NOT NULL)),
        CONSTRAINT [FK_Beneficiaries_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE TABLE [Delegations] (
        [Id] uniqueidentifier NOT NULL,
        [FromEmployeeId] uniqueidentifier NOT NULL,
        [ToEmployeeId] uniqueidentifier NOT NULL,
        [StartsAt] datetime2 NOT NULL,
        [EndsAt] datetime2 NOT NULL,
        [Reason] nvarchar(500) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Delegations] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Delegation_Window] CHECK (([EndsAt] >= [StartsAt])),
        CONSTRAINT [FK_Delegations_Employees_FromEmployeeId] FOREIGN KEY ([FromEmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Delegations_Employees_ToEmployeeId] FOREIGN KEY ([ToEmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Requests_DepartmentId] ON [Requests] ([DepartmentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Requests_RequesterId] ON [Requests] ([RequesterId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_ExpenseRequests_BeneficiaryId] ON [ExpenseRequests] ([BeneficiaryId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Beneficiaries_EmployeeId] ON [Beneficiaries] ([EmployeeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Delegation_Active_From] ON [Delegations] ([IsActive], [FromEmployeeId], [StartsAt], [EndsAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Delegations_FromEmployeeId] ON [Delegations] ([FromEmployeeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Delegations_ToEmployeeId] ON [Delegations] ([ToEmployeeId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Department_Active] ON [Departments] ([IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Department_Code] ON [Departments] ([Code]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Employee_Department_Active] ON [Employees] ([DepartmentId], [IsActive]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_Employee_LineManager] ON [Employees] ([LineManagerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Employee_EntraObjectId] ON [Employees] ([EntraObjectId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_Employee_StaffNumber] ON [Employees] ([StaffNumber]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_SecurityEvent_Actor] ON [SecurityEvents] ([AttemptedByUserId], [OccurredAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    CREATE INDEX [IX_SecurityEvent_Request] ON [SecurityEvents] ([RequestId], [OccurredAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    ALTER TABLE [ExpenseRequests] ADD CONSTRAINT [FK_ExpenseRequests_Beneficiaries_BeneficiaryId] FOREIGN KEY ([BeneficiaryId]) REFERENCES [Beneficiaries] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    ALTER TABLE [Requests] ADD CONSTRAINT [FK_Requests_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    ALTER TABLE [Requests] ADD CONSTRAINT [FK_Requests_Employees_CurrentActorId] FOREIGN KEY ([CurrentActorId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    ALTER TABLE [Requests] ADD CONSTRAINT [FK_Requests_Employees_RequesterId] FOREIGN KEY ([RequesterId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801034852_AddPeopleSecurityAndRequestForeignKeys'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801034852_AddPeopleSecurityAndRequestForeignKeys', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801041840_AddAdvanceRetirementLinks'
)
BEGIN
    CREATE TABLE [AdvanceRetirementLinks] (
        [Id] uniqueidentifier NOT NULL,
        [ExpenseRequestId] uniqueidentifier NOT NULL,
        [CashAdvanceRequestId] uniqueidentifier NOT NULL,
        [AmountAppliedNgn] decimal(18,2) NOT NULL,
        [AppliedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AdvanceRetirementLinks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdvanceRetirementLinks_Requests_CashAdvanceRequestId] FOREIGN KEY ([CashAdvanceRequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_AdvanceRetirementLinks_Requests_ExpenseRequestId] FOREIGN KEY ([ExpenseRequestId]) REFERENCES [Requests] ([RequestId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801041840_AddAdvanceRetirementLinks'
)
BEGIN
    CREATE INDEX [IX_AdvanceRetirementLink_CashAdvanceRequest] ON [AdvanceRetirementLinks] ([CashAdvanceRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801041840_AddAdvanceRetirementLinks'
)
BEGIN
    CREATE UNIQUE INDEX [UQ_AdvanceRetirementLink_ExpenseRequest] ON [AdvanceRetirementLinks] ([ExpenseRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801041840_AddAdvanceRetirementLinks'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801041840_AddAdvanceRetirementLinks', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [ExpenseRequests] ADD [BeneficiaryHasBankDetails] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [Employees] ADD [BankAccountNumber] nvarchar(30) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [Employees] ADD [BankName] nvarchar(100) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [CashAdvanceRequests] ADD [PaymentMethod] int NOT NULL DEFAULT 0;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [Beneficiaries] ADD [BankDetailsSetAt] datetime2 NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    ALTER TABLE [Beneficiaries] ADD [BankDetailsSetByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093600_AddBeneficiaryAndEmployeeBankDetails'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801093600_AddBeneficiaryAndEmployeeBankDetails', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093942_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber'
)
BEGIN

    IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE name = 'CMK_Beneficiary_BankDetails')
        THROW 50001, 'CMK_Beneficiary_BankDetails is missing. Run scripts/Provision-AlwaysEncryptedKeys.ps1 against this database before applying this migration.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE name = 'CEK_Beneficiary_BankDetails')
        THROW 50001, 'CEK_Beneficiary_BankDetails is missing. Run scripts/Provision-AlwaysEncryptedKeys.ps1 against this database before applying this migration.', 1;

END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093942_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber'
)
BEGIN

    IF EXISTS (SELECT 1 FROM [Beneficiaries])
        THROW 50002, 'Beneficiaries already contains rows. Dropping BankAccountNumber would destroy plaintext bank details. Re-encrypt client-side (SSMS Always Encrypted wizard or Set-SqlColumnEncryption), or enable secure enclaves, then record this migration as applied by hand.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        WHERE t.name = N'Beneficiaries'
          AND c.name = N'BankAccountNumber'
          AND c.encryption_type IS NULL
    )
    BEGIN
        ALTER TABLE [Beneficiaries] DROP COLUMN [BankAccountNumber];

        ALTER TABLE [Beneficiaries] ADD [BankAccountNumber] nvarchar(30) COLLATE Latin1_General_BIN2
        ENCRYPTED WITH (
            COLUMN_ENCRYPTION_KEY = CEK_Beneficiary_BankDetails,
            ENCRYPTION_TYPE = Deterministic,
            ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256'
        ) NOT NULL;
    END

END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801093942_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801093942_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber', N'8.0.10');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801101700_AddBeneficiaryBankDetailsSetByUserIdToExpenseRequests'
)
BEGIN
    ALTER TABLE [ExpenseRequests] ADD [BeneficiaryBankDetailsSetByUserId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260801101700_AddBeneficiaryBankDetailsSetByUserIdToExpenseRequests'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260801101700_AddBeneficiaryBankDetailsSetByUserIdToExpenseRequests', N'8.0.10');
END;
GO

COMMIT;
GO

