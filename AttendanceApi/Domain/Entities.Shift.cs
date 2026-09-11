namespace AttendanceApi.Domain;

/// <summary>
/// تصنيف يوم الموظف وفق «جدول الورديات الشهري» الصادر من مسؤول الورديات
/// (مثال: الحراسة 24 ساعة متواصلة) — يُستخدم في التحليل بدل ساعات الدوام الرسمي.
/// </summary>
public enum ShiftDayKind
{
    /// <summary>رمز غير معروف/فارغ (لا قيد يذكر).</summary>
    Unknown = 0,
    /// <summary>وردية دوام (نهار/ليل/24 ساعة...).</summary>
    Duty = 1,
    /// <summary>راحة/عدم دوام وفق الجدول (لا تُحتسب غياباً).</summary>
    Rest = 2,
    /// <summary>إجازة وفق الجدول (سنوية/مرضية/طارئة...).</summary>
    Leave = 3,
    /// <summary>عطلة رسمية وفق الجدول.</summary>
    Holiday = 4,
    /// <summary>دورة/تدريب/مهمة رسمية وفق الجدول.</summary>
    Training = 5
}

/// <summary>
/// قيد يوم واحد لموظف في «جدول الورديات الشهري» (وردية، راحة، إجازة...) كما ورد في ملف مسؤول الورديات.
/// </summary>
public class ShiftScheduleEntry
{
    public long Id { get; set; }

    /// <summary>الرقم الوظيفي (نص لأن القيم تتجاوز حدود int).</summary>
    public string JobNumber { get; set; } = default!;
    /// <summary>اسم الموظف كما ورد في الجدول.</summary>
    public string? EmployeeName { get; set; }
    /// <summary>الإدارة/المديرية كما وردت في الجدول.</summary>
    public string? DepartmentName { get; set; }
    /// <summary>تاريخ اليوم (ميلادي).</summary>
    public DateOnly DutyDate { get; set; }
    /// <summary>رمز الوردية كما ورد في الجدول (ن، ل، ر، 24...).</summary>
    public string ShiftCode { get; set; } = default!;
    /// <summary>التصنيف بعد توحيد الرموز.</summary>
    public ShiftDayKind Kind { get; set; }
    /// <summary>ساعات الوردية (الافتراضي من إعدادات نظام الورديات).</summary>
    public double ShiftHours { get; set; }
    /// <summary>ملاحظات المدقق على القيد.</summary>
    public string? Notes { get; set; }
    /// <summary>معرّف دفعة الاستيراد التي أنشأت القيد (لحذف الجدول كاملاً عند الخطأ).</summary>
    public string BatchId { get; set; } = default!;
    /// <summary>مفتاح الفترة (سنة-شهر) التي يخصّها الجدول.</summary>
    public string? BatchKey { get; set; }
    public string? SourceFile { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>دفعة استيراد لجدول ورديات شهري (تتبّع الملف وفترته وعدد قيوده).</summary>
public class ShiftScheduleBatch
{
    public long Id { get; set; }
    /// <summary>معرّف الدفعة (يُختم به كل قيد مستورد منها).</summary>
    public string BatchId { get; set; } = default!;
    /// <summary>مفتاح الفترة (سنة-شهر) أو الشهر المستورد.</summary>
    public string? BatchKey { get; set; }
    public string FileName { get; set; } = default!;
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public int Rows { get; set; }
    public int Employees { get; set; }
    /// <summary>عدد قيود الدفعة بحسب التصنيف.</summary>
    public int DutyDays { get; set; }
    public int RestDays { get; set; }
    public int LeaveDays { get; set; }
    /// <summary>صيغة الملف المستورد (شبكة شهرية / جدول طويل).</summary>
    public string? Format { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>سطر جدول ورديات في الواجهة (قيد يوم لموظف).</summary>
public sealed record PunchShiftScheduleEntryRow(
    string JobNumber,
    string? EmployeeName,
    string? DepartmentName,
    string DateText,
    string DayName,
    string ShiftCode,
    string KindText,
    double Hours);

/// <summary>سطر دفعة استيراد جدول الورديات في الواجهة.</summary>
public sealed record PunchShiftScheduleBatchRow(
    long Id,
    string BatchId,
    string? BatchKey,
    string FileName,
    string PeriodFrom,
    string PeriodTo,
    int Rows,
    int Employees,
    int DutyDays,
    int RestDays,
    int LeaveDays,
    string Format,
    string ImportedAtText);

/// <summary>عرض «جدول الورديات الشهري»: المؤشرات + دفعات الاستيراد + عيّنة من القيود.</summary>
public sealed record PunchShiftScheduleView(
    PunchShiftRule Rule,
    string RuleSummary,
    bool Enabled,
    int Batches,
    int Employees,
    int Entries,
    int DutyDays,
    int RestDays,
    int LeaveDays,
    int OtherDays,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Months,
    IReadOnlyList<PunchShiftScheduleBatchRow> BatchList,
    IReadOnlyList<PunchShiftScheduleEntryRow> Samples);

/// <summary>نتيجة استيراد جدول ورديات شهري.</summary>
public sealed record PunchShiftImportResult(
    string FileName,
    string BatchId,
    string? BatchKey,
    string Format,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    int Rows,
    int Employees,
    int DutyDays,
    int RestDays,
    int LeaveDays,
    int Replaced,
    DateTime ImportedAtUtc);
