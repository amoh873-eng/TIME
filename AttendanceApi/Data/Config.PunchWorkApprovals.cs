using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    /// <summary>
    /// تكوين «تصاريح العمل الإضافي والدوام المرن»:
    /// تصريح ساري لكل موظف بفترة محدّدة وحدود ساعات (يُدار من الواجهة في وقت التشغيل).
    /// </summary>
    public static void ConfigureWorkApprovals(ModelBuilder mb)
    {
        mb.Entity<PunchWorkApproval>(e =>
        {
            e.ToTable("PunchWorkApprovals");
            e.HasKey(x => x.Id);

            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.EmployeeName).HasMaxLength(200);
            e.Property(x => x.DepartmentName).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.Source).HasMaxLength(100);

            e.Property(x => x.FromDate).HasColumnType("date");
            e.Property(x => x.ToDate).HasColumnType("date");
            e.Property(x => x.IsActive).HasDefaultValue(true);

            e.HasIndex(x => new { x.JobNumber, x.Kind })
             .HasDatabaseName("IX_PunchWorkApprovals_Employee");
            e.HasIndex(x => new { x.FromDate, x.ToDate })
             .HasDatabaseName("IX_PunchWorkApprovals_Period");
        });
    }
}
