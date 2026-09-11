using System.Globalization;
using System.Text.RegularExpressions;
using AttendanceApi.Audit;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// نظام الورديات (مثال: الحراسة التي تعمل بورديات 24 ساعة متواصلة):
/// استيراد «جدول الورديات الشهري» الصادر من مسؤول الورديات، عرضه وحذفه،
/// وتنزيل قالب جاهز لتعبئته + كشف الجدول المستورد — إضافةً إلى تجهيز قيود التحليل.
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>رموز الأعياد/العطل الرسمية في الجداول الشهرية.</summary>
    private static readonly string[] ShiftHolidayTokens =
        { "عطله رسمي", "عيد", "راس السنه", "مناسبه", "رسمي", "استقلال", "ميلاد", "national", "holiday" };

    /// <summary>رموز الراحة/عدم الدوام (مطابقة احتواء بعد توحيد الحروف).</summary>
    private static readonly string[] ShiftRestTokens =
        { "راحه", "استراحه", "راحه اسبوعيه", "بدون دوام", "لا دوام", "نهايه الاسبوع", "عطله", "جمعه", "سبت", "اوف" };

    /// <summary>رموز الراحة المختصرة (مطابقة تامة فقط: ر، ع...).</summary>
    private static readonly string[] ShiftRestExact = { "ر", "ع", "x", "off", "o" };

    /// <summary>رموز الإجازات (مطابقة احتواء).</summary>
    private static readonly string[] ShiftLeaveTokens =
    {
        "اجازه", "سنوي", "مرضي", "عارض", "طارئ", "طارى", "امومه", "وضع", "حج", "زواج", "وفاه",
        "بدون راتب", "دراس", "تفرغ", "استيداع", "امتياز", "اضطرار", "شهاده", "ممهوره",
        "leave", "annual", "sick", "vacation"
    };

    /// <summary>رموز الدورات والمهام الرسمية (مطابقة احتواء).</summary>
    private static readonly string[] ShiftTrainingTokens =
        { "دوره", "تدريب", "مهمه", "انتداب", "بعثه", "training", "workshop" };

    // ملاحظة: أي رمز آخر غير فارغ (ن، ل، ص، م، و، ش، 24، 12، 8، صباحي، مسائي...) يُعامل «يوم وردية دوام»،
    // ويُحتسب عدد ساعاته من الرقم المذكور في الرمز (24 ساعة افتراضياً من إعدادات نظام الورديات).

    // =====================================================================
    //  تحميل الجدول للتحليل + عرض الجدول + حذف دفعة استيراد
    // =====================================================================

    /// <summary>
    /// تحميل قيود «جدول الورديات الشهري» لتمريرها إلى محرّك التحليل
    /// (تعذّر القراءة لا يوقف التحليل — يُتابع بقواعد الدوام العامة).
    /// </summary>
    internal async Task<IReadOnlyList<ShiftScheduleEntry>> LoadShiftScheduleAsync(CancellationToken ct = default)
    {
        try
        {
            return await _db.ShiftScheduleEntries.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة «جدول الورديات الشهري» — سيُتابع التحليل بقواعد الدوام العامة.");
            return Array.Empty<ShiftScheduleEntry>();
        }
    }

    /// <summary>عرض «جدول الورديات الشهري»: إعدادات النظام + دفعات الاستيراد + عيّنة من القيود.</summary>
    public async Task<PunchShiftScheduleView> GetShiftScheduleAsync(
        string? batchKey = null,
        int sampleSize = 60,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rule = WeeklyRule;

        try
        {
            var key = string.IsNullOrWhiteSpace(batchKey) ? null : batchKey.Trim();

            var batches = await _db.ShiftScheduleBatches.AsNoTracking()
                .Where(b => key == null || b.BatchKey == key)
                .OrderByDescending(b => b.ImportedAtUtc)
                .ToListAsync(ct);

            var query = _db.ShiftScheduleEntries.AsNoTracking();
            if (key is not null)
            {
                query = query.Where(e => e.BatchKey == key);
            }

            int entries = await query.CountAsync(ct);
            int duty = await query.CountAsync(e => e.Kind == ShiftDayKind.Duty, ct);
            int rest = await query.CountAsync(e => e.Kind == ShiftDayKind.Rest, ct);
            int leave = await query.CountAsync(e => e.Kind == ShiftDayKind.Leave, ct);

            var departments = await query
                .Where(e => e.DepartmentName != null)
                .Select(e => e.DepartmentName!)
                .Distinct()
                .OrderBy(x => x)
                .Take(60)
                .ToListAsync(ct);

            var months = await query
                .Where(e => e.BatchKey != null)
                .Select(e => e.BatchKey!)
                .Distinct()
                .OrderByDescending(x => x)
                .Take(36)
                .ToListAsync(ct);

            var samples = await query
                .OrderByDescending(e => e.DutyDate)
                .ThenBy(e => e.JobNumber)
                .Take(Math.Clamp(sampleSize, 1, 500))
                .ToListAsync(ct);

            return new PunchShiftScheduleView(
                Rule: rule.Shift,
                RuleSummary: rule.Shift.Summary(),
                Enabled: rule.Shift.Enabled,
                Batches: batches.Count,
                Employees: await query.Select(e => e.JobNumber).Distinct().CountAsync(ct),
                Entries: entries,
                DutyDays: duty,
                RestDays: rest,
                LeaveDays: leave,
                OtherDays: Math.Max(0, entries - duty - rest - leave),
                PeriodFrom: await query.MinAsync(e => (DateOnly?)e.DutyDate, ct),
                PeriodTo: await query.MaxAsync(e => (DateOnly?)e.DutyDate, ct),
                Departments: departments,
                Months: months,
                BatchList: batches.Select(b => new PunchShiftScheduleBatchRow(
                    Id: b.Id,
                    BatchId: b.BatchId,
                    BatchKey: b.BatchKey,
                    FileName: b.FileName,
                    PeriodFrom: b.PeriodFrom.ToString("yyyy/MM/dd"),
                    PeriodTo: b.PeriodTo.ToString("yyyy/MM/dd"),
                    Rows: b.Rows,
                    Employees: b.Employees,
                    DutyDays: b.DutyDays,
                    RestDays: b.RestDays,
                    LeaveDays: b.LeaveDays,
                    Format: string.IsNullOrWhiteSpace(b.Format) ? "—" : b.Format!,
                    ImportedAtText: b.ImportedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"))).ToList(),
                Samples: samples.Select(e => new PunchShiftScheduleEntryRow(
                    JobNumber: e.JobNumber,
                    EmployeeName: e.EmployeeName,
                    DepartmentName: e.DepartmentName,
                    DateText: e.DutyDate.ToString("yyyy/MM/dd"),
                    DayName: PunchCalendar.DayName(e.DutyDate.DayOfWeek),
                    ShiftCode: e.ShiftCode,
                    KindText: ShiftKindText(e.Kind),
                    Hours: e.ShiftHours)).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة «جدول الورديات الشهري» من قاعدة البيانات.");
            return EmptyShiftView(rule);
        }
    }

    /// <summary>عرض فارغ لجدول الورديات (عند غياب البيانات أو تعذّر القراءة).</summary>
    private static PunchShiftScheduleView EmptyShiftView(PunchWeeklyRule rule) => new(
        Rule: rule.Shift,
        RuleSummary: rule.Shift.Summary(),
        Enabled: rule.Shift.Enabled,
        Batches: 0,
        Employees: 0,
        Entries: 0,
        DutyDays: 0,
        RestDays: 0,
        LeaveDays: 0,
        OtherDays: 0,
        PeriodFrom: null,
        PeriodTo: null,
        Departments: Array.Empty<string>(),
        Months: Array.Empty<string>(),
        BatchList: Array.Empty<PunchShiftScheduleBatchRow>(),
        Samples: Array.Empty<PunchShiftScheduleEntryRow>());

    /// <summary>حذف دفعة استيراد كاملة (قيود الجدول + سجلّ الدفعة) عند وجود خطأ في الملف.</summary>
    public async Task<bool> DeleteShiftBatchAsync(long id, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var batch = await _db.ShiftScheduleBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return false;
        }

        var entries = await _db.ShiftScheduleEntries.Where(e => e.BatchId == batch.BatchId).ToListAsync(ct);
        if (entries.Count > 0)
        {
            _db.ShiftScheduleEntries.RemoveRange(entries);
        }

        _db.ShiftScheduleBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "حُذفت دفعة جدول الورديات {Batch} ({Rows} قيداً) والملف {File}.",
            batch.BatchId, entries.Count, batch.FileName);

        return true;
    }

    /// <summary>
    /// استيراد «جدول الورديات الشهري» (.xlsx) الصادر من مسؤول الورديات.
    /// يدعم صيغتين: <b>شبكة شهرية</b> (عمود لكل يوم 1 — 31 برمز الوردية فيه)
    /// أو <b>جدولاً طويلاً</b> (الرقم الوظيفي | التاريخ | الوردية | الساعات اختيارياً).
    /// </summary>
    /// <param name="batchKey">مفتاح الفترة (سنة-شهر) للجدول؛ يُستنتج من الملف إن لم يُرسل.</param>
    /// <param name="replacePeriod">استبدال كل قيود الفترة المستوردة (وإلا فيُستبدل قيود الموظفين أنفسهم في الفترة).</param>
    public async Task<PunchShiftImportResult> ImportShiftScheduleAsync(
        Stream stream,
        string fileName,
        string? batchKey = null,
        int? year = null,
        int? month = null,
        bool replacePeriod = true,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rule = WeeklyRule;
        double defaultHours = Math.Clamp(
            rule.Shift.CycleHours, PunchShiftRule.MinCycleHours, PunchShiftRule.MaxCycleHours);

        var importedAt = DateTime.UtcNow;
        string batchId = Guid.NewGuid().ToString("N");
        string shortFile = Truncate(Path.GetFileName(fileName), 300);
        var entries = new List<ShiftScheduleEntry>(2048);
        var seen = new HashSet<(string Job, DateOnly Date)>();

        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();

        int headerRow = FindShiftHeaderRow(worksheet);
        if (headerRow == 0)
        {
            throw new InvalidOperationException(
                "لم يُعثر على ترويسة «جدول الورديات» (عمود «الرقم الوظيفي» مع أعمدة الأيام أو مع عمودي «التاريخ» و«الوردية»).");
        }

        int jobCol = FindColumn(worksheet, headerRow, "الرقم الوظيفي", "وظيفي", "رقم الموظف", "الرقم");
        if (jobCol == 0)
        {
            throw new InvalidOperationException("عمود «الرقم الوظيفي» غير موجود في ملف جدول الورديات.");
        }

        int nameCol = FindColumn(worksheet, headerRow, "الاسم", "اسم الموظف", "الموظف");
        int deptCol = FindColumn(worksheet, headerRow, "الاداره", "الدائره", "المديريه", "القسم", "الوحده", "الجهه");
        int dateCol = FindColumn(worksheet, headerRow, "التاريخ", "تاريخ اليوم", "تاريخ");
        int shiftCol = FindColumn(worksheet, headerRow, "الورديه", "الشفت", "نوع الدوام", "الرمز", "الحاله", "الدوام");
        int hoursCol = FindColumn(worksheet, headerRow, "الساعات", "عدد الساعات", "ساعات");

        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
        int lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? jobCol;

        string format;
        string? resolvedKey;

        if (dateCol > 0 && shiftCol > 0 && dateCol != jobCol && shiftCol != jobCol)
        {
            // ---- صيغة الجدول الطويل: صف لكل يوم ----
            format = "جدول طويل (صف لكل يوم)";

            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                ct.ThrowIfCancellationRequested();

                var job = ReadShiftText(worksheet.Cell(r, jobCol));
                if (job.Length == 0)
                {
                    continue;
                }

                var date = ReadDate(worksheet.Cell(r, dateCol))
                    ?? ParseLooseDate(worksheet.Cell(r, dateCol).GetFormattedString());
                if (date is null)
                {
                    continue;
                }

                var code = ReadShiftText(worksheet.Cell(r, shiftCol));
                if (code.Length == 0)
                {
                    continue;
                }

                AddShiftEntry(
                    entries, seen,
                    job,
                    nameCol > 0 ? ReadShiftText(worksheet.Cell(r, nameCol)) : string.Empty,
                    deptCol > 0 ? ReadShiftText(worksheet.Cell(r, deptCol)) : string.Empty,
                    date.Value,
                    code,
                    hoursCol > 0 ? ReadShiftNumber(worksheet.Cell(r, hoursCol)) : 0,
                    defaultHours);
            }

            resolvedKey = ShiftBatchKey(batchKey, entries);
        }
        else
        {
            // ---- صيغة الشبكة الشهرية: عمود لكل يوم ----
            format = "شبكة شهرية (أعمدة الأيام)";

            var dayColumns = new List<(int Column, int Day)>();
            for (int c = 1; c <= lastCol; c++)
            {
                if (c == jobCol || c == nameCol || c == deptCol || c == dateCol || c == shiftCol || c == hoursCol)
                {
                    continue;
                }

                int? day = DayNumberOf(worksheet.Cell(headerRow, c).GetFormattedString());
                if (day.HasValue)
                {
                    dayColumns.Add((c, day.Value));
                }
            }

            if (dayColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    "لم يُعثر على أعمدة أيام الشهر (1 — 31) ولا على عمودي «التاريخ» و«الوردية» في الملف.");
            }

            var period = ResolveShiftPeriod(worksheet, headerRow, lastCol, year, month, batchKey, fileName);
            int daysInMonth = DateTime.DaysInMonth(period.Year, period.Month);
            resolvedKey = $"{period.Year:0000}-{period.Month:00}";

            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                ct.ThrowIfCancellationRequested();

                var job = ReadShiftText(worksheet.Cell(r, jobCol));
                if (job.Length == 0 || job.Equals("الرقم الوظيفي", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = nameCol > 0 ? ReadShiftText(worksheet.Cell(r, nameCol)) : string.Empty;
                var dept = deptCol > 0 ? ReadShiftText(worksheet.Cell(r, deptCol)) : string.Empty;
                double rowHours = hoursCol > 0 ? ReadShiftNumber(worksheet.Cell(r, hoursCol)) : 0;

                foreach (var (column, day) in dayColumns)
                {
                    if (day > daysInMonth)
                    {
                        continue;
                    }

                    var code = ReadShiftText(worksheet.Cell(r, column));
                    if (code.Length == 0)
                    {
                        continue;
                    }

                    AddShiftEntry(
                        entries, seen, job, name, dept,
                        new DateOnly(period.Year, period.Month, day),
                        code, rowHours, defaultHours);
                }
            }
        }

        return await PersistShiftImportAsync(
            entries, batchId, resolvedKey, batchKey, shortFile, format, replacePeriod, importedAt, ct);
    }

    /// <summary>
    /// إدراج قيود الجدول بعد استبدال المدى المتأثر (الفترة كاملة أو قيود الموظفين المستوردين فقط)،
    /// ثم تسجيل «دفعة الاستيراد» وإرجاع نتيجتها.
    /// </summary>
    private async Task<PunchShiftImportResult> PersistShiftImportAsync(
        List<ShiftScheduleEntry> entries,
        string batchId,
        string? resolvedKey,
        string? requestedKey,
        string shortFile,
        string format,
        bool replacePeriod,
        DateTime importedAt,
        CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "لم يُقرأ أي قيد وردية من الملف — تأكد من كتابة رمز الوردية (ن/ل/ر/24...) في خانات الأيام.");
        }

        var key = Truncate(
            string.IsNullOrWhiteSpace(requestedKey)
                ? resolvedKey ?? string.Empty
                : requestedKey!.Trim(),
            40);

        var from = entries.Min(e => e.DutyDate);
        var to = entries.Max(e => e.DutyDate);
        var jobs = entries.Select(e => e.JobNumber).Distinct(StringComparer.Ordinal).ToList();

        // ---- الاستبدال: حذف قيود الفترة كاملة (أو قيود الموظفين أنفسهم فقط) قبل الإدراج ----
        int replaced;
        if (replacePeriod)
        {
            var existing = await _db.ShiftScheduleEntries
                .Where(e => e.DutyDate >= from && e.DutyDate <= to)
                .ToListAsync(ct);

            replaced = existing.Count;
            if (existing.Count > 0)
            {
                _db.ShiftScheduleEntries.RemoveRange(existing);
            }

            var batches = await _db.ShiftScheduleBatches
                .Where(b => b.PeriodFrom >= from && b.PeriodTo <= to)
                .ToListAsync(ct);

            if (batches.Count > 0)
            {
                _db.ShiftScheduleBatches.RemoveRange(batches);
            }
        }
        else
        {
            var existing = await _db.ShiftScheduleEntries
                .Where(e => jobs.Contains(e.JobNumber) && e.DutyDate >= from && e.DutyDate <= to)
                .ToListAsync(ct);

            replaced = existing.Count;
            if (existing.Count > 0)
            {
                _db.ShiftScheduleEntries.RemoveRange(existing);
            }
        }

        await _db.SaveChangesAsync(ct);

        foreach (var entry in entries)
        {
            entry.BatchId = batchId;
            entry.BatchKey = key.Length > 0 ? key : null;
            entry.SourceFile = shortFile;
            entry.ImportedAtUtc = importedAt;
        }

        await _db.BulkInsertAsync(entries, cancellationToken: ct);

        int duty = entries.Count(e => e.Kind == ShiftDayKind.Duty);
        int rest = entries.Count(e => e.Kind == ShiftDayKind.Rest);
        int leave = entries.Count(e => e.Kind == ShiftDayKind.Leave);

        _db.ShiftScheduleBatches.Add(new ShiftScheduleBatch
        {
            BatchId = batchId,
            BatchKey = key.Length > 0 ? key : null,
            FileName = shortFile,
            PeriodFrom = from,
            PeriodTo = to,
            Rows = entries.Count,
            Employees = jobs.Count,
            DutyDays = duty,
            RestDays = rest,
            LeaveDays = leave,
            Format = Truncate(format, 60),
            ImportedAtUtc = importedAt
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "اكتمل استيراد جدول الورديات {File} ({Format}): {Rows} قيداً لـ {Employees} موظفاً، الفترة {From} → {To}، حُذف {Replaced} قيداً سابقاً.",
            shortFile, format, entries.Count, jobs.Count, from, to, replaced);

        return new PunchShiftImportResult(
            FileName: shortFile,
            BatchId: batchId,
            BatchKey: key.Length > 0 ? key : null,
            Format: format,
            FirstDate: from,
            LastDate: to,
            Rows: entries.Count,
            Employees: jobs.Count,
            DutyDays: duty,
            RestDays: rest,
            LeaveDays: leave,
            Replaced: replaced,
            ImportedAtUtc: importedAt);
    }

    // =====================================================================
    //  أدوات قراءة جدول الورديات (الترويسة، أعمدة الأيام، الفترة، الرموز)
    // =====================================================================

    /// <summary>البحث عن صف ترويسة «جدول الورديات» (صف يحمل «الرقم الوظيفي» أو «الاسم» مع عمود أيام/تاريخ).</summary>
    private static int FindShiftHeaderRow(IXLWorksheet ws)
    {
        int lastRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 30);
        int lastCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 60);

        for (int r = 1; r <= lastRow; r++)
        {
            for (int c = 1; c <= lastCol; c++)
            {
                var text = Normalize(ws.Cell(r, c).GetFormattedString());
                if (text.Contains("الرقم الوظيفي", StringComparison.Ordinal)
                    || text.Contains("وظيفي", StringComparison.Ordinal))
                {
                    return r;
                }
            }
        }

        // صيغة بديلة: صف فيه «الاسم» أو «الموظف» مع عمود يوم (1 — 31) أو عمود «التاريخ».
        for (int r = 1; r <= lastRow; r++)
        {
            bool hasName = false, hasDay = false;
            for (int c = 1; c <= lastCol; c++)
            {
                var text = Normalize(ws.Cell(r, c).GetFormattedString());
                if (text.Contains("الاسم", StringComparison.Ordinal) || text.Contains("الموظف", StringComparison.Ordinal))
                {
                    hasName = true;
                }

                if (text.Contains("التاريخ", StringComparison.Ordinal) || DayNumberOf(text).HasValue)
                {
                    hasDay = true;
                }
            }

            if (hasName && hasDay)
            {
                return r;
            }
        }

        return 0;
    }

    /// <summary>رقم اليوم من ترويسة عمود (1 — 31) أو من تاريخ كامل، أو null إن لم يكن عمود يوم.</summary>
    private static int? DayNumberOf(string? header)
    {
        var text = NormalizeDigits(Normalize(header));
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("ساع", StringComparison.Ordinal)
            || text.Contains("ملاحظ", StringComparison.Ordinal)
            || text.Contains("اسم", StringComparison.Ordinal)
            || text.Contains("وظيفي", StringComparison.Ordinal)
            || text.Contains("ادار", StringComparison.Ordinal)
            || text.Contains("قسم", StringComparison.Ordinal)
            || text.Contains("رمز", StringComparison.Ordinal))
        {
            return null;
        }

        if (text.Contains('/') || text.Contains('-') || text.Contains('.'))
        {
            var normalized = text.Replace('-', '/').Replace('.', '/');
            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt.Day;
            }
        }

        var match = Regex.Match(text, @"\d{1,2}");
        return match.Success
            && int.TryParse(match.Value, out int day)
            && day is >= 1 and <= 31
                ? day
                : null;
    }

    /// <summary>
    /// تحديد شهر/سنة «الشبكة الشهرية»: من الطلب، ثم من ترويسة الملف، ثم من مفتاح الفترة أو اسم الملف.
    /// </summary>
    private static (int Year, int Month) ResolveShiftPeriod(
        IXLWorksheet ws,
        int headerRow,
        int lastCol,
        int? year,
        int? month,
        string? batchKey,
        string fileName)
    {
        if (year is >= 2000 and <= 2100 && month is >= 1 and <= 12)
        {
            return (year.Value, month.Value);
        }

        for (int r = 1; r <= headerRow; r++)
        {
            for (int c = 1; c <= Math.Min(lastCol, 60); c++)
            {
                var text = NormalizeDigits(Normalize(ws.Cell(r, c).GetFormattedString()));
                if (text.Length == 0)
                {
                    continue;
                }

                var ymd = Regex.Match(text, @"(20\d{2})\s*[/\-\.]\s*(\d{1,2})");
                if (ymd.Success && int.TryParse(ymd.Groups[2].Value, out int m1) && m1 is >= 1 and <= 12)
                {
                    return (int.Parse(ymd.Groups[1].Value), m1);
                }

                var dmy = Regex.Match(text, @"(\d{1,2})\s*[/\-\.]\s*(20\d{2})");
                if (dmy.Success && int.TryParse(dmy.Groups[1].Value, out int m2) && m2 is >= 1 and <= 12)
                {
                    return (int.Parse(dmy.Groups[2].Value), m2);
                }

                var loose = text.Replace('-', '/').Replace('.', '/');
                if (DateTime.TryParse(loose, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    return (dt.Year, dt.Month);
                }
            }
        }

        foreach (var source in new[] { batchKey, fileName })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var ymd = Regex.Match(source, @"(20\d{2})\s*[-_/\.]?\s*(\d{1,2})");
            if (ymd.Success && int.TryParse(ymd.Groups[2].Value, out int m1) && m1 is >= 1 and <= 12)
            {
                return (int.Parse(ymd.Groups[1].Value), m1);
            }

            var dmy = Regex.Match(source, @"(\d{1,2})\s*[-_/\.]\s*(20\d{2})");
            if (dmy.Success && int.TryParse(dmy.Groups[1].Value, out int m2) && m2 is >= 1 and <= 12)
            {
                return (int.Parse(dmy.Groups[2].Value), m2);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        return (today.Year, today.Month);
    }

    /// <summary>قراءة نص خلية من جدول الورديات (مع تجاهل رموز «لا قيد»).</summary>
    private static string ReadShiftText(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return string.Empty;
        }

        string text;
        if (cell.DataType == XLDataType.Number)
        {
            double value = cell.GetDouble();
            text = Math.Abs(value % 1) < 0.000001
                ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.######", CultureInfo.InvariantCulture);
        }
        else
        {
            text = Normalize(cell.GetFormattedString());
        }

        return text is "-" or "_" or "—" or "–" or "." or "*" or "#" ? string.Empty : text;
    }

    /// <summary>قراءة عدد ساعات الوردية من خلية (0 = تُعتمد ساعات إعدادات النظام).</summary>
    private static double ReadShiftNumber(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return 0;
        }

        if (cell.DataType == XLDataType.Number)
        {
            return Math.Clamp(cell.GetDouble(), 0, 24);
        }

        return ParseShiftHours(cell.GetFormattedString());
    }

    /// <summary>استخراج ساعات الوردية من نص رمزها (24، 12، 8، «24 ساعة»...)، و0 إن لم تُذكر.</summary>
    internal static double ParseShiftHours(string? text)
    {
        var normalized = NormalizeDigits(Normalize(text));
        var match = Regex.Match(normalized, @"(\d{1,2}(?:[.,]\d{1,2})?)");
        if (!match.Success)
        {
            return 0;
        }

        var value = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double hours) && hours > 0
            ? Math.Min(hours, 24)
            : 0;
    }

    /// <summary>
    /// تصنيف رمز الوردية إلى يوم دوام/راحة/إجازة/عطلة/دورة —
    /// والرموز غير المعروفة تُعامل «يوم دوام» مع إظهار الرمز في الملاحظات للمراجعة.
    /// </summary>
    internal static ShiftDayKind ClassifyShiftCode(string? rawText, out double hours)
    {
        hours = 0;

        var text = NormalizeDigits(Normalize(rawText));
        if (text.Length == 0)
        {
            return ShiftDayKind.Unknown;
        }

        if (text.All(ch => ch is '-' or '_' or '—' or '–' or '.' or '*' or '0' or '·'))
        {
            return ShiftDayKind.Unknown;
        }

        var lower = text.ToLowerInvariant();

        if (ContainsAny(lower, ShiftHolidayTokens))
        {
            return ShiftDayKind.Holiday;
        }

        if (ContainsAny(lower, ShiftRestTokens) || ShiftRestExact.Contains(lower, StringComparer.Ordinal))
        {
            return ShiftDayKind.Rest;
        }

        if (ContainsAny(lower, ShiftLeaveTokens))
        {
            return ShiftDayKind.Leave;
        }

        if (ContainsAny(lower, ShiftTrainingTokens))
        {
            return ShiftDayKind.Training;
        }

        hours = ParseShiftHours(text);
        return ShiftDayKind.Duty;
    }

    /// <summary>هل النص يحتوي أي من الرموز المذكورة؟</summary>
    private static bool ContainsAny(string text, string[] tokens) =>
        tokens.Any(t => text.Contains(t, StringComparison.Ordinal));

    /// <summary>إضافة قيد يوم واحد للجدول (مع تجاهل الرموز الفارغة والمكرّرة لنفس الموظف واليوم).</summary>
    private static void AddShiftEntry(
        List<ShiftScheduleEntry> entries,
        HashSet<(string Job, DateOnly Date)> seen,
        string jobNumber,
        string name,
        string department,
        DateOnly date,
        string code,
        double hours,
        double defaultHours)
    {
        var kind = ClassifyShiftCode(code, out double parsed);
        if (kind == ShiftDayKind.Unknown || !seen.Add((jobNumber, date)))
        {
            return;
        }

        entries.Add(new ShiftScheduleEntry
        {
            JobNumber = Truncate(NormalizeDigits(Normalize(jobNumber)), 50),
            EmployeeName = name.Length > 0 ? Truncate(name, 200) : null,
            DepartmentName = department.Length > 0 ? Truncate(department, 300) : null,
            DutyDate = date,
            ShiftCode = Truncate(code, 40),
            Kind = kind,
            // ساعات الوردية تُحتسب لأيام الدوام فقط (أيام الراحة/الإجازة/العطلة بلا ساعات دوام).
            ShiftHours = kind == ShiftDayKind.Duty
                ? (parsed > 0 ? parsed : (hours > 0 ? hours : defaultHours))
                : 0,
            Notes = kind == ShiftDayKind.Duty ? null : ShiftKindText(kind),
            BatchId = "pending",
            ImportedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>مفتاح فترة الجدول (سنة-شهر) من الطلب أو من مدى تواريخ القيود.</summary>
    private static string? ShiftBatchKey(string? requested, IReadOnlyList<ShiftScheduleEntry> entries)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var from = entries.Min(e => e.DutyDate);
        var to = entries.Max(e => e.DutyDate);

        return from.Year == to.Year && from.Month == to.Month
            ? $"{from.Year:0000}-{from.Month:00}"
            : $"{from:yyyy-MM}_{to:yyyy-MM}";
    }

    // =====================================================================
    //  قالب جدول الورديات + كشف الجدول المستورد (Excel)
    // =====================================================================

    /// <summary>
    /// تنزيل «قالب جدول الورديات الشهري» (.xlsx) جاهزاً لتعبئته من مسؤول الورديات:
    /// الموظفون (من بصمات الشهر المطلوب، أو من كل البصمات إن لم توجد) + أعمدة أيام الشهر + دليل الرموز.
    /// </summary>
    public async Task<byte[]> BuildShiftTemplateWorkbookAsync(
        int year,
        int month,
        string? department = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        int safeYear = year is >= 2000 and <= 2100 ? year : DateTime.Now.Year;
        int safeMonth = month is >= 1 and <= 12 ? month : DateTime.Now.Month;
        var rule = WeeklyRule;
        var employees = await ReadShiftTemplateEmployeesAsync(safeYear, safeMonth, department, ct);

        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("جدول الورديات");

        ws.Cell(1, 1).Value = $"جدول الورديات الشهري — {safeYear:0000}/{safeMonth:00}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        ws.Cell(2, 1).Value = "يُعبَّأ الرمز في خانة كل يوم (ن = نهار، ل = ليل، ر = راحة، 24 = وردية 24 ساعة، "
            + "إجازة، عطلة رسمية، دورة)، ثم يُستورد الملف من شاشة «جدول الورديات الشهري» في نظام تحليل البصمات.";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#475569");

        int headerRow = 4;
        ws.Cell(headerRow, 1).Value = "الرقم الوظيفي";
        ws.Cell(headerRow, 2).Value = "الاسم";
        ws.Cell(headerRow, 3).Value = "الإدارة";

        int days = DateTime.DaysInMonth(safeYear, safeMonth);
        for (int d = 1; d <= days; d++)
        {
            int col = 3 + d;
            ws.Cell(headerRow, col).Value = d;
            ws.Cell(headerRow + 1, col).Value =
                PunchCalendar.DayName(new DateOnly(safeYear, safeMonth, d).DayOfWeek).Substring(0, 1);
        }

        int headerLastCol = 3 + days;
        var headerRange = ws.Range(headerRow, 1, headerRow, headerLastCol);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        int row = headerRow + 2;
        foreach (var employee in employees)
        {
            ws.Cell(row, 1).Value = employee.JobNumber;
            ws.Cell(row, 2).Value = employee.Name ?? string.Empty;
            ws.Cell(row, 3).Value = employee.Department ?? string.Empty;
            row++;
        }

        ws.Column(1).Width = 16;
        ws.Column(2).Width = 28;
        ws.Column(3).Width = 30;
        for (int d = 1; d <= days; d++)
        {
            ws.Column(3 + d).Width = 5;
        }

        // ---- ورقة دليل الرموز (لا تؤثر على الاستيراد: تُقرأ أول ورقة فقط) ----
        var legend = workbook.AddWorksheet("دليل الرموز");
        legend.Cell(1, 1).Value = "دليل رموز جدول الورديات المعتمدة في التحليل";
        legend.Cell(1, 1).Style.Font.Bold = true;
        legend.Cell(3, 1).Value = "الرمز";
        legend.Cell(3, 2).Value = "المعنى في التحليل";
        legend.Cell(3, 1).Style.Font.Bold = true;
        legend.Cell(3, 2).Style.Font.Bold = true;

        var legendRows = new (string Code, string Meaning)[]
        {
            ("ن / ل / ص / م / و", "وردية دوام (نهار/ليل/صباحي/مسائي) — لا تُقارَن بأوقات الدوام الرسمي"),
            ("24 / 12 / 8", "وردية دوام بعدد الساعات المذكور"),
            ("ر / راحة / ع / استراحة", "راحة وفق الجدول — لا تُحتسب غياباً"),
            ("إجازة / سنوية / مرضية / طارئة / أمومة / حج / زواج / وفاة", "إجازة وفق الجدول — لا تُحتسب غياباً"),
            ("عطلة رسمية / عيد", "عطلة وفق الجدول"),
            ("دورة / تدريب / مهمة / انتداب", "دورة أو مهمة رسمية وفق الجدول"),
            ("- أو خانة فارغة", "لا قيد لهذا اليوم")
        };

        int legendRow = 4;
        foreach (var (code, meaning) in legendRows)
        {
            legend.Cell(legendRow, 1).Value = code;
            legend.Cell(legendRow, 2).Value = meaning;
            legendRow++;
        }

        legend.Column(1).Width = 45;
        legend.Column(2).Width = 70;
        legend.Cell(legendRow + 1, 1).Value = rule.Shift.Summary();
        legend.Cell(legendRow + 1, 1).Style.Font.FontColor = XLColor.FromHtml("#475569");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>الموظفون المقترحون في قالب الجدول (من بصمات الشهر، وإلا من كل البصمات).</summary>
    private async Task<List<(string JobNumber, string? Name, string? Department)>> ReadShiftTemplateEmployeesAsync(
        int year,
        int month,
        string? department,
        CancellationToken ct)
    {
        var rows = await _db.PunchRecords.AsNoTracking()
            .Where(r => r.WorkDate.Year == year && r.WorkDate.Month == month)
            .Select(r => new { r.JobNumber, r.EmployeeName, r.DepartmentName })
            .Distinct()
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            rows = await _db.PunchRecords.AsNoTracking()
                .Select(r => new { r.JobNumber, r.EmployeeName, r.DepartmentName })
                .Distinct()
                .Take(50000)
                .ToListAsync(ct);
        }

        return rows
            .GroupBy(r => r.JobNumber, StringComparer.Ordinal)
            .Select(g => (
                JobNumber: g.Key,
                Name: MostFrequent(g.Select(x => x.EmployeeName)),
                Department: MostFrequentDepartment(g.Select(x => x.DepartmentName))))
            .Where(e => string.IsNullOrWhiteSpace(department)
                        || PunchDepartmentMatcher.Matches(department, e.Department))
            .OrderBy(e => e.Department ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(e => e.JobNumber, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// تنزيل «كشف جدول الورديات المستورد» (.xlsx) للتدقيق: كل قيد يوم لموظف مع تصنيفه وساعاته ودفعته.
    /// </summary>
    public async Task<byte[]> BuildShiftScheduleWorkbookAsync(
        string? batchKey = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rule = WeeklyRule;
        var key = string.IsNullOrWhiteSpace(batchKey) ? null : batchKey.Trim();

        var query = _db.ShiftScheduleEntries.AsNoTracking();
        if (key is not null)
        {
            query = query.Where(e => e.BatchKey == key);
        }

        var entries = await query
            .OrderBy(e => e.DutyDate)
            .ThenBy(e => e.JobNumber)
            .Take(200_000)
            .ToListAsync(ct);

        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("جدول الورديات");

        ws.Cell(1, 1).Value = key is null
            ? "جدول الورديات المستورد — كل الفترات"
            : $"جدول الورديات المستورد — الفترة {key}";
        ws.Cell(1, 1).Style.Font.Bold = true;

        ws.Cell(2, 1).Value = rule.Shift.Summary();
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#475569");

        string[] headers =
        {
            "الرقم الوظيفي", "الاسم", "الإدارة", "التاريخ", "اليوم",
            "رمز الوردية", "التصنيف", "الساعات", "دفعة الاستيراد", "الملف"
        };

        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++)
        {
            ws.Cell(headerRow, c + 1).Value = headers[c];
        }

        var headerRange = ws.Range(headerRow, 1, headerRow, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e293b");
        headerRange.Style.Font.FontColor = XLColor.White;

        int row = headerRow + 1;
        foreach (var entry in entries)
        {
            ws.Cell(row, 1).Value = entry.JobNumber;
            ws.Cell(row, 2).Value = entry.EmployeeName ?? string.Empty;
            ws.Cell(row, 3).Value = entry.DepartmentName ?? string.Empty;
            ws.Cell(row, 4).Value = entry.DutyDate.ToString("yyyy/MM/dd");
            ws.Cell(row, 5).Value = PunchCalendar.DayName(entry.DutyDate.DayOfWeek);
            ws.Cell(row, 6).Value = entry.ShiftCode;
            ws.Cell(row, 7).Value = ShiftKindText(entry.Kind);
            ws.Cell(row, 8).Value = entry.ShiftHours;
            ws.Cell(row, 9).Value = entry.BatchKey ?? entry.BatchId;
            ws.Cell(row, 10).Value = entry.SourceFile ?? string.Empty;
            row++;
        }

        ws.Cell(row + 1, 1).Value = $"إجمالي القيود: {entries.Count} | الموظفون: "
            + entries.Select(e => e.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row + 1, 1).Style.Font.Bold = true;

        ws.Columns(1, 10).AdjustToContents(5, 60);
        ws.SheetView.FreezeRows(headerRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>النص العربي لتصنيف يوم الوردية.</summary>
    internal static string ShiftKindText(ShiftDayKind kind) => kind switch
    {
        ShiftDayKind.Duty => "وردية دوام",
        ShiftDayKind.Rest => "راحة",
        ShiftDayKind.Leave => "إجازة",
        ShiftDayKind.Holiday => "عطلة",
        ShiftDayKind.Training => "دورة/مهمة",
        _ => "غير محدد"
    };
}
