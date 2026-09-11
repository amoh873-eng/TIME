using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureAttendance(ModelBuilder mb)
    {
        mb.Entity<AttendanceRecord>(e =>
        {
            e.ToTable("AttendanceRecords");
            e.HasKey(x => x.Id);

            e.Property(x => x.CheckInDate).HasColumnType("date");
            e.Property(x => x.CheckInTime).HasColumnType("time");
            e.Property(x => x.CheckOutTime).HasColumnType("time");

            e.HasOne(x => x.Employee)
             .WithMany(x => x.AttendanceRecords)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.WorkSchedule)
             .WithMany(x => x.AttendanceRecords)
             .HasForeignKey(x => x.WorkScheduleId)
             .OnDelete(DeleteBehavior.SetNull);

            // ---- الفهارس المركبة المطلوبة (Indexing Strategy) ----
            e.HasIndex(x => new { x.EmployeeId, x.CheckInDate })
             .HasDatabaseName("IX_Attendance_Employee_Date");

            e.HasIndex(x => new { x.EmployeeId, x.Month, x.Year })
             .HasDatabaseName("IX_Attendance_Employee_Month");

            e.HasIndex(x => new { x.EmployeeId, x.WeekNumber, x.Year })
             .HasDatabaseName("IX_Attendance_Employee_Week");
        });
    }
}