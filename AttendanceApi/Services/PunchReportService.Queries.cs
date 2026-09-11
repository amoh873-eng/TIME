using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>نتيجة مُصفَّحة (صفحة) لأي قائمة نتائج.</summary>
public sealed record PunchPage<T>(int Page, int PageSize, int Total, int TotalPages, IReadOnlyList<T> Items);

/// <summary>
/// استعلامات وملخصات نتائج تحليل بصمات الحضور (قراءة من قاعدة البيانات بعد التحليل).
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>ملخص نتائج المراجعة (يُستخدم بعد التحليل وعند القراءة من قاعدة البيانات).</summary>
    internal static PunchAnalysisSummary BuildSummary(
        long recordCount,
        IReadOnlyList<PunchDailyResult> daily,
        IReadOnlyList<PunchWeeklyResult> weekly,
        IReadOnlyList<PunchMonthlyResult> monthly,
        int graceMinutes)
    {
        if (daily.Count == 0)
        {
            return EmptySummary(graceMinutes);
        }

        int employees = daily.Select(d => d.JobNumber).Distinct(StringComparer.Ordinal).Count();
        var withLate = daily.Where(d => d.IsMorningLate).Select(d => d.JobNumber)
            .Distinct(StringComparer.Ordinal).Count();
        var withPenalty = monthly.Where(m => m.Penalty != DisciplinaryActionType.None)
            .Select(m => m.JobNumber).Distinct(StringComparer.Ordinal).Count();
        var withOver4 = daily.Where(d => d.CountsFor118b).Select(d => d.JobNumber)
            .Distinct(StringComparer.Ordinal).Count();
        var withOver60 = weekly.Where(w => w.Exceeds60Minutes).Select(w => w.JobNumber)
            .Distinct(StringComparer.Ordinal).Count();
        var withViolations = daily
            .Where(d => d.CountsFor118b || d.IsAbsent || d.IsIncomplete || d.IsMorningLate)
            .Select(d => d.JobNumber)
            .Concat(weekly.Where(w => w.Exceeds60Minutes).Select(w => w.JobNumber))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new PunchAnalysisSummary(
            EmployeesAnalyzed: employees,
            RecordsAnalyzed: recordCount,
            PeriodFrom: daily.Min(d => d.WorkDate),
            PeriodTo: daily.Max(d => d.WorkDate),
            MorningGraceMinutes: graceMinutes,
            WorkingDays: daily.Count(d => d.IsWorkingDay),
            CompleteDays: daily.Count(d => d.Status == PunchDayStatus.Complete),
            AbsentDays: daily.Count(d => d.IsAbsent),
            AbsentDaysOnWeekend: daily.Count(d => d.IsAbsent && d.IsWeekendDay),
            IncompleteDays: daily.Count(d => d.IsIncomplete),
            NoDataDays: daily.Count(d => d.Status == PunchDayStatus.NoData),
            WeekendDays: daily.Count(d => d.IsWeekendDay && !d.IsCalendarHoliday),
            HolidayDays: daily.Count(d => d.IsCalendarHoliday),
            WeekendWorkDays: daily.Count(d => d.Status == PunchDayStatus.WeekendWork),
            HolidayWorkDays: daily.Count(d => d.Status == PunchDayStatus.HolidayWork),
            CalendarHolidays: daily.Where(d => d.IsCalendarHoliday).Select(d => d.WorkDate).Distinct().Count(),
            TotalLatenessMinutes: daily.Sum(d => (long)d.LatenessMinutes),
            TotalEarlyDepartureMinutes: daily.Sum(d => (long)d.EarlyDepartureMinutes),
            TotalMidDayGapMinutes: daily.Sum(d => (long)d.GapMinutes),
            LateIncidentDays: daily.Count(d => d.IsMorningLate),
            EmployeesWithLateIncidents: withLate,
            EmployeesWithArticle7Penalty: withPenalty,
            TotalArticle7SalaryDays: Math.Round(monthly.Sum(m => m.Article7SalaryDeductionDays), 2),
            WeeksOver60Minutes: weekly.Count(w => w.Exceeds60Minutes),
            EmployeesOver60Minutes: withOver60,
            TotalWeeklyLateMinutes: weekly.Sum(w => w.CountedMinutes),
            TotalArticle118cDays: Math.Round(weekly.Sum(w => w.DeductionDays), 2),
            DaysOver4Hours: daily.Count(d => d.CountsFor118b),
            EmployeesOver4Hours: withOver4,
            TotalArticle118bDays: Math.Round(daily.Sum(d => d.Article118bDays), 2),
            TotalAnnualLeaveDays: Math.Round(monthly.Sum(m => m.AnnualLeaveDays), 2),
            TotalSickLeaveDays: Math.Round(monthly.Sum(m => m.SickLeaveDays), 2),
            TotalEmergencyPermissionDays: Math.Round(monthly.Sum(m => m.EmergencyPermissionDays + m.MedicalPermissionDays), 2),
            TotalOfficialDutyDays: Math.Round(monthly.Sum(m => m.OfficialDutyDays), 2),
            TotalAbsenceForBonusDays: Math.Round(monthly.Sum(m => m.TotalAbsenceDays), 2),
            TotalSalaryDeductionDays: Math.Round(monthly.Sum(m => m.TotalSalaryDeductionDays), 2),
            TotalBonusDeductionPercent: Math.Round(
                monthly.Count(m => m.Exceeds15Days) * 100.0 / Math.Max(1, monthly.Count), 2),
            EmployeesWithViolations: withViolations,
            ShiftDutyDays: daily.Count(d => d.Status == PunchDayStatus.ShiftDuty),
            ShiftRestDays: daily.Count(d => d.Status == PunchDayStatus.ShiftRest),
            ShiftLeaveDays: daily.Count(d => d.Status == PunchDayStatus.ShiftLeave),
            ShiftEmployees: daily.Where(d => d.IsShiftDay)
                .Select(d => d.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            FlexibleDays: daily.Count(d => d.IsFlexibleWork),
            FlexibleEmployees: daily.Where(d => d.IsFlexibleWork)
                .Select(d => d.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            FlexibleMinutes: daily.Sum(d => d.FlexibleMinutes),
            OvertimeDays: daily.Count(d => d.OvertimeMinutes > 0),
            OvertimeEmployees: daily.Where(d => d.OvertimeMinutes > 0)
                .Select(d => d.JobNumber).Distinct(StringComparer.Ordinal).Count(),
            OvertimeMinutes: daily.Sum(d => (long)d.OvertimeMinutes),
            OvertimeRawMinutes: daily.Sum(d => (long)d.RawOvertimeMinutes),
            OvertimeExcludedMinutes: daily.Sum(d => (long)d.OvertimeExcludedMinutes),
            OvertimeNeedsApprovalDays: daily.Count(d => d.OvertimeNeedsApproval),
            OvertimeNeedsApprovalMinutes: daily.Where(d => d.OvertimeNeedsApproval)
                .Sum(d => (long)d.RawOvertimeMinutes),
            OvertimeWeekendDays: daily.Count(d => d.OvertimeKind == OvertimeDayKind.Weekend && d.OvertimeMinutes > 0),
            OvertimeHolidayDays: daily.Count(d => d.OvertimeKind == OvertimeDayKind.Holiday && d.OvertimeMinutes > 0),
            OvertimeHours: Math.Round(monthly.Sum(m => m.OvertimeHours), 2),
            EquivalentOvertimeHours: Math.Round(monthly.Sum(m => m.EquivalentOvertimeHours), 2),
            EmployeesOverMonthlyOvertimeCap: monthly.Count(m => m.OvertimeCapReached),
            RunAtUtc: DateTime.UtcNow,
            HasData: true);
    }

    /// <summary>ملخص جدول مرحلة البصمات (جودة البيانات قبل التحليل).</summary>
    public async Task<PunchStagingSummary> GetStagingSummaryAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var query = _db.PunchRecords.AsNoTracking();
        long total = await query.LongCountAsync(ct);

        if (total == 0)
        {
            return new PunchStagingSummary(0, 0, null, null, new Dictionary<string, int>(), null, null, false);
        }

        var statusCounts = await query
            .GroupBy(r => r.StatusText)
            .Select(g => new { Text = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byText = statusCounts
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Text) ? "(غير محدد)" : x.Text!)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.Ordinal);

        return new PunchStagingSummary(
            TotalRows: total,
            Employees: await query.Select(r => r.JobNumber).Distinct().CountAsync(ct),
            FirstDate: await query.MinAsync(r => (DateOnly?)r.WorkDate, ct),
            LastDate: await query.MaxAsync(r => (DateOnly?)r.WorkDate, ct),
            StatusCounts: byText,
            LastImportUtc: await query.MaxAsync(r => (DateTime?)r.ImportedAtUtc, ct),
            SourceFile: await query.Where(r => r.SourceFile != null).Select(r => r.SourceFile).FirstOrDefaultAsync(ct),
            HasData: true);
    }

    /// <summary>ملخص التحليل المحفوظ في قاعدة البيانات (بلا إعادة معالجة).</summary>
    public async Task<PunchAnalysisSummary> GetAnalysisSummaryAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        if (!await _db.PunchDailyResults.AnyAsync(ct))
        {
            return EmptySummary(AppliedRule.MorningGraceMinutes);
        }

        var daily = await _db.PunchDailyResults.AsNoTracking().ToListAsync(ct);
        var weekly = await _db.PunchWeeklyResults.AsNoTracking().ToListAsync(ct);
        var monthly = await _db.PunchMonthlyResults.AsNoTracking().ToListAsync(ct);
        long records = await _db.PunchRecords.AsNoTracking().LongCountAsync(ct);

        return BuildSummary(records, daily, weekly, monthly, AppliedRule.MorningGraceMinutes);
    }

    /// <summary>نتائج التحليل اليومية (مع إمكانية قصرها على المخالفات).</summary>
    public async Task<PunchPage<PunchDailyResult>> GetDailyAsync(
        int page = 1,
        int pageSize = 50,
        bool onlyViolations = false,
        string? jobNumber = null,
        int? year = null,
        int? month = null,
        int? take = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        var query = _db.PunchDailyResults.AsNoTracking();

        if (onlyViolations)
        {
            query = query.Where(d => d.CountsFor118b || d.IsAbsent || d.IsIncomplete || d.IsMorningLate);
        }

        if (!string.IsNullOrWhiteSpace(jobNumber))
        {
            query = query.Where(d => d.JobNumber == jobNumber);
        }

        if (year.HasValue)
        {
            query = query.Where(d => d.Year == year);
        }

        if (month.HasValue)
        {
            query = query.Where(d => d.Month == month);
        }

        int total = await query.CountAsync(ct);
        int size = take.HasValue ? Math.Clamp(take.Value, 1, 5000) : pageSize;

        var items = await query
            .OrderByDescending(d => d.AbsenceMinutes)
            .ThenBy(d => d.JobNumber)
            .ThenBy(d => d.WorkDate)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new PunchPage<PunchDailyResult>(page, size, total, (int)Math.Ceiling(total / (double)size), items);
    }

    /// <summary>التجميع الأسبوعي (المادة 118/ج) مع إمكانية قصر النتائج على الأسابيع المخالفة.</summary>
    public async Task<PunchPage<PunchWeeklyResult>> GetWeeklyAsync(
        int page = 1,
        int pageSize = 50,
        bool onlyViolations = true,
        string? jobNumber = null,
        int? year = null,
        int? month = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        var query = _db.PunchWeeklyResults.AsNoTracking();

        if (onlyViolations)
        {
            query = query.Where(w => w.Exceeds60Minutes);
        }

        if (!string.IsNullOrWhiteSpace(jobNumber))
        {
            query = query.Where(w => w.JobNumber == jobNumber);
        }

        if (year.HasValue)
        {
            query = query.Where(w => w.Year == year);
        }

        if (month.HasValue)
        {
            query = query.Where(w => w.Month == month);
        }

        int total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(w => w.TotalMinutes)
            .ThenBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PunchPage<PunchWeeklyResult>(page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize), items);
    }

    /// <summary>النتائج الشهرية لكل موظف (المادة 7 + الإجازات + الخصومات).</summary>
    public async Task<PunchPage<PunchMonthlyResult>> GetMonthlyAsync(
        int page = 1,
        int pageSize = 50,
        bool onlyViolations = false,
        string? jobNumber = null,
        int? year = null,
        int? month = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 5000);

        var query = _db.PunchMonthlyResults.AsNoTracking();

        if (onlyViolations)
        {
            query = query.Where(m => m.Penalty != DisciplinaryActionType.None
                                     || m.AbsentDays > 0
                                     || m.IncompleteDays > 0
                                     || m.Article118bDays > 0
                                     || m.Article118cDays > 0);
        }

        if (!string.IsNullOrWhiteSpace(jobNumber))
        {
            query = query.Where(m => m.JobNumber == jobNumber);
        }

        if (year.HasValue)
        {
            query = query.Where(m => m.Year == year);
        }

        if (month.HasValue)
        {
            query = query.Where(m => m.Month == month);
        }

        int total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.TotalSalaryDeductionDays + m.Article118bDays + m.Article118cDays + m.AbsentDays)
            .ThenBy(m => m.JobNumber)
            .ThenBy(m => m.Year)
            .ThenBy(m => m.Month)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PunchPage<PunchMonthlyResult>(page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize), items);
    }

    /// <summary>سجل أيام موظف واحد (لتحليل الحالة الفردية).</summary>
    public async Task<IReadOnlyList<PunchDailyResult>> GetEmployeeTimelineAsync(
        string jobNumber,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var query = _db.PunchDailyResults.AsNoTracking().Where(d => d.JobNumber == jobNumber);

        if (from.HasValue)
        {
            query = query.Where(d => d.WorkDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(d => d.WorkDate <= to.Value);
        }

        return await query.OrderBy(d => d.WorkDate).ToListAsync(ct);
    }

    /// <summary>
    /// لوحة التزام الإدارات/المديريات من بصمات الحضور:
    /// يُنسب كل موظف إلى إدارته (الأكثر تكراراً في صفوفه)، ثم تُحسب نسبة الملتزمين
    /// (بلا أي مخالفة: غياب غير مبرّر أو يوم &gt; 4 ساعات أو أسبوع ≥ 60 دقيقة).
    /// </summary>
    public async Task<IReadOnlyList<PunchComplianceItem>> GetComplianceByDepartmentAsync(
        int minEmployees = 1,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var monthly = await _db.PunchMonthlyResults.AsNoTracking().ToListAsync(ct);

        if (monthly.Count == 0)
        {
            return Array.Empty<PunchComplianceItem>();
        }

        var employees = monthly
            .GroupBy(m => m.JobNumber, StringComparer.Ordinal)
            .Select(g => new
            {
                JobNumber = g.Key,
                Department = g.Select(m => m.DepartmentName)
                    .Where(d => !string.IsNullOrWhiteSpace(d) && d != "_")
                    .GroupBy(d => d!, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count())
                    .Select(x => x.Key)
                    .FirstOrDefault() ?? "(غير محدّد)",
                Article118bDays = g.Sum(m => m.Article118bDays),
                Article118cDays = g.Sum(m => m.Article118cDays),
                AbsentDays = g.Sum(m => m.AbsentDays),
                LateIncidents = g.Sum(m => m.LateIncidents),
                SalaryDays = g.Sum(m => m.TotalSalaryDeductionDays)
            })
            .ToList();

        int threshold = Math.Max(1, minEmployees);

        return employees
            .GroupBy(e => e.Department, StringComparer.Ordinal)
            .Select(g =>
            {
                int total = g.Count();
                int violating = g.Count(e => e.Article118bDays > 0 || e.Article118cDays > 0 || e.AbsentDays > 0);
                double rate = total > 0 ? violating * 100.0 / total : 0;

                return new PunchComplianceItem(
                    Administration: g.Key,
                    Rank: 0,
                    Employees: total,
                    CompliantEmployees: total - violating,
                    ViolatingEmployees: violating,
                    CompliancePercent: Math.Round(100.0 - rate, 2),
                    EmployeesWithLateness: g.Count(e => e.LateIncidents > 0),
                    Article118bDays: Math.Round(g.Sum(e => e.Article118bDays), 2),
                    Article118cDays: Math.Round(g.Sum(e => e.Article118cDays), 2),
                    AbsentDays: (int)g.Sum(e => e.AbsentDays),
                    TotalSalaryDeductionDays: Math.Round(g.Sum(e => e.SalaryDays), 2));
            })
            .Where(x => x.Employees >= threshold)
            .OrderByDescending(x => x.CompliancePercent)
            .ThenByDescending(x => x.Employees)
            .ThenBy(x => x.Administration, StringComparer.Ordinal)
            .Select((x, i) => x with { Rank = i + 1 })
            .ToList();
    }
}
