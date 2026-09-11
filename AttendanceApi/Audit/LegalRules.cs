using AttendanceApi.Domain;

namespace AttendanceApi.Audit;

/// <summary>
/// القواعد القانونية والرياضية المطبقة في محرك المراجعة
/// (المواد 118/ب، 118/ج، 7، 112، وقاعدة الـ 15 يوماً للمكافأة).
/// </summary>
public static class LegalRules
{
    // ---- المادة 118/ب: مخالفة الاستئذان ليوم واحد (> 4 ساعات) ----
    public const int Art118b_MinutesThreshold = 240;   // 4 ساعات
    public const double Art118b_EquivalentDays = 1.0;  // خصم يوم كامل

    /// <summary>تحديد هل تتجاوز مدة المغادرة 4 ساعات.</summary>
    public static bool IsExceeding4Hours(int durationMinutes) =>
        durationMinutes > Art118b_MinutesThreshold;

    /// <summary>أيام الخصم المعادلة وفق المادة 118/ب.</summary>
    public static double EquivalentDaysDeduction(int durationMinutes) =>
        IsExceeding4Hours(durationMinutes) ? Art118b_EquivalentDays : 0.0;

    // ---- المادة 118/ج: الخصم الأسبوعي التراكمي للتأخير ----
    public const int Art118c_WeeklyLateMinutesThreshold = 60; // دقيقة
    public const double Art118c_WeeklyDeductionDays = 1.0;    // يوم

    /// <summary>
    /// دمج التأخير الصباحي مع الانصراف المبكر (ومعه المغادرة أثناء الدوام) في مجموع أسبوعي واحد
    /// <b>قبل</b> تقييده بسقف المادة 118/ج.
    /// </summary>
    public static int TotalWeeklyLateMinutes(int lateMinutes, int earlyDepartureMinutes) =>
        lateMinutes + earlyDepartureMinutes;

    /// <summary>هل بلغ الناتج الأسبوعي (قبل التقيد بالسقف) حدّ المادة 118/ج (60 دقيقة)؟</summary>
    public static bool ExceedsWeeklyThreshold(
        int totalWeeklyMinutes,
        int threshold = Art118c_WeeklyLateMinutesThreshold) =>
        totalWeeklyMinutes >= Math.Max(1, threshold);

    /// <summary>
    /// الناتج الأسبوعي المحتسب وفق المادة 118/ج: دمج (التأخير الصباحي + الانصراف المبكر + المغادرة أثناء الدوام)
    /// في رقم واحد <b>لا يزيد عن 60 دقيقة في الأسبوع</b>؛ ما زاد على السقف لا يُحتسب لأن مخالفة الأسبوع
    /// الواحد تُسوَّى بخصم يوم واحد فقط.
    /// </summary>
    public static int CountedWeeklyMinutes(
        int totalWeeklyMinutes,
        int threshold = Art118c_WeeklyLateMinutesThreshold) =>
        Math.Clamp(totalWeeklyMinutes, 0, Math.Max(0, threshold));

    /// <summary>أيام الخصم الأسبوعية وفق المادة 118/ج.</summary>
    public static double WeeklyLateDeductionDays(int totalWeeklyLateMinutes) =>
        totalWeeklyLateMinutes >= Art118c_WeeklyLateMinutesThreshold
            ? Art118c_WeeklyDeductionDays
            : 0.0;

    // ---- المادة 118/ج بحدود مرنة (تُقرأ من إعدادات التحليل وقت التشغيل) ----

    /// <summary>
    /// هل بلغ الناتج الأسبوعي الحدّ المعتمد (أو تجاوزه، بحسب <paramref name="exceededOnly"/>)?
    /// تُستخدم مع إعدادات المادة 118/ج المرنة بدلاً من الحدّ الثابت 60 دقيقة.
    /// </summary>
    public static bool WeeklyThresholdReached(
        int totalWeeklyMinutes,
        int thresholdMinutes,
        bool exceededOnly = false)
    {
        int threshold = Math.Max(1, thresholdMinutes);
        return exceededOnly ? totalWeeklyMinutes > threshold : totalWeeklyMinutes >= threshold;
    }

    /// <summary>
    /// الناتج الأسبوعي المحتسب: المجموع الفعلي مقيَّداً بسقف الإعدادات
    /// (وعند <paramref name="applyCap"/> = false يُعاد المجموع الفعلي كما هو).
    /// </summary>
    public static int CappedWeeklyMinutes(
        int totalWeeklyMinutes,
        int capMinutes,
        bool applyCap = true) =>
        applyCap ? Math.Clamp(totalWeeklyMinutes, 0, Math.Max(0, capMinutes)) : totalWeeklyMinutes;

