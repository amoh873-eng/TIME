namespace AttendanceApi.Domain;

/// <summary>سجل مغادرة/انصراف (المادة 118/ب).</summary>
public class Departure
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly DepartureDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public DepartureType DepartureType { get; set; }

    /// <summary>مغادرات الصباح تتطلب موافقة المدير في اليوم السابق.</summary>
    public bool RequiresPriorDayManagerApproval { get; set; }
    /// <summary>هل تجاوزت 4 ساعات (المادة 118/ب).</summary>
    public bool IsExceeding4Hours { get; set; }
    /// <summary>أيام الخصم المعادلة (1.0 عند التجاوز).</summary>
    public double EquivalentDaysDeduction { get; set; }

    /// <summary>أسبوع/سنة للتجميع الأسبوعي (المادة 118/ج).</summary>
    public int WeekNumber { get; set; }
    public int Year { get; set; }
}

/// <summary>تجميع أسبوعي لدقائق التأخير/الانصراف المبكر (المادة 118/ج).</summary>
public class DepartureWeekAggregate
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int WeekNumber { get; set; }
    public int Year { get; set; }
    public int TotalLateMinutes { get; set; }
    /// <summary>أيام الخصم: 1.0 عند بلوغ 60 دقيقة أسبوعياً (المادة 118/ج).</summary>
    public double DeductionDays { get; set; }
}

/// <summary>إجراء تأديبي شهري (المادة 7).</summary>
public class DisciplinaryAction
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public int LateCount { get; set; }
    public DisciplinaryActionType ActionType { get; set; }
}

/// <summary>قرار اللجنة الطبية (المادة 112 — إعادة الفحص).</summary>
public class MedicalCommitteeDecision
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly DecisionDate { get; set; }
    public MedicalDecisionType DecisionType { get; set; }
}