using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

public static partial class AttendanceDbContextConfig
{
    public static void ConfigureDeparture(ModelBuilder mb)
    {
        // ---- Departure ----
        mb.Entity<Departure>(e =>
        {
            e.ToTable("Departures");
            e.HasKey(x => x.Id);
            e.Property(x => x.DepartureDate).HasColumnType("date");
            e.Property(x => x.StartTime).HasColumnType("time");
            e.Property(x => x.EndTime).HasColumnType("time");

            e.HasOne(x => x.Employee)
             .WithMany(x => x.Departures)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmployeeId, x.DepartureDate });
            e.HasIndex(x => new { x.EmployeeId, x.WeekNumber, x.Year });
        });

        // ---- DepartureWeekAggregate (المادة 118/ج) ----
        mb.Entity<DepartureWeekAggregate>(e =>
        {
            e.ToTable("DepartureWeekAggregates");
            e.HasKey(x => x.Id);
            e.Property(x => x.DeductionDays).HasPrecision(9, 2);
            e.HasIndex(x => new { x.EmployeeId, x.WeekNumber, x.Year }).IsUnique();
        });

        // ---- DisciplinaryAction (المادة 7) ----
        mb.Entity<DisciplinaryAction>(e =>
        {
            e.ToTable("DisciplinaryActions");
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Employee)
             .WithMany(x => x.DisciplinaryActions)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmployeeId, x.Year, x.Month }).IsUnique();
        });

        // ---- MedicalCommitteeDecision (المادة 112) ----
        mb.Entity<MedicalCommitteeDecision>(e =>
        {
            e.ToTable("MedicalCommitteeDecisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.DecisionDate).HasColumnType("date");

            e.HasOne(x => x.Employee)
             .WithMany(x => x.MedicalCommitteeDecisions)
             .HasForeignKey(x => x.EmployeeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.EmployeeId, x.DecisionDate });
        });
    }
}