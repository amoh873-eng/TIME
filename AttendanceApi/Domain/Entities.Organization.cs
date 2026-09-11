namespace AttendanceApi.Domain;

// =====================================================================
//  الكيانات الأساسية (الهيكل التنظيمي والموظفون)
// =====================================================================

/// <summary>قسم / مديرية (يدعم التسلسل الهرمي عبر ParentDepartmentId).</summary>
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int? ParentDepartmentId { get; set; }
    public Department? Parent { get; set; }
    public ICollection<Department> Children { get; set; } = new List<Department>();
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

/// <summary>فئة وظيفية (بطاقة/مجموعة) تحدد أقصى رصيد إجازة سنوية.</summary>
public class JobCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public ClassGroup ClassGroup { get; set; }
    public int MaxAnnualLeaveDays { get; set; }
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}

/// <summary>الموظف — الحامل المركزي لكل القواعد.</summary>
public class Employee
{
    public int Id { get; set; }
    public string JobNumber { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int JobCategoryId { get; set; }
    public JobCategory? JobCategory { get; set; }
    public DateTime HiringDate { get; set; }
    public string Gender { get; set; } = default!;
    public ContractType ContractType { get; set; }
    public WorkMode WorkMode { get; set; }

    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
    public ICollection<LeaveBalance> LeaveBalances { get; set; } = new List<LeaveBalance>();
    public ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
    public ICollection<Departure> Departures { get; set; } = new List<Departure>();
    public ICollection<DisciplinaryAction> DisciplinaryActions { get; set; } = new List<DisciplinaryAction>();
    public ICollection<MedicalCommitteeDecision> MedicalCommitteeDecisions { get; set; } = new List<MedicalCommitteeDecision>();
    public ICollection<OvertimeRecord> OvertimeRecords { get; set; } = new List<OvertimeRecord>();
    public ICollection<EndOfServiceCompensation> EndOfServiceCompensations { get; set; } = new List<EndOfServiceCompensation>();
}

/// <summary>جدول العمل (نظام الدوام).</summary>
public class WorkSchedule
{
    public int Id { get; set; }
    public ScheduleType ScheduleType { get; set; }
    public double DailyHours { get; set; }
    public double WeeklyHours { get; set; }
    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
}