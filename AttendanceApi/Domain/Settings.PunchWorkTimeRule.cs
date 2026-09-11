using System.Globalization;
using System.Text;

namespace AttendanceApi.Domain;

/// <summary>تحويل نصوص الأوقات («07:30» أو «7:30» أو «07:30:00») إلى <see cref="TimeOnly"/>.</summary>
public static class PunchTimeText
{
    /// <summary>تحليل نص وقت؛ يُرجع null إن كان النص غير صالح.</summary>
    public static TimeOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeDigits(text)
            .Replace('٫', ':')
            .Replace('.', ':')
            .Trim();

        if (normalized.Contains(' '))
        {
            normalized = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        if (TimeOnly.TryParseExact(
                normalized,
                new[] { "HH\\:mm", "H\\:mm", "HH\\:mm\\:ss", "H\\:mm\\:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact;
        }

        return TimeOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
            ? loose
            : null;
    }

    /// <summary>تحليل النص مع قيمة بديلة عند الفشل.</summary>
    public static TimeOnly ParseOr(string? text, TimeOnly fallback) => Parse(text) ?? fallback;

    /// <summary>صيغة العرض الموحّدة للوقت (HH:mm).</summary>
    public static string Format(TimeOnly? value) => value.HasValue ? value.Value.ToString("HH\\:mm") : "—";

    /// <summary>تحويل الأرقام العربية/الفارسية إلى أرقام لاتينية قبل التحليل.</summary>
    private static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            sb.Append(ch switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + (ch - '\u0660')), // ٠١٢...
                >= '\u06F0' and <= '\u06F9' => (char)('0' + (ch - '\u06F0')), // ۰۱۲...
                _ => ch
            });
        }

        return sb.ToString();
    }
}

/// <summary>
/// «الدوام المرن» وفق ضوابط الدوام المرن لموظفي الخدمة المدنية:
/// يُسمح للموظف المصرَّح له بالحضور داخل نافذة زمنية معتمدة (الافتراضي 07:30 — 09:30)،
/// فيتحرّك وقت انتهاء دوامه تلقائياً بقدر تأخّره لإكمال ساعات الدوام اليومية،
/// فلا يُحتسب عليه تأخير صباحي داخل النافذة، ويُحتسب عليه الانصراف المبكر إن لم يُكمل ساعاته.
/// لا يُطبَّق إلا على النطاق المصرَّح له (قوائم الإدارات/الموظفين أو تصاريح سارية في جدول الموافقات)،
/// وهو قابل للتخصيص في وقت التشغيل بلا إعادة تشغيل الخدمة (ملف <c>punch-analysis.json</c>).
/// </summary>
public sealed record PunchFlexibleRule
{
    /// <summary>تفعيل الدوام المرن كلياً؛ عند التعطيل تُطبَّق أوقات الدوام الرسمي على الجميع.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>أبكر وقت حضور مسموح في الدوام المرن (وقبل بدء الدوام الرسمي = حضور مبكر).</summary>
    public string EarliestArrival { get; init; } = "07:30";

    /// <summary>آخر وقت حضور مسموح؛ الحضور بعده يُحتسب تأخيراً صباحياً من هذا الوقت.</summary>
    public string LatestArrival { get; init; } = "09:30";

    /// <summary>الدقائق المطلوب إكمالها يومياً؛ 0 = مدة الدوام الرسمي (7 ساعات = 420 دقيقة).</summary>
    public int CompleteDailyMinutes { get; init; }

    /// <summary>اشتراط تصريح/موافقة سارية (أو ورود الموظف/إدارته في القوائم) لتطبيق الدوام المرن.</summary>
    public bool RequireApproval { get; init; } = true;

    /// <summary>احتساب الحضور قبل بدء الدوام الرسمي ضمن نافذة المرونة كعمل إضافي (الافتراضي: لا).</summary>
    public bool EarlyArrivalCountsAsOvertime { get; init; }

    /// <summary>إدارات/مديريات يُطبَّق عليها الدوام المرن.</summary>
    public IReadOnlyList<string> Departments { get; init; } = Array.Empty<string>();

