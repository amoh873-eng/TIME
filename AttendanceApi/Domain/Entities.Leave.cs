namespace AttendanceApi.Domain;

/// <summary>طلب إجازة (يخضع لشروط المادة 4/أ و 1 يوم إشعار مسبق).</summary>
public class LeaveRequest
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int LeaveTypeId { get; set; }
    public LeaveType? LeaveType { get; set; }

    /// <summary>الموظف البديل — إلزامي (المادة 4/أ).</summary>
    public int? SubstituteEmployeeId { get; set; }
    public Employee? SubstituteEmployee { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public double DaysCount { get; set; }

    /// <summary>عدد الأيام بين تاريخ التقديم وتاريخ البدء — يجب أن يكون ≥ 1.</summary>
    public int SubmittedDaysBeforeStart { get; set; }

    public bool IsApproved { get; set; }
    /// <summary>نافذة الإجازة: لا تُحتسب كمغيّبة إلا بعد الإخطار الكتابي.</summary>
    public bool IsEffective { get; set; }

    public ICollection<SickLeaveDetail> SickLeaveDetails { get; set; } = new List<SickLeaveDetail>();
}

/// <summary>تفاصيل الإجازة المرضية لكل نوبة (المادة 112 — النسب المتناقصة).</summary>
public class SickLeaveDetail
{
    public int Id { get; set; }
    public int LeaveRequestId { get; set; }
    public LeaveRequest? LeaveRequest { get; set; }
    /// <summary>الأيام المرضية التراكمية ضمن النوبة الحالية (1-120، 121-240، 241-360).</summary>
    public int CumulativeSickDaysThisSpell { get; set; }
    /// <summary>نسبة الراتب المستحقة (100/75/50).</summary>
    public decimal SalaryPercentage { get; set; }
}