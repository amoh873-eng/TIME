namespace AttendanceApi.Domain;

/// <summary>
/// تصنيف يوم الموظف كما يخرج من «تقرير الحضور والانصراف» (البصمات الخام)
/// بعد توحيد الحالات العربية ودمج الصفوف المتكرّرة لنفس اليوم.
/// </summary>
public enum PunchDayStatus
{
    /// <summary>حالة غير معروفة (تُدرج في تقرير جودة البيانات).</summary>
    Unknown = 0,
    /// <summary>يوم عمل مكتمل (حضور + انصراف).</summary>
    Complete = 1,
    /// <summary>يوم عمل غير مكتمل (حضور بلا انصراف).</summary>
    Incomplete = 2,
    /// <summary>غياب غير مبرّر (بلا أي بصمة).</summary>
    Absent = 3,
    /// <summary>لا توجد بيانات للنظام في ذلك اليوم (سجل ناقص) — يُستثنى من التحليل.</summary>
    NoData = 4,
    /// <summary>عطلة الأسبوع (الجمعة/السبت).</summary>
    Weekend = 5,
    /// <summary>عطلة رسمية.</summary>
    OfficialHoliday = 6,
    /// <summary>إجازة سنوية.</summary>
    AnnualLeave = 7,
    /// <summary>إجازة مرضية (المادة 112).</summary>
    SickLeave = 8,
    /// <summary>إجازة تعويضية.</summary>
    CompensatoryLeave = 9,
    /// <summary>إجازة وفاة.</summary>
    BereavementLeave = 10,
    /// <summary>استئذان طارئ.</summary>
    EmergencyPermission = 11,
    /// <summary>استئذان طبي.</summary>
    MedicalPermission = 12,
    /// <summary>مهمة عمل رسمية (لا تُحتسب غياباً).</summary>
    OfficialMission = 13,
    /// <summary>انتداب رسمي.</summary>
    Secondment = 14,
    /// <summary>تدريب رسمي.</summary>
    Training = 15,
    /// <summary>دوام في عطلة الأسبوع (بصمات في يوم راحة — تُعرض ولا تُحتسب مخالفة).</summary>
    WeekendWork = 16,
    /// <summary>دوام في عطلة رسمية.</summary>
    HolidayWork = 17,
    /// <summary>يوم وردية دوام وفق «جدول الورديات الشهري» (نظام الورديات — لا تُقارَن بأوقات الدوام الرسمي).</summary>
    ShiftDuty = 18,
    /// <summary>يوم راحة وفق «جدول الورديات الشهري» (لا يُحتسب غياباً).</summary>
    ShiftRest = 19,
    /// <summary>إجازة وفق «جدول الورديات الشهري».</summary>
    ShiftLeave = 20,
    /// <summary>عطلة رسمية وفق «جدول الورديات الشهري».</summary>
    ShiftHoliday = 21,
    /// <summary>دورة/تدريب/مهمة رسمية وفق «جدول الورديات الشهري».</summary>
    ShiftTraining = 22
}

/// <summary>
/// نوع يوم العمل الإضافي (يُحدِّد نسبة التعويض السارية عليه من إعدادات العمل الإضافي).
/// </summary>
public enum OvertimeDayKind
{
    /// <summary>لا يوجد عمل إضافي محتسب في هذا اليوم.</summary>
    None = 0,
    /// <summary>يوم عمل عادي بعد نهاية الدوام الرسمي.</summary>
    Regular = 1,
    /// <summary>دوام في يوم راحة/نهاية أسبوع.</summary>
    Weekend = 2,
    /// <summary>دوام في عطلة رسمية أو دينية.</summary>
    Holiday = 3
}

/// <summary>
/// صف خام من «تقرير الحضور والانصراف» (البصمات) كما يصدّره نظام الدخول والخروج.
/// يُحفظ في جدول مرحلة للحفاظ على البيانات الأصلية + الحقول المُطبَّعة للتحليل القانوني.
/// </summary>
public class PunchRecord
{
    public long Id { get; set; }

