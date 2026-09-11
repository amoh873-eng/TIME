using AttendanceApi.Audit;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>تعريف تقرير في «قائمة تقارير الحضور والانصراف».</summary>
public sealed record PunchReportDefinition(string Key, string Title, string Description, string Rule);

/// <summary>عنصر القائمة الجاهز للعرض: التعريف + عدد الصفوف المتوفرة.</summary>
public sealed record PunchReportItem(
    string Key,
    string Title,
    string Description,
    string Rule,
    int Rows,
    bool HasData,
    string FileName);

/// <summary>مرشّحات تقارير الحضور (الفترة + الرقم الوظيفي).</summary>
public sealed record PunchReportFilter(DateOnly? From = null, DateOnly? To = null, string? JobNumber = null)
{
    public static readonly PunchReportFilter Empty = new();
}

/// <summary>بيانات تقرير واحد (أعمدة + صفوف) للمعاينة في الواجهة وللتصدير إلى Excel.</summary>
public sealed record PunchReportData(
    string Key,
    string Title,
    string Subtitle,
    string Rule,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    int Total);

/// <summary>معاينة مختصرة لتقرير في قائمة التقارير.</summary>
public sealed record PunchReportPreview(
    string Key,
    string Title,
    string Subtitle,
    string Rule,
    IReadOnlyList<string> Columns,
    int Total,
    IReadOnlyList<object?[]> Rows);

