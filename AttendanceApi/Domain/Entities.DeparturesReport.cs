namespace AttendanceApi.Domain;

/// <summary>
/// صف خام من «تقرير المغادرات» (تصدير الطلبات العربية من نظام الموارد البشرية).
/// يُحفظ في جدول مرحلة يحافظ على الأعمدة كما وردت + حقول مُطبَّعة للحساب القانوني.
/// </summary>
public class DeparturesReportRow
{
    public long Id { get; set; }

    // ---- الأعمدة الأصلية كما وردت في التقرير ----
    /// <summary>رقم الطلب</summary>
    public string? RequestNumber { get; set; }
    /// <summary>الرقم الوظيفي (نص لأن القيم تتجاوز حدود int).</summary>
    public string JobNumber { get; set; } = default!;
    /// <summary>الموظف</summary>
    public string? EmployeeName { get; set; }
    /// <summary>من وقت</summary>
    public TimeOnly? FromTime { get; set; }
    /// <summary>إلى وقت</summary>
    public TimeOnly? ToTime { get; set; }
    /// <summary>من تاريخ</summary>
    public DateOnly? FromDate { get; set; }
    /// <summary>إلى تاريخ</summary>
    public DateOnly? ToDate { get; set; }
    /// <summary>المدة (نص عربي مثل: 7 ساعات / 1 ساعات / 30 دقيقة / يوم).</summary>
    public string? DurationText { get; set; }
    /// <summary>حالة الطلب (مقبول / مرفوض / معلق).</summary>
    public string? Status { get; set; }
    /// <summary>نوع الطلب (استئذان / اجازة / رسمية...).</summary>
    public string? RequestType { get; set; }
    /// <summary>تاريخ الطلب</summary>
    public DateOnly? RequestDate { get; set; }
    /// <summary>الإدارة</summary>
    public string? DepartmentName { get; set; }
    /// <summary>الإدارة الرئيسية</summary>
    public string? MainDepartmentName { get; set; }

    // ---- حقول مُطبَّعة للحساب ----
    /// <summary>المدة بالدقائق (من الأوقات إن وُجدت، وإلا من نص المدة).</summary>
    public int DurationMinutes { get; set; }
    /// <summary>عدد الأيام (الفرق بين التاريخين، وإلا من نص المدة).</summary>
    public double DaysCount { get; set; }
    /// <summary>هل الطلب استئذان (يخضع للمادة 118/ب)؟</summary>
    public bool IsAuthorization { get; set; }
    /// <summary>هل تجاوزت المدة 4 ساعات (المادة 118/ب)؟</summary>
    public bool Exceeds4Hours { get; set; }
    /// <summary>تصنيف الطلب (سنوية/مرضية/أخرى...).</summary>
    public ReportLeaveCategory Category { get; set; }