    /// <summary>الرقم الوظيفي (نص لأن القيم تتجاوز حدود int).</summary>
    public string JobNumber { get; set; } = default!;
    /// <summary>اسم الموظف كما ورد في التقرير.</summary>
    public string? EmployeeName { get; set; }
    /// <summary>تاريخ اليوم (ميلادي).</summary>
    public DateOnly WorkDate { get; set; }
    /// <summary>حالة التحضير كما وردت نصاً.</summary>
    public string? StatusText { get; set; }
    /// <summary>الحالة بعد التصنيف.</summary>
    public PunchDayStatus Status { get; set; }
    /// <summary>توقيت الحضور.</summary>
    public TimeOnly? ClockIn { get; set; }
    /// <summary>توقيت الإنصراف.</summary>
    public TimeOnly? ClockOut { get; set; }
    /// <summary>مكان الحضور (البوابة/الجهاز).</summary>
    public string? LocationName { get; set; }
    /// <summary>الإدارة كما وردت في التقرير.</summary>
    public string? DepartmentName { get; set; }
    /// <summary>رقم الصف في ملف المصدر (لتتبع البيانات).</summary>
    public int SourceRow { get; set; }
    public string? SourceFile { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>
/// نتيجة تحليل يوم واحد لكل موظف: التأخير الصباحي، الانصراف المبكر، الغياب عن ساعات الدوام،
/// ومخالفات المادة 118/ب، وصلاحيته للاحتساب في التجميع الأسبوعي (المادة 118/ج).
/// </summary>
public class PunchDailyResult
{
    public long Id { get; set; }
    public string JobNumber { get; set; } = default!;
    public string? EmployeeName { get; set; }
    public string? DepartmentName { get; set; }
    public DateOnly WorkDate { get; set; }
    public PunchDayStatus Status { get; set; }

    public TimeOnly? ClockIn { get; set; }
    public TimeOnly? ClockOut { get; set; }

    /// <summary>دقائق التأخير الصباحي (الحضور بعد 08:30).</summary>
    public int LatenessMinutes { get; set; }
    /// <summary>دقائق الانصراف قبل نهاية الدوام (15:30).</summary>
    public int EarlyDepartureMinutes { get; set; }
    /// <summary>
    /// دقائق الغياب أثناء الدوام (الفجوات بين جلسات البصمات: انصراف ثم حضور لاحق)
    /// تُحتسب «مغادرة أثناء الدوام» ضمن المواد 118/ب و118/ج.
    /// </summary>
    public int GapMinutes { get; set; }
    /// <summary>مجموع دقائق الغياب عن نافذة الدوام (تأخير + انصراف مبكر) بحدّ 420 دقيقة.</summary>
    public int AbsenceMinutes { get; set; }
    /// <summary>الدقائق الفعلية بين الحضور والانصراف.</summary>
    public int WorkedMinutes { get; set; }
    /// <summary>عدد صفوف الملف الأصلي المدمجة لهذا اليوم.</summary>
    public int SourceRows { get; set; }

    public bool IsWorkingDay { get; set; }
    /// <summary>هل بلغ التأخير الصباحي حدّ الاحتساب (المادة 7)؟</summary>
    public bool IsMorningLate { get; set; }
    public bool IsAbsent { get; set; }
    /// <summary>يوم عمل بلا بصمة انصراف (يحتاج تسوية/إبراز عذر).</summary>
    public bool IsIncomplete { get; set; }
    /// <summary>هل استوفى شرط المادة 118/ب (غياب عن الدوام يزيد على 4 ساعات)؟</summary>
    public bool CountsFor118b { get; set; }
    /// <summary>أيام الخصم وفق المادة 118/ب (يوم واحد).</summary>
    public double Article118bDays { get; set; }

    // ---- تقويم العطل: نهاية الأسبوع والعطل الرسمية والدينية ----
    /// <summary>هل اليوم عطلة أسبوعية وفق تقويم النظام (الجمعة/السبت افتراضياً)؟</summary>
    public bool IsWeekendDay { get; set; }
    /// <summary>هل اليوم عطلة رسمية أو دينية وفق تقويم العطل المعتمد؟</summary>
    public bool IsCalendarHoliday { get; set; }
    /// <summary>تصنيف العطلة (رسمية / دينية إسلامية / دينية مسيحية).</summary>
    public HolidayKind HolidayKind { get; set; }
    /// <summary>اسم العطلة كما ورد في تقويم العطل (مثال: عيد المولد النبوي الشريف).</summary>
    public string? HolidayName { get; set; }
    /// <summary>هل يومّ العمل في يوم عطلة (نهاية أسبوع أو عطلة رسمية/دينية)؟</summary>
    public bool IsWorkOnHoliday { get; set; }

    // ---- نظام الورديات (وفق جدول الورديات الشهري الصادر من مسؤول الورديات) ----
    /// <summary>هل هذا اليوم مقيد في «جدول الورديات الشهري» لموظف يعمل بنظام الورديات؟</summary>
    public bool IsShiftDay { get; set; }
    /// <summary>تصنيف يوم الوردية (دوام/راحة/إجازة/عطلة/تدريب) — <see cref="ShiftDayKind.Unknown"/> إن لم يكن يوم وردية.</summary>
    public ShiftDayKind ShiftKind { get; set; }
    /// <summary>رمز الوردية كما ورد في الجدول الشهري (ن، ل، ر، 24...).</summary>
    public string? ShiftCode { get; set; }

    // ---- الدوام المرن (نافذة حضور وإكمال ساعات الدوام) ----
    /// <summary>هل طُبِّق الدوام المرن على هذا اليوم (تأخّر داخل النافذة المسموحة)؟</summary>
    public bool IsFlexibleWork { get; set; }
    /// <summary>بداية نافذة الدوام الفعّالة لهذا اليوم (وقت الحضور الفعلي عند تطبيق الدوام المرن).</summary>
    public TimeOnly? FlexibleStart { get; set; }
    /// <summary>نهاية نافذة الدوام الفعّالة (تتحرّك بقدر التأخّر لإكمال ساعات الدوام).</summary>
    public TimeOnly? FlexibleEnd { get; set; }
    /// <summary>دقائق المرونة المستخدمة (مدّة تحريك نهاية الدوام عن نهاية الدوام الرسمي).</summary>
    public int FlexibleMinutes { get; set; }

    // ---- العمل الإضافي (بحدود قانون الخدمة المدنية وإعدادات التشغيل) ----
    /// <summary>هل الموظف مصرَّح له بالعمل الإضافي في هذا اليوم؟</summary>
    public bool IsOvertimeEligible { get; set; }
    /// <summary>المدة الفعلية خارج نافذة الدوام بالدقائق (قبل تطبيق السقوف والحدود).</summary>
    public int RawOvertimeMinutes { get; set; }
    /// <summary>دقائق العمل الإضافي المحتسبة نهائياً بعد الحدّ اليومي والمدة الدنيا.</summary>
    public int OvertimeMinutes { get; set; }
    /// <summary>دقائق خارج الدوام لم تُحتسب (سقف/بلا تصريح/أقل من المدة الدنيا).</summary>
    public int OvertimeExcludedMinutes { get; set; }
    /// <summary>هل المدة خارج الدوام معلَّقة بانتظار موافقة مسبقة (تصريح ساري/قائمة معتمدة)؟</summary>
    public bool OvertimeNeedsApproval { get; set; }
    /// <summary>نوع يوم العمل الإضافي (عادي/راحة/عطلة) الذي تُبنى عليه نسبة التعويض.</summary>
    public OvertimeDayKind OvertimeKind { get; set; }
    /// <summary>نسبة تعويض العمل الإضافي السارية على هذا اليوم (1.0 عادي، 1.5 راحة، 2.0 عطلة).</summary>
    public double OvertimeRate { get; set; }
    /// <summary>الدقائق المعادلة بعد تطبيق نسبة التعويض (ساعات معادلة للصرف/التعويض).</summary>
    public int EquivalentOvertimeMinutes { get; set; }

    /// <summary>أول يوم من أسبوع العمل (الأحد) الذي يقع فيه اليوم.</summary>
    public DateOnly WorkWeekStart { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>ملاحظات التدقيق لهذا اليوم (دمج صفوف، نقص بيانات...).</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// تجميع أسبوعي (أسبوع العمل: الأحد → الخميس) لدقائق التأخير والانصراف المبكر — المادة 118/ج:
/// بلوغ 60 دقيقة = خصم يوم كامل.
/// </summary>
public class PunchWeeklyResult
{
    public long Id { get; set; }
    public string JobNumber { get; set; } = default!;
    public string? EmployeeName { get; set; }
    public string? DepartmentName { get; set; }

    /// <summary>أول يوم من أسبوع العمل (الأحد).</summary>
    public DateOnly WeekStart { get; set; }
    /// <summary>آخر يوم محتسب في الأسبوع (الخميس).</summary>
    public DateOnly WeekEnd { get; set; }
    /// <summary>السنة/الشهر المنسوب إليه الأسبوع (شهر بداية الأسبوع).</summary>
    public int Year { get; set; }
    public int Month { get; set; }

    /// <summary>عدد أيام العمل المحتسبة في الأسبوع.</summary>
    public int DaysCounted { get; set; }
    public int LatenessMinutes { get; set; }
    public int EarlyDepartureMinutes { get; set; }
    /// <summary>دقائق المغادرة أثناء الدوام (الفجوات بين جلسات البصمات).</summary>
    public int GapMinutes { get; set; }
    /// <summary>مجموع الدقائق الفعلي (تأخير صباحي + انصراف مبكر + مغادرة أثناء الدوام) قبل تطبيق السقف القانوني.</summary>
    public int TotalMinutes { get; set; }
    /// <summary>
    /// الناتج الأسبوعي المحتسب وفق المادة 118/ج: دمج التأخير الصباحي مع الانصراف المبكر (ومعه المغادرة أثناء الدوام)
    /// في رقم واحد <b>لا يزيد عن 60 دقيقة في الأسبوع</b>؛ وهو الرقم المعتمد في الخصم والملخص الشهري والمؤشرات.
    /// </summary>
    public int CountedMinutes { get; set; }
    /// <summary>هل بلغ المجموع الفعلي 60 دقيقة أو أكثر؟</summary>
    public bool Exceeds60Minutes { get; set; }
    /// <summary>أيام الخصم وفق المادة 118/ج.</summary>
    public double DeductionDays { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// النتيجة الشهرية لكل موظف: عقوبة التأخير الصباحي (المادة 7)، الغياب، الأيام غير المكتملة،
/// الإجازات بحسب أنواعها، أيام المادتين 118/ب و118/ج، وخصم المكافأة (حد الـ 15 يوماً).
/// </summary>
public class PunchMonthlyResult
{
    public long Id { get; set; }
    public string JobNumber { get; set; } = default!;
    public string? EmployeeName { get; set; }
    public string? DepartmentName { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }

    public int WorkingDays { get; set; }
    public int CompleteDays { get; set; }

    // ---- أيام العطل المستثناة (لا تُحتسب غياباً ولا مخالفة) ----
    /// <summary>أيام نهاية الأسبوع الواقعة في الشهر (استثنائية من المخالفات).</summary>
    public int WeekendDays { get; set; }
    /// <summary>أيام العطل الرسمية والدينية الواقعة في الشهر.</summary>
    public int HolidayDays { get; set; }
    /// <summary>أيام الدوام الفعلي في نهاية الأسبوع (للعلم والساعات الإضافية).</summary>
    public int WeekendWorkDays { get; set; }
    /// <summary>أيام الدوام الفعلي في العطل الرسمية والدينية (للعلم والساعات الإضافية).</summary>
    public int HolidayWorkDays { get; set; }

    // ---- نظام الورديات (وفق جدول الورديات الشهري) ----
    /// <summary>أيام الورديات (الدوام) المحتسبة في الشهر لموظفي نظام الورديات.</summary>
    public int ShiftDutyDays { get; set; }
    /// <summary>أيام الراحة وفق جدول الورديات (لا تُحتسب غياباً).</summary>
    public int ShiftRestDays { get; set; }
    /// <summary>أيام الإجازات وفق جدول الورديات.</summary>
    public int ShiftLeaveDays { get; set; }

    // ---- المادة 7: التأخير الصباحي ----
    /// <summary>عدد أيام التأخير الصباحي المحتسبة في الشهر.</summary>
    public int LateIncidents { get; set; }
    public DisciplinaryActionType Penalty { get; set; }
    public string? PenaltyText { get; set; }
    /// <summary>أيام حسم الراتب وفق المادة 7 (يومان عند تجاوز 4 تأخيرات).</summary>
    public double Article7SalaryDeductionDays { get; set; }

    // ---- الغياب ----
    /// <summary>أيام الغياب غير المبرّر.</summary>
    public int AbsentDays { get; set; }
    /// <summary>أيام عمل بلا بصمة انصراف.</summary>
    public int IncompleteDays { get; set; }

    // ---- الإجازات ----
    public double AnnualLeaveDays { get; set; }
    public double SickLeaveDays { get; set; }
    public double CompensatoryLeaveDays { get; set; }
    public double BereavementLeaveDays { get; set; }
    public double EmergencyPermissionDays { get; set; }
    public double MedicalPermissionDays { get; set; }
    /// <summary>مهمة عمل + انتداب + تدريب (رسمية).</summary>
    public double OfficialDutyDays { get; set; }

    // ---- المادتان 118/ب و118/ج ----
    public double Article118bDays { get; set; }
    public double Article118cDays { get; set; }
    public int WeeklyLateMinutes { get; set; }
    public int WeeksOver60Minutes { get; set; }

    // ---- خصم المكافأة (حد الـ 15 يوماً) ----
    public double TotalAbsenceDays { get; set; }
    public double BonusDeductionPercent { get; set; }
    public bool Exceeds15Days { get; set; }

    /// <summary>إجمالي أيام حسم الراتب (الغياب + المادة 7).</summary>
    public double TotalSalaryDeductionDays { get; set; }
    /// <summary>الأيام المستنزفة من رصيد الإجازة السنوية (المادة 118/ب).</summary>
    public double AnnualLeaveBalanceUsageDays { get; set; }

    // ---- الدوام المرن (نافذة حضور وإكمال ساعات الدوام) ----
    /// <summary>أيام طُبِّق فيها الدوام المرن خلال الشهر.</summary>
    public int FlexibleDays { get; set; }
    /// <summary>مجموع دقائق المرونة المستخدمة في الشهر.</summary>
    public int FlexibleMinutes { get; set; }

    // ---- العمل الإضافي (احتساب إجمالي الساعات الإضافية طوال الشهر) ----
    /// <summary>أيام العمل الإضافي المحتسبة في الشهر.</summary>
    public int OvertimeDays { get; set; }
    /// <summary>إجمالي دقائق العمل الإضافي المحتسبة في الشهر (بعد الحدّ اليومي والسقف الشهري).</summary>
    public int OvertimeMinutes { get; set; }
    /// <summary>إجمالي ساعات العمل الإضافي المحتسبة في الشهر.</summary>
    public double OvertimeHours { get; set; }
    /// <summary>إجمالي الدقائق الفعلية خارج الدوام قبل تطبيق السقوف (للتدقيق).</summary>
    public int RawOvertimeMinutes { get; set; }
    /// <summary>دقائق خارج الدوام لم تُحتسب لقاعدة العمل الإضافي (سقف يومي/شهري/بلا تصريح/أقل من المدة الدنيا).</summary>
    public int OvertimeExcludedMinutes { get; set; }
    /// <summary>أيام وُجد فيها عمل خارج الدوام بلا موافقة مسبقة (تصريح ساري/قائمة معتمدة) فلم يُحتسب إضافياً.</summary>
    public int OvertimeNeedsApprovalDays { get; set; }
    /// <summary>دقائق خارج الدوام معلَّقة بانتظار موافقة مسبقة.</summary>
    public int OvertimeNeedsApprovalMinutes { get; set; }
    /// <summary>دقائق العمل الإضافي في أيام نهاية الأسبوع.</summary>
    public int OvertimeWeekendMinutes { get; set; }
    /// <summary>دقائق العمل الإضافي في العطل الرسمية والدينية.</summary>
    public int OvertimeHolidayMinutes { get; set; }
    /// <summary>الدقائق المعادلة بعد تطبيق نسب التعويض (ساعات معادلة).</summary>
    public int EquivalentOvertimeMinutes { get; set; }
    /// <summary>إجمالي الساعات المعادلة بعد تطبيق نسب التعويض.</summary>
    public double EquivalentOvertimeHours { get; set; }
    /// <summary>هل بلغ الموظف الحدّ الأعلى للعمل الإضافي الشهري (فاستُبعد ما زاد عنه)؟</summary>
    public bool OvertimeCapReached { get; set; }
    public string? Notes { get; set; }
}

/// <summary>نتيجة استيراد ملف البصمات.</summary>
public sealed record PunchImportResult(
    string FileName,
    long RowsImported,
    long RowsSkipped,
    int Employees,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    DateTime ImportedAtUtc);

/// <summary>ملخص جدول المرحلة (جودة البيانات) قبل التحليل.</summary>
public sealed record PunchStagingSummary(
    long TotalRows,
    int Employees,
    DateOnly? FirstDate,
    DateOnly? LastDate,
    IReadOnlyDictionary<string, int> StatusCounts,
    DateTime? LastImportUtc,
    string? SourceFile,
    bool HasData);

/// <summary>ملخص نتائج التحليل القانوني من البصمات (لوحة المؤشرات).</summary>
public sealed record PunchAnalysisSummary(
    int EmployeesAnalyzed,
    long RecordsAnalyzed,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    int MorningGraceMinutes,
    int WorkingDays,
    int CompleteDays,
    int AbsentDays,
    int AbsentDaysOnWeekend,
    int IncompleteDays,
    int NoDataDays,
    int WeekendDays,
    int HolidayDays,
    int WeekendWorkDays,
    int HolidayWorkDays,
    int CalendarHolidays,
    long TotalLatenessMinutes,
    long TotalEarlyDepartureMinutes,
    long TotalMidDayGapMinutes,
    int LateIncidentDays,
    int EmployeesWithLateIncidents,
    int EmployeesWithArticle7Penalty,
    double TotalArticle7SalaryDays,
    int WeeksOver60Minutes,
    int EmployeesOver60Minutes,
    int TotalWeeklyLateMinutes,
    double TotalArticle118cDays,
    int DaysOver4Hours,
    int EmployeesOver4Hours,
    double TotalArticle118bDays,
    double TotalAnnualLeaveDays,
    double TotalSickLeaveDays,
    double TotalEmergencyPermissionDays,
    double TotalOfficialDutyDays,
    double TotalAbsenceForBonusDays,
    double TotalSalaryDeductionDays,
    double TotalBonusDeductionPercent,
    int EmployeesWithViolations,
    int ShiftDutyDays,
    int ShiftRestDays,
    int ShiftLeaveDays,
    int ShiftEmployees,
    int FlexibleDays,
    int FlexibleEmployees,
    int FlexibleMinutes,
    int OvertimeDays,
    int OvertimeEmployees,
    long OvertimeMinutes,
    long OvertimeRawMinutes,
    long OvertimeExcludedMinutes,
    int OvertimeNeedsApprovalDays,
    long OvertimeNeedsApprovalMinutes,
    int OvertimeWeekendDays,
    int OvertimeHolidayDays,
    double OvertimeHours,
    double EquivalentOvertimeHours,
    int EmployeesOverMonthlyOvertimeCap,
    DateTime RunAtUtc,
    bool HasData);

/// <summary>سطر «التزام إدارة/مديرية» محسوب من بصمات الحضور.</summary>
public sealed record PunchComplianceItem(
    string Administration,
    int Rank,
    int Employees,
    int CompliantEmployees,
    int ViolatingEmployees,
    double CompliancePercent,
    int EmployeesWithLateness,
    double Article118bDays,
    double Article118cDays,
    int AbsentDays,
    double TotalSalaryDeductionDays);