    /// <summary>أرقام وظيفية يُطبَّق عليها الدوام المرن.</summary>
    public IReadOnlyList<string> Employees { get; init; } = Array.Empty<string>();

    /// <summary>ملاحظة/سند التصريح (للتدقيق).</summary>
    public string? Note { get; init; }

    /// <summary>أقصى مدى مرونة مقبول بين أبكر وآخر وقت حضور (5 ساعات).</summary>
    public const int MaxWindowMinutes = 300;

    /// <summary>مدة الدوام الرسمي اليومي بالدقائق (7 ساعات: 08:30 — 15:30).</summary>
    public const int DefaultDailyMinutes = 420;

    /// <summary>أبكر وقت حضور ساري.</summary>
    public TimeOnly EarliestArrivalTime => PunchTimeText.ParseOr(EarliestArrival, new TimeOnly(7, 30));

    /// <summary>آخر وقت حضور ساري.</summary>
    public TimeOnly LatestArrivalTime => PunchTimeText.ParseOr(LatestArrival, new TimeOnly(9, 30));

    /// <summary>الدقائق المطلوب إكمالها يومياً (7 ساعات افتراضياً).</summary>
    public int EffectiveDailyMinutes => CompleteDailyMinutes > 0 ? CompleteDailyMinutes : DefaultDailyMinutes;

    /// <summary>مدى نافذة المرونة بالدقائق.</summary>
    public int WindowMinutes => Math.Max(0, (int)Math.Round(
        LatestArrivalTime.ToTimeSpan().TotalMinutes - EarliestArrivalTime.ToTimeSpan().TotalMinutes));

    /// <summary>
    /// هل يُطبَّق الدوام المرن على موظف/إدارة معيّنة في تاريخ محدّد؟
    /// (تصريح ساري، أو قائمة إدارات/أرقام وظيفية، وإلا فالجميع عند عدم اشتراط التصريح وخلوّ القوائم).
    /// </summary>
    public bool AppliesTo(
        string jobNumber,
        string? department,
        DateOnly date,
        PunchWorkApprovalIndex? approvals = null)
    {
        if (!Enabled)
        {
            return false;
        }

        if (approvals?.Find(jobNumber, WorkApprovalKind.Flexible, date) is not null)
        {
            return true;
        }

        if (PunchDepartmentMatcher.MatchesAny(Departments, department))
        {
            return true;
        }

        if (ListedEmployee(Employees, jobNumber))
        {
            return true;
        }

        return !RequireApproval && Departments.Count == 0 && Employees.Count == 0;
    }

