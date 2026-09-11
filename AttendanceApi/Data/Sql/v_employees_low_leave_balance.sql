-- ============================================================
--  v_employees_low_leave_balance
--  الموظفون الذين انخفض رصيد إجازتهم السنوية عن الحد الأدنى
-- ============================================================
IF OBJECT_ID('dbo.v_employees_low_leave_balance', 'V') IS NOT NULL
    DROP VIEW dbo.v_employees_low_leave_balance;
GO

CREATE VIEW dbo.v_employees_low_leave_balance
AS
SELECT
    e.Id            AS EmployeeId,
    e.JobNumber     AS JobNumber,
    e.Name          AS EmployeeName,
    d.Name          AS DepartmentName,
    jc.Name         AS JobCategoryName,
    lb.Year         AS LeaveYear,
    lb.EntitlementDays,
    lb.CarriedOverDays,
    lb.UsedDays,
    (lb.EntitlementDays + lb.CarriedOverDays - lb.UsedDays) AS RemainingBalance
FROM dbo.Employees e
INNER JOIN dbo.Departments d        ON d.Id = e.DepartmentId
INNER JOIN dbo.JobCategories jc     ON jc.Id = e.JobCategoryId
INNER JOIN dbo.LeaveBalances lb     ON lb.EmployeeId = e.Id
WHERE (lb.EntitlementDays + lb.CarriedOverDays - lb.UsedDays) < (jc.MaxAnnualLeaveDays * 0.2);
GO
