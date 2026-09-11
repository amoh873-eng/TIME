using System.Globalization;

namespace AttendanceApi.Domain;

/// <summary>
/// «نظام الورديات» (مثال: الحراسة التي تعمل بورديات 24 ساعة متواصلة) — مرن وقابل للتخصيص
/// في وقت التشغيل بلا إعادة بناء ولا إعادة تشغيل الخدمة:
/// تُحتسب أيام العاملين بالورديات من «جدول الورديات الشهري» الصادر من مسؤول الحراسة
/// (المستورد إلى <c>ShiftScheduleEntries</c>) لا من ساعات الدوام الرسمي (08:30 — 15:30).
/// </summary>
public sealed record PunchShiftRule
{
    /// <summary>تفعيل نظام الورديات كلياً؛ عند التعطيل تُطبَّق القواعد العامة على الجميع.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>طول الوردية بالساعات (الافتراضي 24 ساعة متواصلة) — يُستخدم في وصف الوردية والتقارير.</summary>
    public double CycleHours { get; init; } = 24;

    /// <summary>إعفاء العاملين بالورديات من قاعدة المادة 118/ج الأسبوعية (لا تجميع أسبوعي ولا خصم).</summary>
    public bool ExemptFromWeeklyRule { get; init; } = true;

    /// <summary>إعفاء العاملين بالورديات من التأخير الصباحي وعقوبة المادة 7.</summary>
    public bool ExemptFromMorningLateness { get; init; } = true;

    /// <summary>إعفاء العاملين بالورديات من الانصراف المبكر (لا معنى له في الورديات المتواصلة).</summary>
    public bool ExemptFromEarlyDeparture { get; init; } = true;

    /// <summary>أيام الراحة/العطل/الإجازات في الجدول الشهري لا تُحتسب غياباً.</summary>
    public bool RestDaysNotAbsence { get; init; } = true;

    /// <summary>
    /// يوم عمل بلا قيد في الجدول الشهري: <c>true</c> = يُستثنى من القواعد العامة،
    /// و<c>false</c> = تُطبَّق عليه القواعد العامة (حتى لا يفلت يوم بلا قيد من المراجعة).
    /// </summary>
    public bool MissingScheduleExempts { get; init; }

    /// <summary>وردية دوام بلا أي بصمة: <c>true</c> = تُحتسب غياباً، <c>false</c> = تُلاحَظ للمراجعة بلا خصم.</summary>
    public bool MissingPunchIsAbsence { get; init; }

    /// <summary>سماح التأخير داخل يوم الوردية (0 = لا يُحتسب تأخير في الورديات).</summary>
    public int DutyGraceMinutes { get; init; }

    /// <summary>إدارات/مديريات تُعامل بنظام الورديات تلقائياً (مثال: الحراسة، الأمن، الدفاع المدني).</summary>
    public IReadOnlyList<string> Departments { get; init; } = Array.Empty<string>();

    // ---- حدود التخصيص المقبولة (حماية من قيم غير منطقية) ----
    public const double MinCycleHours = 1;
    public const double MaxCycleHours = 24;
    public const int MaxDutyGraceMinutes = 240;
    public const int MaxDepartments = 500;

    /// <summary>هل تُعامل هذه الإدارة بنظام الورديات (من قائمة الإدارات المخصّصة)؟</summary>
    public bool AppliesTo(string? department) =>
        Enabled && PunchDepartmentMatcher.MatchesAny(Departments, department);

    /// <summary>طول الوردية بصيغة نصية (24 بدل 24.00).</summary>
    public string CycleHoursText() => CycleHours.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>وصف مختصر لإعدادات الورديات (الواجهة وملاحظات النتائج).</summary>
    public string Summary()
    {
        if (!Enabled)
        {
            return "نظام الورديات معطّل من الإعدادات.";
        }

        var deps = Departments.Count > 0
            ? $" | إدارات ورديات: {string.Join("، ", Departments)}"
            : " | بلا إدارات ورديات محدّدة (الاحتساب من الجدول المستورد فقط)";

        return $"نظام الورديات مُفعّل ({CycleHoursText()} ساعة/وردية)"
             + (ExemptFromWeeklyRule ? " | إعفاء من المادة 118/ج" : string.Empty)
             + (ExemptFromMorningLateness ? " | إعفاء من التأخير الصباحي" : string.Empty)
             + (RestDaysNotAbsence ? " | أيام الراحة لا تُحتسب غياباً" : string.Empty)
             + deps;
    }

