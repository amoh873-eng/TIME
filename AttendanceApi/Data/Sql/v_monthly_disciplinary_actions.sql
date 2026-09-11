-- ============================================================
--  v_monthly_disciplinary_actions  (المادة 7 - تعليمات 2020)
--  العقوبات التأديبية الشهرية: 3 = تنبيه، 4 = إنذار، >4 = حسم يومين
-- ============================================================
IF OBJECT_ID('dbo.v_monthly_disciplinary_actions', 'V') IS NOT NULL
    DROP VIEW dbo.v_monthly_disciplinary_actions;
GO

CREATE VIEW dbo.v_monthly_disciplinary_actions
AS
SELECT
    da.Id,
    da.EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    da.Year,
    da.Month,
    da.LateCount,
    CASE da.ActionType
        WHEN 1 THEN N'تنبيه خطي'
        WHEN 2 THEN N'إنذار خطي'
        WHEN 3 THEN N'حسم يومين من الراتب'
        ELSE N'لا إجراء'
    END AS ActionDescription
FROM dbo.DisciplinaryActions da
INNER JOIN dbo.Employees e ON e.Id = da.EmployeeId;
GO
