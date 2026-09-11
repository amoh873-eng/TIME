-- ============================================================
--  v_weekly_lateness_violations  (المادة 118/ج)
--  المخالفات الأسبوعية: مجموع (تأخير + انصراف مبكر) ≥ 60 دقيقة → خصم يوم
-- ============================================================
IF OBJECT_ID('dbo.v_weekly_lateness_violations', 'V') IS NOT NULL
    DROP VIEW dbo.v_weekly_lateness_violations;
GO

CREATE VIEW dbo.v_weekly_lateness_violations
AS
SELECT
    wa.EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    wa.Year,
    wa.WeekNumber,
    wa.TotalLateMinutes,
    -- القاعدة: عند بلوغ 60 دقيقة → خصم يوم كامل (1.0)
    CASE
        WHEN wa.TotalLateMinutes >= 60 THEN 1.0
        ELSE 0.0
    END AS DeductionDays
FROM dbo.DepartureWeekAggregates wa
INNER JOIN dbo.Employees e ON e.Id = wa.EmployeeId
WHERE wa.TotalLateMinutes >= 60;
GO