/// <summary>
/// «قائمة تقارير الحضور والانصراف»: تقرير Excel مستقل لكل قاعدة/مؤشر
/// (كشف البصمات، التأخير، الانصراف المبكر، المغادرة أثناء الدوام، المادتان 118/ب و118/ج،
/// الغياب، نقص البصمة، الدوام في العطل، تقويم العطل، الملخص الشهري، الإجازات، التزام الإدارات).
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>كتالوج التقارير المتاحة (الترتيب المعروض في الواجهة).</summary>
    private static readonly PunchReportDefinition[] ReportCatalog =
    {
        new("daily-attendance", "كشف الحضور والانصراف اليومي",
            "كل أيام الموظفين: حالة اليوم، وقت الحضور والانصراف، دقائق العمل، التأخير، الانصراف المبكر، المغادرة، وعطلة اليوم إن وُجدت.",
            "سجل البصمات الخام (مصدر التحليل)"),
        new("lateness", "التأخير الصباحي - المادة 7",
            "أيام بلوغ التأخير الصباحي حدّ السماح المعتمد، مرتبة بعدد الدقائق.",
            "المادة 7 من نظام الخدمة المدنية + تعليمات 2020"),
        new("early-departure", "الانصراف المبكر",
            "أيام الانصراف قبل نهاية الدوام الرسمي (15:30) مع عدد الدقائق.",
            "المادة 7 و118/ج"),
        new("midday-gap", "المغادرة أثناء الدوام",
            "الفجوات بين جلسات البصمة (خروج ثم عودة) التي تزيد على حدّ الضجيج المعتمد.",
            "المادة 118/ب و118/ج"),
        new("over-4-hours", "الغياب عن الدوام فوق 4 ساعات - المادة 118/ب",
            "أيام بلوغ مجموع الغياب عن نافذة الدوام أكثر من 240 دقيقة (خصم يوم من الرصيد).",
            "المادة 118/ب"),
        new("weekly-over-60", "الأسابيع التي بلغت الحدّ الأسبوعي - المادة 118/ج",
            "دمج التأخير الصباحي مع الانصراف المبكر (ومعه المغادرة أثناء الدوام): الأسابيع المخالفة للحدّ الأسبوعي المعتمد مع الناتج المحتسب وأيام الخصم (القيم من «إعدادات المادة 118/ج»).",
            "المادة 118/ج"),
        new("absence", "الغياب غير المبرّر",
            "أيام الغياب غير المبرّر (بلا أي بصمة) بعد استثناء العطل الرسمية والدينية ونهاية الأسبوع.",
            "المادة 7 + قاعدة المكافأة (15 يوماً)"),
        new("incomplete-punch", "نقص بصمة الانصراف (أيام للتسوية)",
            "أيام عمل فيها بصمة حضور بلا بصمة انصراف، وتحتاج تسوية أو إبراز عذر.",
            "قواعد ضبط البصمات"),
        new("holiday-work", "الدوام في العطل ونهاية الأسبوع",
            "أيام دوام فعلية وقعت في عطلة رسمية/دينية أو نهاية أسبوع (تُعرض للعلم ولا تُحتسب مخالفة).",
            "قواعد الساعات الإضافية وأيام الراحة"),
        new("holiday-calendar", "تقويم العطل الرسمية والدينية",
            "العطل المعتمدة (رسمية + إسلامية + مسيحية) مع أثر الدوام الفعلي في كل عطلة.",
            "تقويم العطل المعتمد - يُحدَّث سنوياً"),
        new("monthly-summary", "الملخص الشهري والخصومات",
            "ملخص شهري لكل موظف: أيام التأخير، العقوبة (المادة 7)، أيام 118/ب و118/ج، الغياب، وخصم المكافأة.",
            "المواد 7 و112 و118 + قاعدة الـ 15 يوماً"),
        new("leaves-permissions", "الإجازات والاستئذانات",
            "أيام الإجازات (سنوية/مرضية/تعويضية/وفاة) والاستئذانات (طارئ/طبي) والمهام الرسمية لكل موظف شهرياً.",
            "المواد 112 و118 ومادة الإجازات"),
        new("department-compliance", "التزام الإدارات والمديريات",
            "ترتيب الإدارات بنسب الالتزام وعدد المخالفين وأيام الخصم.",
            "لوحة التزام قانونية"),
        new("overtime-flexible", "العمل الإضافي والدوام المرن",
            "أيام العمل الإضافي المحتسب (النوع، الدقائق، نسبة التعويض، الساعات المعادلة) وأيام الدوام المرن ونافذته، مع الدقائق المستبعدة وفق الحدود المعتمدة — ولا يُحتسب أي عمل إضافي بلا موافقة مسبقة (تصريح ساري أو قائمة معتمدة).",
            "قانون الخدمة المدنية — حدود العمل الإضافي وضوابط الدوام المرن"),
        new("full-legal", "التقارير القانونية الكاملة (17 ورقة)",
            "ملف شامل: ملخص تنفيذي + ورقة لكل قاعدة + تقويم العطل + الدوام في العطل + العمل الإضافي والدوام المرن + كشف خصومات مُجمَّع + منهجية وجودة بيانات.",
            "المواد 7 و112 و118 + قاعدة الـ 15 يوماً")
    };

    /// <summary>
    /// كتالوج التقارير مع القيم الفعلية لقاعدة المادة 118/ج السارية
    /// (الحدّ الأسبوعي وسقف الناتج المحتسب وأيام الخصم) — تُحدَّث فوراً عند تغيير الإعدادات.
    /// </summary>
    private IReadOnlyList<PunchReportDefinition> Catalog()
    {
        var rule = WeeklyRule;

        return ReportCatalog
            .Select(d => d.Key == "weekly-over-60"
                ? d with
                {
                    Title = $"الأسابيع التي بلغت {rule.ThresholdMinutes} دقيقة - المادة 118/ج",
                    Description = "دمج التأخير الصباحي مع الانصراف المبكر (ومعه المغادرة أثناء الدوام): الأسابيع التي "
                        + $"{(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلغ")} مجموعها الفعلي {rule.ThresholdMinutes} دقيقة، مع الناتج المحتسب "
                        + (rule.CapCountedMinutes ? $"المقيَّد بـ {rule.EffectiveCapMinutes} دقيقة أسبوعياً" : "الفعلي بلا تقييد")
                        + $" (خصم {rule.DeductionDaysText()} يوم عن كل أسبوع).",
                    Rule = $"المادة 118/ج — {rule.Summary()}"
                }
                : d)
            .ToList();
    }

    /// <summary>قائمة تقارير الحضور والانصراف مع عدد الصفوف المتاحة في كل تقرير.</summary>
    public async Task<IReadOnlyList<PunchReportItem>> GetReportsCatalogAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var daily = _db.PunchDailyResults.AsNoTracking();
        var weekly = _db.PunchWeeklyResults.AsNoTracking();
        var monthly = _db.PunchMonthlyResults.AsNoTracking();

        bool hasAnalysis = await daily.AnyAsync(ct);
        var holidays = hasAnalysis ? await LoadCalendarAsync(ct) : PunchCalendar.Default();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["daily-attendance"] = await daily.CountAsync(ct),
            ["lateness"] = await daily.CountAsync(d => d.IsMorningLate, ct),
            ["early-departure"] = await daily.CountAsync(d => d.EarlyDepartureMinutes > 0, ct),
            ["midday-gap"] = await daily.CountAsync(d => d.GapMinutes > 0, ct),
            ["over-4-hours"] = await daily.CountAsync(d => d.CountsFor118b, ct),
            ["weekly-over-60"] = await weekly.CountAsync(w => w.Exceeds60Minutes, ct),
            ["absence"] = await daily.CountAsync(d => d.IsAbsent, ct),
            ["incomplete-punch"] = await daily.CountAsync(d => d.IsIncomplete, ct),
            ["holiday-work"] = await daily.CountAsync(d => d.IsWorkOnHoliday, ct),
            ["holiday-calendar"] = holidays.HolidayCount,
            ["monthly-summary"] = await monthly.CountAsync(ct),
            ["leaves-permissions"] = await monthly.CountAsync(m =>
                m.AnnualLeaveDays > 0 || m.SickLeaveDays > 0 || m.CompensatoryLeaveDays > 0
                || m.BereavementLeaveDays > 0 || m.EmergencyPermissionDays > 0
                || m.MedicalPermissionDays > 0 || m.OfficialDutyDays > 0, ct),
            ["department-compliance"] = await monthly.Select(m => m.DepartmentName).Distinct().CountAsync(ct),
            ["overtime-flexible"] = await daily.CountAsync(
                d => d.OvertimeMinutes > 0 || d.RawOvertimeMinutes > 0 || d.IsFlexibleWork, ct),
            ["full-legal"] = hasAnalysis ? 17 : 0
        };

        return Catalog()
            .Select(d => new PunchReportItem(
                d.Key,
                d.Title,
                d.Description,
                d.Rule,
                Rows: counts.GetValueOrDefault(d.Key),
                HasData: counts.GetValueOrDefault(d.Key) > 0,
                FileName: SafeFileName($"تقرير-{d.Title}-{DateTime.Now:yyyyMMdd}.xlsx")))
            .ToList();
    }

    /// <summary>مفاتيح التقارير المعتمدة (للتحقق من الطلبات القادمة من الواجهة).</summary>
    public static bool IsKnownReportKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && ReportCatalog.Any(d => d.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>وصف المرشّحات المطبقة على التقرير.</summary>
    private static string FilterLabel(PunchReportFilter filter)
    {
        var parts = new List<string>(3);

        if (filter.From.HasValue)
        {
            parts.Add($"من {filter.From:yyyy/MM/dd}");
        }

        if (filter.To.HasValue)
        {
            parts.Add($"إلى {filter.To:yyyy/MM/dd}");
        }

        if (!string.IsNullOrWhiteSpace(filter.JobNumber))
        {
            parts.Add($"الرقم الوظيفي {filter.JobNumber}");
        }

        return parts.Count == 0 ? "كل الفترة المتوفرة" : string.Join(" | ", parts);
    }

    /// <summary>
    /// بناء بيانات تقرير واحد (أعمدة + صفوف) مع تطبيق المرشّحات وسقف الصفوف
    /// (maxRows = 0 يعني بلا سقف).
    /// </summary>
    public async Task<PunchReportData> GetReportDataAsync(
        string key,
        PunchReportFilter? filter = null,
        int maxRows = 0,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);
        filter ??= PunchReportFilter.Empty;

        var definition = Catalog().FirstOrDefault(d =>
            d.Key.Equals(key?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            throw new ArgumentException($"تقرير غير معروف: {key}", nameof(key));
        }

        string subtitle = $"{FilterLabel(filter)} | الأساس القانوني: {definition.Rule}";

        switch (definition.Key)
        {
            case "daily-attendance":
                return await BuildDailyReportAsync(definition, subtitle,
                    _ => true, filter, maxRows, ct);

            case "lateness":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.IsMorningLate, filter, maxRows, ct);

            case "early-departure":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.EarlyDepartureMinutes > 0, filter, maxRows, ct);

            case "midday-gap":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.GapMinutes > 0, filter, maxRows, ct);

            case "over-4-hours":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.CountsFor118b, filter, maxRows, ct);

            case "absence":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.IsAbsent, filter, maxRows, ct);

            case "incomplete-punch":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.IsIncomplete, filter, maxRows, ct);

            case "holiday-work":
                return await BuildDailyReportAsync(definition, subtitle,
                    d => d.IsWorkOnHoliday, filter, maxRows, ct);

            case "overtime-flexible":
                return await BuildWorkTimeReportAsync(definition, subtitle, filter, maxRows, ct);

            case "weekly-over-60":
                return await BuildWeeklyReportAsync(definition, subtitle, filter, maxRows, ct);

            case "monthly-summary":
                return await BuildMonthlyReportAsync(definition, subtitle,
                    _ => true, filter, maxRows, ct);

            case "leaves-permissions":
                return await BuildMonthlyReportAsync(definition, subtitle,
                    m => m.AnnualLeaveDays > 0 || m.SickLeaveDays > 0 || m.CompensatoryLeaveDays > 0
                         || m.BereavementLeaveDays > 0 || m.EmergencyPermissionDays > 0
                         || m.MedicalPermissionDays > 0 || m.OfficialDutyDays > 0,
                    filter, maxRows, ct);

            case "department-compliance":
                return await BuildComplianceReportAsync(definition, subtitle, maxRows, ct);

            case "holiday-calendar":
                return await BuildHolidayCalendarDataAsync(definition, subtitle, filter, maxRows, ct);

            default:
                throw new ArgumentException(
                    $"التقرير «{definition.Title}» يُصدَّر كملف مستقل (full-legal).", nameof(key));
        }
    }

    // ---- أعمدة وصفوف تقرير كشف الحضور والانصراف اليومي ----

    private static readonly string[] DailyReportColumns =
    {
        "الرقم الوظيفي", "اسم الموظف", "الإدارة", "التاريخ", "اليوم", "حالة اليوم",
        "وقت الحضور", "وقت الانصراف", "دقائق العمل", "دقائق التأخير", "دقائق الانصراف المبكر",
        "دقائق المغادرة", "الغياب عن الدوام (دقيقة)", "أيام 118/ب",
        "تصنيف اليوم", "اسم العطلة", "دوام في عطلة؟", "ملاحظات"
    };

    private static object?[] DailyReportRow(PunchDailyResult d) => new object?[]
    {
        d.JobNumber,
        d.EmployeeName,
        d.DepartmentName,
        d.WorkDate.ToString("yyyy/MM/dd"),
        PunchCalendar.DayName(d.WorkDate.DayOfWeek),
        StatusLabel(d.Status),
        TimeLabel(d.ClockIn),
        TimeLabel(d.ClockOut),
        d.WorkedMinutes,
        d.LatenessMinutes,
        d.EarlyDepartureMinutes,
        d.GapMinutes,
        d.AbsenceMinutes,
        d.Article118bDays,
        d.IsCalendarHoliday ? "عطلة رسمية/دينية" : d.IsWeekendDay ? "عطلة نهاية أسبوع" : "يوم عمل",
        d.HolidayName ?? (d.IsWeekendDay ? "نهاية الأسبوع" : "—"),
        d.IsWorkOnHoliday ? "نعم" : "لا",
        d.Notes
    };

    /// <summary>بناء تقرير من النتائج اليومية (مع مرشّح اختياري على نوع المخالفة).</summary>
    private async Task<PunchReportData> BuildDailyReportAsync(
        PunchReportDefinition definition,
        string subtitle,
        Func<PunchDailyResult, bool> predicate,
        PunchReportFilter filter,
        int maxRows,
        CancellationToken ct)
    {
        var query = _db.PunchDailyResults.AsNoTracking();

        if (filter.From.HasValue)
        {
            query = query.Where(d => d.WorkDate >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(d => d.WorkDate <= filter.To.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.JobNumber))
        {
            query = query.Where(d => d.JobNumber == filter.JobNumber);
        }

        var all = await query
            .OrderByDescending(d => d.WorkDate)
            .ThenBy(d => d.JobNumber)
            .ToListAsync(ct);

        var selected = all.Where(predicate).ToList();
        int total = selected.Count;

        var rows = (maxRows > 0 && total > maxRows ? selected.Take(maxRows) : selected)
            .Select(DailyReportRow)
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عدد الصفوف: {total}",
            definition.Rule,
            DailyReportColumns,
            rows,
            total);
    }

    // ---- أعمدة وصفوف تقرير العمل الإضافي والدوام المرن ----

    private static readonly string[] WorkTimeReportColumns =
    {
        "الرقم الوظيفي", "اسم الموظف", "الإدارة", "التاريخ", "اليوم", "حالة اليوم",
        "وقت الحضور", "وقت الانصراف", "دوام مرن؟", "نافذة الدوام الفعّالة",
        "نوع العمل الإضافي", "نسبة التعويض", "دقائق إضافي محتسبة", "الساعات المعادلة",
        "دقائق إضافي مستبعدة", "مصرَّح بالعمل الإضافي؟", "حالة الموافقة المسبقة", "ملاحظات"
    };

    /// <summary>عنوان نوع يوم العمل الإضافي (عادي/نهاية أسبوع/عطلة).</summary>
    private static string OvertimeKindLabel(OvertimeDayKind kind) => kind switch
    {
        OvertimeDayKind.Holiday => "عطلة رسمية/دينية",
        OvertimeDayKind.Weekend => "نهاية أسبوع/يوم راحة",
        OvertimeDayKind.Regular => "يوم عمل عادي",
        _ => "—"
    };

    private static object?[] WorkTimeReportRow(PunchDailyResult d) => new object?[]
    {
        d.JobNumber,
        d.EmployeeName,
        d.DepartmentName,
        d.WorkDate.ToString("yyyy/MM/dd"),
        PunchCalendar.DayName(d.WorkDate.DayOfWeek),
        StatusLabel(d.Status),
        TimeLabel(d.ClockIn),
        TimeLabel(d.ClockOut),
        d.IsFlexibleWork ? "نعم" : "لا",
        d.IsFlexibleWork
            ? $"{PunchTimeText.Format(d.FlexibleStart)} — {PunchTimeText.Format(d.FlexibleEnd)}"
            : $"{PunchTimeText.Format(LegalRules.WorkdayStart)} — {PunchTimeText.Format(LegalRules.WorkdayEnd)}",
        OvertimeKindLabel(d.OvertimeKind),
        d.OvertimeRate > 0 ? PunchOvertimeRule.RateText(d.OvertimeRate) : "—",
        d.OvertimeMinutes,
        Math.Round(d.EquivalentOvertimeMinutes / 60.0, 2),
        d.OvertimeExcludedMinutes,
        d.IsOvertimeEligible ? "نعم" : "لا",
        OvertimeApprovalLabel(d),
        d.Notes
    };

    /// <summary>
    /// حالة الموافقة المسبقة على العمل الإضافي لذلك اليوم: معلَّق بانتظار تصريح، أو مصرَّح
    /// (تصريح ساري/قائمة معتمدة)، أو غير مصرَّح وفق الإعدادات (نهاية أسبوع/عطلة أو تعطيل القاعدة).
    /// </summary>
    private static string OvertimeApprovalLabel(PunchDailyResult d) => d.OvertimeNeedsApproval
        ? "معلَّق بانتظار تصريح مسبق"
        : d.IsOvertimeEligible
            ? "مصرَّح (تصريح/قائمة معتمدة)"
            : "غير مصرَّح وفق الإعدادات";

    /// <summary>بناء تقرير أيام العمل الإضافي والدوام المرن (اليوميات ذات الصلة فقط).</summary>
    private async Task<PunchReportData> BuildWorkTimeReportAsync(
        PunchReportDefinition definition,
        string subtitle,
        PunchReportFilter filter,
        int maxRows,
        CancellationToken ct)
    {
        var query = _db.PunchDailyResults.AsNoTracking()
            .Where(d => d.OvertimeMinutes > 0 || d.RawOvertimeMinutes > 0 || d.IsFlexibleWork);

        if (filter.From.HasValue)
        {
            query = query.Where(d => d.WorkDate >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(d => d.WorkDate <= filter.To.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.JobNumber))
        {
            query = query.Where(d => d.JobNumber == filter.JobNumber);
        }

        var all = await query
            .OrderByDescending(d => d.WorkDate)
            .ThenBy(d => d.JobNumber)
            .ToListAsync(ct);

        int total = all.Count;
        int overtimeMinutes = all.Sum(d => d.OvertimeMinutes);
        int flexibleDays = all.Count(d => d.IsFlexibleWork);
        int pendingApprovalDays = all.Count(d => d.OvertimeNeedsApproval);
        int pendingApprovalMinutes = all.Where(d => d.OvertimeNeedsApproval).Sum(d => d.RawOvertimeMinutes);

        var rows = (maxRows > 0 && total > maxRows ? all.Take(maxRows) : all)
            .Select(WorkTimeReportRow)
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عدد الأيام: {total} | إجمالي العمل الإضافي: {PunchFlexibleRule.HoursText(overtimeMinutes)}"
            + $" | أيام الدوام المرن: {flexibleDays}"
            + (pendingApprovalDays > 0
                ? $" | معلَّق بانتظار تصريح مسبق: {pendingApprovalDays} يوماً"
                    + $" ({PunchFlexibleRule.HoursText(pendingApprovalMinutes)})"
                : string.Empty),
            definition.Rule,
            WorkTimeReportColumns,
            rows,
            total);
    }

    // ---- أعمدة وصفوف التقارير الأسبوعية والشهرية ----

    private static readonly string[] WeeklyReportColumns =
    {
        "الرقم الوظيفي", "اسم الموظف", "الإدارة", "بداية الأسبوع", "نهاية الأسبوع",
        "أيام محتسبة", "دقائق التأخير الصباحي", "دقائق الانصراف المبكر", "دقائق المغادرة",
        "المجموع الفعلي (دقيقة)", "الناتج المحتسب", "بلغت الحدّ؟", "أيام الخصم (118/ج)", "ملاحظات"
    };

    private static readonly string[] MonthlyReportColumns =
    {
        "الرقم الوظيفي", "اسم الموظف", "الإدارة", "السنة", "الشهر",
        "أيام العمل", "أيام مكتملة", "أيام التأخير", "الإجراء التأديبي (المادة 7)",
        "حسم الراتب (أيام)", "أيام الغياب", "أيام بلا انصراف",
        "إجازة سنوية", "إجازة مرضية", "إجازة تعويضية", "إجازة وفاة",
        "استئذان طارئ", "استئذان طبي", "مهام رسمية",
        "أيام 118/ب", "أيام 118/ج", "الناتج الأسبوعي المحتسب", "أسابيع فوق الحدّ",
        "إجمالي أيام المكافأة المحتسبة", "نسبة خصم المكافأة %", "تجاوز 15 يوماً؟",
        "إجمالي أيام الحسم", "أيام العطل الرسمية والدينية", "أيام نهاية الأسبوع",
        "دوام في نهاية الأسبوع", "دوام في العطل",
        "أيام الدوام المرن", "دقائق المرونة",
        "أيام العمل الإضافي", "ساعات العمل الإضافي", "الساعات المعادلة", "دقائق إضافي مستبعدة",
        "ملاحظات"
    };

    /// <summary>أعمدة تقرير الأسابيع المخالفة وفق القاعدة السارية (الحدّ والسقف وأيام الخصم).</summary>
    private string[] WeeklyColumns()
    {
        var rule = WeeklyRule;

        return new[]
        {
            "الرقم الوظيفي", "اسم الموظف", "الإدارة", "بداية الأسبوع", "نهاية الأسبوع",
            "أيام محتسبة", "دقائق التأخير الصباحي", "دقائق الانصراف المبكر", "دقائق المغادرة",
            "المجموع الفعلي (دقيقة)",
            rule.CapCountedMinutes ? $"الناتج المحتسب (≤ {rule.EffectiveCapMinutes} دقيقة)" : "الناتج المحتسب (المجموع الفعلي)",
            $"بلغت {rule.ThresholdMinutes} دقيقة؟",
            $"أيام الخصم (118/ج) — {rule.DeductionDaysText()}/أسبوع", "ملاحظات"
        };
    }

    /// <summary>أعمدة الملخص الشهري وفق القاعدة السارية.</summary>
    private string[] MonthlyColumns()
    {
        var rule = WeeklyRule;

        return MonthlyReportColumns
            .Select(c => c.StartsWith("الناتج الأسبوعي المحتسب", StringComparison.Ordinal)
                ? (rule.CapCountedMinutes
                    ? $"الناتج الأسبوعي المحتسب (≤ {rule.EffectiveCapMinutes} دقيقة/أسبوع)"
                    : "الناتج الأسبوعي المحتسب (المجموع الفعلي/أسبوع)")
                : c.StartsWith("أسابيع فوق", StringComparison.Ordinal)
                    ? $"أسابيع فوق {rule.ThresholdMinutes} دقيقة"
                    : c)
            .ToArray();
    }

    /// <summary>بناء تقرير الأسابيع المخالفة للمادة 118/ج وفق القاعدة السارية.</summary>
    private async Task<PunchReportData> BuildWeeklyReportAsync(
        PunchReportDefinition definition,
        string subtitle,
        PunchReportFilter filter,
        int maxRows,
        CancellationToken ct)
    {
        var query = _db.PunchWeeklyResults.AsNoTracking().Where(w => w.Exceeds60Minutes);

        if (filter.From.HasValue)
        {
            query = query.Where(w => w.WeekEnd >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(w => w.WeekStart <= filter.To.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.JobNumber))
        {
            query = query.Where(w => w.JobNumber == filter.JobNumber);
        }

        var all = await query
            .OrderByDescending(w => w.TotalMinutes)
            .ThenBy(w => w.JobNumber)
            .ToListAsync(ct);

        int total = all.Count;
        var rows = (maxRows > 0 && total > maxRows ? all.Take(maxRows) : all)
            .Select(w => new object?[]
            {
                w.JobNumber,
                w.EmployeeName,
                w.DepartmentName,
                w.WeekStart.ToString("yyyy/MM/dd"),
                w.WeekEnd.ToString("yyyy/MM/dd"),
                w.DaysCounted,
                w.LatenessMinutes,
                w.EarlyDepartureMinutes,
                w.GapMinutes,
                w.TotalMinutes,
                w.CountedMinutes,
                w.Exceeds60Minutes ? "نعم" : "لا",
                w.DeductionDays,
                w.Notes
            })
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عدد الأسابيع: {total}",
            definition.Rule,
            WeeklyColumns(),
            rows,
            total);
    }

    /// <summary>بناء التقرير الشهري (الملخص والخصومات أو الإجازات والاستئذانات).</summary>
    private async Task<PunchReportData> BuildMonthlyReportAsync(
        PunchReportDefinition definition,
        string subtitle,
        Func<PunchMonthlyResult, bool> predicate,
        PunchReportFilter filter,
        int maxRows,
        CancellationToken ct)
    {
        var query = _db.PunchMonthlyResults.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.JobNumber))
        {
            query = query.Where(m => m.JobNumber == filter.JobNumber);
        }

        if (filter.From.HasValue)
        {
            var fromKey = filter.From.Value.Year * 100 + filter.From.Value.Month;
            query = query.Where(m => m.Year * 100 + m.Month >= fromKey);
        }

        if (filter.To.HasValue)
        {
            var toKey = filter.To.Value.Year * 100 + filter.To.Value.Month;
            query = query.Where(m => m.Year * 100 + m.Month <= toKey);
        }

        var all = await query
            .OrderByDescending(m => m.Year)
            .ThenByDescending(m => m.Month)
            .ThenBy(m => m.JobNumber)
            .ToListAsync(ct);

        var selected = all.Where(predicate).ToList();
        int total = selected.Count;

        var rows = (maxRows > 0 && total > maxRows ? selected.Take(maxRows) : selected)
            .Select(m => new object?[]
            {
                m.JobNumber,
                m.EmployeeName,
                m.DepartmentName,
                m.Year,
                MonthName(m.Month),
                m.WorkingDays,
                m.CompleteDays,
                m.LateIncidents,
                m.PenaltyText ?? LegalRules.DisciplinaryActionText(m.Penalty),
                m.Article7SalaryDeductionDays,
                m.AbsentDays,
                m.IncompleteDays,
                m.AnnualLeaveDays,
                m.SickLeaveDays,
                m.CompensatoryLeaveDays,
                m.BereavementLeaveDays,
                m.EmergencyPermissionDays,
                m.MedicalPermissionDays,
                m.OfficialDutyDays,
                m.Article118bDays,
                m.Article118cDays,
                m.WeeklyLateMinutes,
                m.WeeksOver60Minutes,
                m.TotalAbsenceDays,
                m.BonusDeductionPercent,
                m.Exceeds15Days ? "نعم" : "لا",
                m.TotalSalaryDeductionDays,
                m.HolidayDays,
                m.WeekendDays,
                m.WeekendWorkDays,
                m.HolidayWorkDays,
                m.FlexibleDays,
                m.FlexibleMinutes,
                m.OvertimeDays,
                m.OvertimeHours,
                m.EquivalentOvertimeHours,
                m.OvertimeExcludedMinutes,
                m.Notes
            })
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عدد السجلات: {total}",
            definition.Rule,
            MonthlyColumns(),
            rows,
            total);
    }

    // ---- أعمدة وصفوف تقرير التزام الإدارات وتقويم العطل ----

    private static readonly string[] ComplianceReportColumns =
    {
        "الترتيب", "الإدارة / المديرية", "عدد الموظفين", "الملتزمون", "المخالفون",
        "نسبة الالتزام %", "موظفون لديهم تأخير", "أيام 118/ب", "أيام 118/ج",
        "أيام الغياب", "إجمالي أيام الحسم"
    };

    private static readonly string[] HolidayReportColumns =
    {
        "التاريخ", "اليوم", "اسم العطلة / المناسبة", "نوع العطلة", "المصدر",
        "موظفون دوّموا", "دقائق العمل في العطلة", "ملاحظة"
    };

    /// <summary>بناء تقرير التزام الإدارات والمديريات.</summary>
    private async Task<PunchReportData> BuildComplianceReportAsync(
        PunchReportDefinition definition,
        string subtitle,
        int maxRows,
        CancellationToken ct)
    {
        var all = await GetComplianceByDepartmentAsync(1, ct);
        int total = all.Count;

        var rows = (maxRows > 0 && total > maxRows ? all.Take(maxRows) : all)
            .Select(c => new object?[]
            {
                c.Rank,
                c.Administration,
                c.Employees,
                c.CompliantEmployees,
                c.ViolatingEmployees,
                c.CompliancePercent,
                c.EmployeesWithLateness,
                c.Article118bDays,
                c.Article118cDays,
                c.AbsentDays,
                c.TotalSalaryDeductionDays
            })
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عدد الإدارات: {total}",
            definition.Rule,
            ComplianceReportColumns,
            rows,
            total);
    }

    /// <summary>بناء تقرير تقويم العطل الرسمية والدينية مع أثر الدوام الفعلي فيها.</summary>
    private async Task<PunchReportData> BuildHolidayCalendarDataAsync(
        PunchReportDefinition definition,
        string subtitle,
        PunchReportFilter filter,
        int maxRows,
        CancellationToken ct)
    {
        var calendar = await LoadCalendarAsync(ct);
        var stats = await ReadHolidayPunchStatsAsync(ct);

        var holidays = calendar.Holidays
            .Where(h => !filter.From.HasValue || h.Date >= filter.From.Value)
            .Where(h => !filter.To.HasValue || h.Date <= filter.To.Value)
            .OrderBy(h => h.Date)
            .ToList();

        int total = holidays.Count;

        var rows = (maxRows > 0 && total > maxRows ? holidays.Take(maxRows) : holidays)
            .Select(h =>
            {
                var stat = stats.GetValueOrDefault(h.Date);

                return new object?[]
                {
                    h.Date.ToString("yyyy/MM/dd"),
                    PunchCalendar.DayName(h.Date.DayOfWeek),
                    h.Name,
                    h.KindText,
                    h.Source,
                    stat.Punched,
                    stat.Minutes,
                    stat.Punched > 0
                        ? "دوام فعلي في عطلة - يُعرض للعلم ولا يُحتسب مخالفة"
                        : "عطلة معتمدة - لا تُحتسب غياباً"
                };
            })
            .ToList();

        return new PunchReportData(
            definition.Key,
            definition.Title,
            $"{subtitle} | عطلة نهاية الأسبوع: {calendar.WeekendDaysText} | عدد العطل: {total}",
            definition.Rule,
            HolidayReportColumns,
            rows,
            total);
    }

    /// <summary>اسم ورقة Excel للأسابيع المخالفة وفق الحدّ الأسبوعي الساري (المادة 118/ج).</summary>
    internal string WeeklySheetName() => $"المتأخرون {WeeklyRule.ThresholdMinutes} دقيقة - 118ج";

    /// <summary>
    /// أوراق الملف القانوني الشامل (16 ورقة) لعرضها في المعاينة —
    /// أسماء الأوراق المتعلقة بالمادة 118/ج تُبنى من القاعدة السارية.
    /// </summary>
    private IReadOnlyList<(string Sheet, string Content)> LegalSheets()
    {
        var rule = WeeklyRule;

        return new (string Sheet, string Content)[]
        {
        ("الملخص التنفيذي", "مؤشرات كاملة للفترة: نطاق البيانات، أوقات الدوام، المواد 7 و118/ب و118/ج، الإجازات، أيام العطل، والنتائج النهائية"),
        ("دليل القواعد القانونية", "كل قاعدة: المرجع + العتبة المطبقة + الأثر القانوني"),
        ("التأخير الصباحي - المادة 7", "كل موظف/شهر مع الإجراء التأديبي وأيام حسم الراتب"),
        ("المخالفات اليومية", "تفاصيل كل يوم: الحالة، البصمات، التأخير، الانصراف المبكر، المغادرة، الغياب عن الدوام"),
        (WeeklySheetName(), $"دمج التأخير الصباحي مع الانصراف المبكر: كل أسبوع {(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلغ")} {rule.ThresholdMinutes} دقيقة مع الناتج المحتسب وتفصيل أيام الأسبوع"),
        ("الاستئذان فوق 4 ساعات - 118ب", "الأيام التي تجاوز غيابها 4 ساعات مع أيام الخصم من الرصيد"),
        ("الغياب غير المبرر", "أيام الغياب شهرياً لكل موظف (بعد استثناء أيام العطل)"),
        ("نقص بصمة الانصراف", "الأيام غير المكتملة التي تحتاج تسوية"),
        ("رصيد الإجازة السنوية", "الأيام المستنزفة (118/ب + 118/ج) لكل موظف/شهر"),
        ("خصم المكافأة - 15 يوم", "الشهور المتجاوزة 15 يوماً ونسبة الخصم 50%"),
        ("الإجازة المرضية - المادة 112", "التراكم والشريحة (100% / 75% / 50%)"),
        ("التزام الإدارات", "ترتيب الإدارات/المديريات بنسب الالتزام وأيام الخصم"),
        ("كشف الخصومات المُجمَّع", "14 عموداً لكل موظف (تأخيرات، إجراءات، أسابيع، 118/ب/ج، غياب، حسم الراتب، الرصيد، المكافأة)"),
        ("المنهجية وجودة البيانات", "المصدر، توزيع حالات الملف، قواعد المعالجة، القيود، ونتيجة المراجعة"),
        ("تقويم العطل الرسمية والدينية", "العطل المعتمدة (رسمية/إسلامية/مسيحية) مع أثر الدوام الفعلي في كل عطلة"),
        ("الدوام في العطل ونهاية الأسبوع", "أيام دوام فعلية في العطل ونهاية الأسبوع (للعلم والساعات الإضافية)"),
        ("العمل الإضافي والدوام المرن", "أيام العمل الإضافي المحتسب (النوع والنسبة والساعات المعادلة) وأيام الدوام المرن ونافذته والدقائق المستبعدة وفق الحدود المعتمدة")
        };
    }

    /// <summary>معاينة مختصرة لتقرير واحد (تُستخدم في «قائمة تقارير الحضور والانصراف»).</summary>
    public async Task<PunchReportPreview> GetReportPreviewAsync(
        string key,
        int pageSize = 15,
        PunchReportFilter? filter = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);

        // التقرير الشامل: تُعرض أوراقه الست عشرة بدلاً من جدول بيانات واحد.
        if ((key ?? string.Empty).Trim().Equals("full-legal", StringComparison.OrdinalIgnoreCase))
        {
            var summary = await GetAnalysisSummaryAsync(ct);
            var sheets = LegalSheets()
                .Select((sheet, index) => new object?[] { index + 1, sheet.Sheet, sheet.Content })
                .ToList();

            return new PunchReportPreview(
                "full-legal",
                "التقارير القانونية الكاملة (16 ورقة)",
                $"الفترة: {summary.PeriodFrom:yyyy/MM/dd} — {summary.PeriodTo:yyyy/MM/dd} | " +
                $"{summary.EmployeesAnalyzed} موظفاً | آخر تحليل: {summary.RunAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}",
                "المواد 7 و112 و118 + قاعدة الـ 15 يوماً",
                new[] { "#", "الورقة", "المضمون" },
                sheets.Count,
                sheets);
        }

        var data = await GetReportDataAsync(key ?? string.Empty, filter, pageSize, ct);

        return new PunchReportPreview(
            data.Key,
            data.Title,
            data.Subtitle,
            data.Rule,
            data.Columns,
            data.Total,
            data.Rows);
    }

    /// <summary>
    /// بناء ملف Excel لتقرير واحد من «قائمة تقارير الحضور والانصراف»
    /// (ورقة واحدة جاهزة للطباعة والمراجعة القانونية).
    /// </summary>
    public async Task<byte[]> BuildReportWorkbookAsync(
        string key,
        PunchReportFilter? filter = null,
        int maxRows = 0,
        CancellationToken ct = default)
    {
        var normalized = (key ?? string.Empty).Trim();

        if (normalized.Equals("full-legal", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildLegalReportsWorkbookAsync(ct);
        }

        if (normalized.Equals("holiday-calendar", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildHolidayCalendarWorkbookAsync(filter?.From, filter?.To, ct);
        }

        var data = await GetReportDataAsync(normalized, filter, maxRows, ct);
        var generatedAt = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
        int columns = Math.Max(1, data.Columns.Count);

        using var workbook = new XLWorkbook();
        var ws = CreateSheet(workbook, SafeSheetName(data.Title), data.Title, data.Subtitle, columns);
        int row = WriteHeader(ws, 4, data.Columns.ToArray());

        foreach (var item in data.Rows)
        {
            for (int c = 0; c < columns; c++)
            {
                SetCellValue(ws.Cell(row, c + 1), c < item.Length ? item[c] : null);
            }

            row++;
        }

        int lastRow = Math.Max(4, row - 1);
        StyleTable(ws, 4, lastRow, columns);
        ws.SheetView.FreezeRows(4);

        if (lastRow > 4)
        {
            ws.Range(4, 1, lastRow, columns).SetAutoFilter();
        }

        if (data.Rows.Count > 0 && data.Rows.Count <= 3000)
        {
            ws.Columns(1, columns).AdjustToContents(10, 42);
        }
        else
        {
            for (int c = 1; c <= columns; c++)
            {
                ws.Column(c).Width = c <= 4 ? 14 : 16;
            }
        }

        if (maxRows > 0 && data.Total > data.Rows.Count)
        {
            AddFooter(ws, row + 1, columns,
                $"ملاحظة: عُرضت {data.Rows.Count} من {data.Total} صفاً (سقف الصفوف المطلوب {maxRows}).");
            row++;
        }

        AddFooter(ws, row + 2, columns, Signature(generatedAt, data.Rule));

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>كتابة قيمة خلية بمراعاة نوعها (نص/رقم/منطقي).</summary>
    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string text:
                cell.Value = text;
                break;
            case bool flag:
                cell.Value = flag;
                break;
            case int number:
                cell.Value = (double)number;
                break;
            case long number:
                cell.Value = (double)number;
                break;
            case double number:
                cell.Value = number;
                break;
            case decimal number:
                cell.Value = (double)number;
                break;
            case DateOnly date:
                cell.Value = date.ToString("yyyy/MM/dd");
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    /// <summary>اسم ملف تنزيل صالح: تُزال الرموز الممنوعة في أسماء الملفات وتُحوَّل المسافات إلى شرطات.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty)
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray())
            .Replace(' ', '-');

        return cleaned.Length == 0 ? "report.xlsx" : cleaned;
    }

    /// <summary>اسم ورقة Excel صالح (بلا رموز ممنوعة وبحد 31 حرفاً).</summary>
    private static string SafeSheetName(string title)
    {
        var cleaned = new string((title ?? string.Empty)
            .Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
        {
            cleaned = "تقرير";
        }

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    /// <summary>كتابة ورقة تقرير عامة من بيانات <see cref="PunchReportData"/> (تُستخدم في الملف الشامل).</summary>
    private static void AddDataSheet(XLWorkbook workbook, PunchReportData data, string generatedAt)
    {
        int columns = Math.Max(1, data.Columns.Count);
        var ws = CreateSheet(workbook, SafeSheetName(data.Title), data.Title, data.Subtitle, columns);
        int row = WriteHeader(ws, 4, data.Columns.ToArray());

        foreach (var item in data.Rows)
        {
            for (int c = 0; c < columns; c++)
            {
                SetCellValue(ws.Cell(row, c + 1), c < item.Length ? item[c] : null);
            }

            row++;
        }

        int lastRow = Math.Max(4, row - 1);
        StyleTable(ws, 4, lastRow, columns);
        ws.SheetView.FreezeRows(4);

        if (lastRow > 4)
        {
            ws.Range(4, 1, lastRow, columns).SetAutoFilter();
        }

        for (int c = 1; c <= columns; c++)
        {
            ws.Column(c).Width = c <= 4 ? 14 : 16;
        }

        AddFooter(ws, row + 2, columns, Signature(generatedAt, data.Rule));
    }

    /// <summary>ورقة تقويم العطل الرسمية والدينية (ضمن الملف القانوني الشامل).</summary>
    private async Task AddHolidayCalendarSheet(XLWorkbook workbook, string generatedAt, CancellationToken ct)
    {
        var definition = new PunchReportDefinition(
            "holiday-calendar",
            "تقويم العطل الرسمية والدينية",
            "العطل المعتمدة (رسمية + إسلامية + مسيحية) مع أثر الدوام الفعلي في كل عطلة.",
            "تقويم العطل المعتمد - يُحدَّث سنوياً بقرار رسمي");

        var data = await BuildHolidayCalendarDataAsync(
            definition, definition.Description, PunchReportFilter.Empty, 0, ct);

        AddDataSheet(workbook, data, generatedAt);
    }

    /// <summary>ورقة الدوام في العطل ونهاية الأسبوع (ضمن الملف القانوني الشامل).</summary>
    private static void AddHolidayWorkSheet(
        XLWorkbook workbook,
        IReadOnlyList<PunchDailyResult> daily,
        string generatedAt)
    {
        var definition = new PunchReportDefinition(
            "holiday-work",
            "الدوام في العطل ونهاية الأسبوع",
            "أيام دوام فعلية وقعت في عطلة رسمية/دينية أو نهاية أسبوع - تُعرض للعلم ولا تُحتسب مخالفة.",
            "قواعد الساعات الإضافية وأيام الراحة");

        var selected = daily
            .Where(d => d.IsWorkOnHoliday)
            .OrderBy(d => d.WorkDate)
            .ThenBy(d => d.JobNumber)
            .ToList();

        var data = new PunchReportData(
            definition.Key,
            definition.Title,
            $"{definition.Description} | عدد الأيام: {selected.Count}",
            definition.Rule,
            DailyReportColumns,
            selected.Select(DailyReportRow).ToList(),
            selected.Count);

        AddDataSheet(workbook, data, generatedAt);
    }
}
