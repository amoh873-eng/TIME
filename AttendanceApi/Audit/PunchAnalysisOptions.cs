namespace AttendanceApi.Audit;

/// <summary>
/// خيارات محرّك تحليل بصمات الحضور (تقرير الحضور والانصراف) — قابلة للتخصيص من appsettings.
/// </summary>
public sealed class PunchAnalysisOptions
{
    public const string SectionName = "PunchAnalysis";

    /// <summary>حدّ السماح الصباحي لاحتساب التأخير الصباحي (المادة 7) بالدقائق.</summary>
    public int MorningGraceMinutes { get; set; } = LegalRules.MorningGraceMinutes;

    /// <summary>حدّ المجموع الأسبوعي لدقائق التأخير/الانصراف المبكر (المادة 118/ج).</summary>
    public int WeeklyLateMinutesThreshold { get; set; } = LegalRules.Art118c_WeeklyLateMinutesThreshold;

    /// <summary>
    /// تقييد الناتج الأسبوعي المحتسب (التأخير الصباحي + الانصراف المبكر + المغادرة) بحدّ المادة 118/ج
    /// فلا يزيد المعروض/المُرحَّل إلى الملخص الشهري عن 60 دقيقة في الأسبوع.
    /// </summary>
    public bool CapWeeklyCountedMinutes { get; set; } = true;

    /// <summary>حدّ الغياب عن الدوام في اليوم الواحد لاستحقاق المادة 118/ب (4 ساعات).</summary>
    public int DailyAbsenceMinutesThreshold { get; set; } = LegalRules.Art118b_MinutesThreshold;

    /// <summary>احتساب أيام العمل التي غاب فيها انصراف البصمة كأيام مخالفة تحتاج تسوية.</summary>
    public bool TreatIncompleteDaysAsViolation { get; set; } = true;

    /// <summary>
    /// أيام عطلة نهاية الأسبوع الرسمية (0=الأحد، 1=الاثنين، 4=الخميس، 5=الجمعة، 6=السبت).
    /// الافتراضي في الأردن: الجمعة والسبت. أي يوم هنا لا يُحتسب غياباً ولا مخالفة.
    /// </summary>
    public int[] WeekendDays { get; set; } = { 5, 6 };

    /// <summary>
    /// اعتماد الكتالوج المدمج للعطل الرسمية والدينية (الأردن 2023 — 2025) في التحليل،
    /// مع إضافة/تعديل العطل من واجهة «تقويم العطل» (جدول OfficialHolidays).
    /// </summary>
    public bool UseBuiltInHolidayCalendar { get; set; } = true;

    // =====================================================================
    //  نظام الورديات (مثال: الحراسة بورديات 24 ساعة متواصلة وفق جدول شهري)
    // =====================================================================

    /// <summary>تفعيل نظام الورديات (الاحتساب من «جدول الورديات الشهري» المستورد).</summary>
    public bool ShiftSystemEnabled { get; set; } = true;

    /// <summary>طول الوردية بالساعات (الافتراضي 24 ساعة متواصلة).</summary>
    public double ShiftCycleHours { get; set; } = 24;

    /// <summary>إعفاء العاملين بالورديات من قاعدة المادة 118/ج الأسبوعية.</summary>
    public bool ShiftExemptFromWeeklyRule { get; set; } = true;

    /// <summary>إعفاء العاملين بالورديات من التأخير الصباحي وعقوبة المادة 7.</summary>
    public bool ShiftExemptFromMorningLateness { get; set; } = true;

    /// <summary>إعفاء العاملين بالورديات من الانصراف المبكر.</summary>
    public bool ShiftExemptFromEarlyDeparture { get; set; } = true;

    /// <summary>أيام الراحة/العطل/الإجازات في الجدول الشهري لا تُحتسب غياباً.</summary>
    public bool ShiftRestDaysNotAbsence { get; set; } = true;

    /// <summary>يوم بلا قيد في الجدول الشهري يُستثنى من القواعد العامة (بدل تطبيقها عليه).</summary>
    public bool ShiftMissingScheduleExempts { get; set; }

    /// <summary>وردية دوام بلا أي بصمة تُحتسب غياباً (بدل الاكتفاء بالملاحظة للمراجعة).</summary>
    public bool ShiftMissingPunchIsAbsence { get; set; }

    /// <summary>سماح التأخير داخل يوم الوردية بالدقائق (0 = لا يُحتسب تأخير في الورديات).</summary>
    public int ShiftDutyGraceMinutes { get; set; }

    /// <summary>إدارات/مديريات تُعامل بنظام الورديات تلقائياً (مثال: الحراسة، الأمن، الدفاع المدني).</summary>
    public string[] ShiftDepartments { get; set; } = Array.Empty<string>();

    // =====================================================================
    //  الدوام المرن (نافذة حضور وإكمال ساعات الدوام)
    // =====================================================================

