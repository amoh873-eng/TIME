namespace AttendanceApi.Domain;

/// <summary>
/// عطلة رسمية أو دينية في تقويم العطل المعتمد لتحليل الحضور والانصراف.
/// تُستخدم لاستثناء أيام العطل من احتساب الغياب والمخالفات (المادتان 7 و118).
/// </summary>
public class OfficialHoliday
{
    public int Id { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Description { get; set; } = default!;

    /// <summary>تصنيف العطلة (رسمية / دينية إسلامية / دينية مسيحية / مناسبة وطنية).</summary>
    public HolidayKind Kind { get; set; } = HolidayKind.Official;

    /// <summary>مصدر القيد: «كتالوج النظام» أو «إدخال يدوي» أو «استيراد Excel».</summary>
    public string? Source { get; set; }

    /// <summary>وقت إدراج القيد في جدول العطل.</summary>
    public DateTime? CreatedAtUtc { get; set; }

    /// <summary>
    /// إلغاء/استبعاد تاريخ عطلة (يُستخدم لاستبعاد عطلة من الكتالوج المدمج
    /// عندما يقرّر المشغّل أنها لم تعد عطلة رسمية) دون حذف سجل الكتالوج نفسه.
    /// </summary>
    public bool IsCancelled { get; set; }
}

/// <summary>سجل عمل إضافي.</summary>
public class OvertimeRecord
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly WorkDate { get; set; }
    public double OvertimeHours { get; set; }
}

/// <summary>تعويض نهاية الخدمة عن الإجازات غير المستخدمة — سقف 60 يوماً (المادة 105).</summary>
public class EndOfServiceCompensation
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public double TotalUnusedLeaveDays { get; set; }
    public double CompensatedDays { get; set; }
}

/// <summary>سجلّ تشغيل لكل دورة مراجعة (Audit Engine).</summary>
public class AuditRunLog
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public AuditRunStatus Status { get; set; }
    public long RecordsProcessed { get; set; }
    public string? ErrorMessage { get; set; }
}