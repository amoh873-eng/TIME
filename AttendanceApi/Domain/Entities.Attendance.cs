namespace AttendanceApi.Domain;

/// <summary>سجل حضور يومي (يدعم الملايين من السجلات).</summary>
public class AttendanceRecord
{
    public long Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int? WorkScheduleId { get; set; }
    public WorkSchedule? WorkSchedule { get; set; }

    public DateOnly CheckInDate { get; set; }
    public TimeOnly CheckInTime { get; set; }
    public TimeOnly? CheckOutTime { get; set; }

    /// <summary>دقائق التأخير الصباحي المحسوبة حسب الجدول.</summary>
    public int LateMinutes { get; set; }
    /// <summary>دقائق الانصراف المبكر المحسوبة حسب الجدول.</summary>
    public int EarlyDepartureMinutes { get; set; }

    // حقول مشتقة للفهرسة والتجميع (تُملأ عند الإدخال/المراجعة)
    public int Month { get; set; }
    public int Year { get; set; }
    public int WeekNumber { get; set; }

    public AttendanceSession Session { get; set; } = AttendanceSession.Morning;
}

/// <summary>نوع الإجازة (سنوية/مرضية/إجازة أمومة...).</summary>
public class LeaveType
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>هل تُتراكم (تُرصّد) عبر السنوات. الإجازة المرضية لا تُرصّد (المادة 112).</summary>
    public bool IsCumulative { get; set; }
    public ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
}

/// <summary>رصيد الإجازة السنوية لموظف في سنة معينة.</summary>
public class LeaveBalance
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int Year { get; set; }
    public double EntitlementDays { get; set; }
    public double CarriedOverDays { get; set; }
    public double UsedDays { get; set; }
    public double RemainingBalance => EntitlementDays + CarriedOverDays - UsedDays;
}