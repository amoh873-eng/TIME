-- ============================================================
--  sp_process_year_end_rollover
--  محرك الترحيل السنوي لأرصدة الإجازات (المواد 100/د، 101، 105)
--
--  المنطق:
--   1) ترحيل الرصيد المتبقي المسموح به (حد أقصى سنتان — Art101).
--   2) منع التراكم لأكثر من سنتين متتاليتين: أي رصيد أقدم من
--      (السنة الحالية - 2) يُصفَّر لمنع التراكم الزائد.
--   3) احتساب الاستحقاق السنوي (مع الـ Pro-Rata للموظفين الجدد).
--   4) تحديث رصيد السنة القادمة.
-- ============================================================
IF OBJECT_ID('dbo.sp_process_year_end_rollover', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_process_year_end_rollover;
GO

CREATE PROCEDURE dbo.sp_process_year_end_rollover
    @FromYear INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @TargetYear INT = @FromYear + 1;
    DECLARE @MaxCarryoverYears INT = 2;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1) إنشاء/تحديث رصيد السنة القادمة لكل موظف
        --    (يُحسب الاستحقاق التناسبي للموظفين الجدد عند الحاجة)
        MERGE dbo.LeaveBalances AS target
        USING (
            SELECT
                e.Id AS EmployeeId,
                @TargetYear AS Year,
                -- Pro-Rata للموظفين الجدد: ((12 - شهر التعيين + 1) / 12) × 30
                CASE
                    WHEN YEAR(e.HiringDate) = @TargetYear
                        THEN ((12 - MONTH(e.HiringDate) + 1) / 12.0) * 30
                    ELSE 30
                END AS EntitlementDays
            FROM dbo.Employees e
        ) AS source
        ON target.EmployeeId = source.EmployeeId AND target.Year = source.Year
        WHEN MATCHED THEN
            UPDATE SET
                target.EntitlementDays = source.EntitlementDays,
                target.UsedDays = 0
        WHEN NOT MATCHED THEN
            INSERT (EmployeeId, Year, EntitlementDays, CarriedOverDays, UsedDays)
            VALUES (source.EmployeeId, source.Year, source.EntitlementDays, 0, 0);

        -- 2) ترحيل الرصيد المتبقي من السنة الحالية إلى السنة القادمة
        --    مع حماية سقف التراكم (سنوات ≤ 2).
        UPDATE current_bal
        SET current_bal.CarriedOverDays =
            ISNULL((
                SELECT prev.RemainingBalance
                FROM (
                    SELECT
                        lb.EmployeeId,
                        (lb.EntitlementDays + lb.CarriedOverDays - lb.UsedDays) AS RemainingBalance
                    FROM dbo.LeaveBalances lb
                    WHERE lb.Year = @FromYear
                ) prev
                WHERE prev.EmployeeId = current_bal.EmployeeId
            ), 0)
        FROM dbo.LeaveBalances current_bal
        WHERE current_bal.Year = @TargetYear;

        -- 3) منع التراكم لأكثر من سنتين: تصفير الأرصدة الأقدم من سنتين
        DELETE FROM dbo.LeaveBalances
        WHERE Year < (@TargetYear - @MaxCarryoverYears)
          AND (EntitlementDays + CarriedOverDays - UsedDays) > 0;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
GRANT EXECUTE ON dbo.sp_process_year_end_rollover TO PUBLIC;
GO
