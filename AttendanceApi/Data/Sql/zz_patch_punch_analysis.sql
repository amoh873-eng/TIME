-- ============================================================
--  جداول تحليل بصمات الحضور والانصراف (تقرير الحضور والانصراف الخام)
--  تُنشأ بشكل idempotent لأن EnsureCreated لا يعدّل قاعدة بيانات موجودة.
--  الاحتساب: المادة 7 (التأخير الصباحي)، المادة 118/ب (غياب > 4 ساعات)،
--            المادة 118/ج (60 دقيقة أسبوعياً)، قاعدة الـ 15 يوماً للمكافأة.
-- ============================================================

IF OBJECT_ID('dbo.StagingPunchRecords', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StagingPunchRecords
    (
        Id            bigint         IDENTITY(1,1) NOT NULL,
        JobNumber     nvarchar(50)   NOT NULL,
        EmployeeName  nvarchar(200)  NULL,
        WorkDate      date           NOT NULL,
        StatusText    nvarchar(150)  NULL,
        Status        int            NOT NULL,
        ClockIn       time           NULL,
        ClockOut      time           NULL,
        LocationName  nvarchar(200)  NULL,
        DepartmentName nvarchar(300) NULL,
        SourceRow     int            NOT NULL,
        SourceFile    nvarchar(300)  NULL,
        ImportedAtUtc datetime2      NOT NULL,
        CONSTRAINT PK_StagingPunchRecords PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.StagingPunchRecords', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_StagingPunch_Employee_Day'
                     AND object_id = OBJECT_ID('dbo.StagingPunchRecords'))
    CREATE INDEX IX_StagingPunch_Employee_Day ON dbo.StagingPunchRecords (JobNumber, WorkDate);
GO

IF OBJECT_ID('dbo.StagingPunchRecords', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_StagingPunch_Status'
                     AND object_id = OBJECT_ID('dbo.StagingPunchRecords'))
    CREATE INDEX IX_StagingPunch_Status ON dbo.StagingPunchRecords (Status) INCLUDE (JobNumber, WorkDate);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PunchDailyResults
    (
        Id                     bigint        IDENTITY(1,1) NOT NULL,
        JobNumber              nvarchar(50)  NOT NULL,
        EmployeeName           nvarchar(200) NULL,
        DepartmentName         nvarchar(300) NULL,
        WorkDate               date          NOT NULL,
        Status                 int           NOT NULL,
        ClockIn                time          NULL,
        ClockOut               time          NULL,
        LatenessMinutes        int           NOT NULL,
        EarlyDepartureMinutes  int           NOT NULL,
        GapMinutes             int           NOT NULL,
        AbsenceMinutes         int           NOT NULL,
        WorkedMinutes          int           NOT NULL,
        SourceRows             int           NOT NULL,
        IsWorkingDay           bit           NOT NULL,
        IsMorningLate          bit           NOT NULL,
        IsAbsent               bit           NOT NULL,
        IsIncomplete           bit           NOT NULL,
        CountsFor118b          bit           NOT NULL,
        Article118bDays        real          NOT NULL,
        WorkWeekStart          date          NOT NULL,
        Year                   int           NOT NULL,
        Month                  int           NOT NULL,
        Notes                  nvarchar(400) NULL,
        CONSTRAINT PK_PunchDailyResults PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_Employee_Day'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE UNIQUE INDEX IX_PunchDaily_Employee_Day ON dbo.PunchDailyResults (JobNumber, WorkDate);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_WeekStart'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_WeekStart ON dbo.PunchDailyResults (WorkWeekStart)
        INCLUDE (JobNumber, LatenessMinutes, EarlyDepartureMinutes);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_Over4Hours'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_Over4Hours ON dbo.PunchDailyResults (CountsFor118b)
        INCLUDE (JobNumber, WorkDate, AbsenceMinutes);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_MorningLate'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_MorningLate ON dbo.PunchDailyResults (IsMorningLate)
        INCLUDE (JobNumber, WorkDate, LatenessMinutes);
GO
IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchDailyResults', 'GapMinutes') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD GapMinutes int NOT NULL CONSTRAINT DF_PunchDaily_Gap DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchWeeklyResults', 'GapMinutes') IS NULL
    ALTER TABLE dbo.PunchWeeklyResults
        ADD GapMinutes int NOT NULL CONSTRAINT DF_PunchWeekly_Gap DEFAULT (0);
GO

-- الناتج الأسبوعي المحتسب (المادة 118/ج): دمج التأخير الصباحي مع الانصراف المبكر بحدّ 60 دقيقة أسبوعياً.
IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchWeeklyResults', 'CountedMinutes') IS NULL
    ALTER TABLE dbo.PunchWeeklyResults
        ADD CountedMinutes int NOT NULL CONSTRAINT DF_PunchWeekly_Counted DEFAULT (0);
GO

IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PunchWeeklyResults
    (
        Id                    bigint        IDENTITY(1,1) NOT NULL,
        JobNumber             nvarchar(50)  NOT NULL,
        EmployeeName          nvarchar(200) NULL,
        DepartmentName        nvarchar(300) NULL,
        WeekStart             date          NOT NULL,
        WeekEnd               date          NOT NULL,
        Year                  int           NOT NULL,
        Month                 int           NOT NULL,
        DaysCounted           int           NOT NULL,
        LatenessMinutes       int           NOT NULL,
        EarlyDepartureMinutes int           NOT NULL,
        GapMinutes            int           NOT NULL,
        TotalMinutes          int           NOT NULL,
        CountedMinutes        int           NOT NULL,
        Exceeds60Minutes      bit           NOT NULL,
        DeductionDays         real          NOT NULL,
        Notes                 nvarchar(400) NULL,
        CONSTRAINT PK_PunchWeeklyResults PRIMARY KEY (Id)
    );
END
GO

-- توافق مع الصفوف المحفوظة قبل إضافة العمود: الناتج المحتسب = المجموع الفعلي مقيَّداً بـ 60 دقيقة.
IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.PunchWeeklyResults', 'CountedMinutes') IS NOT NULL
    UPDATE dbo.PunchWeeklyResults
       SET CountedMinutes = CASE WHEN TotalMinutes > 60 THEN 60 ELSE TotalMinutes END
     WHERE CountedMinutes = 0
       AND TotalMinutes > 0;
GO

IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchWeekly_Employee_Week'
                     AND object_id = OBJECT_ID('dbo.PunchWeeklyResults'))
    CREATE UNIQUE INDEX IX_PunchWeekly_Employee_Week ON dbo.PunchWeeklyResults (JobNumber, WeekStart);
