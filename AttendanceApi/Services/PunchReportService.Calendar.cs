using System.Globalization;
using AttendanceApi.Audit;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>صف عطلة في واجهة «تقويم العطل» (من الكتالوج المدمج و/أو من قاعدة البيانات).</summary>
public sealed record PunchHolidayRow(
    int Id,
    DateOnly Date,
    string DayName,
    string Name,
    HolidayKind Kind,
    string KindText,
    bool InDatabase,
    bool IsCancelled,
    string? Source,
    int PunchedEmployees,
    int WorkedMinutes)
{
    public string DateText => Date.ToString("yyyy/MM/dd");
}

/// <summary>ملخص تقويم العطل المعتمد في تحليل الحضور.</summary>
public sealed record PunchCalendarView(
    int TotalHolidays,
    int OfficialHolidays,
    int IslamicHolidays,
    int ChristianHolidays,
    int NationalOccasions,
    int DatabaseHolidays,
    int CancelledHolidays,
    IReadOnlyList<string> WeekendDays,
    bool UseBuiltInCatalog,
    DateOnly? CatalogFrom,
    DateOnly? CatalogTo,
    IReadOnlyList<PunchHolidayRow> Holidays);

/// <summary>
/// تقويم العطل الرسمية والدينية: القراءة من قاعدة البيانات (OfficialHolidays)
/// مدمجةً مع الكتالوج المدمج، والإضافة والتعديل والإلغاء والاستيراد من Excel والتصدير.
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>تحميل تقويم العطل المعتمد: عطل قاعدة البيانات (غير الملغاة) + الكتالوج المدمج.</summary>
    internal async Task<PunchCalendar> LoadCalendarAsync(CancellationToken ct = default)
    {
        var databaseHolidays = await ReadDatabaseHolidaysAsync(ct);
        var cancelled = await ReadCancelledDatesAsync(ct);
        return PunchCalendar.Build(databaseHolidays, _options, cancelled);
    }

    /// <summary>قراءة التواريخ المستبعدة صراحةً (عطل مُلغاة) لمنع إعادة اعتمادها من الكتالوج المدمج.</summary>
    private async Task<HashSet<DateOnly>> ReadCancelledDatesAsync(CancellationToken ct)
    {
        try
        {
            var dates = await _db.OfficialHolidays.AsNoTracking()
                .Where(h => h.IsCancelled)
                .Select(h => h.HolidayDate)
                .ToListAsync(ct);

            return new HashSet<DateOnly>(dates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة العطل المستبعدة من قاعدة البيانات.");
            return new HashSet<DateOnly>();
        }
    }

    /// <summary>قراءة عطل قاعدة البيانات النشطة بصيغة تقويم (مع تجاهل الملغاة).</summary>
    private async Task<List<PunchHoliday>> ReadDatabaseHolidaysAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _db.OfficialHolidays.AsNoTracking()
                .Where(h => !h.IsCancelled && h.Description != null)
                .Select(h => new { h.HolidayDate, h.Description, h.Kind, h.Source })
                .ToListAsync(ct);

            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Description))
                .GroupBy(r => r.HolidayDate)
                .Select(g => g.OrderBy(r => r.Source).Last())
                .Select(r => new PunchHoliday(
                    r.HolidayDate,
                    r.Description!.Trim(),
                    r.Kind,
                    string.IsNullOrWhiteSpace(r.Source) ? "قاعدة البيانات" : r.Source!))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "تعذّر قراءة تقويم العطل من قاعدة البيانات — سيُعتمد الكتالوج المدمج فقط.");
            return new List<PunchHoliday>();
        }
    }

    /// <summary>عرض تقويم العطل المعتمد (الكتالوج المدمج + قاعدة البيانات) مع أثر الدوام فيه.</summary>
    public async Task<PunchCalendarView> GetCalendarAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var calendar = await LoadCalendarAsync(ct);
        var databaseRows = await ReadDatabaseRowsAsync(ct);

        var byDate = new Dictionary<DateOnly, OfficialHoliday>();
        foreach (var row in databaseRows.OrderBy(r => r.Id))
        {
            byDate[row.HolidayDate] = row;
        }

        var punchStats = await ReadHolidayPunchStatsAsync(ct);

        var dates = new SortedSet<DateOnly>(byDate.Keys);
        foreach (var holiday in calendar.Holidays)
        {
            dates.Add(holiday.Date);
        }

        var rows = new List<PunchHolidayRow>(dates.Count);

        foreach (var date in dates)
        {
            byDate.TryGetValue(date, out var dbRow);
            calendar.TryGetHoliday(date, out var catalogHoliday);

            bool cancelled = dbRow?.IsCancelled == true && catalogHoliday is null;
            string name = dbRow is not null && !dbRow.IsCancelled
                ? dbRow.Description
                : cancelled
                    ? "عطلة ملغاة (غير معتمدة لهذا العام)"
                    : catalogHoliday!.Name;

            var kind = dbRow is not null && !dbRow.IsCancelled ? dbRow.Kind : catalogHoliday?.Kind ?? HolidayKind.Official;

            int punched = 0, workedMinutes = 0;
            if (punchStats.TryGetValue(date, out var stat))
            {
                punched = stat.Punched;
                workedMinutes = stat.Minutes;
            }

            rows.Add(new PunchHolidayRow(
                Id: dbRow?.Id ?? 0,
                Date: date,
                DayName: PunchCalendar.DayName(date.DayOfWeek),
                Name: name,
                Kind: kind,
                KindText: cancelled ? "ملغاة" : PunchCalendar.KindText(kind),
                InDatabase: dbRow is not null,
                IsCancelled: cancelled,
                Source: dbRow?.Source ?? catalogHoliday?.Source,
                PunchedEmployees: punched,
                WorkedMinutes: workedMinutes));
        }

        return new PunchCalendarView(
            TotalHolidays: rows.Count(r => !r.IsCancelled),
            OfficialHolidays: rows.Count(r => !r.IsCancelled && r.Kind == HolidayKind.Official),
            IslamicHolidays: rows.Count(r => !r.IsCancelled && r.Kind == HolidayKind.ReligiousIslamic),
            ChristianHolidays: rows.Count(r => !r.IsCancelled && r.Kind == HolidayKind.ReligiousChristian),
            NationalOccasions: rows.Count(r => !r.IsCancelled && r.Kind == HolidayKind.NationalOccasion),
            DatabaseHolidays: databaseRows.Count(r => !r.IsCancelled),
            CancelledHolidays: rows.Count(r => r.IsCancelled),
            WeekendDays: calendar.WeekendDays.Select(PunchCalendar.DayName).ToList(),
            UseBuiltInCatalog: _options.UseBuiltInHolidayCalendar,
            CatalogFrom: JordanHolidayCatalog.FirstDate,
            CatalogTo: JordanHolidayCatalog.LastDate,
            Holidays: rows);
    }

    /// <summary>إحصاء الدوام الفعلي في أيام العطل (عدد الموظفين ودقائق العمل) من نتائج التحليل.</summary>
    private async Task<Dictionary<DateOnly, (int Punched, int Minutes)>> ReadHolidayPunchStatsAsync(
        CancellationToken ct)
    {
        try
        {
            var stats = await _db.PunchDailyResults.AsNoTracking()
                .Where(d => d.IsWorkOnHoliday)
                .GroupBy(d => d.WorkDate)
                .Select(g => new { Date = g.Key, Punched = g.Count(), Minutes = g.Sum(x => x.WorkedMinutes) })
                .ToListAsync(ct);

            return stats.ToDictionary(x => x.Date, x => (x.Punched, x.Minutes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر حساب إحصاء الدوام في أيام العطل من نتائج التحليل.");
            return new Dictionary<DateOnly, (int, int)>();
        }
    }

    /// <summary>قراءة كل صفوف جدول العطل (بما فيها الملغاة) لبناء واجهة التقويم.</summary>
    private async Task<List<OfficialHoliday>> ReadDatabaseRowsAsync(CancellationToken ct)
    {
        try
        {
            return await _db.OfficialHolidays.AsNoTracking()
                .OrderBy(h => h.HolidayDate)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة جدول العطل من قاعدة البيانات.");
            return new List<OfficialHoliday>();
        }
    }

    /// <summary>إضافة عطلة أو تحديثها (يُلغى أي استبعاد سابق لنفس التاريخ).</summary>
    public async Task<bool> SaveHolidayAsync(
        DateOnly date,
        string name,
        HolidayKind kind,
        string? source = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var normalizedName = Normalize(name);
        if (normalizedName.Length == 0)
        {
            return false;
        }

        string src = Truncate(string.IsNullOrWhiteSpace(source) ? "إدخال يدوي" : source!, 100);
        var row = await _db.OfficialHolidays.FirstOrDefaultAsync(h => h.HolidayDate == date, ct);

        if (row is null)
        {
            _db.OfficialHolidays.Add(new OfficialHoliday
            {
                HolidayDate = date,
                Description = Truncate(normalizedName, 300),
                Kind = kind,
                Source = src,
                IsCancelled = false,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            row.Description = Truncate(normalizedName, 300);
            row.Kind = kind;
            row.Source = src;
            row.IsCancelled = false;
            row.CreatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// حذف عطلة مسجّلة في قاعدة البيانات، أو استبعاد عطلة من الكتالوج المدمج
    /// (بإدراج قيد إلغاء يمنع اعتمادها في التقويم مع إمكانية استعادتها لاحقاً).
    /// </summary>
    public async Task<bool> RemoveHolidayAsync(DateOnly date, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var row = await _db.OfficialHolidays.FirstOrDefaultAsync(h => h.HolidayDate == date, ct);
        bool inCatalog = JordanHolidayCatalog.Holidays.Any(h => h.Date == date);

        if (row is not null && !inCatalog)
        {
            _db.OfficialHolidays.Remove(row);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (inCatalog)
        {
            if (row is null)
            {
                _db.OfficialHolidays.Add(new OfficialHoliday
                {
                    HolidayDate = date,
                    Description = "عطلة ملغاة (غير معتمدة لهذا العام)",
                    Kind = HolidayKind.NationalOccasion,
                    Source = "استبعاد يدوي",
                    IsCancelled = true,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                row.IsCancelled = true;
                row.Source = "استبعاد يدوي";
                row.CreatedAtUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }

    /// <summary>استعادة كتالوج العطل المدمج في قاعدة البيانات (وإلغاء أي استبعاد سابق).</summary>
    public async Task<int> RestoreDefaultHolidaysAsync(int? year = null, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var existing = await _db.OfficialHolidays.ToListAsync(ct);
        var byDate = new Dictionary<DateOnly, OfficialHoliday>();
        foreach (var row in existing.OrderBy(r => r.Id))
        {
            byDate[row.HolidayDate] = row;
        }

        int saved = 0;

        foreach (var holiday in JordanHolidayCatalog.Holidays)
        {
            ct.ThrowIfCancellationRequested();

            if (year.HasValue && holiday.Date.Year != year.Value)
            {
                continue;
            }

            if (byDate.TryGetValue(holiday.Date, out var row))
            {
                if (row.IsCancelled)
                {
                    row.IsCancelled = false;
                    row.Description = Truncate(holiday.Name, 300);
                    row.Kind = holiday.Kind;
                    row.Source = holiday.Source;
                    row.CreatedAtUtc = DateTime.UtcNow;
                    saved++;
                }

                continue;
            }

            _db.OfficialHolidays.Add(new OfficialHoliday
            {
                HolidayDate = holiday.Date,
                Description = Truncate(holiday.Name, 300),
                Kind = holiday.Kind,
                Source = holiday.Source,
                IsCancelled = false,
                CreatedAtUtc = DateTime.UtcNow
            });
            saved++;
        }

        if (saved > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return saved;
    }

    /// <summary>
    /// إضافة مجموعة عطل من نص حر (سطر لكل عطلة بالصيغة: التاريخ | الاسم | النوع).
    /// يقبل التواريخ بصيغة yyyy-MM-dd أو dd/MM/yyyy أو dd-MM-yyyy،
    /// والنوع: «رسمية» أو «دينية إسلامية» أو «دينية مسيحية» أو «وطنية» (اختياري).
    /// </summary>
    public async Task<int> AddHolidaysFromTextAsync(string text, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int saved = 0;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            ct.ThrowIfCancellationRequested();

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(new[] { '|', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();

            var date = ParseLooseDate(parts.Length > 0 ? parts[0] : null);
            if (date is null)
            {
                continue;
            }

            var name = parts.Length > 1 ? Normalize(parts[1]) : "عطلة رسمية";
            if (name.Length == 0)
            {
                name = "عطلة رسمية";
            }

            var kind = ParseKind(parts.Length > 2 ? parts[2] : null);

            if (await SaveHolidayAsync(date.Value, name, kind, "إدخال يدوي (قائمة)", ct))
            {
                saved++;
            }
        }

        return saved;
    }

    /// <summary>
    /// استيراد تقويم العطل من ملف Excel (ترويسة تحتوي «التاريخ» و«الاسم/المناسبة» و«النوع»).
    /// </summary>
    public async Task<int> ImportHolidaysAsync(
        Stream stream,
        string fileName,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();
        int headerRow = FindHolidayHeaderRow(ws);

        if (headerRow == 0)
        {
            throw new InvalidOperationException(
                "لم يُعثر على ترويسة تقويم العطل (عمود «التاريخ» و«الاسم/المناسبة») في الملف.");
        }

        int dateCol = FindColumn(ws, headerRow, "تاريخ");
        int nameCol = FindColumn(ws, headerRow, "اسم", "مناسبه", "وصف", "عطله");
        int kindCol = FindColumn(ws, headerRow, "نوع");

        if (dateCol == 0)
        {
            throw new InvalidOperationException("عمود «التاريخ» غير موجود في الملف.");
        }

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;
        string source = $"استيراد Excel: {Truncate(Path.GetFileName(fileName), 60)}";
        int imported = 0;

        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();

            DateOnly? date = ReadDate(ws.Cell(r, dateCol))
                ?? ParseLooseDate(ws.Cell(r, dateCol).GetFormattedString());
            if (date is null)
            {
                continue;
            }

            var name = nameCol > 0 ? Normalize(ws.Cell(r, nameCol).GetFormattedString()) : string.Empty;
            if (name.Length == 0)
            {
                name = "عطلة رسمية";
            }

            var kind = ParseKind(kindCol > 0 ? ws.Cell(r, kindCol).GetFormattedString() : null);

            if (await SaveHolidayAsync(date.Value, name, kind, source, ct))
            {
                imported++;
            }
        }

        _logger.LogInformation("اكتمل استيراد تقويم العطل من {File}: {Count} عطلة.", fileName, imported);
        return imported;
    }

    /// <summary>البحث عن صف الترويسة في ملف تقويم العطل (صف يحتوي «التاريخ»).</summary>
    private static int FindHolidayHeaderRow(IXLWorksheet ws)
    {
        int last = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 20);
        int cols = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 30);

        for (int r = 1; r <= last; r++)
        {
            for (int c = 1; c <= cols; c++)
            {
                if (Normalize(ws.Cell(r, c).GetFormattedString()).Contains("تاريخ", StringComparison.Ordinal))
                {
                    return r;
                }
            }
        }

        return 0;
    }

    /// <summary>البحث عن عمود بالاسم (بعد توحيد الحروف) في صف الترويسة.</summary>
    private static int FindColumn(IXLWorksheet ws, int headerRow, params string[] candidates)
    {
        int cols = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 40);

        for (int c = 1; c <= cols; c++)
        {
            var header = Normalize(ws.Cell(headerRow, c).GetFormattedString());
            if (header.Length == 0)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (header.Contains(candidate, StringComparison.Ordinal))
                {
                    return c;
                }
            }
        }

        return 0;
    }

    /// <summary>تحويل نص تاريخ (yyyy-MM-dd / dd/MM/yyyy / dd-MM-yyyy) إلى تاريخ مع توحيد الأرقام العربية.</summary>
    private static DateOnly? ParseLooseDate(string? text)
    {
        var normalized = NormalizeDigits(Normalize(text)).Replace('.', '/').Replace('-', '/');
        if (normalized.Length == 0)
        {
            return null;
        }

        var timeSplit = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (timeSplit.Length > 0)
        {
            normalized = timeSplit[0];
        }

        if (DateOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var direct))
        {
            return direct;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && int.TryParse(parts[0], out int first)
            && int.TryParse(parts[1], out int second)
            && int.TryParse(parts[2], out int third))
        {
            int year = third < 100 ? 2000 + third : third;
            int day = first, month = second;

            // صيغة (سنة/شهر/يوم): يُقرأ الجزء الأول سنة عندما يتجاوز 31.
            if (first > 31)
            {
                year = first;
                month = second;
                day = third;
            }

            if (month is >= 1 and <= 12 && day is >= 1 and <= 31 && year is >= 1900 and <= 2200)
            {
                try
                {
                    return new DateOnly(year, month, day);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>قراءة تصنيف العطلة من نص حر (رسمية / دينية إسلامية / دينية مسيحية / وطنية).</summary>
    private static HolidayKind ParseKind(string? text)
    {
        var normalized = Normalize(text);

        if (normalized.Contains("اسلام", StringComparison.Ordinal)
            || normalized.Contains("هجري", StringComparison.Ordinal))
        {
            return HolidayKind.ReligiousIslamic;
        }

        if (normalized.Contains("مسيح", StringComparison.Ordinal)
            || normalized.Contains("ميلاد", StringComparison.Ordinal))
        {
            return HolidayKind.ReligiousChristian;
        }

        if (normalized.Contains("وطني", StringComparison.Ordinal)
            || normalized.Contains("مناسبه", StringComparison.Ordinal))
        {
            return HolidayKind.NationalOccasion;
        }

        return HolidayKind.Official;
    }

    /// <summary>قراءة تصنيف العطلة من رقم وارد من الواجهة (1=رسمية، 2=إسلامية، 3=مسيحية، 4=وطنية).</summary>
    public static bool TryParseHolidayKind(int? value, out HolidayKind kind)
    {
        if (value is >= 1 and <= 4)
        {
            kind = (HolidayKind)value.Value;
            return true;
        }

        kind = HolidayKind.Official;
        return false;
    }

    /// <summary>قراءة تصنيف العطلة من نص حر (واجهة أو استيراد).</summary>
    public static HolidayKind ParseHolidayKind(string? text) => ParseKind(text);

    /// <summary>
    /// ملف Excel لتقويم العطل الرسمية والدينية المعتمد (مع أثر الدوام الفعلي في كل عطلة).
    /// </summary>
    public async Task<byte[]> BuildHolidayCalendarWorkbookAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        var calendar = await LoadCalendarAsync(ct);
        var stats = await ReadHolidayPunchStatsAsync(ct);

        var rows = calendar.Holidays
            .Where(h => !from.HasValue || h.Date >= from.Value)
            .Where(h => !to.HasValue || h.Date <= to.Value)
            .OrderBy(h => h.Date)
            .ToList();

        var generatedAt = DateTime.Now.ToString("yyyy/MM/dd HH:mm");

        using var workbook = new XLWorkbook();
        var ws = CreateSheet(workbook, "تقويم العطل الرسمية والدينية",
            "تقويم العطل الرسمية والدينية المعتمد في تحليل الحضور والانصراف",
            $"عطلة نهاية الأسبوع: {calendar.WeekendDaysText} | عدد العطل: {rows.Count} | " +
            $"كتالوج النظام: {JordanHolidayCatalog.FirstDate:yyyy/MM/dd} — {JordanHolidayCatalog.LastDate:yyyy/MM/dd}",
            8);

        int row = WriteHeader(ws, 4,
            "التاريخ", "اليوم", "اسم العطلة / المناسبة", "نوع العطلة", "المصدر",
            "موظفون دوّموا", "دقائق العمل في العطلة", "ملاحظة");

        foreach (var holiday in rows)
        {
            var stat = stats.GetValueOrDefault(holiday.Date);

            ws.Cell(row, 1).Value = holiday.Date.ToString("yyyy/MM/dd");
            ws.Cell(row, 2).Value = PunchCalendar.DayName(holiday.Date.DayOfWeek);
            ws.Cell(row, 3).Value = holiday.Name;
            ws.Cell(row, 4).Value = holiday.KindText;
            ws.Cell(row, 5).Value = holiday.Source;
            ws.Cell(row, 6).Value = stat.Punched;
            ws.Cell(row, 7).Value = stat.Minutes;
            ws.Cell(row, 8).Value = stat.Punched > 0
                ? "دوام فعلي في عطلة — يُعرض للعلم ولا يُحتسب مخالفة (ساعات إضافية محتملة)"
                : "عطلة معتمدة — لا تُحتسب غياباً";
            row++;
        }

        StyleTable(ws, 4, row - 1, 8);
        SetWidths(ws, 14, 12, 44, 22, 28, 14, 18, 58);

        int totalPunched = rows.Sum(h => stats.GetValueOrDefault(h.Date).Punched);
        int totalMinutes = rows.Sum(h => stats.GetValueOrDefault(h.Date).Minutes);

        var totalRow = ws.Range(row, 1, row, 8);
        totalRow.Style.Fill.BackgroundColor = PunchTotalFill;
        totalRow.Style.Font.Bold = true;
        ws.Cell(row, 1).Value = "الإجمالي";
        ws.Cell(row, 3).Value = $"{rows.Count} عطلة رسمية ودينية";
        ws.Cell(row, 6).Value = totalPunched;
        ws.Cell(row, 7).Value = totalMinutes;

        AddFooter(ws, row + 2, 8,
            Signature(generatedAt, "تقويم العطل الرسمية والدينية — يُحدَّث سنوياً بقرار رسمي"));

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