    /// <summary>تفعيل الدوام المرن (لا يُطبَّق إلا على النطاق المصرَّح له: القوائم أو التصاريح).</summary>
    public bool FlexibleEnabled { get; set; } = true;

    /// <summary>أبكر وقت حضور مسموح في الدوام المرن (HH:mm).</summary>
    public string FlexibleEarliestArrival { get; set; } = "07:30";

    /// <summary>آخر وقت حضور مسموح في الدوام المرن (HH:mm).</summary>
    public string FlexibleLatestArrival { get; set; } = "09:30";

    /// <summary>الدقائق المطلوب إكمالها يومياً في الدوام المرن (0 = مدة الدوام الرسمي 7 ساعات).</summary>
    public int FlexibleCompleteDailyMinutes { get; set; }

    /// <summary>اشتراط تصريح/قائمة لتطبيق الدوام المرن (الافتراضي: نعم).</summary>
    public bool FlexibleRequireApproval { get; set; } = true;

    /// <summary>احتساب الحضور المبكر قبل بدء الدوام عمل إضافي عند تطبيق الدوام المرن.</summary>
    public bool FlexibleEarlyArrivalCountsAsOvertime { get; set; }

    /// <summary>إدارات/مديريات يُطبَّق عليها الدوام المرن.</summary>
    public string[] FlexibleDepartments { get; set; } = Array.Empty<string>();

    /// <summary>أرقام وظيفية يُطبَّق عليها الدوام المرن.</summary>
    public string[] FlexibleEmployees { get; set; } = Array.Empty<string>();

    // =====================================================================
    //  العمل الإضافي (الساعات المسموح بها والموظفون المصرَّح لهم)
    // =====================================================================

    /// <summary>تفعيل احتساب العمل الإضافي.</summary>
    public bool OvertimeEnabled { get; set; } = true;

    /// <summary>الحدّ الأعلى للعمل الإضافي اليومي بالدقائق (3 ساعات = 180 دقيقة).</summary>
    public int OvertimeMaxMinutesPerDay { get; set; } = 180;

    /// <summary>الحدّ الأعلى للعمل الإضافي الشهري بالدقائق (20 ساعة = 1200 دقيقة؛ 0 = بلا سقف).</summary>
    public int OvertimeMaxMinutesPerMonth { get; set; } = 1200;

    /// <summary>أقل مدة عمل إضافي تُحتسب في اليوم (30 دقيقة).</summary>
    public int OvertimeMinMinutesPerDay { get; set; } = 30;

    /// <summary>خصم دقائق التأخير الصباحي من العمل الإضافي في اليوم نفسه.</summary>
    public bool OvertimeCompensateLateness { get; set; } = true;

    /// <summary>احتساب الحضور قبل بدء الدوام عمل إضافي (الافتراضي: لا).</summary>
    public bool OvertimeCountEarlyArrival { get; set; }

    /// <summary>احتساب الدوام الفعلي في نهاية الأسبوع عمل إضافي.</summary>
    public bool OvertimeCountOnWeekends { get; set; } = true;

    /// <summary>احتساب الدوام الفعلي في العطل الرسمية والدينية عمل إضافي.</summary>
    public bool OvertimeCountOnHolidays { get; set; } = true;

    /// <summary>نسبة تعويض العمل الإضافي في أيام العمل العادية.</summary>
    public double OvertimeRegularRate { get; set; } = 1.0;

    /// <summary>نسبة تعويض العمل الإضافي في أيام نهاية الأسبوع.</summary>
    public double OvertimeWeekendRate { get; set; } = 1.5;

    /// <summary>نسبة تعويض العمل الإضافي في العطل الرسمية والدينية.</summary>
    public double OvertimeHolidayRate { get; set; } = 2.0;

    /// <summary>تدوير دقائق العمل الإضافي (0 = بلا تدوير).</summary>
    public int OvertimeRoundToMinutes { get; set; }

    /// <summary>اشتراط تصريح/موافقة **مسبقة** سارية لاحتساب العمل الإضافي (الافتراضي: نعم).</summary>
    public bool OvertimeRequireApproval { get; set; } = true;

    /// <summary>إدارات/مديريات مصرَّح لها بالعمل الإضافي.</summary>
    public string[] OvertimeDepartments { get; set; } = Array.Empty<string>();

    /// <summary>أرقام وظيفية مصرَّح لها بالعمل الإضافي.</summary>
    public string[] OvertimeEmployees { get; set; } = Array.Empty<string>();


    /// <summary>أيام نهاية الأسبوع الفعلية بعد التصحيح (لا تكون فارغة أبداً).</summary>
    public DayOfWeek[] EffectiveWeekendDays()
    {
        var days = (WeekendDays ?? Array.Empty<int>())
            .Where(v => v is >= 0 and <= 6)
            .Select(v => (DayOfWeek)v)
            .Distinct()
            .ToArray();

        return days.Length > 0 ? days : PunchCalendar.DefaultWeekendDays.ToArray();
    }
}
