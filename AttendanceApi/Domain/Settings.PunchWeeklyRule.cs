using System.Globalization;

namespace AttendanceApi.Domain;

/// <summary>
/// قاعدة المادة 118/ج الأسبوعية (دمج التأخير الصباحي مع الانصراف المبكر والمغادرة أثناء الدوام) —
/// <b>مرنة وقابلة للتخصيص في وقت التشغيل</b> بلا إعادة بناء ولا إعادة تشغيل الخدمة:
/// تُحفظ في ملف <c>punch-analysis.json</c> بجوار التطبيق (انظر <c>Services.PunchWeeklyRuleStore</c>)،
/// وتكون قيم <c>appsettings.json → PunchAnalysis</c> هي المرجع الافتراضي الذي يُعاد إليه بزر «إعادة الافتراضي».
/// </summary>
public sealed record PunchWeeklyRule
{
    /// <summary>تفعيل القاعدة الأسبوعية كلياً؛ عند التعطيل لا يُبنى تجميع أسبوعي ولا خصم للمادة 118/ج.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>دمج دقائق الانصراف المبكر مع التأخير الصباحي في الناتج الأسبوعي.</summary>
    public bool MergeEarlyDeparture { get; init; } = true;

    /// <summary>دمج دقائق المغادرة أثناء الدوام (الفجوات بين جلسات البصمة) في الناتج الأسبوعي.</summary>
    public bool MergeMidDayGaps { get; init; } = true;

    /// <summary>حدّ الدقائق الأسبوعي (المادة 118/ج — الافتراضي 60 دقيقة).</summary>
    public int ThresholdMinutes { get; init; } = 60;

    /// <summary>
    /// هل يُشترط <b>تجاوز</b> الحدّ (أكبر من) بدلاً من بلوغه (أكبر أو يساوي)؟
    /// الافتراضي <c>false</c> = المخالفة عند <b>بلوغ</b> الحدّ.
    /// </summary>
    public bool ViolationWhenExceededOnly { get; init; }

    /// <summary>تقييد الناتج المحتسب (المعروض والمُرحَّل للخصم والملخص الشهري) بسقف أعلى.</summary>
    public bool CapCountedMinutes { get; init; } = true;

    /// <summary>سقف الناتج المحتسب بالدقائق؛ <c>0</c> = يتبع حدّ الدقائق الأسبوعي.</summary>
    public int CapMinutes { get; init; }

    /// <summary>أيام الخصم عن كل أسبوع مخالف (الافتراضي يوم واحد).</summary>
    public double DeductionDaysPerWeek { get; init; } = 1.0;

    /// <summary>اعتماد الناتج المحتسب (المقيَّد بالسقف) في الملخص الشهري والمؤشرات بدل المجموع الفعلي.</summary>
    public bool UseCountedInMonthlyRollup { get; init; } = true;

    /// <summary>حدّ السماح الصباحي لدقائق التأخير (المادة 7) — الافتراضي 15 دقيقة.</summary>
    public int MorningGraceMinutes { get; init; } = 15;

    /// <summary>حدود خاصة بإدارات محدّدة: اسم الإدارة ← حدّ الدقائق الأسبوعي الخاص بها.</summary>
    public Dictionary<string, int> DepartmentThresholdMinutes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// نظام الورديات (مثال: الحراسة بورديات 24 ساعة متواصلة وفق جدول شهري يصدره مسؤول الحراسة):
    /// تُحتسب أيامهم من الجدول الشهري المستورد لا من أوقات الدوام الرسمي، ويُعفون من قاعدة 118/ج.
    /// </summary>
    public PunchShiftRule Shift { get; init; } = new();

    /// <summary>
    /// الدوام المرن: نافذة حضور معتمدة يتحرّك بها وقت انتهاء الدوام لإكمال ساعات العمل اليومية
    /// (لا تأخير داخل النافذة، ويُحتسب النقص عند عدم إكمال الساعات) للموظفين/الإدارات المصرَّح لهم.
    /// </summary>
    public PunchFlexibleRule Flexible { get; init; } = new();