GO

IF OBJECT_ID('dbo.PunchWeeklyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchWeekly_Over60'
                     AND object_id = OBJECT_ID('dbo.PunchWeeklyResults'))
    CREATE INDEX IX_PunchWeekly_Over60 ON dbo.PunchWeeklyResults (Exceeds60Minutes)
        INCLUDE (JobNumber, TotalMinutes, CountedMinutes, DeductionDays);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PunchMonthlyResults
    (
        Id                            bigint        IDENTITY(1,1) NOT NULL,
        JobNumber                     nvarchar(50)  NOT NULL,
        EmployeeName                  nvarchar(200) NULL,
        DepartmentName                nvarchar(300) NULL,
        Year                          int           NOT NULL,
        Month                         int           NOT NULL,
        WorkingDays                   int           NOT NULL,
        CompleteDays                  int           NOT NULL,
        LateIncidents                 int           NOT NULL,
        Penalty                       int           NOT NULL,
        PenaltyText                   nvarchar(150) NULL,
        Article7SalaryDeductionDays   real          NOT NULL,
        AbsentDays                    int           NOT NULL,
        IncompleteDays                int           NOT NULL,
        AnnualLeaveDays               real          NOT NULL,
        SickLeaveDays                 real          NOT NULL,
        CompensatoryLeaveDays         real          NOT NULL,
        BereavementLeaveDays          real          NOT NULL,
        EmergencyPermissionDays       real          NOT NULL,
        MedicalPermissionDays         real          NOT NULL,
        OfficialDutyDays              real          NOT NULL,
        Article118bDays               real          NOT NULL,
        Article118cDays               real          NOT NULL,
        WeeklyLateMinutes             int           NOT NULL,
        WeeksOver60Minutes            int           NOT NULL,
        TotalAbsenceDays              real          NOT NULL,
        BonusDeductionPercent         real          NOT NULL,
        Exceeds15Days                 bit           NOT NULL,
        TotalSalaryDeductionDays      real          NOT NULL,
        AnnualLeaveBalanceUsageDays   real          NOT NULL,
        Notes                         nvarchar(1000) NULL,
        CONSTRAINT PK_PunchMonthlyResults PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchMonthly_Employee_Month'
                     AND object_id = OBJECT_ID('dbo.PunchMonthlyResults'))
    CREATE UNIQUE INDEX IX_PunchMonthly_Employee_Month ON dbo.PunchMonthlyResults (JobNumber, Year, Month);
GO

IF OBJECT_ID('dbo.PunchMonthlyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchMonthly_Over15Days'
                     AND object_id = OBJECT_ID('dbo.PunchMonthlyResults'))
    CREATE INDEX IX_PunchMonthly_Over15Days ON dbo.PunchMonthlyResults (Exceeds15Days)
        INCLUDE (JobNumber, Year, Month, TotalAbsenceDays, BonusDeductionPercent);
GO

