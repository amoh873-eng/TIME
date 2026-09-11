using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureLeave(ModelBuilder mb)
    {
        // ---- LeaveType ----
        mb.Entity<LeaveType>(e =>
        {
            e.ToTable("LeaveTypes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        // ---- LeaveBalance ----
        mb.Entity<LeaveBalance>(e =>
        {
            e.ToTable("LeaveBalances");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntitlementDays).HasPrecision(9, 2);
            e.Property(x => x.CarriedOverDays).HasPrecision(9, 2);
            e.Property(x => x.UsedDays).HasPrecision(9, 2);

            e.HasOne(x => x.Employee)
             .WithMany(x => x.LeaveBalances)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmployeeId, x.Year }).IsUnique();
        });

        // ---- LeaveRequest ----
        mb.Entity<LeaveRequest>(e =>
        {
            e.ToTable("LeaveRequests");
            e.HasKey(x => x.Id);
            e.Property(x => x.StartDate).HasColumnType("date");
            e.Property(x => x.EndDate).HasColumnType("date");
            e.Property(x => x.DaysCount).HasPrecision(9, 2);

            e.HasOne(x => x.Employee)
             .WithMany(x => x.LeaveRequests)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.LeaveType)
             .WithMany(x => x.LeaveRequests)
             .HasForeignKey(x => x.LeaveTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            // البديل إلزامي (المادة 4/أ)
            e.HasOne(x => x.SubstituteEmployee)
             .WithMany()
             .HasForeignKey(x => x.SubstituteEmployeeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.EmployeeId, x.StartDate });
            e.HasIndex(x => new { x.EmployeeId, x.LeaveTypeId, x.StartDate });
        });

        // ---- SickLeaveDetail ----
        mb.Entity<SickLeaveDetail>(e =>
        {
            e.ToTable("SickLeaveDetails");
            e.HasKey(x => x.Id);
            e.Property(x => x.SalaryPercentage).HasPrecision(5, 2);

            e.HasOne(x => x.LeaveRequest)
             .WithMany(x => x.SickLeaveDetails)
             .HasForeignKey(x => x.LeaveRequestId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}