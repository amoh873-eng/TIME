using AttendanceApi.Audit;
using AttendanceApi.Data;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// استيراد وتحليل «تقرير الحضور والانصراف» الخام (البصمات):
/// استيراد الملف إلى جدول مرحلة، ثم تحليل قانوني (المادة 7، المادة 118/ب، المادة 118/ج،
/// قاعدة الـ 15 يوماً للمكافأة) مع تخزين النتائج اليومية والأسبوعية والشهرية وإتاحة التقارير.
/// </summary>
public sealed partial class PunchReportService
{
    private const int InsertBatchSize = 5_000;

    private readonly AttendanceDbContext _db;
    private readonly DatabaseInitializer _initializer;
    private readonly PunchAnalysisOptions _options;
    private readonly PunchWeeklyRuleStore _weeklyRules;
    private readonly ILogger<PunchReportService> _logger;

    public PunchReportService(
        AttendanceDbContext db,
        DatabaseInitializer initializer,
        PunchAnalysisOptions options,
        PunchWeeklyRuleStore weeklyRules,
        ILogger<PunchReportService> logger)
    {
        _db = db;
        _initializer = initializer;
        _options = options;
        _weeklyRules = weeklyRules;
        _logger = logger;
    }

    /// <summary>
    /// قاعدة المادة 118/ج الأسبوعية السارية الآن (مرنة: الحدّ، السقف، أيام الخصم، خيارات الدمج،
    /// وتجاوزات الإدارات) — تُقرأ من ملف الإعدادات في وقت التشغيل بلا إعادة تشغيل الخدمة.
    /// </summary>
    internal PunchWeeklyRule WeeklyRule => _weeklyRules.Load();

    /// <summary>خطوات أعمدة ملف البصمات بعد التعرف عليها من الترويسة.</summary>
    private sealed record ColumnMap(
        int JobNumber,
        int EmployeeName,
        int WorkDate,
        int Status,
        int ClockIn,
        int Location,
        int ClockOut,
        int Department);

    // =====================================================================
    //  الاستيراد من ملف Excel
    // =====================================================================

    /// <summary>رفع ملف «تقرير الحضور والانصراف» إلى جدول المرحلة مع توحيد الحالات.</summary>
    public async Task<PunchImportResult> ImportAsync(
        Stream stream,
        string fileName,
        bool replaceExisting = true,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);
        _logger.LogInformation("بدء استيراد ملف بصمات الحضور: {File}", fileName);

        var importedAt = DateTime.UtcNow;
        var employees = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new List<PunchRecord>(InsertBatchSize);
        long imported = 0, skipped = 0;
        DateOnly? first = null, last = null;
        bool truncated = !replaceExisting;

        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();
        int headerRow = FindHeaderRow(worksheet);

        if (headerRow == 0)
        {
            throw new InvalidOperationException(
                "لم يُعثر على صف الترويسة العربي (الرقم الوظيفي / الإسم / التاريخ / حالة التحضير) في الملف.");
        }

        var columns = ResolveColumns(worksheet, headerRow);
        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;

        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();

            var record = MapRow(worksheet, r, columns, fileName, importedAt);
            if (record is null)
            {
                skipped++;
                continue;
            }

            if (!truncated)
            {
                await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE StagingPunchRecords;", ct);
                truncated = true;
            }

            employees.Add(record.JobNumber);
            first = first is null || record.WorkDate < first ? record.WorkDate : first;
            last = last is null || record.WorkDate > last ? record.WorkDate : last;
            buffer.Add(record);

            if (buffer.Count >= InsertBatchSize)
            {
                await _db.BulkInsertAsync(buffer, cancellationToken: ct);
                imported += buffer.Count;
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await _db.BulkInsertAsync(buffer, cancellationToken: ct);
            imported += buffer.Count;
        }

        _logger.LogInformation(
            "اكتمل استيراد البصمات: {Imported} صفاً لـ {Employees} موظفاً (متجاهَل {Skipped})، الفترة {From} — {To}.",
            imported, employees.Count, skipped, first, last);

