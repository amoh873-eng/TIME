-- ============================================================
--  v_annual_sick_leave_report  (المادة 112)
--  للتقديم إلى رئيس الوزراء / الوزير
-- ============================================================
IF OBJECT_ID('dbo.v_annual_sick_leave_report', 'V') IS NOT NULL
    DROP VIEW dbo.v_annual_sick_leave_report;
GO

CREATE VIEW dbo.v_annual_sick_leave_report
AS
SELECT
    e.Id AS EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    YEAR(lr.StartDate) AS ReportYear,
    COUNT(DISTINCT lr.Id) AS SickSpells,
    SUM(lr.DaysCount) AS TotalSickDays,
    -- النسبة حسب آخر نوبة ضمن السنة (الأيام التراكمية)
    MAX(sld.SalaryPercentage) AS MaxSalaryPercentage,
    CASE
        WHEN MAX(sld.CumulativeSickDaysThisSpell) <= 120 THEN 100
        WHEN MAX(sld.CumulativeSickDaysThisSpell) <= 240 THEN 75
        ELSE 50
    END AS SalaryPercentTier
FROM dbo.LeaveRequests lr
INNER JOIN dbo.LeaveTypes lt  ON lt.Id = lr.LeaveTypeId AND lt.Code = N'SICK'
INNER JOIN dbo.Employees e    ON e.Id = lr.EmployeeId
LEFT JOIN dbo.SickLeaveDetails sld ON sld.LeaveRequestId = lr.Id
WHERE lr.IsEffective = 1
GROUP BY e.Id, e.JobNumber, e.Name, YEAR(lr.StartDate);
GO
