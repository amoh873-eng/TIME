namespace AttendanceApi.Domain;

/// <summary>نوع التصريح/الموافقة المسجّلة لموظف (عمل إضافي أو دوام مرن).</summary>
public enum WorkApprovalKind
{
    /// <summary>تصريح بالعمل الإضافي (بحدود ساعات).</summary>
    Overtime = 1,

    /// <summary>تصريح بالدوام المرن (نافذة حضور وإكمال ساعات).</summary>
    Flexible = 2
}

/// <summary>
/// تصريح ساري لموظف بالعمل الإضافي أو بالدوام المرن خلال فترة محدّدة
/// (وفق ما تقتضيه ضوابط الخدمة المدنية من تكليف/موافقة مسبقة):
/// يُحتسب للموظف العمل الإضافي أو يُطبَّق عليه الدوام المرن داخل الفترة فقط.
/// </summary>
public class PunchWorkApproval
{
    public long Id { get; set; }

    /// <summary>نوع التصريح: عمل إضافي أو دوام مرن.</summary>
    public WorkApprovalKind Kind { get; set; }

    /// <summary>الرقم الوظيفي (نص لأن القيم تتجاوز حدود int).</summary>
    public string JobNumber { get; set; } = default!;

    /// <summary>اسم الموظف كما ورد في التصريح.</summary>
    public string? EmployeeName { get; set; }

    /// <summary>الإدارة/المديرية كما وردت في التصريح.</summary>
    public string? DepartmentName { get; set; }

    /// <summary>بداية سريان التصريح.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>نهاية سريان التصريح.</summary>
    public DateOnly ToDate { get; set; }

    /// <summary>حدّ الساعات المسموح بها يومياً لهذا التصريح (بالدقائق؛ null = يتبع الإعدادات).</summary>
    public int? MaxMinutesPerDay { get; set; }

    /// <summary>إجمالي الساعات المسموح بها للتصريح كاملاً (بالدقائق؛ null = يتبع السقف الشهري العام).</summary>
    public int? MaxMinutesTotal { get; set; }

    /// <summary>ملاحظة/سند التصريح (رقم الكتاب، الجهة المصرِّحة...).</summary>
    public string? Note { get; set; }

    /// <summary>هل التصريح ساري (يمكن إيقافه بلا حذف للتدقيق)؟</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>مصدر القيد: «إدخال يدوي» أو «استيراد نصي».</summary>
    public string? Source { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>هل يغطّي هذا التصريح تاريخاً محدّداً؟</summary>
    public bool Covers(DateOnly date) => IsActive && date >= FromDate && date <= ToDate;

    /// <summary>أيام سريان التصريح.</summary>
    public int Days => Math.Max(0, ToDate.DayNumber - FromDate.DayNumber + 1);
}

/// <summary>
/// فهرس تصاريح موظف واحد (عمل إضافي/دوام مرن) للبحث السريع داخل محرّك التحليل
/// بلا استعلامات متكرّرة على قاعدة البيانات.
/// </summary>
public sealed class PunchWorkApprovalIndex
{
    private readonly Dictionary<string, List<PunchWorkApproval>> _byEmployee;
    private readonly Dictionary<string, List<PunchWorkApproval>> _byDepartment;

    /// <summary>فهرس فارغ (يُستخدم عند غياب التصاريح).</summary>
    public static readonly PunchWorkApprovalIndex Empty = new(Array.Empty<PunchWorkApproval>());

    public PunchWorkApprovalIndex(IEnumerable<PunchWorkApproval>? approvals)
    {
        _byEmployee = new Dictionary<string, List<PunchWorkApproval>>(StringComparer.OrdinalIgnoreCase);
        _byDepartment = new Dictionary<string, List<PunchWorkApproval>>(StringComparer.OrdinalIgnoreCase);

        foreach (var approval in approvals ?? Array.Empty<PunchWorkApproval>())
        {
            if (string.IsNullOrWhiteSpace(approval.JobNumber))
            {
                continue;
            }

            var key = PunchFlexibleRule.NormalizeJobNumber(approval.JobNumber);
            if (!_byEmployee.TryGetValue(key, out var list))
            {
                list = new List<PunchWorkApproval>();
                _byEmployee[key] = list;
            }

            list.Add(approval);

            var department = PunchDepartmentMatcher.Normalize(approval.DepartmentName);
            if (department.Length > 0)
            {
                if (!_byDepartment.TryGetValue(department, out var byDept))
                {
                    byDept = new List<PunchWorkApproval>();
                    _byDepartment[department] = byDept;
                }

                byDept.Add(approval);
            }
        }
    }

    /// <summary>هل يوجد أي تصريح محمّل؟</summary>
    public bool HasAny => _byEmployee.Count > 0;

    /// <summary>عدد التصاريح المحمّلة.</summary>
    public int Count => _byEmployee.Sum(kv => kv.Value.Count);

    /// <summary>
    /// التصريح الساري للموظف في تاريخ محدّد (بحسب نوعه)، ويُرجَّح الأوسع حدّاً عند التعدّد.
    /// </summary>
    public PunchWorkApproval? Find(string? jobNumber, WorkApprovalKind kind, DateOnly date)
    {
        var approvals = All(jobNumber)
            .Where(a => a.Kind == kind && a.Covers(date))
            .ToList();

        if (approvals.Count == 0)
        {
            return null;
        }

        return approvals
            .OrderByDescending(a => a.MaxMinutesPerDay ?? int.MaxValue)
            .ThenByDescending(a => a.ToDate)
            .First();
    }

    /// <summary>كل تصاريح موظف (بأي نوع).</summary>
    public IReadOnlyList<PunchWorkApproval> All(string? jobNumber) =>
        string.IsNullOrWhiteSpace(jobNumber)
            || !_byEmployee.TryGetValue(PunchFlexibleRule.NormalizeJobNumber(jobNumber), out var list)
                ? Array.Empty<PunchWorkApproval>()
                : list;

    /// <summary>كل تصاريح إدارة محدّدة (بأي نوع).</summary>
    public IReadOnlyList<PunchWorkApproval> ByDepartment(string? department)
    {
        var key = PunchDepartmentMatcher.FindKey(_byDepartment, department);
        return key is null ? Array.Empty<PunchWorkApproval>() : _byDepartment[key];
    }
}

/// <summary>سطر تصريح في الواجهة (عمل إضافي / دوام مرن).</summary>
public sealed record PunchWorkApprovalRow(
    long Id,
    string Kind,
    string JobNumber,
    string? EmployeeName,
    string? DepartmentName,
    string FromText,
    string ToText,
    int Days,
    int? MaxMinutesPerDay,
    int? MaxMinutesTotal,
    bool IsActive,
    string Source,
    string? Note);

/// <summary>عرض «التصاريح والموافقات» في الواجهة: القواعد السارية + المؤشرات + التصاريح المسجّلة.</summary>
public sealed record PunchWorkApprovalsView(
    PunchFlexibleRule Flexible,
    PunchOvertimeRule Overtime,
    string FlexibleSummary,
    string OvertimeSummary,
    int Total,
    int OvertimeCount,
    int FlexibleCount,
    int ActiveCount,
    int EmployeesCount,
    bool HasApprovals,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    IReadOnlyList<PunchWorkApprovalRow> Items);

/// <summary>نتيجة استيراد/إضافة تصاريح من قائمة نصية.</summary>
public sealed record PunchWorkApprovalBulkResult(int Added, int Skipped, IReadOnlyList<string> Errors);

