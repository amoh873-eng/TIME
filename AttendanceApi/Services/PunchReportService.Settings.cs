using AttendanceApi.Audit;
using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// إعدادات قاعدة المادة 118/ج المرنة (مرآة الواجهة): قراءة القاعدة السارية، حفظ قاعدة جديدة،
/// إعادة الافتراضي، والتحقق من صحة القيم — كلها بلا إعادة تشغيل الخدمة.
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>
    /// القاعدة التي بُنيت عليها آخر نتائج تحليل (بصمة التحليل المحفوظة)، وإلا القاعدة السارية —
    /// تُستخدم في عرض حدّ السماح الصباحي ووصف النتائج المُخزَّنة.
    /// </summary>
    internal PunchWeeklyRule AppliedRule => _weeklyRules.AppliedRule ?? WeeklyRule;

    /// <summary>عرض إعدادات المادة 118/ج الحالية (السارية + الافتراضية + حالة آخر تحليل).</summary>
    public PunchWeeklyRuleView GetWeeklyRuleView()
    {
        var rule = WeeklyRule;

        return new PunchWeeklyRuleView(
            Rule: rule,
            Defaults: _weeklyRules.Defaults,
            IsCustomized: _weeklyRules.IsCustomized,
            PendingReanalysis: _weeklyRules.PendingReanalysis,
            SavedAtUtc: _weeklyRules.LastSavedUtc,
            LastAnalyzedAtUtc: _weeklyRules.LastAnalyzedAtUtc,
            Summary: rule.Summary(),
            Description: rule.Description(),
            SettingsFile: _weeklyRules.FilePath);
    }

    /// <summary>حفظ إعدادات المادة 118/ج الجديدة وإرجاع الحالة بعد الحفظ.</summary>
    public async Task<PunchWeeklyRuleView> SaveWeeklyRuleAsync(
        PunchWeeklyRule rule,
        CancellationToken ct = default)
    {
        await _weeklyRules.SaveAsync(rule, ct);
        return GetWeeklyRuleView();
    }

    /// <summary>إعادة إعدادات المادة 118/ج إلى القيم الافتراضية من appsettings.</summary>
    public async Task<PunchWeeklyRuleView> ResetWeeklyRuleAsync(CancellationToken ct = default)
    {
        await _weeklyRules.ResetAsync(ct);
        return GetWeeklyRuleView();
    }

    /// <summary>التحقق من صحة قيم القاعدة قبل الحفظ (تُرجع رسالة الخطأ أو null).</summary>
    public static string? ValidateWeeklyRule(PunchWeeklyRule rule)
    {
        if (rule.ThresholdMinutes is < 1 or > PunchWeeklyRule.MaxThresholdMinutes)
        {
            return $"حدّ الدقائق الأسبوعي يجب أن يكون بين 1 و{PunchWeeklyRule.MaxThresholdMinutes} دقيقة.";
        }

        if (rule.CapMinutes is < 0 || rule.CapMinutes > PunchWeeklyRule.MaxCapMinutes)
        {
            return $"سقف الناتج المحتسب يجب أن يكون بين 0 و{PunchWeeklyRule.MaxCapMinutes} دقيقة (0 = يتبع الحدّ الأسبوعي).";
        }

        if (rule.DeductionDaysPerWeek is < 0 or > PunchWeeklyRule.MaxDeductionDaysPerWeek)
        {
            return $"أيام الخصم عن كل أسبوع مخالف يجب أن تكون بين 0 و{PunchWeeklyRule.MaxDeductionDaysPerWeek} يوم.";
        }

        if (rule.MorningGraceMinutes is < 0 or > PunchWeeklyRule.MaxGraceMinutes)
        {
            return $"حدّ السماح الصباحي يجب أن يكون بين 0 و{PunchWeeklyRule.MaxGraceMinutes} دقيقة.";
        }

        if (rule.CapCountedMinutes && rule.EffectiveCapMinutes < 1)
        {
            return "سقف الناتج المحتسب أو الحدّ الأسبوعي غير صحيح.";
        }

        if (rule.DepartmentThresholdMinutes is null)
        {
            return null;
        }

        foreach (var pair in rule.DepartmentThresholdMinutes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return "اسم الإدارة في الحدود الخاصة مطلوب.";
            }

            if (pair.Value is < 1 or > PunchWeeklyRule.MaxThresholdMinutes)
            {
                return $"حدّ الإدارة «{pair.Key}» يجب أن يكون بين 1 و{PunchWeeklyRule.MaxThresholdMinutes} دقيقة.";
            }
        }

        if (rule.Shift.CycleHours is < PunchShiftRule.MinCycleHours or > PunchShiftRule.MaxCycleHours)
        {
            return $"طول الوردية يجب أن يكون بين {PunchShiftRule.MinCycleHours:0.##} و{PunchShiftRule.MaxCycleHours:0.##} ساعة.";
        }

        if (rule.Shift.DutyGraceMinutes is < 0 or > PunchShiftRule.MaxDutyGraceMinutes)
        {
            return $"سماح تأخير الوردية يجب أن يكون بين 0 و{PunchShiftRule.MaxDutyGraceMinutes} دقيقة.";
        }

        if ((rule.Shift.Departments?.Count ?? 0) > PunchShiftRule.MaxDepartments)
        {
            return $"عدد إدارات الورديات يجب ألا يزيد على {PunchShiftRule.MaxDepartments} إدارة.";
        }

        foreach (var pair in rule.DepartmentExceptions
                     ?? new Dictionary<string, PunchDepartmentException>(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return "اسم الإدارة في «الاستثناءات الخاصة» مطلوب.";
            }

            if (pair.Value?.ThresholdMinutes is int threshold
                && (threshold < 1 || threshold > PunchWeeklyRule.MaxThresholdMinutes))
            {
                return $"الحدّ الأسبوعي الخاص بالإدارة «{pair.Key}» يجب أن يكون بين 1 و{PunchWeeklyRule.MaxThresholdMinutes} دقيقة.";
            }

            if (pair.Value?.CapMinutes is int cap && (cap < 1 || cap > PunchWeeklyRule.MaxCapMinutes))
            {
                return $"السقف الخاص بالإدارة «{pair.Key}» يجب أن يكون بين 1 و{PunchWeeklyRule.MaxCapMinutes} دقيقة.";
            }
        }

        // ---- الدوام المرن ----
        if (rule.Flexible.WindowMinutes > PunchFlexibleRule.MaxWindowMinutes)
        {
            return $"نافذة الدوام المرن يجب ألا تزيد على {PunchFlexibleRule.MaxWindowMinutes / 60} ساعات بين أبكر وآخر وقت حضور.";
        }

        if (rule.Flexible.LatestArrivalTime <= rule.Flexible.EarliestArrivalTime)
        {
            return "آخر وقت حضور في الدوام المرن يجب أن يكون بعد أبكر وقت حضور.";
        }

        if (rule.Flexible.CompleteDailyMinutes is < 0 or > 720)
        {
            return "ساعات الدوام المطلوب إكمالها في الدوام المرن يجب أن تكون بين 0 و720 دقيقة (0 = مدة الدوام الرسمي).";
        }

        // ---- العمل الإضافي ----
        if (rule.Overtime.MaxMinutesPerDay is < 0 or > PunchOvertimeRule.MaxDailyMinutesLimit)
        {
            return $"الحدّ الأعلى للعمل الإضافي اليومي يجب أن يكون بين 0 و{PunchOvertimeRule.MaxDailyMinutesLimit} دقيقة.";
        }

        if (rule.Overtime.MaxMinutesPerMonth is < 0 or > PunchOvertimeRule.MaxMonthlyMinutesLimit)
        {
            return $"الحدّ الأعلى للعمل الإضافي الشهري يجب أن يكون بين 0 و{PunchOvertimeRule.MaxMonthlyMinutesLimit} دقيقة (0 = بلا سقف).";
        }

        if (rule.Overtime.MinMinutesPerDay is < 0 or > PunchOvertimeRule.MaxDailyMinutesLimit)
        {
            return $"أقل مدة عمل إضافي تُحتسب في اليوم يجب أن تكون بين 0 و{PunchOvertimeRule.MaxDailyMinutesLimit} دقيقة.";
        }

        if (rule.Overtime.MinMinutesPerDay > rule.Overtime.MaxMinutesPerDay && rule.Overtime.MaxMinutesPerDay > 0)
        {
            return "أقل مدة تُحتسب في العمل الإضافي يجب ألا تزيد على الحدّ الأعلى اليومي.";
        }

        if (rule.Overtime.MaxMinutesPerDay > rule.Overtime.EffectiveMaxMinutesPerMonth
            && rule.Overtime.EffectiveMaxMinutesPerMonth > 0)
        {
            return "الحدّ الأعلى اليومي للعمل الإضافي يجب ألا يزيد على الحدّ الأعلى الشهري.";
        }

        if (rule.Overtime.RateFor(OvertimeDayKind.Regular) <= 0
            || rule.Overtime.RateFor(OvertimeDayKind.Weekend) <= 0
            || rule.Overtime.RateFor(OvertimeDayKind.Holiday) <= 0)
        {
            return $"نسب تعويض العمل الإضافي يجب أن تكون أكبر من صفر وألا تزيد على {PunchOvertimeRule.MaxRate:0.##}.";
        }

        return null;
    }

    /// <summary>
    /// معاينة أثر قاعدة مرشّحة (بلا حفظ وبلا تعديل أي بيانات): تُقارن نتائج الأسابيع المخزّنة حالياً
    /// بما ستؤول إليه لو طُبِّقت القاعدة الجديدة على نفس النتائج اليومية.
    /// </summary>
    public async Task<PunchWeeklyRulePreview> PreviewWeeklyRuleAsync(
        PunchWeeklyRule rule,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var daily = await _db.PunchDailyResults.AsNoTracking().ToListAsync(ct);
        var current = await _db.PunchWeeklyResults.AsNoTracking().ToListAsync(ct);

        // النتائج المتوقّعة: تجميع النتائج اليومية بنفس منطق التحليل لكن بالقاعدة المرشّحة،
        // مع استبعاد موظفي الورديات والإعفاءات الإدارية من التجميع الأسبوعي (كما يفعل المحرّك).
        var scheduleDates = (await LoadShiftScheduleAsync(ct))
            .GroupBy(e => e.JobNumber, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.DutyDate).ToHashSet(),
                StringComparer.Ordinal);

        var employeeFlags = new Dictionary<string, WeeklyExemption>(StringComparer.Ordinal);
        foreach (var group in daily.GroupBy(d => d.JobNumber, StringComparer.Ordinal))
        {
            var department = MostFrequentDepartment(group.Select(d => d.DepartmentName));
            var dates = scheduleDates.GetValueOrDefault(group.Key);
            bool shiftEmployee = rule.Shift.Enabled
                && ((dates is { Count: > 0 }) || rule.UsesShiftSystem(department));
            var exception = rule.ExceptionFor(department);

            employeeFlags[group.Key] = new WeeklyExemption(
                ExemptAllWeeks: exception?.ExemptFromWeeklyRule == true,
                ShiftExemptWeeks: dates is { Count: > 0 } && shiftEmployee && rule.Shift.ExemptFromWeeklyRule,
                ScheduleDates: dates,
                NoLateness: (shiftEmployee && rule.Shift.ExemptFromMorningLateness) || exception?.ExemptFromMorningLateness == true,
                NoEarly: (shiftEmployee && rule.Shift.ExemptFromEarlyDeparture) || exception?.ExemptFromEarlyDeparture == true,
                NoGaps: shiftEmployee);
        }

        var projected = rule.Enabled
            ? daily
                .Where(d => d.IsWorkingDay && !employeeFlags.GetValueOrDefault(d.JobNumber).Skip(d))
                .GroupBy(d => (d.JobNumber, d.WorkWeekStart))
                .Select(g => ProjectWeek(
                    g.Key.JobNumber,
                    g.Key.WorkWeekStart,
                    g,
                    rule,
                    employeeFlags.GetValueOrDefault(g.Key.JobNumber)))
                .ToList()
            : new List<ProjectedWeek>();

        var currentImpact = new PunchWeeklyRuleImpact(
            RuleEnabled: true,
            Weeks: current.Count,
            ViolatingWeeks: current.Count(w => w.Exceeds60Minutes),
            Employees: current.Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            EmployeesWithViolations: current.Where(w => w.Exceeds60Minutes)
                .Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            CountedMinutes: current.Sum(w => (long)w.CountedMinutes),
            TotalMinutes: current.Sum(w => (long)w.TotalMinutes),
            DeductionDays: Math.Round(current.Sum(w => w.DeductionDays), 2));

        var projectedImpact = new PunchWeeklyRuleImpact(
            RuleEnabled: rule.Enabled,
            Weeks: projected.Count,
            ViolatingWeeks: projected.Count(w => w.Violation),
            Employees: projected.Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            EmployeesWithViolations: projected.Where(w => w.Violation)
                .Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            CountedMinutes: projected.Sum(w => (long)w.CountedMinutes),
            TotalMinutes: projected.Sum(w => (long)w.TotalMinutes),
            DeductionDays: Math.Round(projected.Sum(w => w.DeductionDays), 2));

        var samples = BuildImpactSamples(current, projected);

        return new PunchWeeklyRulePreview(
            ProposedRule: rule,
            ProposedSummary: rule.Summary(),
            Current: currentImpact,
            Projected: projectedImpact,
            ChangedWeeks: samples.Count,
            Samples: samples.Take(50).ToList());
    }

    /// <summary>أسبوع مجمَّع وفق قاعدة مرشّحة (حساب معاينة فقط).</summary>
    private sealed record ProjectedWeek(
        string JobNumber,
        string? EmployeeName,
        string? DepartmentName,
        DateOnly WeekStart,
        int TotalMinutes,
        int CountedMinutes,
        double DeductionDays,
        bool Violation);

    /// <summary>إعفاءات الإدارات/الفئات في معاينة القاعدة الأسبوعية.</summary>
    private readonly record struct WeeklyExemption(
        bool ExemptAllWeeks,
        bool ShiftExemptWeeks,
        HashSet<DateOnly>? ScheduleDates,
        bool NoLateness,
        bool NoEarly,
        bool NoGaps)
    {
        /// <summary>هل يُستبعد هذا اليوم من التجميع الأسبوعي وفق الإعفاءات؟</summary>
        public bool Skip(PunchDailyResult day) =>
            ExemptAllWeeks
            || (ShiftExemptWeeks && ScheduleDates is { Count: > 0 } && CoversWeek(day.WorkWeekStart));

        private bool CoversWeek(DateOnly weekStart)
        {
            var weekEnd = weekStart.AddDays(4);
            foreach (var date in ScheduleDates!)
            {
                if (date >= weekStart && date <= weekEnd)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>تجميع أسبوع واحد وفق القاعدة المرشّحة (نفس منطق التحليل بلا تخزين).</summary>
    private static ProjectedWeek ProjectWeek(
        string jobNumber,
        DateOnly weekStart,
        IEnumerable<PunchDailyResult> days,
        PunchWeeklyRule rule,
        WeeklyExemption exemption)
    {
        var list = days.ToList();
        var department = MostFrequentDepartment(list.Select(d => d.DepartmentName));

        int threshold = rule.ThresholdFor(department);
        int cap = rule.CapFor(department);
        int lateness = exemption.NoLateness ? 0 : list.Sum(d => d.LatenessMinutes);
        int early = rule.MergeEarlyDeparture && !exemption.NoEarly ? list.Sum(d => d.EarlyDepartureMinutes) : 0;
        int gaps = rule.MergeMidDayGaps && !exemption.NoGaps ? list.Sum(d => d.GapMinutes) : 0;
        int total = lateness + early + gaps;
        bool violation = rule.IsViolation(total, threshold);

        return new ProjectedWeek(
            JobNumber: jobNumber,
            EmployeeName: MostFrequent(list.Select(d => d.EmployeeName)),
            DepartmentName: department,
            WeekStart: weekStart,
            TotalMinutes: total,
            CountedMinutes: rule.CountedMinutes(total, cap),
            DeductionDays: LegalRules.WeeklyDeductionDays(violation, rule.DeductionDaysPerWeek),
            Violation: violation);
    }

    /// <summary>الأسابيع التي تغيّر حكمها (خصم أو ناتج محتسب) بين المخزَّن والمتوقّع.</summary>
    private static List<PunchWeeklyRuleImpactDelta> BuildImpactSamples(
        IReadOnlyList<PunchWeeklyResult> current,
        IReadOnlyList<ProjectedWeek> projected)
    {
        var currentMap = current.ToDictionary(
            w => (w.JobNumber, w.WeekStart),
            w => w);

        var projectedMap = projected.ToDictionary(
            w => (w.JobNumber, w.WeekStart),
            w => w);

        var deltas = new List<PunchWeeklyRuleImpactDelta>();

        foreach (var key in currentMap.Keys.Union(projectedMap.Keys).Distinct())
        {
            currentMap.TryGetValue(key, out var before);
            projectedMap.TryGetValue(key, out var after);

            int beforeCounted = before?.CountedMinutes ?? 0;
            double beforeDays = before?.DeductionDays ?? 0;
            int afterCounted = after?.CountedMinutes ?? 0;
            double afterDays = after?.DeductionDays ?? 0;

            if (beforeCounted == afterCounted && beforeDays.Equals(afterDays))
            {
                continue;
            }

            deltas.Add(new PunchWeeklyRuleImpactDelta(
                JobNumber: key.Item1,
                EmployeeName: after?.EmployeeName ?? before?.EmployeeName,
                DepartmentName: after?.DepartmentName ?? before?.DepartmentName,
                WeekStart: key.Item2.ToString("yyyy/MM/dd"),
                TotalMinutes: after?.TotalMinutes ?? before?.TotalMinutes ?? 0,
                CurrentCountedMinutes: beforeCounted,
                CurrentDeductionDays: beforeDays,
                ProjectedCountedMinutes: afterCounted,
                ProjectedDeductionDays: afterDays));
        }

        return deltas
            .OrderByDescending(d => Math.Abs(d.ProjectedDeductionDays - d.CurrentDeductionDays))
            .ThenByDescending(d => Math.Abs(d.ProjectedCountedMinutes - d.CurrentCountedMinutes))
            .ThenBy(d => d.JobNumber, StringComparer.Ordinal)
            .ToList();
    }
}
