-- ============================================================
--  ترقيع (Patch): تقويم العطل الرسمية والدينية لتحليل بصمات الحضور
--  الهدف: إضافة أعمدة التقويم إلى جدول العطل وجداول نتائج التحليل
--         دون المساس بأي جدول من جداول «تقرير المغادرات».
--  التطبيق: يُنفَّذ تلقائياً عند إقلاع التطبيق (DatabaseInitializer)
--           وهو تكرار آمن (Idempotent) — كل أمر محميّ بفحص وجود مسبق.
-- ============================================================

-- ---- 1) جدول العطل الرسمية: تصنيف العطلة ومصدر القيد وحالة الإلغاء ----
IF COL_LENGTH('dbo.OfficialHolidays', 'Kind') IS NULL
    ALTER TABLE dbo.OfficialHolidays
        ADD Kind int NOT NULL CONSTRAINT DF_OfficialHolidays_Kind DEFAULT (1);
GO

IF COL_LENGTH('dbo.OfficialHolidays', 'Source') IS NULL
    ALTER TABLE dbo.OfficialHolidays ADD Source nvarchar(100) NULL;
GO

IF COL_LENGTH('dbo.OfficialHolidays', 'CreatedAtUtc') IS NULL
    ALTER TABLE dbo.OfficialHolidays ADD CreatedAtUtc datetime2 NULL;
GO

IF COL_LENGTH('dbo.OfficialHolidays', 'IsCancelled') IS NULL
    ALTER TABLE dbo.OfficialHolidays
        ADD IsCancelled bit NOT NULL CONSTRAINT DF_OfficialHolidays_IsCancelled DEFAULT (0);
GO

-- ---- 2) النتائج اليومية: تصنيف العطلة (نهاية أسبوع / رسمية / دينية) ----
IF COL_LENGTH('dbo.PunchDailyResults', 'IsWeekendDay') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsWeekendDay bit NOT NULL CONSTRAINT DF_PunchDaily_IsWeekendDay DEFAULT (0);
GO

IF COL_LENGTH('dbo.PunchDailyResults', 'IsCalendarHoliday') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsCalendarHoliday bit NOT NULL CONSTRAINT DF_PunchDaily_IsCalendarHoliday DEFAULT (0);
GO

IF COL_LENGTH('dbo.PunchDailyResults', 'HolidayKind') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD HolidayKind int NOT NULL CONSTRAINT DF_PunchDaily_HolidayKind DEFAULT (1);
GO

IF COL_LENGTH('dbo.PunchDailyResults', 'HolidayName') IS NULL
    ALTER TABLE dbo.PunchDailyResults ADD HolidayName nvarchar(200) NULL;
GO

IF COL_LENGTH('dbo.PunchDailyResults', 'IsWorkOnHoliday') IS NULL
    ALTER TABLE dbo.PunchDailyResults
        ADD IsWorkOnHoliday bit NOT NULL CONSTRAINT DF_PunchDaily_IsWorkOnHoliday DEFAULT (0);
GO

-- ---- 3) النتائج الشهرية: أعداد أيام العطل والدوام فيها ----
IF COL_LENGTH('dbo.PunchMonthlyResults', 'WeekendDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD WeekendDays int NOT NULL CONSTRAINT DF_PunchMonthly_WeekendDays DEFAULT (0);
GO

IF COL_LENGTH('dbo.PunchMonthlyResults', 'HolidayDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD HolidayDays int NOT NULL CONSTRAINT DF_PunchMonthly_HolidayDays DEFAULT (0);
GO

IF COL_LENGTH('dbo.PunchMonthlyResults', 'WeekendWorkDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD WeekendWorkDays int NOT NULL CONSTRAINT DF_PunchMonthly_WeekendWorkDays DEFAULT (0);
GO

IF COL_LENGTH('dbo.PunchMonthlyResults', 'HolidayWorkDays') IS NULL
    ALTER TABLE dbo.PunchMonthlyResults
        ADD HolidayWorkDays int NOT NULL CONSTRAINT DF_PunchMonthly_HolidayWorkDays DEFAULT (0);
GO

-- ---- 4) فهارس مساعدة لتقارير العطل والدوام فيها ----
IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_WorkOnHoliday'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_WorkOnHoliday ON dbo.PunchDailyResults (IsWorkOnHoliday)
        INCLUDE (WorkDate, WorkedMinutes, JobNumber);
GO

IF OBJECT_ID('dbo.PunchDailyResults', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_PunchDaily_CalendarHoliday'
                     AND object_id = OBJECT_ID('dbo.PunchDailyResults'))
    CREATE INDEX IX_PunchDaily_CalendarHoliday ON dbo.PunchDailyResults (IsCalendarHoliday)
        INCLUDE (WorkDate, IsWeekendDay);
GO

IF OBJECT_ID('dbo.OfficialHolidays', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_OfficialHolidays_Kind'
                     AND object_id = OBJECT_ID('dbo.OfficialHolidays'))
    CREATE INDEX IX_OfficialHolidays_Kind ON dbo.OfficialHolidays (Kind, IsCancelled)
        INCLUDE (HolidayDate, Description);
GO