        return new PunchImportResult(fileName, imported, skipped, employees.Count, first, last, importedAt);
    }

    /// <summary>تحديد صف الترويسة داخل أول 30 صفاً من الورقة.</summary>
    private static int FindHeaderRow(IXLWorksheet ws)
    {
        int lastRow = Math.Min(30, ws.LastRowUsed()?.RowNumber() ?? 0);
        int lastColumn = Math.Min(15, ws.LastColumnUsed()?.ColumnNumber() ?? 0);

        for (int r = 1; r <= lastRow; r++)
        {
            for (int c = 1; c <= lastColumn; c++)
            {
                var text = Normalize(ws.Cell(r, c).GetFormattedString());
                if (text.Length == 0)
                {
                    continue;
                }

                if (text.Contains(PunchHeaders.JobNumber, StringComparison.Ordinal))
                {
                    return r;
                }
            }
        }

        return 0;
    }

    /// <summary>مطابقة أعمدة الورقة بأسماء الترويسة، مع الرجوع إلى ترتيب التقرير المعتاد.</summary>
    private static ColumnMap ResolveColumns(IXLWorksheet ws, int headerRow)
    {
        int lastColumn = Math.Min(20, ws.LastColumnUsed()?.ColumnNumber() ?? 0);
        int job = 0, name = 0, date = 0, status = 0, clockIn = 0, location = 0, clockOut = 0, department = 0;

        for (int c = 1; c <= lastColumn; c++)
        {
            var text = Normalize(ws.Cell(headerRow, c).GetFormattedString());
            if (text.Length == 0)
            {
                continue;
            }

            if (job == 0 && text.Contains(PunchHeaders.JobNumber, StringComparison.Ordinal))
            {
                job = c;
            }
            else if (name == 0 && text.Contains(PunchHeaders.EmployeeName, StringComparison.Ordinal))
            {
                name = c;
            }
            else if (date == 0 && text.Contains(PunchHeaders.WorkDate, StringComparison.Ordinal))
            {
                date = c;
            }
            else if (status == 0 && text.Contains(PunchHeaders.Status, StringComparison.Ordinal))
            {
                status = c;
            }
            else if (clockIn == 0 && text.Contains(PunchHeaders.ClockIn, StringComparison.Ordinal))
            {
                clockIn = c;
            }
            else if (location == 0 && text.Contains(PunchHeaders.Location, StringComparison.Ordinal))
            {
                location = c;
            }
            else if (clockOut == 0 && text.Contains(PunchHeaders.ClockOut, StringComparison.Ordinal))
            {
                clockOut = c;
            }
            else if (department == 0 && text.Contains(PunchHeaders.Department, StringComparison.Ordinal))
            {
                department = c;
            }
        }

        // الترتيب المعتاد في تصدير النظام: 1 رقم، 2 اسم، 3 تاريخ، 4 حالة، 5 حضور، 6 مكان، 7 انصراف، 9 إدارة
        return new ColumnMap(
            job > 0 ? job : 1,
            name > 0 ? name : 2,
            date > 0 ? date : 3,
            status > 0 ? status : 4,
            clockIn > 0 ? clockIn : 5,
            location > 0 ? location : 6,
            clockOut > 0 ? clockOut : 7,
            department > 0 ? department : 9);
    }

    /// <summary>تحويل صف Excel واحد إلى صف مرحلة (أو null إذا كان بلا رقم وظيفي أو تاريخ).</summary>
    private static PunchRecord? MapRow(
        IXLWorksheet ws, int row, ColumnMap columns, string fileName, DateTime importedAt)
    {
        var jobNumber = Normalize(ws.Cell(row, columns.JobNumber).GetFormattedString());
        if (jobNumber.Length == 0 || jobNumber == "-" || jobNumber == "_")
        {
            return null;
        }

        var workDate = ReadDate(ws.Cell(row, columns.WorkDate));
        if (workDate is null)
        {
            return null;
        }

        var statusText = Normalize(ws.Cell(row, columns.Status).GetFormattedString());
        var department = Normalize(ws.Cell(row, columns.Department).GetFormattedString());
        var location = Normalize(ws.Cell(row, columns.Location).GetFormattedString());
        var employeeName = Normalize(ws.Cell(row, columns.EmployeeName).GetFormattedString());

        return new PunchRecord
        {
            JobNumber = jobNumber,
            EmployeeName = employeeName.Length > 0 ? Truncate(employeeName, 200) : null,
            WorkDate = workDate.Value,
            StatusText = statusText.Length > 0 ? Truncate(statusText, 150) : null,
            Status = ClassifyStatus(statusText),
            ClockIn = ReadTime(ws.Cell(row, columns.ClockIn)),
            ClockOut = ReadTime(ws.Cell(row, columns.ClockOut)),
            LocationName = location.Length > 0 && location != "-" ? Truncate(location, 200) : null,
            DepartmentName = department.Length > 0 ? Truncate(department, 300) : null,
            SourceRow = row,
            SourceFile = Truncate(fileName, 300),
            ImportedAtUtc = importedAt
        };
    }

    // =====================================================================
    //  التحليل القانوني والتخزين
    // =====================================================================

    /// <summary>
    /// تشغيل التحليل القانوني على صفوف جدول المرحلة وتخزين النتائج
    /// (يومية + أسبوعية + شهرية) وإرجاع ملخص المؤشرات.
    /// </summary>
    public async Task<PunchAnalysisSummary> AnalyzeAsync(
        int? graceMinutes = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rule = WeeklyRule;
        int grace = graceMinutes ?? rule.MorningGraceMinutes;
        var records = await _db.PunchRecords.AsNoTracking().ToListAsync(ct);

        if (records.Count == 0)
        {
            return EmptySummary(grace);
        }

        var shiftSchedule = await LoadShiftScheduleAsync(ct);
        var workApprovals = await LoadWorkApprovalsAsync(ct);
        var bundle = BuildAnalysis(records, grace, await LoadCalendarAsync(ct), rule, shiftSchedule, workApprovals);

        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE PunchDailyResults;", ct);
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE PunchWeeklyResults;", ct);
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE PunchMonthlyResults;", ct);

        await InsertInBatchesAsync(bundle.Daily, ct);
        await InsertInBatchesAsync(bundle.Weekly, ct);
        await InsertInBatchesAsync(bundle.Monthly, ct);

        // تسجيل القاعدة المطبَّقة (وبصمة وقتها) ليتتبّع المدقّق القيم التي بُنيت عليها النتائج.
        await _weeklyRules.MarkAnalyzedAsync(rule, ct);

        _logger.LogInformation(
            "اكتمل التحليل القانوني للبصمات: {Daily} حالة يومية، {Weekly} أسبوعاً، {Monthly} شهراً (حدّ التأخير {Grace} دقيقة، قاعدة 118/ج: {Rule}).",
            bundle.Daily.Count, bundle.Weekly.Count, bundle.Monthly.Count, grace, rule.Summary());

        return BuildSummary(records.Count, bundle.Daily, bundle.Weekly, bundle.Monthly, grace);
    }

    private async Task InsertInBatchesAsync<T>(IReadOnlyList<T> items, CancellationToken ct) where T : class
    {
        for (int i = 0; i < items.Count; i += InsertBatchSize)
        {
            var batch = items.Skip(i).Take(InsertBatchSize).ToList();
            await _db.BulkInsertAsync(batch, cancellationToken: ct);
        }
    }

    /// <summary>ملخص فارغ عند عدم وجود بيانات مُحلَّلة.</summary>
    private static PunchAnalysisSummary EmptySummary(int grace) => new(
        EmployeesAnalyzed: 0,
        RecordsAnalyzed: 0,
        PeriodFrom: null,
        PeriodTo: null,
        MorningGraceMinutes: grace,
        WorkingDays: 0,
        CompleteDays: 0,
        AbsentDays: 0,
        AbsentDaysOnWeekend: 0,
        IncompleteDays: 0,
        NoDataDays: 0,
        WeekendDays: 0,
        HolidayDays: 0,
        WeekendWorkDays: 0,
        HolidayWorkDays: 0,
        CalendarHolidays: 0,
        TotalLatenessMinutes: 0,
        TotalEarlyDepartureMinutes: 0,
        TotalMidDayGapMinutes: 0,
        LateIncidentDays: 0,
        EmployeesWithLateIncidents: 0,
        EmployeesWithArticle7Penalty: 0,
        TotalArticle7SalaryDays: 0,
        WeeksOver60Minutes: 0,
        EmployeesOver60Minutes: 0,
        TotalWeeklyLateMinutes: 0,
        TotalArticle118cDays: 0,
        DaysOver4Hours: 0,
        EmployeesOver4Hours: 0,
        TotalArticle118bDays: 0,
        TotalAnnualLeaveDays: 0,
        TotalSickLeaveDays: 0,
        TotalEmergencyPermissionDays: 0,
        TotalOfficialDutyDays: 0,
        TotalAbsenceForBonusDays: 0,
        TotalSalaryDeductionDays: 0,
        TotalBonusDeductionPercent: 0,
        EmployeesWithViolations: 0,
        ShiftDutyDays: 0,
        ShiftRestDays: 0,
        ShiftLeaveDays: 0,
        ShiftEmployees: 0,
        FlexibleDays: 0,
        FlexibleEmployees: 0,
        FlexibleMinutes: 0,
        OvertimeDays: 0,
        OvertimeEmployees: 0,
        OvertimeMinutes: 0,
        OvertimeRawMinutes: 0,
        OvertimeExcludedMinutes: 0,
        OvertimeNeedsApprovalDays: 0,
        OvertimeNeedsApprovalMinutes: 0,
        OvertimeWeekendDays: 0,
        OvertimeHolidayDays: 0,
        OvertimeHours: 0,
        EquivalentOvertimeHours: 0,
        EmployeesOverMonthlyOvertimeCap: 0,
        RunAtUtc: DateTime.UtcNow,
        HasData: false);
}
