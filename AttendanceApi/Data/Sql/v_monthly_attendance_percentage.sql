-- ============================================================
--  v_monthly_attendance_percentage
--  نسبة الحضور الشهري لكل موظف (أيام الحضور / أيام الدوام)
-- ============================================================
IF OBJECT_ID('dbo.v_monthly_attendance_percentage', 'V') IS NOT NULL
    DROP VIEW dbo.v_monthly_attendance_percentage;
GO

CREATE VIEW dbo.v_monthly_attendance_percentage
AS
SELECT
    a.EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    a.Year,
    a.Month,
    COUNT(*)                                                  AS WorkingDays,
    SUM(CASE WHEN a.CheckInTime IS NOT NULL THEN 1 ELSE 0 END) AS PresentDays,
    CAST(
        100.0 * SUM(CASE WHEN a.CheckInTime IS NOT NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0)
        AS DECIMAL(6,2)
    ) AS AttendancePercentage
FROM dbo.AttendanceRecords a
INNER JOIN dbo.Employees e ON e.Id = a.EmployeeId
GROUP BY a.EmployeeId, e.JobNumber, e.Name, a.Year, a.Month;
GO
