using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    /// <summary>
    /// تكوين «جدول الورديات الشهري»: قيود أيام الموظفين بنظام الورديات (وردية/راحة/إجازة)
    /// + دفعات الاستيراد الصادرة من مسؤول الورديات.
    /// </summary>
    public static void ConfigureShifts(ModelBuilder mb)
    {
        mb.Entity<ShiftScheduleEntry>(e =>
        {
            e.ToTable("ShiftScheduleEntries");
            e.HasKey(x => x.Id);

            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.ShiftCode).HasMaxLength(40);
            e.Property(x => x.Notes).HasMaxLength(300);
            e.Property(x => x.BatchId).HasMaxLength(40).IsRequired();
            e.Property(x => x.BatchKey).HasMaxLength(20);
            e.Property(x => x.SourceFile).HasMaxLength(300);
            e.Property(x => x.ShiftHours).HasPrecision(9, 2);

            e.Property(x => x.DutyDate).HasColumnType("date");

            e.HasIndex(x => new { x.JobNumber, x.DutyDate }).IsUnique()
             .HasDatabaseName("IX_ShiftSchedule_Employee_Day");
            e.HasIndex(x => x.DutyDate).HasDatabaseName("IX_ShiftSchedule_Date");
            e.HasIndex(x => x.BatchId).HasDatabaseName("IX_ShiftSchedule_Batch");
            e.HasIndex(x => x.Kind).HasDatabaseName("IX_ShiftSchedule_Kind");
        });

        mb.Entity<ShiftScheduleBatch>(e =>
        {
            e.ToTable("ShiftScheduleBatches");
            e.HasKey(x => x.Id);

            e.Property(x => x.BatchId).HasMaxLength(40).IsRequired();
            e.Property(x => x.BatchKey).HasMaxLength(40);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.Format).HasMaxLength(60);

            e.Property(x => x.PeriodFrom).HasColumnType("date");
            e.Property(x => x.PeriodTo).HasColumnType("date");

            e.HasIndex(x => x.BatchId).IsUnique().HasDatabaseName("IX_ShiftBatches_Batch");
            e.HasIndex(x => x.BatchKey).HasDatabaseName("IX_ShiftBatches_Key");
        });
    }
}
