using AttendanceApi.Audit;
using AttendanceApi.Data;
using AttendanceApi.Domain;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

public sealed partial class DeparturesReportService
{
    /// <summary>صف مُسقَط من التقرير مع التاريخ المرجعي المعتمد.</summary>
    private sealed record StagedItem(DeparturesReportRow Row, DateOnly? Date);

    /// <summary>
    /// مراجعة التقرير: تطبيق المادة 118/ب (استئذان &gt; 4 ساعات = خصم يوم)
    /// وقاعدة المكافأة الشهرية (تجاوز 15 يوم غياب = خصم 50%) لكل موظف/شهر.
    /// </summary>
    public async Task<DeparturesReviewResult> ReviewAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);
        _logger.LogInformation("بدء مراجعة تقرير المغادرات...");

        var rows = await _db.DeparturesReportRows.AsNoTracking().ToListAsync(ct);

        var accepted = rows
            .Where(r => IsAccepted(r.Status))
            .Select(r => new StagedItem(r, r.FromDate ?? r.RequestDate))
            .Where(x => x.Date.HasValue)
            .ToList();

        var reviews = new List<DeparturesReportReview>();

        // ---- المادة 118/ج: تجميع دقائق التأخير/الانصراف المبكر أسبوعياً لكل موظف ----
        var weekly = BuildWeeklyLateness(accepted);
        var weeklyByMonth = weekly.ToLookup(w => (w.JobNumber, w.Year, w.Month));

        foreach (var group in accepted.GroupBy(x => new { x.Row.JobNumber, x.Date!.Value.Year, x.Date.Value.Month }))
        {
            var items = group.ToList();

            double annual = SumDays(items, ReportLeaveCategory.AnnualLeave);
            double sick = SumDays(items, ReportLeaveCategory.SickLeave);
            double official = SumDays(items, ReportLeaveCategory.OfficialDuty);
            double other = SumDays(items, ReportLeaveCategory.Other)
                         + SumDays(items, ReportLeaveCategory.Unknown);

            int authCount = items.Count(i => i.Row.Category == ReportLeaveCategory.Authorization);
            int over4 = items.Count(i => i.Row.Exceeds4Hours);
            int authMinutes = items
                .Where(i => i.Row.Category == ReportLeaveCategory.Authorization)
                .Sum(i => i.Row.DurationMinutes);

            double art118bDays = over4 * LegalRules.Art118b_EquivalentDays;

            // ---- المادة 118/ج: أسابيع التأخير/الانصراف المبكر المنسوبة لهذا الشهر ----
            var weeklyForMonth = weeklyByMonth[(group.Key.JobNumber, group.Key.Year, group.Key.Month)].ToList();
            int weeksOver60 = weeklyForMonth.Count(w => w.Exceeds60Minutes);
            int weeklyLateMinutes = weeklyForMonth.Sum(w => w.LateMinutes);
            double art118cDays = weeklyForMonth.Sum(w => w.DeductionDays);

            double totalAbsence = LegalRules.TotalMonthlyAbsenceDays(annual, sick, art118bDays, art118cDays);
            double bonusDeduction = LegalRules.BonusDeductionPercent(totalAbsence);
            bool exceeds15 = totalAbsence > LegalRules.Bonus_AbsenceDaysThreshold;

            var notes = new List<string>();
            if (over4 > 0)
            {
                notes.Add($"استئذان يتجاوز 4 ساعات: {over4} → خصم {art118bDays:0.##} يوم (المادة 118/ب)");
            }

            if (weeksOver60 > 0)
            {
                notes.Add($"تأخير/انصراف مبكر أسبوعي: {weeksOver60} أسبوع بلغ 60 دقيقة " +
                          $"({FormatMinutes(weeklyLateMinutes)} داخل الدوام) → خصم {art118cDays:0.##} يوم (المادة 118/ج)");
            }

            if (exceeds15)
            {
                notes.Add($"إجمالي الغياب {totalAbsence:0.##} يوم يتجاوز 15 → خصم {bonusDeduction * 100:0}% من المكافأة");
            }

            var sample = items[0].Row;

            reviews.Add(new DeparturesReportReview
            {
                JobNumber = group.Key.JobNumber,
                EmployeeName = sample.EmployeeName,
                Year = group.Key.Year,
                Month = group.Key.Month,
                AuthorizationCount = authCount,
                AuthorizationOver4hCount = over4,
                TotalAuthorizationMinutes = authMinutes,
                Article118bDays = art118bDays,
                WeeklyLateMinutes = weeklyLateMinutes,
                WeeksOver60Minutes = weeksOver60,
                Article118cDays = art118cDays,
                AnnualLeaveDays = annual,
                SickLeaveDays = sick,
                OfficialDutyDays = official,
                OtherLeaveDays = other,
                TotalAbsenceDays = totalAbsence,
                BonusDeductionPercent = bonusDeduction,
                Exceeds15Days = exceeds15,
                HasViolation = over4 > 0 || weeksOver60 > 0 || exceeds15,
                Notes = notes.Count > 0 ? string.Join(" | ", notes) : null
            });
        }

        reviews = reviews
            .OrderBy(r => r.JobNumber)
            .ThenBy(r => r.Year)
            .ThenBy(r => r.Month)
            .ToList();

        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE DeparturesReportReviews;", ct);
        if (reviews.Count > 0)
        {
            await _db.BulkInsertAsync(reviews, cancellationToken: ct);
        }

        // ---- حفظ التجميع الأسبوعي (المادة 118/ج) ----
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE DeparturesWeeklyLateness;", ct);
        if (weekly.Count > 0)
        {
            await _db.BulkInsertAsync(weekly, cancellationToken: ct);
        }

        var result = new DeparturesReviewResult(
            TotalRequests: rows.Count,
            AcceptedRequests: accepted.Count,
            EmployeesReviewed: reviews.Select(r => r.JobNumber).Distinct().Count(),
            EmployeesWithViolations: reviews.Count(r => r.HasViolation),
            EmployeesOver4Hours: reviews.Count(r => r.AuthorizationOver4hCount > 0),
            TotalArticle118bDays: reviews.Sum(r => r.Article118bDays),
            EmployeesExceeding15Days: reviews.Count(r => r.Exceeds15Days),
            WeeksOver60Minutes: weekly.Count(w => w.Exceeds60Minutes),
            EmployeesOver60Minutes: weekly
                .Where(w => w.Exceeds60Minutes)
                .Select(w => w.JobNumber)
                .Distinct()
                .Count(),
            TotalWeeklyLateMinutes: weekly.Sum(w => w.LateMinutes),
            TotalArticle118cDays: weekly.Sum(w => w.DeductionDays),
            RunAtUtc: DateTime.UtcNow);

        _logger.LogInformation(
            "اكتملت المراجعة: {Employees} موظفاً، مخالفات {Violations}، أيام 118/ب {B:0.##}، "
            + "أسابيع ≥60 دقيقة {Weeks}، أيام 118/ج {C:0.##}.",
            result.EmployeesReviewed, result.EmployeesWithViolations,
            result.TotalArticle118bDays, result.WeeksOver60Minutes, result.TotalArticle118cDays);

        return result;
    }

    /// <summary>مجموع أيام فئة معيّنة ضمن مجموعة الموظف/الشهر.</summary>
    private static double SumDays(List<StagedItem> items, ReportLeaveCategory category) =>
        items.Where(i => i.Row.Category == category).Sum(i => i.Row.DaysCount);

    /// <summary>
    /// بناء التجميع الأسبوعي لدقائق التأخير/الانصراف المبكر (المادة 118/ج):
    /// يُجمَع كل استئذان <b>لا</b> يتجاوز 4 ساعات (المتجاوز يُعاقب بموجب 118/ب، فلا ازدواج)
    /// ووقع في يوم واحد داخل نافذة الدوام الرسمي 08:30–15:30؛ فإذا بلغ مجموع دقائق
    /// أسبوع الموظف 60 دقيقة أو أكثر → خصم يوم كامل.
    /// يُنسب الأسبوع إلى شهر أول مغادرة محتسبة فيه (لبناء التقرير الشهري).
    /// </summary>
    private static List<DeparturesWeeklyLateness> BuildWeeklyLateness(IEnumerable<StagedItem> accepted)
    {
        var result = new List<DeparturesWeeklyLateness>();

        var candidates = accepted
            .Where(i => IsWeeklyLatenessCandidate(i.Row))
            .Select(i => new
            {
                i.Row,
                Date = i.Row.FromDate!.Value,
                Week = LegalRules.StartOfIsoWeek(i.Row.FromDate!.Value),
                Minutes = LegalRules.MinutesInsideWorkday(i.Row.FromTime, i.Row.ToTime, i.Row.DurationMinutes)
            })
            .Where(x => x.Minutes > 0);

        foreach (var employeeWeek in candidates.GroupBy(x => new { x.Row.JobNumber, x.Week }))
        {
            int totalMinutes = employeeWeek.Sum(x => x.Minutes);
            var first = employeeWeek.OrderBy(x => x.Date).First();
            var last = employeeWeek.OrderByDescending(x => x.Date).First();

            result.Add(new DeparturesWeeklyLateness
            {
                JobNumber = employeeWeek.Key.JobNumber,
                EmployeeName = first.Row.EmployeeName,
                WeekStart = employeeWeek.Key.Week,
                WeekEnd = last.Date,
                Year = first.Date.Year,
                Month = first.Date.Month,
                DepartureCount = employeeWeek.Count(),
                LateMinutes = totalMinutes,
                Exceeds60Minutes = totalMinutes >= LegalRules.Art118c_WeeklyLateMinutesThreshold,
                DeductionDays = LegalRules.WeeklyLateDeductionDays(totalMinutes),
                Notes = $"{employeeWeek.Count()} مغادرة داخل الدوام — {FormatMinutes(totalMinutes)} " +
                        $"(من {first.Date:yyyy-MM-dd} إلى {last.Date:yyyy-MM-dd})"
            });
        }

        return result
            .OrderBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .ToList();
    }

    /// <summary>
    /// شروط إدخال مغادرة في التجميع الأسبوعي للمادة 118/ج:
    /// استئذان (يشمل الاستئذان الطبي) لا يتجاوز 4 ساعات وفي يوم واحد.
    /// </summary>
    private static bool IsWeeklyLatenessCandidate(DeparturesReportRow row) =>
        row.Category == ReportLeaveCategory.Authorization
        && !row.Exceeds4Hours
        && row.FromDate.HasValue
        && row.ToDate.HasValue
        && row.FromDate.Value == row.ToDate.Value;

    /// <summary>تنسيق الدقائق بالعربية (ساعات ودقائق).</summary>
    internal static string FormatMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            return "0 دقيقة";
        }

        int hours = minutes / 60;
        int rest = minutes % 60;

        var parts = new List<string>(2);
        if (hours > 0)
        {
            parts.Add(FormatCount(hours, "ساعة", "ساعتان", "ساعات"));
        }

        if (rest > 0)
        {
            parts.Add(FormatCount(rest, "دقيقة", "دقيقتان", "دقائق"));
        }

        return string.Join(" و", parts);
    }

    /// <summary>صياغة العدد بالعربية (مفرد/مثنى/جمع، والتمييز من 11 فأكثر بالمفرد).</summary>
    private static string FormatCount(int value, string singular, string dual, string plural) => value switch
    {
        1 => $"1 {singular}",
        2 => dual,
        >= 3 and <= 10 => $"{value} {plural}",
        _ => $"{value} {singular}"
    };

    /// <summary>قراءة صفحات نتائج المراجعة الشهرية.</summary>
    public async Task<DeparturesReviewsPage> GetReviewsAsync(
        int page = 1,
        int pageSize = 50,
        bool onlyViolations = false,
        int? year = null,
        int? month = null,
        string? jobNumber = null,
        CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 500 ? 50 : pageSize;

        var query = _db.DeparturesReportReviews.AsNoTracking();

        if (onlyViolations)
        {
            query = query.Where(r => r.HasViolation);
        }

        if (!string.IsNullOrWhiteSpace(jobNumber))
        {
            var job = jobNumber.Trim();
            query = query.Where(r => r.JobNumber == job);
        }

        if (year.HasValue)
        {
            query = query.Where(r => r.Year == year.Value);
        }

        if (month.HasValue)
        {
            query = query.Where(r => r.Month == month.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.HasViolation)
            .ThenBy(r => r.JobNumber)
            .ThenBy(r => r.Year)
            .ThenBy(r => r.Month)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new DeparturesReviewsPage(total, page, pageSize, items);
    }

    /// <summary>الطلبات التي تجاوزت 4 ساعات (مخالفات المادة 118/ب) من جدول المرحلة.</summary>
    public async Task<IReadOnlyList<DeparturesReportRow>> GetOver4HoursAsync(
        int take = 1000, CancellationToken ct = default)
    {
        take = take is < 1 or > 20_000 ? 1000 : take;

        return await _db.DeparturesReportRows
            .AsNoTracking()
            .Where(r => r.Exceeds4Hours)
            .OrderBy(r => r.JobNumber)
            .ThenBy(r => r.FromDate)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>التجميع الأسبوعي لدقائق التأخير/الانصراف المبكر لكل موظف (المادة 118/ج).</summary>
    public async Task<DeparturesWeeklyLatenessSummary> GetWeeklyLatenessAsync(
        bool onlyViolations = true,
        string? jobNumber = null,
        int take = 2000,
        CancellationToken ct = default)
    {
        take = take is < 1 or > 50_000 ? 2000 : take;

        // يُحمَّل التجميع الأسبوعي كاملاً في الذاكرة (آلاف الصفوف فقط) لتجنّب إشكال
        // ترجمة SUM على عمود من نوع real الذي يعيده SQL Server كـ double.
        var all = await _db.WeeklyLateness.AsNoTracking().ToListAsync(ct);

        var job = jobNumber?.Trim();

        var items = all
            .Where(w => !onlyViolations || w.Exceeds60Minutes)
            .Where(w => string.IsNullOrWhiteSpace(job) || w.JobNumber == job)
            .OrderByDescending(w => w.LateMinutes)
            .ThenBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .Take(take)
            .ToList();

        return new DeparturesWeeklyLatenessSummary(
            WeeksTotal: all.Count,
            WeeksOver60Minutes: all.Count(w => w.Exceeds60Minutes),
            EmployeesOver60Minutes: all
                .Where(w => w.Exceeds60Minutes)
                .Select(w => w.JobNumber)
                .Distinct()
                .Count(),
            TotalLateMinutes: all.Sum(w => w.LateMinutes),
            TotalDeductionDays: all.Sum(w => w.DeductionDays),
            Items: items);
    }

    /// <summary>ملخص جدول المرحلة (للوحة الاختبار).</summary>
    public async Task<DeparturesStagingSummary> GetStagingSummaryAsync(CancellationToken ct = default)
    {
        var rows = _db.DeparturesReportRows.AsNoTracking();

        return new DeparturesStagingSummary(
            Total: await rows.CountAsync(ct),
            Authorizations: await rows.CountAsync(r => r.IsAuthorization, ct),
            Over4Hours: await rows.CountAsync(r => r.Exceeds4Hours, ct),
            AnnualLeaves: await rows.CountAsync(r => r.Category == ReportLeaveCategory.AnnualLeave, ct),
            SickLeaves: await rows.CountAsync(r => r.Category == ReportLeaveCategory.SickLeave, ct),
            OfficialDuty: await rows.CountAsync(r => r.Category == ReportLeaveCategory.OfficialDuty, ct),
            Others: await rows.CountAsync(
                r => r.Category == ReportLeaveCategory.Other || r.Category == ReportLeaveCategory.Unknown, ct),
            Reviews: await _db.DeparturesReportReviews.CountAsync(ct));
    }
}

/// <summary>نتيجة استيراد تقرير المغادرات.</summary>
public sealed record DeparturesImportResult(
    string FileName,
    long RowsImported,
    long RowsSkipped,
    IReadOnlyDictionary<string, int> RequestTypes,
    bool ReplacedExisting = true);

/// <summary>ملخص نتائج مراجعة التقرير.</summary>
public sealed record DeparturesReviewResult(
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
    DateTime RunAtUtc);

/// <summary>ملخص التجميع الأسبوعي لدقائق التأخير/الانصراف المبكر (المادة 118/ج).</summary>
public sealed record DeparturesWeeklyLatenessSummary(
    int WeeksTotal,
    int WeeksOver60Minutes,
    int EmployeesOver60Minutes,
    int TotalLateMinutes,
    double TotalDeductionDays,
    IReadOnlyList<DeparturesWeeklyLateness> Items);

/// <summary>صفحة من نتائج المراجعة الشهرية.</summary>
public sealed record DeparturesReviewsPage(
    int Total, int Page, int PageSize, IReadOnlyList<DeparturesReportReview> Items);

/// <summary>ملخص جدول مرحلة تقرير المغادرات.</summary>
public sealed record DeparturesStagingSummary(
    int Total,
    int Authorizations,
    int Over4Hours,
    int AnnualLeaves,
    int SickLeaves,
    int OfficialDuty,
    int Others,
    int Reviews);