    /// <summary>هل القاعدتان متطابقتان في كل القيم (لمعرفة إن كانت النتائج تحتاج إعادة تحليل)؟</summary>
    public bool SameAs(PunchShiftRule? other)
    {
        if (other is null)
        {
            return false;
        }

        var a = SortedDepartments(Departments);
        var b = SortedDepartments(other.Departments);

        return Enabled == other.Enabled
            && CycleHours.Equals(other.CycleHours)
            && ExemptFromWeeklyRule == other.ExemptFromWeeklyRule
            && ExemptFromMorningLateness == other.ExemptFromMorningLateness
            && ExemptFromEarlyDeparture == other.ExemptFromEarlyDeparture
            && RestDaysNotAbsence == other.RestDaysNotAbsence
            && MissingScheduleExempts == other.MissingScheduleExempts
            && MissingPunchIsAbsence == other.MissingPunchIsAbsence
            && DutyGraceMinutes == other.DutyGraceMinutes
            && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>قائمة إدارات موحّدة ومنظّمة (للمقارنة والعرض).</summary>
    internal static List<string> SortedDepartments(IEnumerable<string>? departments) =>
        (departments ?? Array.Empty<string>())
            .Select(PunchDepartmentMatcher.Normalize)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>تقييد قيم نظام الورديات داخل الحدود المنطقية وتنظيف قائمة الإدارات.</summary>
    public static PunchShiftRule Normalize(PunchShiftRule? rule)
    {
        rule ??= new PunchShiftRule();

        return rule with
        {
            CycleHours = Math.Clamp(rule.CycleHours, MinCycleHours, MaxCycleHours),
            DutyGraceMinutes = Math.Clamp(rule.DutyGraceMinutes, 0, MaxDutyGraceMinutes),
            Departments = SortedDepartments(rule.Departments).Take(MaxDepartments).ToList()
        };
    }
}


/// <summary>
/// «استثناء خاص» لإدارة/مديرية محدّدة: إعفاء من قاعدة أو أكثر، أو حدّ أسبوعي/سقف مختلف،
/// أو معاملتها بنظام الورديات — يُدار من الواجهة في وقت التشغيل بلا إعادة تشغيل الخدمة.
/// </summary>
public sealed record PunchDepartmentException
{
    /// <summary>تُعامل الإدارة بنظام الورديات (وفق جدولها الشهري).</summary>
    public bool ShiftSystem { get; init; }

    /// <summary>إعفاء من قاعدة المادة 118/ج الأسبوعية (لا تجميع ولا خصم لهذه الإدارة).</summary>
    public bool ExemptFromWeeklyRule { get; init; }

    /// <summary>إعفاء من التأخير الصباحي وعقوبة المادة 7.</summary>
    public bool ExemptFromMorningLateness { get; init; }

    /// <summary>إعفاء من الانصراف المبكر.</summary>
    public bool ExemptFromEarlyDeparture { get; init; }

    /// <summary>إعفاء من احتساب الغياب وساعات الغياب (المادة 118/ب).</summary>
    public bool ExemptFromAbsence { get; init; }

    /// <summary>حدّ أسبوعي خاص بالإدارة (المادة 118/ج) بدل الحدّ العام.</summary>
    public int? ThresholdMinutes { get; init; }

    /// <summary>سقف خاص للناتج المحتسب بدل السقف العام.</summary>
    public int? CapMinutes { get; init; }

    /// <summary>سبب الاستثناء / سنده (للتدقيق).</summary>
    public string? Note { get; init; }

    /// <summary>هل الاستثناء فارغ (لا يغيّر أي حكم)؟</summary>
    public bool IsEmpty =>
        !ShiftSystem
        && !ExemptFromWeeklyRule
        && !ExemptFromMorningLateness
        && !ExemptFromEarlyDeparture
        && !ExemptFromAbsence
        && ThresholdMinutes is null
        && CapMinutes is null
        && string.IsNullOrWhiteSpace(Note);

    /// <summary>وصف مختصر للاستثناء (الواجهة والتقارير وملاحظات النتائج).</summary>
    public string Text()
    {
        var parts = new List<string>(6);

        if (ShiftSystem)
        {
            parts.Add("نظام ورديات");
        }

        if (ExemptFromWeeklyRule)
        {
            parts.Add("إعفاء من 118/ج");
        }

        if (ExemptFromMorningLateness)
        {
            parts.Add("إعفاء من التأخير الصباحي");
        }

        if (ExemptFromEarlyDeparture)
        {
            parts.Add("إعفاء من الانصراف المبكر");
        }

        if (ExemptFromAbsence)
        {
            parts.Add("إعفاء من احتساب الغياب");
        }

        if (ThresholdMinutes is > 0)
        {
            parts.Add($"حدّ خاص {ThresholdMinutes} دقيقة");
        }

        if (CapMinutes is > 0)
        {
            parts.Add($"سقف خاص {CapMinutes} دقيقة");
        }

        if (!string.IsNullOrWhiteSpace(Note))
        {
            parts.Add($"السبب: {Note!.Trim()}");
        }

        return parts.Count > 0 ? string.Join(" + ", parts) : "لا استثناء";
    }

    /// <summary>تقييد الحدود داخل القيم المقبولة وتنظيف السبب.</summary>
    public PunchDepartmentException Normalized() => this with
    {
        ThresholdMinutes = ThresholdMinutes is > 0
            ? Math.Clamp(ThresholdMinutes.Value, 1, PunchWeeklyRule.MaxThresholdMinutes)
            : null,
        CapMinutes = CapMinutes is > 0
            ? Math.Clamp(CapMinutes.Value, 1, PunchWeeklyRule.MaxCapMinutes)
            : null,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note!.Trim()
    };
}
