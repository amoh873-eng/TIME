using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureOrganization(ModelBuilder mb)
    {
        // ---- Department ----
        mb.Entity<Department>(e =>
        {
            e.ToTable("Departments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();

            e.HasOne(x => x.Parent)
             .WithMany(x => x.Children)
             .HasForeignKey(x => x.ParentDepartmentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- JobCategory ----
        mb.Entity<JobCategory>(e =>
        {
            e.ToTable("JobCategories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.MaxAnnualLeaveDays).HasDefaultValue(30);
        });

        // ---- Employee ----
        mb.Entity<Employee>(e =>
        {
            e.ToTable("Employees");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Gender).HasMaxLength(10).IsRequired();
            e.HasIndex(x => x.JobNumber).IsUnique();

            e.HasOne(x => x.Department)
             .WithMany(x => x.Employees)
             .HasForeignKey(x => x.DepartmentId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.JobCategory)
             .WithMany(x => x.Employees)
             .HasForeignKey(x => x.JobCategoryId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- WorkSchedule ----
        mb.Entity<WorkSchedule>(e =>
        {
            e.ToTable("WorkSchedules");
            e.HasKey(x => x.Id);
            e.Property(x => x.DailyHours).HasPrecision(5, 2);
            e.Property(x => x.WeeklyHours).HasPrecision(5, 2);
        });
    }
}