-- ============================================================
--  ترقيع (Patch): الدوام المرن والعمل الإضافي
--  الهدف: جدول «تصاريح العمل الإضافي والدوام المرن» (PunchWorkApprovals)
--         + أعمدة الدوام المرن والعمل الإضافي في النتائج اليومية والشهرية.
--  يُنفَّذ عند إقلاع التطبيق وهو تكرار آمن (Idempotent).
-- ============================================================

IF OBJECT_ID('dbo.PunchWorkApprovals', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PunchWorkApprovals
    (
        Id                 bigint        IDENTITY(1,1) NOT NULL,
        Kind               int           NOT NULL,
        JobNumber          nvarchar(50)  NOT NULL,
        EmployeeName       nvarchar(200) NULL,
        DepartmentName     nvarchar(300) NULL,
        FromDate           date          NOT NULL,
        ToDate             date          NOT NULL,
        MaxMinutesPerDay   int           NULL,
        MaxMinutesTotal    int           NULL,
        Note               nvarchar(300) NULL,
        IsActive           bit           NOT NULL CONSTRAINT DF_PunchWorkApprovals_Active DEFAULT (1),
        Source             nvarchar(100) NULL,
        CreatedAtUtc       datetime2     NOT NULL,
        CONSTRAINT PK_PunchWorkApprovals PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.PunchWorkApprovals', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchWorkApprovals_Employee'
                     AND object_id = OBJECT_ID('dbo.PunchWorkApprovals'))
    CREATE INDEX IX_PunchWorkApprovals_Employee ON dbo.PunchWorkApprovals (JobNumber, Kind)
        INCLUDE (FromDate, ToDate, MaxMinutesPerDay, MaxMinutesTotal, IsActive);
GO

IF OBJECT_ID('dbo.PunchWorkApprovals', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchWorkApprovals_Period'
                     AND object_id = OBJECT_ID('dbo.PunchWorkApprovals'))
    CREATE INDEX IX_PunchWorkApprovals_Period ON dbo.PunchWorkApprovals (FromDate, ToDate)
        INCLUDE (JobNumber, Kind, IsActive);
GO

-- ---- أعمدة الدوام المرن والعمل الإضافي في النتائج اليومية ----
IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'IsFlexibleWork') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsFlexibleWork bit NOT NULL CONSTRAINT DF_PunchDaily_Flexible DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'FlexibleStart') IS NULL
    ALTER TABLE dbo.PunchDailyResults ADD FlexibleStart time NULL;
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'FlexibleEnd') IS NULL
    ALTER TABLE dbo.PunchDailyResults ADD FlexibleEnd time NULL;
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'FlexibleMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD FlexibleMinutes int NOT NULL CONSTRAINT DF_PunchDaily_FlexibleMinutes DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'IsOvertimeEligible') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsOvertimeEligible bit NOT NULL CONSTRAINT DF_PunchDaily_OvertimeEligible DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'RawOvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD RawOvertimeMinutes int NOT NULL CONSTRAINT DF_PunchDaily_RawOvertime DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'OvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD OvertimeMinutes int NOT NULL CONSTRAINT DF_PunchDaily_Overtime DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'OvertimeExcludedMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD OvertimeExcludedMinutes int NOT NULL CONSTRAINT DF_PunchDaily_OvertimeExcluded DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'OvertimeNeedsApproval') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD OvertimeNeedsApproval bit NOT NULL CONSTRAINT DF_PunchDaily_OvertimeNeedsApproval DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'OvertimeKind') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD OvertimeKind int NOT NULL CONSTRAINT DF_PunchDaily_OvertimeKind DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'OvertimeRate') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD OvertimeRate float NOT NULL CONSTRAINT DF_PunchDaily_OvertimeRate DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'EquivalentOvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD EquivalentOvertimeMinutes int NOT NULL CONSTRAINT DF_PunchDaily_EquivalentOvertime DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_Overtime'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_Overtime ON dbo.PunchDailyResults (OvertimeMinutes)
        INCLUDE (JobNumber, WorkDate, IsFlexibleWork, OvertimeKind, OvertimeRate);
GO

-- ---- أعمدة الدوام المرن والعمل الإضافي في النتائج الشهرية ----
IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'FlexibleDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD FlexibleDays int NOT NULL CONSTRAINT DF_PunchMonthly_FlexibleDays DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'FlexibleMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD FlexibleMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_FlexibleMinutes DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeDays int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeDays DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeMinutes DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeHours') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeHours float NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeHours DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'RawOvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD RawOvertimeMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_RawOvertime DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeExcludedMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeExcludedMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeExcluded DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeWeekendMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeWeekendMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeWeekend DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeHolidayMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeHolidayMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeHoliday DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'EquivalentOvertimeMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD EquivalentOvertimeMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_EquivalentOvertime DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'EquivalentOvertimeHours') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD EquivalentOvertimeHours float NOT NULL CONSTRAINT DF_PunchMonthly_EquivalentHours DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeNeedsApprovalDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeNeedsApprovalDays int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeNeedsApprovalDays DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeNeedsApprovalMinutes') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeNeedsApprovalMinutes int NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeNeedsApprovalMinutes DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchMonthlyResults', 'OvertimeCapReached') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD OvertimeCapReached bit NOT NULL CONSTRAINT DF_PunchMonthly_OvertimeCap DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchMonthly_Overtime'
                     AND object_id = OBJECT_ID('dbo.PunchMonthlyResults'))
    CREATE INDEX IX_PunchMonthly_Overtime ON dbo.PunchMonthlyResults (OvertimeMinutes)
        INCLUDE (JobNumber, Year, Month, OvertimeDays, OvertimeHours, EquivalentOvertimeHours);
GO



-- ---- عرض مساعد: ساعات العمل الإضافي والدوام المرن الشهرية المحتسبة من البصمات ----
IF OBJECT_ID('dbo.v_punch_overtime_monthly', 'V') IS NOT NULL
    DROP VIEW dbo.v_punch_overtime_monthly;
GO

CREATE VIEW dbo.v_punch_overtime_monthly
AS
SELECT
    m.JobNumber,
    m.EmployeeName,
    m.DepartmentName,
    m.Year,
    m.Month,
    m.OvertimeDays,
    m.OvertimeMinutes,
    m.OvertimeHours,
    m.EquivalentOvertimeHours,
    m.RawOvertimeMinutes,
    m.OvertimeExcludedMinutes,
    m.OvertimeWeekendMinutes,
    m.OvertimeHolidayMinutes,
    m.FlexibleDays,
    m.FlexibleMinutes,
    m.OvertimeNeedsApprovalDays,
    m.OvertimeNeedsApprovalMinutes,
    m.OvertimeCapReached
FROM dbo.PunchMonthlyResults m
WHERE m.OvertimeMinutes > 0 OR m.FlexibleDays > 0 OR m.OvertimeNeedsApprovalMinutes > 0;
GO
