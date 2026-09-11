-- ============================================================
--  v_low_absence_bonus_eligibility  (قاعدة الـ 15 يوماً)
--  أهلية المكافأة: إذا تجاوز إجمالي الغياب الشهري 15 يوماً → خصم 50%
--  إجمالي الغياب = إجازة سنوية + مرضية + 118/ب + 118/ج
-- ============================================================
IF OBJECT_ID('dbo.v_low_absence_bonus_eligibility', 'V') IS NOT NULL
    DROP VIEW dbo.v_low_absence_bonus_eligibility;
GO

CREATE VIEW dbo.v_low_absence_bonus_eligibility
AS
WITH AbsenceSummary AS
(
    -- الإجازات السنوية الفعّالة
    SELECT
        lr.EmployeeId,
        YEAR(lr.StartDate) AS Year,
        MONTH(lr.StartDate) AS Month,
        SUM(lr.DaysCount) AS AnnualLeaveDays
    FROM dbo.LeaveRequests lr
    INNER JOIN dbo.LeaveTypes lt ON lt.Id = lr.LeaveTypeId
    WHERE lr.IsEffective = 1 AND lr.IsApproved = 1 AND lt.Code = N'ANNUAL'
    GROUP BY lr.EmployeeId, YEAR(lr.StartDate), MONTH(lr.StartDate)
),
SickSummary AS
(
    SELECT
        lr.EmployeeId,
        YEAR(lr.StartDate) AS Year,
        MONTH(lr.StartDate) AS Month,
        SUM(lr.DaysCount) AS SickLeaveDays
    FROM dbo.LeaveRequests lr
    INNER JOIN dbo.LeaveTypes lt ON lt.Id = lr.LeaveTypeId
    WHERE lr.IsEffective = 1 AND lr.IsApproved = 1 AND lt.Code = N'SICK'
    GROUP BY lr.EmployeeId, YEAR(lr.StartDate), MONTH(lr.StartDate)
),
Art118bSummary AS
(
    SELECT
        d.EmployeeId,
        YEAR(d.DepartureDate) AS Year,
        MONTH(d.DepartureDate) AS Month,
        SUM(d.EquivalentDaysDeduction) AS Art118bDays
    FROM dbo.Departures d
    WHERE d.IsExceeding4Hours = 1
    GROUP BY d.EmployeeId, YEAR(d.DepartureDate), MONTH(d.DepartureDate)
),
Art118cSummary AS
(
    SELECT
        wa.EmployeeId,
        wa.Year,
        -- تمثيل الأسبوع في الشهر (تقريبي) للتجميع الشهري
        ((wa.WeekNumber - 1) / 4 + 1) AS Month,
        SUM(wa.DeductionDays) AS Art118cDays
    FROM dbo.DepartureWeekAggregates wa
    GROUP BY wa.EmployeeId, wa.Year, ((wa.WeekNumber - 1) / 4 + 1)
)
SELECT
    COALESCE(a.EmployeeId, s.EmployeeId, b.EmployeeId, c.EmployeeId) AS EmployeeId,
    e.JobNumber,
    e.Name AS EmployeeName,
    COALESCE(a.Year, s.Year, b.Year, c.Year) AS Year,
    COALESCE(a.Month, s.Month, b.Month, c.Month) AS Month,
    ISNULL(a.AnnualLeaveDays, 0) + ISNULL(s.SickLeaveDays, 0)
        + ISNULL(b.Art118bDays, 0) + ISNULL(c.Art118cDays, 0) AS TotalAbsenceDays,
    CASE
        WHEN ISNULL(a.AnnualLeaveDays, 0) + ISNULL(s.SickLeaveDays, 0)
            + ISNULL(b.Art118bDays, 0) + ISNULL(c.Art118cDays, 0) > 15.0
            THEN 0.50
        ELSE 0.00
    END AS BonusDeductionPercent,
    CASE
        WHEN ISNULL(a.AnnualLeaveDays, 0) + ISNULL(s.SickLeaveDays, 0)
            + ISNULL(b.Art118bDays, 0) + ISNULL(c.Art118cDays, 0) > 15.0
            THEN N'خصم 50%'
        ELSE N'لا خصم (ضمن الحد)'
    END AS BonusStatus
FROM AbsenceSummary a
FULL OUTER JOIN SickSummary s      ON s.EmployeeId = a.EmployeeId AND s.Year = a.Year AND s.Month = a.Month
FULL OUTER JOIN Art118bSummary b   ON b.EmployeeId = COALESCE(a.EmployeeId, s.EmployeeId) AND b.Year = COALESCE(a.Year, s.Year) AND b.Month = COALESCE(a.Month, s.Month)
FULL OUTER JOIN Art118cSummary c   ON c.EmployeeId = COALESCE(a.EmployeeId, s.EmployeeId, b.EmployeeId) AND c.Year = COALESCE(a.Year, s.Year, b.Year) AND c.Month = COALESCE(a.Month, s.Month, b.Month)
INNER JOIN dbo.Employees e ON e.Id = COALESCE(a.EmployeeId, s.EmployeeId, b.EmployeeId, c.EmployeeId);
GO
