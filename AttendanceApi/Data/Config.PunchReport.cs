using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    /// <summary>
    /// تكوين جداول تحليل بصمات الحضور: جدول مرحلة يحفظ الصفوف الخام
    /// + نتائج يومية وأسبوعية وشهرية قابلة للاستعلام والتقارير.
    /// </summary>
    public static void ConfigurePunchReport(ModelBuilder mb)
    {
        // ---- جدول مرحلة البصمات (كما وردت في ملف الحضور والانصراف) ----
        mb.Entity<PunchRecord>(e =>
        {
            e.ToTable("StagingPunchRecords");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.StatusText).HasMaxLength(150);
            e.Property(x => x.LocationName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.SourceFile).HasMaxLength(300);

            e.Property(x => x.WorkDate).HasColumnType("date");
            e.Property(x => x.ClockIn).HasColumnType("time");
            e.Property(x => x.ClockOut).HasColumnType("time");

            e.HasIndex(x => x.JobNumber).HasDatabaseName("IX_StagingPunch_JobNumber");
            e.HasIndex(x => new { x.JobNumber, x.WorkDate }).HasDatabaseName("IX_StagingPunch_Employee_Day");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_StagingPunch_Status");
        });

        // ---- النتائج اليومية ----
        mb.Entity<PunchDailyResult>(e =>
        {
            e.ToTable("PunchDailyResults");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(400);

            e.Property(x => x.WorkDate).HasColumnType("date");
            e.Property(x => x.WorkWeekStart).HasColumnType("date");
            e.Property(x => x.ClockIn).HasColumnType("time");
            e.Property(x => x.ClockOut).HasColumnType("time");
            e.Property(x => x.Article118bDays).HasPrecision(9, 2);

            e.HasIndex(x => new { x.JobNumber, x.WorkDate }).IsUnique()
             .HasDatabaseName("IX_PunchDaily_Employee_Day");
            e.HasIndex(x => x.WorkWeekStart).HasDatabaseName("IX_PunchDaily_WeekStart");
            e.HasIndex(x => x.CountsFor118b).HasDatabaseName("IX_PunchDaily_Over4Hours");
            e.HasIndex(x => x.IsMorningLate).HasDatabaseName("IX_PunchDaily_MorningLate");
        });

        // ---- النتائج الأسبوعية (المادة 118/ج) ----
        mb.Entity<PunchWeeklyResult>(e =>
        {
            e.ToTable("PunchWeeklyResults");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(400);

            e.Property(x => x.WeekStart).HasColumnType("date");
            e.Property(x => x.WeekEnd).HasColumnType("date");
            e.Property(x => x.DeductionDays).HasPrecision(9, 2);

            e.HasIndex(x => new { x.JobNumber, x.WeekStart }).IsUnique()
             .HasDatabaseName("IX_PunchWeekly_Employee_Week");
            e.HasIndex(x => x.Exceeds60Minutes).HasDatabaseName("IX_PunchWeekly_Over60");
        });

        // ---- النتائج الشهرية ----
        mb.Entity<PunchMonthlyResult>(e =>
        {
            e.ToTable("PunchMonthlyResults");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.PenaltyText).HasMaxLength(150);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.Property(x => x.Article118bDays).HasPrecision(9, 2);
            e.Property(x => x.Article118cDays).HasPrecision(9, 2);
            e.Property(x => x.Article7SalaryDeductionDays).HasPrecision(9, 2);
            e.Property(x => x.AnnualLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.SickLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.CompensatoryLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.BereavementLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.EmergencyPermissionDays).HasPrecision(9, 2);
            e.Property(x => x.MedicalPermissionDays).HasPrecision(9, 2);
            e.Property(x => x.OfficialDutyDays).HasPrecision(9, 2);
            e.Property(x => x.TotalAbsenceDays).HasPrecision(9, 2);
            e.Property(x => x.BonusDeductionPercent).HasPrecision(5, 2);
            e.Property(x => x.TotalSalaryDeductionDays).HasPrecision(9, 2);
            e.Property(x => x.AnnualLeaveBalanceUsageDays).HasPrecision(9, 2);

            e.HasIndex(x => new { x.JobNumber, x.Year, x.Month }).IsUnique()
             .HasDatabaseName("IX_PunchMonthly_Employee_Month");
            e.HasIndex(x => x.Exceeds15Days).HasDatabaseName("IX_PunchMonthly_Over15Days");
        });
    }
}
