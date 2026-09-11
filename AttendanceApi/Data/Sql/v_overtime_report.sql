-- ============================================================
--  v_overtime_report  — تقرير العمل الإضافي
-- ============================================================
IF OBJECT_ID('dbo.v_overtime_report', 'V') IS NOT NULL
    DROP VIEW dbo.v_overtime_report;
GO

CREATE VIEW dbo.v_overtime_report
AS
SELECT
    o.EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    d.Name AS DepartmentName,
    o.WorkDate,
    o.OvertimeHours,
    YEAR(o.WorkDate)  AS Year,
    MONTH(o.WorkDate) AS Month
FROM dbo.OvertimeRecords o
INNER JOIN dbo.Employees e    ON e.Id = o.EmployeeId
INNER JOIN dbo.Departments d  ON d.Id = e.DepartmentId;
GO
