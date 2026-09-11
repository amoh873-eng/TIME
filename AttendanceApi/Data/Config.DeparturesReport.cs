using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureDeparturesReport(ModelBuilder mb)
    {
        // ---- جدول مرحلة «تقرير المغادرات» (كما ورد في ملف Excel العربي) ----
        mb.Entity<DeparturesReportRow>(e =>
        {
            e.ToTable("StagingDeparturesReport");
            e.HasKey(x => x.Id);

            e.Property(x => x.RequestNumber).HasMaxLength(50);
            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DurationText).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.RequestType).HasMaxLength(120);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.MainDepartmentName).HasMaxLength(300);
            e.Property(x => x.SourceFile).HasMaxLength(300);

            e.Property(x => x.FromTime).HasColumnType("time");
            e.Property(x => x.ToTime).HasColumnType("time");
            e.Property(x => x.FromDate).HasColumnType("date");
            e.Property(x => x.ToDate).HasColumnType("date");
            e.Property(x => x.RequestDate).HasColumnType("date");

            e.HasIndex(x => x.JobNumber).HasDatabaseName("IX_StagingDepartures_JobNumber");
            e.HasIndex(x => new { x.IsAuthorization, x.Exceeds4Hours })
             .HasDatabaseName("IX_StagingDepartures_Authorization");
            e.HasIndex(x => x.RequestType).HasDatabaseName("IX_StagingDepartures_Type");
        });

        // ---- نتائج المراجعة الشهرية ----
        mb.Entity<DeparturesReportReview>(e =>
        {
            e.ToTable("DeparturesReportReviews");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.Property(x => x.Article118bDays).HasPrecision(9, 2);
            e.Property(x => x.Article118cDays).HasPrecision(9, 2);
            e.Property(x => x.AnnualLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.SickLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.OfficialDutyDays).HasPrecision(9, 2);
            e.Property(x => x.OtherLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.TotalAbsenceDays).HasPrecision(9, 2);
            e.Property(x => x.BonusDeductionPercent).HasPrecision(5, 2);

            e.HasIndex(x => new { x.JobNumber, x.Year, x.Month })
             .IsUnique()
             .HasDatabaseName("IX_DeparturesReview_Employee_Month");
        });

        // ---- التجميع الأسبوعي لدقائق التأخير (المادة 118/ج) ----
        mb.Entity<DeparturesWeeklyLateness>(e =>
        {
            e.ToTable("DeparturesWeeklyLateness");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);

            e.Property(x => x.WeekStart).HasColumnType("date");
            e.Property(x => x.WeekEnd).HasColumnType("date");
            e.Property(x => x.DeductionDays).HasPrecision(9, 2);

            e.HasIndex(x => new { x.JobNumber, x.WeekStart })
             .HasDatabaseName("IX_DeparturesWeekly_Employee_Week");
            e.HasIndex(x => x.Exceeds60Minutes)
             .HasDatabaseName("IX_DeparturesWeekly_Over60");
        });
    }
}