    /// <summary>
    /// العمل الإضافي: احتساب ما يزيد على نهاية الدوام (أو نهاية الدوام الفعّالة في الدوام المرن)
    /// بحدّ أعلى يومي وشهري وأقل مدة تُحتسب ونسب تعويض (عادي/راحة/عطلة) للموظفين المصرَّح لهم.
    /// </summary>
    public PunchOvertimeRule Overtime { get; init; } = new();

    /// <summary>
    /// استثناءات إدارات/مديريات خاصة: اسم الإدارة ← الاستثناء (إعفاء من 118/ج أو التأخير الصباحي
    /// أو الانصراف المبكر أو الغياب، أو حدّ/سقف مختلف، أو معاملتها بنظام الورديات).
    /// </summary>
    public Dictionary<string, PunchDepartmentException> DepartmentExceptions { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>حدود التخصيص المقبولة (حماية من قيم غير منطقية).</summary>
    public const int MaxThresholdMinutes = 720;
    public const int MaxCapMinutes = 1440;
    public const int MaxGraceMinutes = 240;
    public const double MaxDeductionDaysPerWeek = 30.0;

    /// <summary>سقف الناتج المحتسب الفعلي (عند <c>CapMinutes = 0</c> يتبع الحدّ الأسبوعي).</summary>
    public int EffectiveCapMinutes => CapMinutes > 0 ? CapMinutes : ThresholdMinutes;

    /// <summary>الحدّ الأسبوعي الساري على إدارة معيّنة (مع تجاوزات الإدارات إن وُجدت).</summary>
    public int ThresholdFor(string? department)
    {
        var exception = ExceptionFor(department);
        if (exception?.ThresholdMinutes is > 0)
        {
            return Math.Clamp(exception.ThresholdMinutes.Value, 1, MaxThresholdMinutes);
        }

        var key = department?.Trim();
        if (!string.IsNullOrWhiteSpace(key)
            && DepartmentThresholdMinutes.TryGetValue(key, out int custom)
            && custom > 0)
        {
            return Math.Clamp(custom, 1, MaxThresholdMinutes);
        }

        // مطابقة مرنة (احتواء/توحيد الهمزات) لأسماء الإدارات كما ترد في ملف البصمات.
        var matchedThreshold = MatchedThreshold(department);
        if (matchedThreshold is > 0)
        {
            return Math.Clamp(matchedThreshold.Value, 1, MaxThresholdMinutes);
        }

        return Math.Clamp(ThresholdMinutes, 1, MaxThresholdMinutes);
    }

    /// <summary>حدّ إدارة خاص مطابق بمرونة (توحيد الهمزات واحتواء الاسم)، أو null.</summary>
    private int? MatchedThreshold(string? department)
    {
        var key = PunchDepartmentMatcher.FindKey(DepartmentThresholdMinutes, department);
        return key is not null && DepartmentThresholdMinutes.TryGetValue(key, out int value) && value > 0
            ? value
            : null;
    }

    /// <summary>سقف الناتج المحتسب الساري على إدارة معيّنة.</summary>
    public int CapFor(string? department) =>
        ExceptionFor(department)?.CapMinutes is > 0
            ? Math.Clamp(ExceptionFor(department)!.CapMinutes!.Value, 1, MaxCapMinutes)
            : CapMinutes > 0
                ? Math.Clamp(CapMinutes, 1, MaxCapMinutes)
                : ThresholdFor(department);

    /// <summary>الاستثناء الخاص المطابق لإدارة الموظف (أو null إن لم يوجد استثناء).</summary>
    public PunchDepartmentException? ExceptionFor(string? department)
    {
        var key = PunchDepartmentMatcher.FindKey(DepartmentExceptions, department);
        return key is null ? null : DepartmentExceptions[key];
    }

    /// <summary>هل الإدارة معفاة من قاعدة المادة 118/ج الأسبوعية (استثناء إداري)؟</summary>
    public bool ExemptFromWeeklyRule(string? department) =>
        ExceptionFor(department)?.ExemptFromWeeklyRule == true;

    /// <summary>هل الإدارة معفاة من التأخير الصباحي وعقوبة المادة 7؟</summary>
    public bool ExemptFromMorningLateness(string? department) =>
        ExceptionFor(department)?.ExemptFromMorningLateness == true;

    /// <summary>هل الإدارة معفاة من الانصراف المبكر؟</summary>
    public bool ExemptFromEarlyDeparture(string? department) =>
        ExceptionFor(department)?.ExemptFromEarlyDeparture == true;

    /// <summary>هل الإدارة معفاة من احتساب الغياب وساعات الغياب (المادة 118/ب)؟</summary>
    public bool ExemptFromAbsence(string? department) =>
        ExceptionFor(department)?.ExemptFromAbsence == true;

    /// <summary>هل تُعامل الإدارة بنظام الورديات (قائمة إدارات الورديات أو استثناء الإدارة)؟</summary>
    public bool UsesShiftSystem(string? department) =>
        Shift.AppliesTo(department) || ExceptionFor(department)?.ShiftSystem == true;

    /// <summary>هل يُعدّ الأسبوع مخالفاً (بلغ الحدّ أو تجاوزه بحسب الإعداد)؟</summary>
    public bool IsViolation(int totalMinutes, int thresholdMinutes)
    {
        int threshold = Math.Max(1, thresholdMinutes);
        return ViolationWhenExceededOnly ? totalMinutes > threshold : totalMinutes >= threshold;
    }

    /// <summary>الناتج المحتسب: المجموع الفعلي مقيَّداً بالسقف وفق الإعداد.</summary>
    public int CountedMinutes(int totalMinutes, int capMinutes) =>
        CapCountedMinutes ? Math.Clamp(totalMinutes, 0, Math.Max(0, capMinutes)) : totalMinutes;

    /// <summary>أيام الخصم بصيغة نصية (1 بدل 1.00).</summary>
    public string DeductionDaysText() =>
        DeductionDaysPerWeek.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>وصف مختصر للقاعدة السارية (يُستخدم في الواجهة والتقارير).</summary>
    public string Summary()
    {
        if (!Enabled)
        {
            return "قاعدة المادة 118/ج الأسبوعية معطّلة من الإعدادات.";
        }

        var overrides = DepartmentThresholdMinutes.Count > 0
            ? $" + {DepartmentThresholdMinutes.Count} إدارة بحدّ خاص"
            : string.Empty;

        var exceptions = DepartmentExceptions.Count > 0
            ? $" + {DepartmentExceptions.Count} استثناء إداري"
            : string.Empty;

        var shift = Shift.Enabled ? " + نظام الورديات" : string.Empty;

        var workTime = (Flexible.Enabled ? " + دوام مرن" : string.Empty)
            + (Overtime.Enabled ? " + عمل إضافي" : string.Empty);

        return $"{(ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} {ThresholdMinutes} دقيقة أسبوعياً = خصم {DeductionDaysText()} يوم"
             + (CapCountedMinutes ? $" | الناتج المحتسب ≤ {EffectiveCapMinutes} دقيقة" : " | بلا تقييد للناتج المحتسب")
             + overrides + exceptions + shift + workTime;
    }

    /// <summary>وصف قانوني تفصيلي (المنهجية، دليل القواعد، وترويسة تقرير الأسابيع المخالفة).</summary>
    public string Description()
    {
        if (!Enabled)
        {
            return "قاعدة المادة 118/ج الأسبوعية معطّلة من الإعدادات — لا يُبنى تجميع أسبوعي ولا يُحتسب خصم للمادة 118/ج"
                 + " (تبقى دقائق التأخير والانصراف المبكر في النتائج اليومية للتدقيق).";
        }

        var merged = MergeEarlyDeparture
            ? (MergeMidDayGaps ? "التأخير الصباحي + الانصراف المبكر + المغادرة أثناء الدوام" : "التأخير الصباحي + الانصراف المبكر")
            : (MergeMidDayGaps ? "التأخير الصباحي + المغادرة أثناء الدوام" : "التأخير الصباحي");

        var cap = CapCountedMinutes
            ? $"، والناتج المحتسب المعروض/المُرحَّل لا يزيد عن {EffectiveCapMinutes} دقيقة أسبوعياً"
            : "، ويُعرض المجموع الفعلي كاملاً بلا تقييد بسقف";

        var overrides = DepartmentThresholdMinutes.Count > 0
            ? $" | حدود خاصة: {string.Join("، ", DepartmentThresholdMinutes.Select(kv => $"{kv.Key} = {kv.Value} دقيقة"))}"
            : string.Empty;

        var exceptions = DepartmentExceptions.Count > 0
            ? $" | استثناءات إدارية: {string.Join("، ", DepartmentExceptions.Select(kv => $"{kv.Key}: {kv.Value.Text()}"))}"
            : string.Empty;

        var shift = Shift.Enabled
            ? $" | {Shift.Summary()}"
            : string.Empty;

        var workTime = (Flexible.Enabled ? $" | {Flexible.Summary()}" : string.Empty)
            + (Overtime.Enabled ? $" | {Overtime.Summary()}" : string.Empty);

        return $"يُدمج ({merged}) في ناتج أسبوعي واحد خلال أسبوع العمل (الأحد — الخميس)، والمخالفة عند "
             + $"{(ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} {ThresholdMinutes} دقيقة، وخصم {DeductionDaysText()} يوم عن كل أسبوع مخالف"
             + cap + overrides + exceptions + shift + workTime + ".";
    }

    /// <summary>هل القاعدتان متطابقتان في كل القيم (لمعرفة إن كانت النتائج تحتاج إعادة تحليل)؟</summary>
    public bool SameAs(PunchWeeklyRule? other) =>
        other is not null
        && Enabled == other.Enabled
        && MergeEarlyDeparture == other.MergeEarlyDeparture
        && MergeMidDayGaps == other.MergeMidDayGaps
        && ThresholdMinutes == other.ThresholdMinutes
        && ViolationWhenExceededOnly == other.ViolationWhenExceededOnly
        && CapCountedMinutes == other.CapCountedMinutes
        && CapMinutes == other.CapMinutes
        && DeductionDaysPerWeek.Equals(other.DeductionDaysPerWeek)
        && UseCountedInMonthlyRollup == other.UseCountedInMonthlyRollup
        && MorningGraceMinutes == other.MorningGraceMinutes
        && SameOverrides(DepartmentThresholdMinutes, other.DepartmentThresholdMinutes)
        && Shift.SameAs(other.Shift)
        && Flexible.SameAs(other.Flexible)
        && Overtime.SameAs(other.Overtime)
        && SameExceptions(DepartmentExceptions, other.DepartmentExceptions);

    /// <summary>مقارنة استثناءات الإدارات (المفتاح + كل قيم الاستثناء) بغضّ النظر عن حالة الأحرف والترتيب.</summary>
    private static bool SameExceptions(
        Dictionary<string, PunchDepartmentException>? left,
        Dictionary<string, PunchDepartmentException>? right)
    {
        var a = left ?? new Dictionary<string, PunchDepartmentException>();
        var b = right ?? new Dictionary<string, PunchDepartmentException>();

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            var key = PunchDepartmentMatcher.FindKey(b, pair.Key);
            if (key is null || b[key] != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>مقارنة تجاوزات الإدارات بغضّ النظر عن حالة الأحرف.</summary>
    private static bool SameOverrides(Dictionary<string, int>? left, Dictionary<string, int>? right)
    {
        var a = left ?? new Dictionary<string, int>();
        var b = right ?? new Dictionary<string, int>();

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out int value) || value != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// تقييد القيم داخل الحدود المنطقية وتنظيف تجاوزات الإدارات
    /// (يُطبَّق على كل قراءة من الملف وعلى كل حفظ من الواجهة).
    /// </summary>
    public static PunchWeeklyRule Normalize(PunchWeeklyRule rule)
    {
        var overrides = (rule.DepartmentThresholdMinutes ?? new Dictionary<string, int>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
            .GroupBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Math.Clamp(g.First().Value, 1, MaxThresholdMinutes),
                StringComparer.OrdinalIgnoreCase);

        var exceptions = (rule.DepartmentExceptions ?? new Dictionary<string, PunchDepartmentException>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null && !kv.Value.IsEmpty)
            .GroupBy(kv => PunchDepartmentMatcher.Normalize(kv.Key), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                g => g.First().Value.Normalized(),
                StringComparer.OrdinalIgnoreCase);

        return rule with
        {
            ThresholdMinutes = Math.Clamp(rule.ThresholdMinutes, 1, MaxThresholdMinutes),
            CapMinutes = rule.CapMinutes <= 0 ? 0 : Math.Clamp(rule.CapMinutes, 1, MaxCapMinutes),
            DeductionDaysPerWeek = Math.Clamp(rule.DeductionDaysPerWeek, 0, MaxDeductionDaysPerWeek),
            MorningGraceMinutes = Math.Clamp(rule.MorningGraceMinutes, 0, MaxGraceMinutes),
            DepartmentThresholdMinutes = overrides,
            Shift = PunchShiftRule.Normalize(rule.Shift),
            Flexible = PunchFlexibleRule.Normalize(rule.Flexible),
            Overtime = PunchOvertimeRule.Normalize(rule.Overtime),
            DepartmentExceptions = exceptions
        };
    }
}

/// <summary>الملف المحفوظ على القرص: القاعدة السارية + آخر قيم طُبِّقت فعلياً في التحليل.</summary>
public sealed record PunchWeeklyRuleFile(
    PunchWeeklyRule Rule,
    DateTime? SavedAtUtc = null,
    PunchWeeklyRule? AppliedRule = null,
    DateTime? LastAnalyzedAtUtc = null);

/// <summary>عرض القاعدة الأسبوعية في الواجهة: السارية + الافتراضية + حالة آخر تحليل.</summary>
public sealed record PunchWeeklyRuleView(
    PunchWeeklyRule Rule,
    PunchWeeklyRule Defaults,
    bool IsCustomized,
    bool PendingReanalysis,
    DateTime? SavedAtUtc,
    DateTime? LastAnalyzedAtUtc,
    string Summary,
    string Description,
    string SettingsFile);

/// <summary>ملخص إحصائي لتطبيق قاعدة أسبوعية (يُستخدم في معاينة أثر الإعدادات).</summary>
public sealed record PunchWeeklyRuleImpact(
    bool RuleEnabled,
    int Weeks,
    int ViolatingWeeks,
    int Employees,
    int EmployeesWithViolations,
    long CountedMinutes,
    long TotalMinutes,
    double DeductionDays);

/// <summary>أسبوع تغيّر حكمه بين القاعدة الحالية والقاعدة المرشّحة (معاينة فقط، بلا حفظ).</summary>
public sealed record PunchWeeklyRuleImpactDelta(
    string JobNumber,
    string? EmployeeName,
    string? DepartmentName,
    string WeekStart,
    int TotalMinutes,
    int CurrentCountedMinutes,
    double CurrentDeductionDays,
    int ProjectedCountedMinutes,
    double ProjectedDeductionDays);

/// <summary>
/// معاينة أثر قاعدة المادة 118/ج المرشّحة قبل حفظها: تُعاد مقارنة بين النتائج المخزّنة حالياً
/// والنتائج المتوقّعة لو طُبِّقت القاعدة الجديدة (من دون أي تعديل على البيانات).
/// </summary>
public sealed record PunchWeeklyRulePreview(
    PunchWeeklyRule ProposedRule,
    string ProposedSummary,
    PunchWeeklyRuleImpact Current,
    PunchWeeklyRuleImpact Projected,
    int ChangedWeeks,
    IReadOnlyList<PunchWeeklyRuleImpactDelta> Samples);