    /// <summary>أيام الخصم الأسبوعية بحدود مرنة (عدد أيام قابل للتخصيص لكل أسبوع مخالف).</summary>
    public static double WeeklyDeductionDays(bool violation, double deductionDaysPerWeek) =>
        violation ? Math.Max(0, deductionDaysPerWeek) : 0.0;

    // ---- ساعات الدوام الرسمي (08:30 - 15:30 = 7 ساعات) ----
    /// <summary>بداية الدوام الرسمي (08:30).</summary>
    public static readonly TimeOnly WorkdayStart = new(8, 30);

    /// <summary>نهاية الدوام الرسمي (15:30).</summary>
    public static readonly TimeOnly WorkdayEnd = new(15, 30);

    /// <summary>مدة الدوام الرسمي بالدقائق = 7 ساعات (08:30–15:30).</summary>
    public const int WorkdayMinutes = 420;

    /// <summary>
    /// الدقائق الفعلية الواقعة داخل نافذة الدوام الرسمي (08:30–15:30)
    /// تُستخدم لاحتساب التأخير الصباحي والانصراف المبكر من أوقات طلب المغادرة.
    /// عند غياب الأوقات أو تعذّر المقارنة يُستخدم نص المدة بحد أقصى ساعات الدوام.
    /// </summary>
    public static int MinutesInsideWorkday(TimeOnly? from, TimeOnly? to, int fallbackMinutes)
    {
        if (from is null || to is null || to <= from)
        {
            return Math.Clamp(fallbackMinutes, 0, WorkdayMinutes);
        }

        var start = from < WorkdayStart ? WorkdayStart : from.Value;
        var end = to > WorkdayEnd ? WorkdayEnd : to.Value;

        return end <= start ? 0 : (int)Math.Round((end - start).TotalMinutes);
    }

    /// <summary>الاثنين (بداية أسبوع ISO) الذي يقع فيه التاريخ.</summary>
    public static DateOnly StartOfIsoWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    // ---- المادة 7 (تعليمات 2020): العقوبات التأديبية الشهرية ----
    public static DisciplinaryActionType MonthlyLatePenalty(int monthlyLateCount) =>
        monthlyLateCount switch
        {
            <= 2 => DisciplinaryActionType.None,
            3    => DisciplinaryActionType.WrittenWarning,     // تنبيه خطي
            4    => DisciplinaryActionType.WrittenCaution,     // إنذار خطي
            _    => DisciplinaryActionType.TwoDaysSalaryDeduction // حسم يومين
        };

    // ---- قاعدة المكافأة الشهرية (حد الـ 15 يوماً) ----
    public const double Bonus_AbsenceDaysThreshold = 15.0;
    public const double Bonus_DeductionPercent = 0.50;   // 50%

    /// <summary>
    /// إجمالي أيام الغياب الشهري = إجازة سنوية + إجازة مرضية + أيام 118/ب + أيام 118/ج.
    /// </summary>
    public static double TotalMonthlyAbsenceDays(
        double annualLeaveDays,
        double sickLeaveDays,
        double article118bDays,
        double article118cWeeklyLateDays) =>
        annualLeaveDays + sickLeaveDays + article118bDays + article118cWeeklyLateDays;

    /// <summary>نسبة خصم المكافأة: 50% إذا تجاوز الغياب 15 يوماً، وإلا 0%.</summary>
    public static double BonusDeductionPercent(double totalMonthlyAbsenceDays) =>
        totalMonthlyAbsenceDays > Bonus_AbsenceDaysThreshold
            ? Bonus_DeductionPercent
            : 0.0;

    // ---- المادة 112: النسب المتناقصة للإجازة المرضية ----
    public const int Sick_Tier1_MaxDays = 120;  // 100%
    public const int Sick_Tier2_MaxDays = 240;  // 75%
    public const int Sick_Tier3_MaxDays = 360;  // 50%

    public static decimal SickSalaryPercentage(int cumulativeSickDays) =>
        cumulativeSickDays switch
        {
            <= 120 => 100m,
            <= 240 => 75m,
            <= 360 => 50m,
            _      => 0m // تجاوز السقف القانوني
        };

    // ---- المواد 100/د، 101، 105: الترجيع والاحتساب التناسبي ونهاية الخدمة ----
    public const int AnnualLeaveFullEntitlement = 30; // يوم
    public const int MaxCarryoverYears = 2;           // منع تراكم أكثر من سنتين
    public const int EndOfService_MaxCompensationDays = 60;