    /// <summary>هل الرقم الوظيفي مدرج في قائمة الأرقام (مقارنة نصية مرنة)؟</summary>
    internal static bool ListedEmployee(IEnumerable<string>? employees, string? jobNumber)
    {
        if (employees is null || string.IsNullOrWhiteSpace(jobNumber))
        {
            return false;
        }

        var target = NormalizeJobNumber(jobNumber);
        foreach (var item in employees)
        {
            if (NormalizeJobNumber(item).Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>توحيد الرقم الوظيفي للمقارنة (بلا مسافات).</summary>
    internal static string NormalizeJobNumber(string? value) =>
        (value ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("\u00A0", string.Empty);

    /// <summary>صيغة الساعات النصية (دقائق ⇒ «3 ساعات» أو «1.5 ساعة»).</summary>
    public static string HoursText(int minutes) =>
        minutes <= 0
            ? "بلا سقف"
            : minutes % 60 == 0
                ? $"{minutes / 60} ساعة"
                : $"{(minutes / 60.0).ToString("0.##", CultureInfo.InvariantCulture)} ساعة";

    /// <summary>نص النطاق المصرَّح به (مشترك بين قاعدتي الدوام المرن والعمل الإضافي).</summary>
    internal static string ScopeText(
        IReadOnlyList<string> departments,
        IReadOnlyList<string> employees,
        bool requireApproval,
        string label)
    {
        if (departments.Count > 0 || employees.Count > 0)
        {
            var parts = new List<string>(2);
            if (departments.Count > 0)
            {
                parts.Add($"{departments.Count} إدارة");
            }

            if (employees.Count > 0)
            {
                parts.Add($"{employees.Count} رقماً وظيفياً");
            }

            return $"محدّد بـ {string.Join(" + ", parts)}";
        }

        return requireApproval
            ? $"بلا تصاريح/قوائم مسجّلة (لا يُطبَّق {label} على أحد حالياً)"
            : "جميع الموظفين";
    }

    /// <summary>وصف مختصر لإعدادات الدوام المرن (الواجهة والتقارير).</summary>
    public string Summary()
    {
        if (!Enabled)
        {
            return "الدوام المرن معطّل من الإعدادات.";
        }

        var scope = ScopeText(Departments, Employees, RequireApproval, "الدوام المرن");

        return $"الدوام المرن مُفعّل: حضور {PunchTimeText.Format(EarliestArrivalTime)} — {PunchTimeText.Format(LatestArrivalTime)}"
             + $" وإكمال {HoursText(EffectiveDailyMinutes)} يومياً (نهاية الدوام تتحرّك بقدر التأخّر)"
             + $" | النطاق: {scope}"
             + (EarlyArrivalCountsAsOvertime ? " | الحضور المبكر يُحتسب إضافياً" : string.Empty);
    }

    /// <summary>هل القاعدتان متطابقتان (لمعرفة إن كانت النتائج تحتاج إعادة تحليل)؟</summary>
    public bool SameAs(PunchFlexibleRule? other) =>
        other is not null
        && Enabled == other.Enabled
        && EarliestArrivalTime == other.EarliestArrivalTime
        && LatestArrivalTime == other.LatestArrivalTime
        && EffectiveDailyMinutes == other.EffectiveDailyMinutes
        && RequireApproval == other.RequireApproval
        && EarlyArrivalCountsAsOvertime == other.EarlyArrivalCountsAsOvertime
        && PunchShiftRule.SortedDepartments(Departments)
            .SequenceEqual(PunchShiftRule.SortedDepartments(other.Departments), StringComparer.OrdinalIgnoreCase)
        && PunchShiftRule.SortedDepartments(Employees)
            .SequenceEqual(PunchShiftRule.SortedDepartments(other.Employees), StringComparer.OrdinalIgnoreCase);

    /// <summary>تقييد قيم الدوام المرن داخل الحدود المنطقية وتنظيف القوائم.</summary>
    public static PunchFlexibleRule Normalize(PunchFlexibleRule? rule)
    {
        rule ??= new PunchFlexibleRule();

        var earliest = rule.EarliestArrivalTime;
        var latest = rule.LatestArrivalTime;

        if (latest <= earliest)
        {
            latest = earliest.AddMinutes(60);
        }

        if (rule.WindowMinutes > MaxWindowMinutes)
        {
            latest = earliest.AddMinutes(MaxWindowMinutes);
        }

        return rule with
        {
            EarliestArrival = PunchTimeText.Format(earliest),
            LatestArrival = PunchTimeText.Format(latest),
            CompleteDailyMinutes = rule.CompleteDailyMinutes <= 60
                ? 0
                : Math.Clamp(rule.CompleteDailyMinutes, 60, 720),
            Departments = PunchShiftRule.SortedDepartments(rule.Departments)
                .Take(PunchShiftRule.MaxDepartments).ToList(),
            Employees = (rule.Employees ?? Array.Empty<string>())
                .Select(e => (e ?? string.Empty).Trim())
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .Take(PunchShiftRule.MaxDepartments)
                .ToList(),
            Note = string.IsNullOrWhiteSpace(rule.Note) ? null : rule.Note!.Trim()
        };
    }
}

/// <summary>
/// «العمل الإضافي» وفق قانون الخدمة المدنية والحدود المعتمدة للعمل الإضافي:
/// تُحتسب المدة التي تزيد على نهاية الدوام الرسمي (أو على نهاية الدوام الفعّالة عند تطبيق الدوام المرن)
/// للموظفين المصرَّح لهم، بحدّ أعلى يومي وشهري وأقل مدة تُحتسب، مع نسب تعويض مختلفة لأيام العمل العادية
/// وأيام الراحة والعطل، وتُحتسب «الساعات المعادلة» وفق هذه النسب.
/// كل القيم قابلة للتخصيص في وقت التشغيل بلا إعادة تشغيل الخدمة (ملف <c>punch-analysis.json</c>).
/// </summary>
public sealed record PunchOvertimeRule
{
    /// <summary>تفعيل احتساب العمل الإضافي كلياً.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>الحدّ الأعلى للعمل الإضافي في اليوم بالدقائق (الافتراضي 3 ساعات = 180 دقيقة).</summary>
    public int MaxMinutesPerDay { get; init; } = 180;

    /// <summary>الحدّ الأعلى للعمل الإضافي طوال الشهر بالدقائق (الافتراضي 20 ساعة = 1200 دقيقة؛ 0 = بلا سقف).</summary>
    public int MaxMinutesPerMonth { get; init; } = 1200;

    /// <summary>أقل مدة عمل إضافي تُحتسب في اليوم (الافتراضي 30 دقيقة؛ وما دونها لا يُحتسب).</summary>
    public int MinMinutesPerDay { get; init; } = 30;

    /// <summary>خصم دقائق التأخير الصباحي من العمل الإضافي في اليوم نفسه (تعويض التأخّر قبل الاحتساب).</summary>
    public bool CompensateLateness { get; init; } = true;

    /// <summary>احتساب الحضور قبل بدء الدوام الرسمي عمل إضافي (الافتراضي: لا، فالتكليف يبدأ من بدء الدوام).</summary>
    public bool CountEarlyArrival { get; init; }

    /// <summary>احتساب الدوام الفعلي في أيام نهاية الأسبوع عملاً إضافياً.</summary>
    public bool CountOnWeekends { get; init; } = true;

    /// <summary>احتساب الدوام الفعلي في العطل الرسمية والدينية عملاً إضافياً.</summary>
    public bool CountOnHolidays { get; init; } = true;

    /// <summary>نسبة تعويض العمل الإضافي في أيام العمل العادية (الافتراضي 1.0).</summary>
    public double RegularRate { get; init; } = 1.0;

    /// <summary>نسبة تعويض العمل الإضافي في أيام نهاية الأسبوع (الافتراضي 1.5).</summary>
    public double WeekendRate { get; init; } = 1.5;

    /// <summary>نسبة تعويض العمل الإضافي في العطل الرسمية والدينية (الافتراضي 2.0).</summary>
    public double HolidayRate { get; init; } = 2.0;

    /// <summary>تدوير دقائق العمل الإضافي إلى مضاعف هذا الرقم بالدقائق (0 = بلا تدوير).</summary>
    public int RoundToMinutes { get; init; }

    /// <summary>
    /// اشتراط تصريح/موافقة سارية **مسبقاً** لاحتساب العمل الإضافي (الافتراضي: نعم)؛
    /// فلا يُحتسب أي عمل إضافي لمن ليس لديه تصريح ساري ولا هو داخل قوائم الإدارات/الأرقام المصرَّح لها.
    /// </summary>
    public bool RequireApproval { get; init; } = true;

    /// <summary>إدارات/مديريات مصرَّح لها بالعمل الإضافي.</summary>
    public IReadOnlyList<string> Departments { get; init; } = Array.Empty<string>();

    /// <summary>أرقام وظيفية مصرَّح لها بالعمل الإضافي.</summary>
    public IReadOnlyList<string> Employees { get; init; } = Array.Empty<string>();

    /// <summary>ملاحظة/سند الحدود المعتمدة (للتدقيق).</summary>
    public string? Note { get; init; }

    /// <summary>حدود القيم المقبولة (حماية من قيم غير منطقية).</summary>
    public const int MaxDailyMinutesLimit = 720;

    public const int MaxMonthlyMinutesLimit = 24000;

    public const double MaxRate = 5.0;

    /// <summary>الحدّ الأعلى الساري للعمل الإضافي اليومي بعد التقييد (0 = بلا حدّ).</summary>
    public int EffectiveMaxMinutesPerDay =>
        MaxMinutesPerDay <= 0 ? 0 : Math.Clamp(MaxMinutesPerDay, 0, MaxDailyMinutesLimit);

    /// <summary>الحدّ الأعلى الساري للعمل الإضافي الشهري (0 = بلا سقف).</summary>
    public int EffectiveMaxMinutesPerMonth =>
        MaxMinutesPerMonth <= 0 ? 0 : Math.Clamp(MaxMinutesPerMonth, 0, MaxMonthlyMinutesLimit);

    /// <summary>نسبة التعويض السارية وفق نوع يوم العمل الإضافي.</summary>
    public double RateFor(OvertimeDayKind kind) => kind switch
    {
        OvertimeDayKind.Holiday => Math.Clamp(HolidayRate, 1, MaxRate),
        OvertimeDayKind.Weekend => Math.Clamp(WeekendRate, 1, MaxRate),
        _ => Math.Clamp(RegularRate, 1, MaxRate)
    };

    /// <summary>هل النطاق مقيَّد بقوائم/تصاريح (أي لا يُحتسب الإضافي إلا للمصرَّح لهم)؟</summary>
    public bool IsScoped => RequireApproval || Departments.Count > 0 || Employees.Count > 0;

    /// <summary>
    /// استحقاق موظف للعمل الإضافي في تاريخ محدّد: تصريح ساري، أو قائمة إدارات/أرقام وظيفية،
    /// وإلا فالجميع عند عدم اشتراط التصريح وخلوّ القوائم.
    /// </summary>
    public OvertimeEligibility EligibleFor(
        string jobNumber,
        string? department,
        DateOnly date,
        PunchWorkApprovalIndex? approvals)
    {
        if (!Enabled)
        {
            return new OvertimeEligibility(false, 0, null, false, "العمل الإضافي معطّل من الإعدادات");
        }

        if (approvals?.Find(jobNumber, WorkApprovalKind.Overtime, date) is { } approval)
        {
            var note = string.IsNullOrWhiteSpace(approval.Note) ? string.Empty : $" — {approval.Note}";
            return new OvertimeEligibility(
                true,
                approval.MaxMinutesPerDay is > 0 ? approval.MaxMinutesPerDay!.Value : EffectiveMaxMinutesPerDay,
                approval.MaxMinutesTotal is > 0 ? approval.MaxMinutesTotal : null,
                false,
                $"تصريح عمل إضافي ساري حتى {approval.ToDate:yyyy/MM/dd}{note}");
        }

        if (PunchDepartmentMatcher.MatchesAny(Departments, department))
        {
            return new OvertimeEligibility(true, EffectiveMaxMinutesPerDay, null, false, "إدارة مصرَّح لها بالعمل الإضافي");
        }

        if (PunchFlexibleRule.ListedEmployee(Employees, jobNumber))
        {
            return new OvertimeEligibility(true, EffectiveMaxMinutesPerDay, null, false, "موظف مصرَّح له بالعمل الإضافي");
        }

        if (!RequireApproval && Departments.Count == 0 && Employees.Count == 0)
        {
            return new OvertimeEligibility(true, EffectiveMaxMinutesPerDay, null, false, "بلا اشتراط تصريح");
        }

        // لا تصريح ساري ولا قائمة معتمدة ⇒ لا يُحتسب أي عمل إضافي بلا موافقة مسبقة.
        return new OvertimeEligibility(false, 0, null, true, "بلا تصريح ساري بالعمل الإضافي");
    }

    /// <summary>نسبة التعويض بصيغة نصية (1.5 ⇒ «1.5×»).</summary>
    public static string RateText(double rate) => $"{rate.ToString("0.##", CultureInfo.InvariantCulture)}×";

    /// <summary>وصف مختصر لإعدادات العمل الإضافي (الواجهة والتقارير).</summary>
    public string Summary()
    {
        if (!Enabled)
        {
            return "العمل الإضافي معطّل من الإعدادات.";
        }

        var scope = PunchFlexibleRule.ScopeText(Departments, Employees, RequireApproval, "العمل الإضافي");

        return $"العمل الإضافي مُفعّل: حدّ {PunchFlexibleRule.HoursText(EffectiveMaxMinutesPerDay)} يومياً"
             + $" و{PunchFlexibleRule.HoursText(EffectiveMaxMinutesPerMonth)} شهرياً، وأقل مدة تُحتسب {MinMinutesPerDay} دقيقة"
             + $" | نسب التعويض: عادي {RateText(RegularRate)} — نهاية أسبوع {RateText(WeekendRate)} — عطلة {RateText(HolidayRate)}"
             + $" | النطاق: {scope}";
    }

    /// <summary>هل القاعدتان متطابقتان (لمعرفة إن كانت النتائج تحتاج إعادة تحليل)؟</summary>
    public bool SameAs(PunchOvertimeRule? other) =>
        other is not null
        && Enabled == other.Enabled
        && EffectiveMaxMinutesPerDay == other.EffectiveMaxMinutesPerDay
        && EffectiveMaxMinutesPerMonth == other.EffectiveMaxMinutesPerMonth
        && MinMinutesPerDay == other.MinMinutesPerDay
        && CompensateLateness == other.CompensateLateness
        && CountEarlyArrival == other.CountEarlyArrival
        && CountOnWeekends == other.CountOnWeekends
        && CountOnHolidays == other.CountOnHolidays
        && RegularRate.Equals(other.RegularRate)
        && WeekendRate.Equals(other.WeekendRate)
        && HolidayRate.Equals(other.HolidayRate)
        && RoundToMinutes == other.RoundToMinutes
        && RequireApproval == other.RequireApproval
        && PunchShiftRule.SortedDepartments(Departments)
            .SequenceEqual(PunchShiftRule.SortedDepartments(other.Departments), StringComparer.OrdinalIgnoreCase)
        && PunchShiftRule.SortedDepartments(Employees)
            .SequenceEqual(PunchShiftRule.SortedDepartments(other.Employees), StringComparer.OrdinalIgnoreCase);

    /// <summary>تقييد قيم العمل الإضافي داخل الحدود المنطقية وتنظيف القوائم.</summary>
    public static PunchOvertimeRule Normalize(PunchOvertimeRule? rule)
    {
        rule ??= new PunchOvertimeRule();

        return rule with
        {
            MaxMinutesPerDay = rule.MaxMinutesPerDay <= 0
                ? 0
                : Math.Clamp(rule.MaxMinutesPerDay, 0, MaxDailyMinutesLimit),
            MaxMinutesPerMonth = rule.MaxMinutesPerMonth <= 0
                ? 0
                : Math.Clamp(rule.MaxMinutesPerMonth, 0, MaxMonthlyMinutesLimit),
            MinMinutesPerDay = Math.Clamp(rule.MinMinutesPerDay, 0, MaxDailyMinutesLimit),
            RegularRate = Math.Clamp(rule.RegularRate, 1, MaxRate),
            WeekendRate = Math.Clamp(rule.WeekendRate, 1, MaxRate),
            HolidayRate = Math.Clamp(rule.HolidayRate, 1, MaxRate),
            RoundToMinutes = rule.RoundToMinutes is >= 5 and <= 60 ? rule.RoundToMinutes : 0,
            Departments = PunchShiftRule.SortedDepartments(rule.Departments)
                .Take(PunchShiftRule.MaxDepartments).ToList(),
            Employees = (rule.Employees ?? Array.Empty<string>())
                .Select(e => (e ?? string.Empty).Trim())
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .Take(PunchShiftRule.MaxDepartments)
                .ToList(),
            Note = string.IsNullOrWhiteSpace(rule.Note) ? null : rule.Note!.Trim()
        };
    }
}

/// <summary>نتيجة استحقاق موظف للعمل الإضافي في يوم واحد (مع سقوفه السارية وسبب القرار).</summary>
public sealed record OvertimeEligibility(
    bool Eligible,
    int MaxMinutesPerDay,
    int? MaxMinutesTotal,
    bool NeedsApproval,
    string Source);
