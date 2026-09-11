-- ============================================================
--  ترقيع المادة 118/ج (التأخير الأسبوعي) على جداول «تقرير المغادرات»
--  يُنفَّذ في كل إقلاع بشكل idempotent لأن EnsureCreated لا يعدّل جدولاً موجوداً.
--  القاعدة: مجموع دقائق التأخير/الانصراف المبكر داخل الدوام الرسمي
--          (08:30–15:30 = 7 ساعات) خلال الأسبوع ≥ 60 دقيقة → خصم يوم كامل.
-- ============================================================

IF COL_LENGTH('dbo.DeparturesReportReviews', 'WeeklyLateMinutes') IS NULL
    ALTER TABLE dbo.DeparturesReportReviews
        ADD WeeklyLateMinutes int NOT NULL CONSTRAINT DF_DeparturesReview_WeeklyLate DEFAULT (0);
GO

IF COL_LENGTH('dbo.DeparturesReportReviews', 'WeeksOver60Minutes') IS NULL
    ALTER TABLE dbo.DeparturesReportReviews
        ADD WeeksOver60Minutes int NOT NULL CONSTRAINT DF_DeparturesReview_Weeks60 DEFAULT (0);
GO

-- إن كان العمود موجوداً بنوع لا يطابق تعيين EF Core (double → real) يُحذف ويُعاد إضافته بالنوع الصحيح
-- (بياناته مشتقة وتُعاد في كل مراجعة، وقيوده الافتراضية تُحذف معه).
IF EXISTS (SELECT 1 FROM sys.columns c
           JOIN sys.types t ON t.user_type_id = c.user_type_id
           WHERE c.object_id = OBJECT_ID('dbo.DeparturesReportReviews')
             AND c.name = 'Article118cDays'
             AND t.name <> 'real')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_DeparturesReview_Art118c'
                 AND parent_object_id = OBJECT_ID('dbo.DeparturesReportReviews'))
        ALTER TABLE dbo.DeparturesReportReviews DROP CONSTRAINT DF_DeparturesReview_Art118c;

    ALTER TABLE dbo.DeparturesReportReviews DROP COLUMN Article118cDays;
END
GO

IF COL_LENGTH('dbo.DeparturesReportReviews', 'Article118cDays') IS NULL
    ALTER TABLE dbo.DeparturesReportReviews
        ADD Article118cDays real NOT NULL CONSTRAINT DF_DeparturesReview_Art118c DEFAULT (0);
GO

-- إن أُنشئ جدول التجميع الأسبوعي بعمود بنوع غير مطابق لتعيين EF يُحذف الجدول ويُعاد إنشاؤه.
IF OBJECT_ID('dbo.DeparturesWeeklyLateness', 'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns c
               JOIN sys.types t ON t.user_type_id = c.user_type_id
               WHERE c.object_id = OBJECT_ID('dbo.DeparturesWeeklyLateness')
                 AND c.name = 'DeductionDays'
                 AND t.name <> 'real')
    DROP TABLE dbo.DeparturesWeeklyLateness;
GO

IF OBJECT_ID('dbo.DeparturesWeeklyLateness', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeparturesWeeklyLateness
    (
        Id                bigint         IDENTITY(1,1) NOT NULL,
        JobNumber         nvarchar(50)   NOT NULL,
        EmployeeName      nvarchar(200)  NULL,
        WeekStart         date           NOT NULL,
        WeekEnd           date           NOT NULL,
        Year              int            NOT NULL,
        Month             int            NOT NULL,
        DepartureCount    int            NOT NULL,
        LateMinutes       int            NOT NULL,
        Exceeds60Minutes  bit            NOT NULL,
        DeductionDays     real           NOT NULL,
        Notes             nvarchar(1000) NULL,
        CONSTRAINT PK_DeparturesWeeklyLateness PRIMARY KEY (Id)
    );
END
GO

IF OBJECT_ID('dbo.DeparturesWeeklyLateness', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_DeparturesWeekly_Employee_Week'
                     AND object_id = OBJECT_ID('dbo.DeparturesWeeklyLateness'))
    CREATE INDEX IX_DeparturesWeekly_Employee_Week
        ON dbo.DeparturesWeeklyLateness (JobNumber, WeekStart);
GO

IF OBJECT_ID('dbo.DeparturesWeeklyLateness', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'IX_DeparturesWeekly_Over60'
                     AND object_id = OBJECT_ID('dbo.DeparturesWeeklyLateness'))
    CREATE INDEX IX_DeparturesWeekly_Over60
        ON dbo.DeparturesWeeklyLateness (Exceeds60Minutes)
        INCLUDE (JobNumber, LateMinutes, DeductionDays);
GO