    /// <summary>الاحتساب التناسبي للموظف الجديد: ((12 - شهر التعيين + 1) / 12) × 30.</summary>
    public static double ProRataAnnualLeave(int hiringMonth) =>
        ((12 - hiringMonth + 1) / 12.0) * AnnualLeaveFullEntitlement;

    /// <summary>سقف تعويض نهاية الخدمة (60 يوماً).</summary>
    public static double CapEndOfServiceCompensation(double totalUnusedLeaveDays) =>
        Math.Min(totalUnusedLeaveDays, EndOfService_MaxCompensationDays);

    // ---- ISO Week Number ----
    public static int IsoWeekNumber(DateOnly date)
    {
        var d = date.ToDateTime(TimeOnly.MinValue);
        var calendar = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        return calendar.GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    // =====================================================================
    //  الاحتساب القائم على بصمات الحضور (تقرير الحضور والانصراف الخام)
    // =====================================================================

    /// <summary>
    /// حدّ السماح الصباحي المعتمد لاحتساب «التأخير الصباحي» (المادة 7) بالدقائق؛
    /// التأخير الأقل من هذا الحدّ يُرصد في التقارير ولا يُحتسب إجراءً تأديبياً.
    /// </summary>
    public const int MorningGraceMinutes = 15;

    /// <summary>
    /// الفجوة الزمنية الدنيا (بالدقائق) بين جلستي بصمة لتُحتسب «مغادرة أثناء الدوام»؛
    /// الفجوات الأقل منها تُعدّ تكرار بصمة عارضاً ولا تُحتسب.
    /// </summary>
    public const int PunchNoiseMinutes = 10;

    /// <summary>أول يوم من أسبوع العمل الرسمي (الأحد) الذي يقع فيه التاريخ.</summary>
    public static DateOnly StartOfWorkWeek(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    /// <summary>آخر يوم من أسبوع العمل الرسمي (الخميس).</summary>
    public static DateOnly EndOfWorkWeek(DateOnly date) => StartOfWorkWeek(date).AddDays(4);

    /// <summary>هل يُحتسب التأخير الصباحي مخالفةً للمادة 7 (بلوغ حدّ السماح)؟</summary>
    public static bool IsMorningLateness(int latenessMinutes, int graceMinutes = MorningGraceMinutes) =>
        latenessMinutes > 0 && latenessMinutes >= graceMinutes;

    /// <summary>دقائق الغياب الفعلية عن نافذة الدوام الرسمي (تأخير + انصراف مبكر) بحدّ ساعات الدوام.</summary>
    public static int WorkdayAbsenceMinutes(int latenessMinutes, int earlyDepartureMinutes) =>
        Math.Clamp(latenessMinutes + earlyDepartureMinutes, 0, WorkdayMinutes);

    /// <summary>أيام الخصم من رصيد الإجازة السنوية وفق المادة 118/ب لجزاء غياب يوم واحد.</summary>
    public static double Article118bDaysFromAbsence(int absenceMinutes) =>
        EquivalentDaysDeduction(absenceMinutes);

    /// <summary>نص الإجراء التأديبي (المادة 7) للعرض في التقارير.</summary>
    public static string DisciplinaryActionText(DisciplinaryActionType action) => action switch
    {
        DisciplinaryActionType.WrittenWarning => "تنبيه خطي (3 تأخيرات)",
        DisciplinaryActionType.WrittenCaution => "إنذار خطي (4 تأخيرات)",
        DisciplinaryActionType.TwoDaysSalaryDeduction => "حسم يومين من الراتب (أكثر من 4 تأخيرات)",
        _ => "لا إجراء"
    };

    /// <summary>أيام حسم الراتب وفق عقوبة المادة 7.</summary>
    public static double Article7SalaryDeductionDays(DisciplinaryActionType action) =>
        action == DisciplinaryActionType.TwoDaysSalaryDeduction ? 2.0 : 0.0;

    /// <summary>نص شريحة الإجازة المرضية وأجرها (المادة 112).</summary>
    public static string SickLeaveTierText(decimal salaryPercentage) => salaryPercentage switch
    {
        >= 100m => "الأيام 1–120 بأجر كامل",
        >= 75m => "الأيام 121–240 بثلاثة أرباع الأجر",
        >= 50m => "الأيام 241–360 بنصف الأجر",
        _ => "تجاوز السقف القانوني (بلا أجر)"
    };
}