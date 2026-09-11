-- ============================================================
--  ØªØ±Ù‚ÙŠØ¹ (Patch): Ù†Ø¸Ø§Ù… Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª ÙˆØ¬Ø¯ÙˆÙ„ Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª Ø§Ù„Ø´Ù‡Ø±ÙŠ
--  Ø§Ù„Ù‡Ø¯Ù: Ø¬Ø¯Ø§ÙˆÙ„ Â«Ø¬Ø¯ÙˆÙ„ Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª Ø§Ù„Ø´Ù‡Ø±ÙŠÂ» (Ù‚ÙŠÙˆØ¯ Ø§Ù„Ù…ÙˆØ¸ÙÙŠÙ† ÙŠÙˆÙ…Ø§Ù‹ Ø¨ÙŠÙˆÙ… + Ø¯ÙØ¹Ø§Øª Ø§Ù„Ø§Ø³ØªÙŠØ±Ø§Ø¯)
--         ÙˆØ£Ø¹Ù…Ø¯Ø© Ù†Ø¸Ø§Ù… Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª ÙÙŠ Ù†ØªØ§Ø¦Ø¬ Ø§Ù„ØªØ­Ù„ÙŠÙ„ Ø§Ù„ÙŠÙˆÙ…ÙŠØ© ÙˆØ§Ù„Ø´Ù‡Ø±ÙŠØ©.
--  ÙŠÙÙ†ÙÙŽÙ‘Ø° Ø¹Ù†Ø¯ Ø¥Ù‚Ù„Ø§Ø¹ Ø§Ù„ØªØ·Ø¨ÙŠÙ‚ ÙˆÙ‡Ùˆ ØªÙƒØ±Ø§Ø± Ø¢Ù…Ù† (Idempotent).
-- ============================================================

IF OBJECT_ID('dbo.ShiftScheduleEntries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShiftScheduleEntries
    (
        Id             bigint        IDENTITY(1,1) NOT NULL,
        JobNumber      nvarchar(50)  NOT NULL,
        EmployeeName   nvarchar(200) NULL,
        DepartmentName nvarchar(300) NULL,
        DutyDate       date          NOT NULL,
        ShiftCode      nvarchar(40)  NOT NULL,
        Kind           int           NOT NULL,
        ShiftHours     real          NOT NULL,
        Notes          nvarchar(300) NULL,
        BatchId        nvarchar(40)  NOT NULL,
        BatchKey       nvarchar(40)  NULL,
        SourceFile     nvarchar(300) NULL,
        ImportedAtUtc  datetime2     NOT NULL,
        CONSTRAINT PK_ShiftScheduleEntries PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.ShiftScheduleEntries', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftSchedule_Employee_Day'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleEntries'))
    CREATE UNIQUE INDEX IX_ShiftSchedule_Employee_Day ON dbo.ShiftScheduleEntries (JobNumber, DutyDate);
GO

IF OBJECT_ID('dbo.ShiftScheduleEntries', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftSchedule_Date'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleEntries'))
    CREATE INDEX IX_ShiftSchedule_Date ON dbo.ShiftScheduleEntries (DutyDate)
        INCLUDE (JobNumber, Kind, ShiftCode);
GO

IF OBJECT_ID('dbo.ShiftScheduleEntries', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftSchedule_Batch'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleEntries'))
    CREATE INDEX IX_ShiftSchedule_Batch ON dbo.ShiftScheduleEntries (BatchId);
GO

IF OBJECT_ID('dbo.ShiftScheduleEntries', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftSchedule_Kind'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleEntries'))
    CREATE INDEX IX_ShiftSchedule_Kind ON dbo.ShiftScheduleEntries (Kind)
        INCLUDE (JobNumber, DutyDate);
GO

IF OBJECT_ID('dbo.ShiftScheduleBatches', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShiftScheduleBatches
    (
        Id            bigint        IDENTITY(1,1) NOT NULL,
        BatchId       nvarchar(40)  NOT NULL,
        BatchKey      nvarchar(40)  NULL,
        FileName      nvarchar(300) NULL,
        PeriodFrom    date          NOT NULL,
        PeriodTo      date          NOT NULL,
        Rows          int           NOT NULL,
        Employees     int           NOT NULL,
        DutyDays      int           NOT NULL,
        RestDays      int           NOT NULL,
        LeaveDays     int           NOT NULL,
        Format        nvarchar(60)  NULL,
        ImportedAtUtc datetime2     NOT NULL,
        CONSTRAINT PK_ShiftScheduleBatches PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.ShiftScheduleBatches', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftBatches_Batch'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleBatches'))
    CREATE UNIQUE INDEX IX_ShiftBatches_Batch ON dbo.ShiftScheduleBatches (BatchId);
GO

IF OBJECT_ID('dbo.ShiftScheduleBatches', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_ShiftBatches_Key'
                     AND object_id = OBJECT_ID('dbo.ShiftScheduleBatches'))
    CREATE INDEX IX_ShiftBatches_Key ON dbo.ShiftScheduleBatches (BatchKey)
        INCLUDE (PeriodFrom, PeriodTo, Rows, Employees);
GO


-- ---- Ø£Ø¹Ù…Ø¯Ø© Ù†Ø¸Ø§Ù… Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª ÙÙŠ Ø§Ù„Ù†ØªØ§Ø¦Ø¬ Ø§Ù„ÙŠÙˆÙ…ÙŠØ© ----
IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'IsShiftDay') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsShiftDay bit NOT NULL CONSTRAINT DF_PunchDaily_IsShiftDay DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'ShiftKind') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD ShiftKind int NOT NULL CONSTRAINT DF_PunchDaily_ShiftKind DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'ShiftCode') IS NULL
    ALTER TABLE dbo.PunchDailyResults ADD ShiftCode nvarchar(40) NULL;
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_ShiftDay'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_ShiftDay ON dbo.PunchDailyResults (IsShiftDay)
        INCLUDE (JobNumber, WorkDate, ShiftKind, ShiftCode);
GO

-- ---- Ø£Ø¹Ù…Ø¯Ø© Ù†Ø¸Ø§Ù… Ø§Ù„ÙˆØ±Ø¯ÙŠØ§Øª ÙÙŠ Ø§Ù„Ù†ØªØ§Ø¦Ø¬ Ø§Ù„Ø´Ù‡Ø±ÙŠØ© ----
IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'ShiftDutyDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD ShiftDutyDays int NOT NULL CONSTRAINT DF_PunchMonthly_ShiftDuty DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'ShiftRestDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD ShiftRestDays int NOT NULL CONSTRAINT DF_PunchMonthly_ShiftRest DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'ShiftLeaveDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD ShiftLeaveDays int NOT NULL CONSTRAINT DF_PunchMonthly_ShiftLeave DEFAULT (0);
GO