    // ---- بيانات الإدخال ----
    public string? SourceFile { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>تصنيف طلب المغادرة/الإجازة المستخرج من «نوع الطلب».</summary>
public enum ReportLeaveCategory
{
    Unknown = 0,
    /// <summary>استئذان (طارئ/طبي...) — المادة 118/ب.</summary>
    Authorization = 1,
    /// <summary>إجازة سنوية.</summary>
    AnnualLeave = 2,
    /// <summary>إجازة مرضية — المادة 112.</summary>
    SickLeave = 3,
    /// <summary>مهمة رسمية / انتداب / تدريب (لا تُحتسب غياباً).</summary>
    OfficialDuty = 4,
    /// <summary>أخرى (وفاة/تعويضية/أمومة...).</summary>
    Other = 5
}

/// <summary>
/// نتيجة مراجعة شهرية لكل موظف من تقرير المغادرات
/// (المادة 118/ب + قاعدة المكافأة الشهرية — حد الـ 15 يوماً).
/// </summary>
public class DeparturesReportReview
{
    public long Id { get; set; }
    /// <summary>الرقم الوظيفي كما ورد في التقرير.</summary>
    public string JobNumber { get; set; } = default!;
    public string? EmployeeName { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }

    public int AuthorizationCount { get; set; }
    /// <summary>عدد الاستئذانات التي تجاوزت 4 ساعات.</summary>
    public int AuthorizationOver4hCount { get; set; }
    public int TotalAuthorizationMinutes { get; set; }

    /// <summary>أيام الخصم وفق المادة 118/ب (يوم لكل استئذان &gt; 4 ساعات).</summary>
    public double Article118bDays { get; set; }
    /// <summary>مجموع دقائق التأخير/الانصراف المبكر داخل الدوام الرسمي المحتسبة أسبوعياً (المادة 118/ج).</summary>
    public int WeeklyLateMinutes { get; set; }
    /// <summary>عدد الأسابيع التي بلغ مجموع دقائقها 60 دقيقة أو أكثر (المادة 118/ج).</summary>
    public int WeeksOver60Minutes { get; set; }
    /// <summary>أيام الخصم وفق المادة 118/ج (يوم كامل لكل أسبوع بلغ 60 دقيقة).</summary>
    public double Article118cDays { get; set; }
    public double AnnualLeaveDays { get; set; }
    public double SickLeaveDays { get; set; }
    public double OfficialDutyDays { get; set; }
    public double OtherLeaveDays { get; set; }

    /// <summary>إجمالي أيام الغياب = سنوية + مرضية + 118/ب + 118/ج.</summary>
    public double TotalAbsenceDays { get; set; }
    /// <summary>نسبة خصم المكافأة (50% عند تجاوز 15 يوماً).</summary>
    public double BonusDeductionPercent { get; set; }
    public bool Exceeds15Days { get; set; }
    /// <summary>هل يوجد مخالفة تستوجب المراجعة اليدوية؟</summary>
    public bool HasViolation { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// التجميع الأسبوعي لدقائق التأخير/الانصراف المبكر لكل موظف (المادة 118/ج)
/// من «تقرير المغادرات»: مجموع الدقائق داخل ساعات الدوام الرسمي (08:30–15:30)
/// خلال الأسبوع؛ فإذا بلغ 60 دقيقة أو أكثر → يُحسم يوم كامل من رصيد الإجازات.
/// </summary>
public class DeparturesWeeklyLateness
{
    public long Id { get; set; }
    /// <summary>الرقم الوظيفي كما ورد في التقرير.</summary>
    public string JobNumber { get; set; } = default!;
    public string? EmployeeName { get; set; }

    /// <summary>أول يوم من الأسبوع (الاثنين).</summary>
    public DateOnly WeekStart { get; set; }
    /// <summary>آخر يوم ظهرت فيه مغادرة محتسبة داخل الأسبوع.</summary>
    public DateOnly WeekEnd { get; set; }
    /// <summary>السنة المنسوب إليها الأسبوع (سنة أول مغادرة محتسبة).</summary>
    public int Year { get; set; }
    /// <summary>الشهر المنسوب إليه الأسبوع (شهر أول مغادرة محتسبة).</summary>
    public int Month { get; set; }

    /// <summary>عدد المغادرات/الاستئذانات الداخلة في التجميع.</summary>
    public int DepartureCount { get; set; }
    /// <summary>مجموع الدقائق الواقعة داخل ساعات الدوام الرسمي.</summary>
    public int LateMinutes { get; set; }
    /// <summary>هل بلغت 60 دقيقة أو أكثر (المادة 118/ج)؟</summary>
    public bool Exceeds60Minutes { get; set; }
    /// <summary>أيام الخصم (1.0 يوم عند بلوغ 60 دقيقة).</summary>
    public double DeductionDays { get; set; }
    /// <summary>تفصيل أوقات المغادرات المحتسبة داخل الأسبوع.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// ملخص نتائج المراجعة كما هي محفوظة في قاعدة البيانات
/// (يُستخدم لعرض اللوحة مباشرةً من قاعدة البيانات بلا إعادة معالجة).
/// </summary>
public sealed record DeparturesReviewSummary(
    long TotalRequests,
    long AcceptedRequests,
    int EmployeesReviewed,
    int EmployeesWithViolations,
    int EmployeesOver4Hours,
    double TotalArticle118bDays,
    int EmployeesExceeding15Days,
    int WeeksOver60Minutes,
    int EmployeesOver60Minutes,
    int TotalWeeklyLateMinutes,
    double TotalArticle118cDays,
    DateTime? LastImportUtc,
    string? SourceLabel,
    IReadOnlyDictionary<string, int> RequestTypes,
    int Reviews,
    bool HasData);
