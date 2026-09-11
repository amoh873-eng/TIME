using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

/// <summary>
/// سياق قاعدة البيانات — تكوين Fluent API كامل لجميع الكيانات.
/// </summary>
public partial class AttendanceDbContext : DbContext
{
    public AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : base(options) { }

    // ---- الكيانات الأساسية ----
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<JobCategory> JobCategories => Set<JobCategory>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<WorkSchedule> WorkSchedules => Set<WorkSchedule>();

    // ---- الحضور ----
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    // ---- الإجازات ----
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<SickLeaveDetail> SickLeaveDetails => Set<SickLeaveDetail>();

    // ---- المغادرات والمخالفات ----
    public DbSet<Departure> Departures => Set<Departure>();
    public DbSet<DepartureWeekAggregate> DepartureWeekAggregates => Set<DepartureWeekAggregate>();
    public DbSet<DisciplinaryAction> DisciplinaryActions => Set<DisciplinaryAction>();
    public DbSet<MedicalCommitteeDecision> MedicalCommitteeDecisions => Set<MedicalCommitteeDecision>();

    // ---- العطل والإضافي ونهاية الخدمة ----
    public DbSet<OfficialHoliday> OfficialHolidays => Set<OfficialHoliday>();
    public DbSet<OvertimeRecord> OvertimeRecords => Set<OvertimeRecord>();
    public DbSet<EndOfServiceCompensation> EndOfServiceCompensations => Set<EndOfServiceCompensation>();

    // ---- تقرير المغادرات (جدول المرحلة + نتائج المراجعة) ----
    public DbSet<DeparturesReportRow> DeparturesReportRows => Set<DeparturesReportRow>();
    public DbSet<DeparturesReportReview> DeparturesReportReviews => Set<DeparturesReportReview>();
    public DbSet<DeparturesWeeklyLateness> WeeklyLateness => Set<DeparturesWeeklyLateness>();

    // ---- بصمات الحضور والانصراف (البصمات الخام + نتائج التحليل القانوني) ----
    public DbSet<PunchRecord> PunchRecords => Set<PunchRecord>();
    public DbSet<PunchDailyResult> PunchDailyResults => Set<PunchDailyResult>();
    public DbSet<PunchWeeklyResult> PunchWeeklyResults => Set<PunchWeeklyResult>();
    public DbSet<PunchMonthlyResult> PunchMonthlyResults => Set<PunchMonthlyResult>();

    // ---- جدول الورديات الشهري (نظام الورديات: الحراسة وبقية الإدارات ذات الورديات) ----
    public DbSet<ShiftScheduleEntry> ShiftScheduleEntries => Set<ShiftScheduleEntry>();
    public DbSet<ShiftScheduleBatch> ShiftScheduleBatches => Set<ShiftScheduleBatch>();

    // ---- تصاريح العمل الإضافي والدوام المرن ----
    public DbSet<PunchWorkApproval> PunchWorkApprovals => Set<PunchWorkApproval>();

    // ---- سجلّات التشغيل ----
    public DbSet<AuditRunLog> AuditRunLogs => Set<AuditRunLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        AttendanceDbContextConfig.ConfigureOrganization(modelBuilder);
        AttendanceDbContextConfig.ConfigureAttendance(modelBuilder);
        AttendanceDbContextConfig.ConfigureLeave(modelBuilder);
        AttendanceDbContextConfig.ConfigureDeparture(modelBuilder);
        AttendanceDbContextConfig.ConfigureMisc(modelBuilder);
        AttendanceDbContextConfig.ConfigureDeparturesReport(modelBuilder);
        AttendanceDbContextConfig.ConfigurePunchReport(modelBuilder);
        AttendanceDbContextConfig.ConfigureShifts(modelBuilder);
        AttendanceDbContextConfig.ConfigureWorkApprovals(modelBuilder);
    }
}