using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureMisc(ModelBuilder mb)
    {
        // ---- OfficialHoliday (تقويم العطل الرسمية والدينية) ----
        mb.Entity<OfficialHoliday>(e =>
        {
            e.ToTable("OfficialHolidays");
            e.HasKey(x => x.Id);
            e.Property(x => x.HolidayDate).HasColumnType("date");
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.IsCancelled).HasDefaultValue(false);
            e.HasIndex(x => x.HolidayDate).IsUnique();
        });

        // ---- OvertimeRecord ----
        mb.Entity<OvertimeRecord>(e =>
        {
            e.ToTable("OvertimeRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkDate).HasColumnType("date");
            e.Property(x => x.OvertimeHours).HasPrecision(9, 2);

            e.HasOne(x => x.Employee)
             .WithMany(x => x.OvertimeRecords)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmployeeId, x.WorkDate });
        });

        // ---- EndOfServiceCompensation ----
        mb.Entity<EndOfServiceCompensation>(e =>
        {
            e.ToTable("EndOfServiceCompensations");
            e.HasKey(x => x.Id);
            e.Property(x => x.TotalUnusedLeaveDays).HasPrecision(9, 2);
            e.Property(x => x.CompensatedDays).HasPrecision(9, 2);

            e.HasOne(x => x.Employee)
             .WithMany(x => x.EndOfServiceCompensations)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.EmployeeId).IsUnique();
        });

        // ---- AuditRunLog ----
        mb.Entity<AuditRunLog>(e =>
        {
            e.ToTable("AuditRunLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.HasIndex(x => x.Status);
        });
    }
}