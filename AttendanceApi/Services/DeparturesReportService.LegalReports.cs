using AttendanceApi.Audit;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// التقارير القانونية الاحترافية: تقرير مستقل لكل قاعدة في الوثيقة المرجعية
/// (المادة 118/ب، المادة 118/ج، المادة 7، قاعدة الـ 15 يوماً للمكافأة، والمادة 112)
/// تُبنى كلها من نتائج معالجة «تقرير المغادرات» (جدول المرحلة + المراجعة الشهرية + التجميع الأسبوعي)،
/// إضافةً إلى ملخص تنفيذي، ودليل للقواعد، وكشف خصومات مُجمَّع لكل موظف، وورقة منهجية الاحتساب.
/// </summary>
public sealed partial class DeparturesReportService
{
    // ---- المراجع القانونية كما ترد في الوثيقة ----
    private const string Ref118b = "المادة 118/ب — نظام الخدمة المدنية";
    private const string Ref118c = "المادة 118/ج — نظام الخدمة المدنية";
    private const string RefArticle7 = "المادة 7 — تعليمات الحضور والانصراف 2020";
    private const string RefBonus = "قاعدة الـ 15 يوماً — تعليمات المكافأة الشهرية";
    private const string Ref112 = "المادة 112 — نظام الخدمة المدنية (الإجازة المرضية)";
    private const string Ref100 = "المواد 100/د و101 و105 — نظام الخدمة المدنية";

    // ---- الهوية البصرية للتقارير ----
    private static readonly XLColor BrandFill = XLColor.FromHtml("#12314F");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1B4B7A");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#DCE6F1");
    private static readonly XLColor WarnFont = XLColor.FromHtml("#B00020");
    private static readonly XLColor MutedFont = XLColor.FromHtml("#5A6B7B");
    private static readonly XLColor BorderLine = XLColor.FromHtml("#B8C4D0");

    /// <summary>نسبة الاستحقاق السنوي التي يُعدّ ما دونها رصيداً منخفضاً (20% × 30 يوماً).</summary>
    private const double LowBalanceThresholdDays = LegalRules.AnnualLeaveFullEntitlement * 0.2;

    /// <summary>بناء ملف Excel للتقارير القانونية الاحترافية (10 أوراق) من نتائج معالجة تقرير المغادرات.</summary>
    public async Task<byte[]> BuildLegalReportsWorkbookAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rows = await _db.DeparturesReportRows.AsNoTracking().ToListAsync(ct);
        var reviews = await _db.DeparturesReportReviews.AsNoTracking().ToListAsync(ct);
        var weekly = await _db.WeeklyLateness.AsNoTracking().ToListAsync(ct);

        var administrations = ResolveAdministrations(
            rows.Select(r => new AdministrationRow(r.JobNumber, r.DepartmentName, r.MainDepartmentName)),
            byMainDepartment: true);

        // الطلبات المعتبرة = «مقبول» فقط، ولها تاريخ مرجعي (تاريخ المغادرة وإلا تاريخ الطلب).
        var accepted = AcceptedItems(rows);

        var over4 = accepted
            .Where(i => i.Row.Exceeds4Hours)
            .OrderByDescending(i => i.Row.DurationMinutes)
            .ThenBy(i => i.Row.JobNumber)
            .ToList();

        var weeks60 = weekly
            .Where(w => w.Exceeds60Minutes)
            .OrderByDescending(w => w.LateMinutes)
            .ThenBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .ToList();

        var allMorningLate = BuildMorningLatenessRows(accepted);
        var morningLate = allMorningLate.Where(m => m.Action != DisciplinaryActionType.None).ToList();
        var sick = BuildSickLeaveRows(accepted);
        var usage = BuildAnnualLeaveUsageRows(accepted);
        var bonusMonths = reviews
            .Where(r => r.Exceeds15Days)
            .OrderByDescending(r => r.TotalAbsenceDays)
            .ThenBy(r => r.JobNumber)
            .ToList();
        var ledger = BuildDeductionLedger(reviews, weekly, allMorningLate);

        var period = ResolveReportPeriod(accepted);
        var generatedAt = DateTime.Now;
        var source = rows.Select(r => r.SourceFile).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))
                     ?? "ربط مباشر بقاعدة البيانات";

        using var workbook = new XLWorkbook();

        AddExecutiveSummarySheet(workbook, period, source, over4, weeks60, morningLate, bonusMonths, sick, usage, generatedAt);
        AddRulesReferenceSheet(workbook, generatedAt);
        AddWeekly60Sheet(workbook, weeks60, administrations, period, generatedAt);
        AddLegalOver4Sheet(workbook, over4, administrations, period, generatedAt);
        AddMorningLatenessSheet(workbook, morningLate, administrations, period, generatedAt);
        AddBonusSheet(workbook, bonusMonths, administrations, period, generatedAt);
        AddSickLeaveSheet(workbook, sick, administrations, period, generatedAt);
        AddAnnualBalanceSheet(workbook, usage, administrations, period, generatedAt);
        AddDeductionLedgerSheet(workbook, ledger, administrations, period, generatedAt);
        AddMethodologySheet(workbook, generatedAt);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        _logger.LogInformation(
            "تم بناء التقارير القانونية الاحترافية: {Over4} طلباً > 4 ساعات، {Weeks} أسبوعاً ≥ 60 دقيقة، {Ledger} موظفاً في كشف الخصومات.",
            over4.Count, weeks60.Count, ledger.Count);

        return ms.ToArray();
    }
    // =====================================================================
    //  بناء البيانات (قواعد الوثيقة مطبَّقة على صفوف تقرير المغادرات)
    // =====================================================================

    /// <summary>صف معتمد مع تاريخه المرجعي (تاريخ المغادرة، وإلا تاريخ الطلب).</summary>
    private sealed record LegalItem(DeparturesReportRow Row, DateOnly Date);

    private static List<LegalItem> AcceptedItems(IEnumerable<DeparturesReportRow> rows) =>
        rows.Where(r => IsAccepted(r.Status))
            .Select(r => new { Row = r, Date = r.FromDate ?? r.RequestDate })
            .Where(x => x.Date.HasValue)
            .Select(x => new LegalItem(x.Row, x.Date!.Value))
            .ToList();

    /// <summary>
    /// التأخير الصباحي المرصود في التقرير: استئذان في اليوم نفسه يبدأ عند 08:30 أو قبله
    /// وينتهي بعده (غياب عن بداية الدوام)، ولا يتجاوز 4 ساعات حتى لا يتكرر مع المادة 118/ب.
    /// </summary>
    private static bool IsMorningLatenessItem(LegalItem item)
    {
        var r = item.Row;

        return r.Category == ReportLeaveCategory.Authorization
               && !r.Exceeds4Hours
               && r.FromDate.HasValue && r.ToDate.HasValue && r.FromDate.Value == r.ToDate.Value
               && r.FromTime.HasValue && r.ToTime.HasValue
               && r.FromTime.Value <= LegalRules.WorkdayStart
               && r.ToTime.Value > LegalRules.WorkdayStart
               && r.ToTime.Value <= LegalRules.WorkdayEnd;
    }

    /// <summary>تجميع أيام التأخير الصباحي لكل موظف/شهر ثم تطبيق المادة 7 (3 = تنبيه، 4 = إنذار، أكثر من 4 = حسم يومين).</summary>
    private static List<MorningLateRow> BuildMorningLatenessRows(IReadOnlyList<LegalItem> accepted) =>
        accepted.Where(IsMorningLatenessItem)
            .GroupBy(i => new { i.Row.JobNumber, i.Date.Year, i.Date.Month })
            .Select(g =>
            {
                int lateDays = g.Select(i => i.Date).Distinct().Count();

                return new MorningLateRow(
                    JobNumber: g.Key.JobNumber,
                    EmployeeName: g.First().Row.EmployeeName,
                    Year: g.Key.Year,
                    Month: g.Key.Month,
                    LateDays: lateDays,
                    Action: LegalRules.MonthlyLatePenalty(lateDays));
            })
            .OrderByDescending(x => x.LateDays)
            .ThenBy(x => x.JobNumber)
            .ThenBy(x => x.Year)
            .ThenBy(x => x.Month)
            .ToList();
    /// <summary>الإجازات المرضية لكل موظف/سنة (عدد النوبات وإجمالي الأيام) لتطبيق تدرّج المادة 112.</summary>
    private static List<SickLeaveRow> BuildSickLeaveRows(IReadOnlyList<LegalItem> accepted) =>
        accepted.Where(i => i.Row.Category == ReportLeaveCategory.SickLeave)
            .GroupBy(i => new { i.Row.JobNumber, i.Date.Year })
            .Select(g => new SickLeaveRow(
                JobNumber: g.Key.JobNumber,
                EmployeeName: g.First().Row.EmployeeName,
                Year: g.Key.Year,
                Spells: g.Select(i => i.Row.RequestNumber ?? i.Row.Id.ToString()).Distinct(StringComparer.Ordinal).Count(),
                Days: Math.Round(g.Sum(i => i.Row.DaysCount), 2)))
            .OrderByDescending(x => x.Days)
            .ThenBy(x => x.JobNumber)
            .ToList();

    /// <summary>استخدام الإجازة السنوية وكل أيام الطلبات لكل موظف/سنة (لبناء ورقة الرصيد).</summary>
    private static List<LeaveUsageRow> BuildAnnualLeaveUsageRows(IReadOnlyList<LegalItem> accepted) =>
        accepted.GroupBy(i => new { i.Row.JobNumber, i.Date.Year })
            .Select(g => new LeaveUsageRow(
                JobNumber: g.Key.JobNumber,
                EmployeeName: g.First().Row.EmployeeName,
                Year: g.Key.Year,
                AnnualDays: Math.Round(SumDays(g, ReportLeaveCategory.AnnualLeave), 2),
                SickDays: Math.Round(SumDays(g, ReportLeaveCategory.SickLeave), 2),
                OfficialDays: Math.Round(SumDays(g, ReportLeaveCategory.OfficialDuty), 2),
                OtherDays: Math.Round(SumDays(g, ReportLeaveCategory.Other) + SumDays(g, ReportLeaveCategory.Unknown), 2),
                TotalRequestDays: Math.Round(g.Sum(i => i.Row.DaysCount), 2)))
            .OrderByDescending(x => x.AnnualDays)
            .ThenBy(x => x.JobNumber)
            .ToList();

    private static double SumDays(IEnumerable<LegalItem> items, ReportLeaveCategory category) =>
        items.Where(i => i.Row.Category == category).Sum(i => i.Row.DaysCount);

    /// <summary>
    /// كشف الخصومات المُجمَّع لكل موظف: أيام 118/ب + أيام 118/ج (من رصيد الإجازات)،
    /// ومرات حسم الراتب (المادة 7)، وأشهر خصم المكافأة 50% (تجاوز 15 يوماً).
    /// </summary>
    private static List<DeductionLedgerRow> BuildDeductionLedger(
        IReadOnlyList<DeparturesReportReview> reviews,
        IReadOnlyList<DeparturesWeeklyLateness> weekly,
        IReadOnlyList<MorningLateRow> morningLate)
    {
        var jobs = reviews.Select(r => r.JobNumber)
            .Concat(weekly.Select(w => w.JobNumber))
            .Concat(morningLate.Select(m => m.JobNumber))
            .Distinct(StringComparer.Ordinal);

        var result = new List<DeductionLedgerRow>();

        foreach (var job in jobs)
        {
            var jobReviews = reviews.Where(r => r.JobNumber == job).ToList();
            var jobWeekly = weekly.Where(w => w.JobNumber == job).ToList();
            var jobLate = morningLate.Where(m => m.JobNumber == job).ToList();

            // المادة 7: كل شهر بلغ فيه التأخير الصباحي أكثر من 4 مرات = حسم يومين من الراتب.
            int penaltyMonths = jobLate.Count(m => m.Action == DisciplinaryActionType.TwoDaysSalaryDeduction);

            result.Add(new DeductionLedgerRow(
                JobNumber: job,
                EmployeeName: jobReviews.FirstOrDefault()?.EmployeeName
                              ?? jobWeekly.FirstOrDefault()?.EmployeeName
                              ?? jobLate.FirstOrDefault()?.EmployeeName,
                Over4Requests: jobReviews.Sum(r => r.AuthorizationOver4hCount),
                Days118b: Math.Round(jobReviews.Sum(r => r.Article118bDays), 2),
                WeeksOver60: jobWeekly.Count(w => w.Exceeds60Minutes),
                Days118c: Math.Round(jobWeekly.Sum(w => w.DeductionDays), 2),
                PenaltyMonths: penaltyMonths,
                SalaryDays: penaltyMonths * 2.0,
                BonusMonths: jobReviews.Count(r => r.Exceeds15Days)));
        }

        return result
            .Where(x => x.Days118b > 0 || x.Days118c > 0 || x.SalaryDays > 0 || x.BonusMonths > 0)
            .OrderByDescending(x => x.Days118b + x.Days118c)
            .ThenByDescending(x => x.SalaryDays)
            .ThenBy(x => x.JobNumber)
            .ToList();
    }
    /// <summary>الفترة الزمنية للبيانات وعدد الموظفين والطلبات المعتبرة.</summary>
    private static ReportPeriod ResolveReportPeriod(IReadOnlyList<LegalItem> accepted) =>
        accepted.Count == 0
            ? new ReportPeriod(null, null, 0, 0)
            : new ReportPeriod(
                accepted.Min(i => i.Date),
                accepted.Max(i => i.Date),
                accepted.Select(i => i.Row.JobNumber).Distinct(StringComparer.Ordinal).Count(),
                accepted.Count);

    /// <summary>اسم الإدارة الرئيسية للموظف (الأكثر تكراراً في صفوفه، وإلا «(غير محدّد)»).</summary>
    private static string Administration(IReadOnlyDictionary<string, string> map, string jobNumber) =>
        map.TryGetValue(jobNumber, out var name) ? name : UnspecifiedAdministration;

    // ---- أنواع صفوف التقارير ----
    private sealed record MorningLateRow(
        string JobNumber, string? EmployeeName, int Year, int Month, int LateDays, DisciplinaryActionType Action);

    private sealed record SickLeaveRow(
        string JobNumber, string? EmployeeName, int Year, int Spells, double Days);

    private sealed record LeaveUsageRow(
        string JobNumber, string? EmployeeName, int Year,
        double AnnualDays, double SickDays, double OfficialDays, double OtherDays, double TotalRequestDays);

    private sealed record DeductionLedgerRow(
        string JobNumber, string? EmployeeName,
        int Over4Requests, double Days118b, int WeeksOver60, double Days118c,
        int PenaltyMonths, double SalaryDays, int BonusMonths);

    private sealed record ReportPeriod(DateOnly? From, DateOnly? To, int Employees, int Requests);
    // =====================================================================
    //  أدوات التنسيق الاحترافي للأوراق
    // =====================================================================

    /// <summary>نص الفترة الزمنية بصيغة مقروءة.</summary>
    private static string PeriodLabel(ReportPeriod period) =>
        period.From.HasValue && period.To.HasValue
            ? $"الفترة: من {period.From:yyyy-MM-dd} إلى {period.To:yyyy-MM-dd}"
            : "الفترة: لا توجد صفوف معتمدة";

    /// <summary>إنشاء ورقة بتنسيق احترافي: عنوان مدمج + سطر توضيحي + سطر فاصل (الترويسة في الصف 4).</summary>
    private static IXLWorksheet CreateReportSheet(
        XLWorkbook workbook, string sheetName, string title, string subtitle, int columns)
    {
        var ws = workbook.Worksheets.Add(sheetName);
        ws.RightToLeft = true;
        ws.ShowGridLines = false;

        int span = Math.Max(1, columns);

        var titleRange = ws.Range(1, 1, 1, span);
        titleRange.Merge();
        titleRange.Value = title;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 14;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Fill.BackgroundColor = BrandFill;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 30;

        var subRange = ws.Range(2, 1, 2, span);
        subRange.Merge();
        subRange.Value = subtitle;
        subRange.Style.Font.FontSize = 10;
        subRange.Style.Font.FontColor = MutedFont;
        subRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        subRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        subRange.Style.Alignment.WrapText = true;
        ws.Row(2).Height = 30;

        ws.Row(3).Height = 6;
        return ws;
    }

    /// <summary>كتابة الترويسة في صف محدد، وإرجاع رقم أول صف بيانات.</summary>
    private static int WriteHeader(IXLWorksheet ws, int row, params string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }

        return row + 1;
    }

    /// <summary>تنسيق الجدول: ترويسة ملوّنة + حدود + تجميد الترويسة + مرشّح تلقائي.</summary>
    private static void StyleTable(IXLWorksheet ws, int headerRow, int lastDataRow, int columns)
    {
        var header = ws.Range(headerRow, 1, headerRow, columns);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = HeaderFill;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Alignment.WrapText = true;
        ws.Row(headerRow).Height = 32;

        if (lastDataRow > headerRow)
        {
            var body = ws.Range(headerRow + 1, 1, lastDataRow, columns);
            body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            body.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            body.Style.Border.OutsideBorderColor = BorderLine;
            body.Style.Border.InsideBorderColor = BorderLine;
            body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        ws.Range(headerRow, 1, Math.Max(lastDataRow, headerRow), columns).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
    }

    /// <summary>سطر الإجمالي أسفل الجدول (خلفية مميّزة + خط عريض).</summary>
    private static void StyleTotalRow(IXLWorksheet ws, int row, int columns)
    {
        var range = ws.Range(row, 1, row, columns);
        range.Style.Fill.BackgroundColor = TotalFill;
        range.Style.Font.Bold = true;
    }

    /// <summary>سطر ملاحظات أسفل الورقة.</summary>
    private static void AddFooterNote(IXLWorksheet ws, int row, int columns, string text)
    {
        var range = ws.Range(row, 1, row, Math.Max(1, columns));
        range.Merge();
        range.Value = text;
        range.Style.Font.FontSize = 9.5;
        range.Style.Font.Italic = true;
        range.Style.Font.FontColor = MutedFont;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        range.Style.Alignment.WrapText = true;
    }

    /// <summary>ضبط عرض الأعمدة صراحةً (أسرع وأدق من الاحتساب التلقائي على آلاف الصفوف).</summary>
    private static void SetColumnWidths(IXLWorksheet ws, params double[] widths)
    {
        for (int i = 0; i < widths.Length; i++)
        {
            ws.Column(i + 1).Width = widths[i];
        }
    }

    /// <summary>إبراز عمود أيام الخصم باللون التحذيري.</summary>
    private static void HighlightWarnings(IXLWorksheet ws, int firstRow, int lastDataRow, int column)
    {
        if (lastDataRow < firstRow)
        {
            return;
        }

        var range = ws.Range(firstRow, column, lastDataRow, column);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = WarnFont;
    }

    /// <summary>تذييل موحّد لكل ورقة.</summary>
    private static string FooterSignature(string generatedAt, string extra) =>
        $"أُعِدّ آلياً بواسطة نظام مراجعة الحضور — تاريخ الإصدار: {generatedAt} {extra}".TrimEnd();
    // =====================================================================
    //  الأوراق: الملخص التنفيذي ودليل القواعد
    // =====================================================================

    private static void AddExecutiveSummarySheet(
        XLWorkbook workbook,
        ReportPeriod period,
        string source,
        IReadOnlyList<LegalItem> over4,
        IReadOnlyList<DeparturesWeeklyLateness> weeks60,
        IReadOnlyList<MorningLateRow> morningLate,
        IReadOnlyList<DeparturesReportReview> bonusMonths,
        IReadOnlyList<SickLeaveRow> sick,
        IReadOnlyList<LeaveUsageRow> usage,
        DateTime generatedAt)
    {
        const int columns = 7;

        var ws = CreateReportSheet(
            workbook, "الملخص التنفيذي",
            "الملخص التنفيذي — نتائج المعالجة القانونية لتقرير المغادرات",
            $"{PeriodLabel(period)}  |  الموظفون: {period.Employees}  |  الطلبات المعتمدة: {period.Requests}  |  المصدر: {source}",
            columns);

        int header = WriteHeader(ws, 4,
            "البند", "المرجع القانوني", "عدد الحالات", "عدد الموظفين",
            "أيام الخصم من الإجازات", "أيام الحسم من الراتب", "الملاحظة");

        double days118b = over4.Count * LegalRules.Art118b_EquivalentDays;
        double days118c = Math.Round(weeks60.Sum(w => w.DeductionDays), 2);
        double salaryDays = morningLate.Count(m => m.Action == DisciplinaryActionType.TwoDaysSalaryDeduction) * 2.0;
        int sickReduced = sick.Count(s => s.Days > LegalRules.Sick_Tier1_MaxDays);
        int lowBalance = usage.Count(u => LegalRules.AnnualLeaveFullEntitlement - u.AnnualDays < LowBalanceThresholdDays);

        int row = header;

        ws.Cell(row, 1).Value = "المتأخرون عن الدوام — تأخير/انصراف مبكر أسبوعي بلغ 60 دقيقة أو أكثر";
        ws.Cell(row, 2).Value = Ref118c;
        ws.Cell(row, 3).Value = weeks60.Count;
        ws.Cell(row, 4).Value = weeks60.Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = days118c;
        ws.Cell(row, 6).Value = 0;
        ws.Cell(row, 7).Value = "خصم يوم كامل من رصيد الإجازات لكل أسبوع بلغ 60 دقيقة داخل الدوام الرسمي (08:30–15:30)";
        row++;

        ws.Cell(row, 1).Value = "استئذان تجاوز 4 ساعات في يوم واحد";
        ws.Cell(row, 2).Value = Ref118b;
        ws.Cell(row, 3).Value = over4.Count;
        ws.Cell(row, 4).Value = over4.Select(i => i.Row.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = days118b;
        ws.Cell(row, 6).Value = 0;
        ws.Cell(row, 7).Value = "خصم يوم كامل من رصيد الإجازة السنوية لكل استئذان يتجاوز 240 دقيقة";
        row++;

        ws.Cell(row, 1).Value = "التأخير الصباحي المتكرر شهرياً";
        ws.Cell(row, 2).Value = RefArticle7;
        ws.Cell(row, 3).Value = morningLate.Count;
        ws.Cell(row, 4).Value = morningLate.Select(m => m.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = 0;
        ws.Cell(row, 6).Value = salaryDays;
        ws.Cell(row, 7).Value = "3 = تنبيه خطي، 4 = إنذار خطي، أكثر من 4 = حسم يومين من الراتب (حسب كل شهر)";
        row++;

        ws.Cell(row, 1).Value = "غياب شهري تجاوز 15 يوماً";
        ws.Cell(row, 2).Value = RefBonus;
        ws.Cell(row, 3).Value = bonusMonths.Count;
        ws.Cell(row, 4).Value = bonusMonths.Select(r => r.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = 0;
        ws.Cell(row, 6).Value = 0;
        ws.Cell(row, 7).Value = "خصم 50% من المكافأة الشهرية (الغياب = سنوية + مرضية + 118/ب + 118/ج)";
        row++;

        ws.Cell(row, 1).Value = "إجازة مرضية انخفضت نسبتها عن 100%";
        ws.Cell(row, 2).Value = Ref112;
        ws.Cell(row, 3).Value = sickReduced;
        ws.Cell(row, 4).Value = sick.Where(s => s.Days > LegalRules.Sick_Tier1_MaxDays)
            .Select(s => s.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = 0;
        ws.Cell(row, 6).Value = 0;
        ws.Cell(row, 7).Value = "75% من الراتب بعد 120 يوماً، و50% بعد 240 يوماً، وبلا راتب بعد 360 يوماً (بلا ترحيل)";
        row++;

        ws.Cell(row, 1).Value = "رصيد إجازة سنوية منخفض (أقل من 20% من الاستحقاق)";
        ws.Cell(row, 2).Value = Ref100;
        ws.Cell(row, 3).Value = lowBalance;
        ws.Cell(row, 4).Value = usage.Where(u => LegalRules.AnnualLeaveFullEntitlement - u.AnnualDays < LowBalanceThresholdDays)
            .Select(u => u.JobNumber).Distinct(StringComparer.Ordinal).Count();
        ws.Cell(row, 5).Value = 0;
        ws.Cell(row, 6).Value = 0;
        ws.Cell(row, 7).Value = "يستوجب مراجعة الرصيد قبل الموافقة على إجازات جديدة (الاستحقاق 30 يوماً)";
        row++;

        ws.Cell(row, 1).Value = "الإجمالي";
        ws.Cell(row, 4).Value = period.Employees;
        ws.Cell(row, 5).Value = Math.Round(days118b + days118c, 2);
        ws.Cell(row, 6).Value = salaryDays;
        ws.Cell(row, 7).Value = "الخصم من الإجازات = أيام 118/ب + أيام 118/ج، والحسم من الراتب = المادة 7";
        int totalRow = row;

        StyleTable(ws, header, totalRow - 1, columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, totalRow, 5);
        HighlightWarnings(ws, header, totalRow, 6);
        SetColumnWidths(ws, 46, 30, 12, 12, 14, 14, 52);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"), "| التقارير التفصيلية لكل قاعدة في الأوراق التالية."));
    }
    /// <summary>ورقة «دليل القواعد القانونية»: كل قاعدة في الوثيقة ونصها والعقوبة وأساس الاحتساب من الملف.</summary>
    private static void AddRulesReferenceSheet(XLWorkbook workbook, DateTime generatedAt)
    {
        const int columns = 4;

        var ws = CreateReportSheet(
            workbook, "دليل القواعد القانونية",
            "دليل القواعد القانونية المطبَّقة — الوثيقة المرجعية",
            "المرجع والنص والعقوبة، وبيان ما يمكن احتسابه مباشرةً من «تقرير المغادرات» وما يتطلب بيانات إضافية.",
            columns);

        int header = WriteHeader(ws, 4,
            "المرجع", "نص القاعدة", "العقوبة / الأثر", "أساس الاحتساب من تقرير المغادرات");

        (string Ref, string Rule, string Penalty, string Basis)[] rules =
        {
            (Ref118c,
             "يُجمع تأخير الموظف وانصرافه المبكر داخل ساعات الدوام الرسمي (08:30–15:30 = 7 ساعات / 420 دقيقة) خلال الأسبوع (الاثنين–الأحد)، فإذا بلغ 60 دقيقة أو أكثر استحق خصماً.",
             "خصم يوم كامل (1.00) من رصيد الإجازات لكل أسبوع.",
             "مجموع دقائق الاستئذانات الواقعة داخل نافذة الدوام لكل أسبوع ISO، مع استثناء الطلبات التي تجاوزت 4 ساعات (لعدم الازدواج مع المادة 118/ب)."),

            (Ref118b,
             "الاستئذان الذي يتجاوز 4 ساعات (240 دقيقة) في اليوم الواحد يُعدّ غياباً كاملاً.",
             "خصم يوم كامل (1.00) من رصيد الإجازة السنوية.",
             "كل صف استئذان حالته «مقبول» ومدة إجمالية تزيد على 240 دقيقة."),

            (RefArticle7,
             "تُعدّ التأخيرات الصباحية شهرياً لكل موظف: 3 تنبيه خطي، 4 إنذار خطي، وأكثر من 4 حسم يومين من الراتب.",
             "تنبيه خطي / إنذار خطي / حسم يومين من الراتب.",
             "أيام الاستئذان الصباحي (استئذان يبدأ عند 08:30 أو قبله وينتهي بعده في اليوم نفسه) لكل شهر، دون الطلبات التي تجاوزت 4 ساعات."),

            (RefBonus,
             "إذا تجاوز إجمالي غياب الموظف الشهري 15 يوماً (سنوية + مرضية + 118/ب + 118/ج) استحق خصم المكافأة.",
             "خصم 50% من المكافأة الشهرية.",
             "إجمالي أيام الإجازات والاستئذانات المحتسبة لكل موظف/شهر من نتائج المراجعة."),

            (Ref112,
             "نسب راتب الإجازة المرضية تتناقص حسب الأيام التراكمية في السنة، ولا تُرحَّل الأيام إلى السنة التالية.",
             "حتى 120 يوماً = 100%، من 121 إلى 240 = 75%، من 241 إلى 360 = 50%، وبعد 360 بلا راتب.",
             "إجمالي أيام الإجازة المرضية لكل موظف/سنة وعدد نوباتها، ثم تطبيق التدرّج القانوني."),

            (Ref100,
             "الاحتساب التناسبي لإجازة الموظف الجديد ((12 − شهر التعيين + 1) ÷ 12) × 30، ومنع تراكم الإجازات لأكثر من سنتين، وسقف تعويض نهاية الخدمة 60 يوماً.",
             "ضبط الاستحقاق والتراكم وسقف التعويض عند انتهاء الخدمة.",
             "يتطلب تاريخ التعيين وأرصدة الإجازات وبيانات انتهاء الخدمة — غير متوفر في تقرير المغادرات، لذا يُدرَج هذا التقرير للمرجعية فقط."),

            ("المواد 4/أ وقواعد الحضور",
             "البديل إلزامي، والإشعار المسبق للمغادرة لا يقل عن يوم واحد، والفصل بين الطلب الفعّال والموافقة، وموافقة المدير المسبقة لمغادرات الصباح.",
             "إجراء تنظيمي ومسؤولية إدارية.",
             "حالة الطلب ونوعه وأوقاته في تقرير المغادرات (المقبول فقط يدخل في الاحتساب).")
        };

        int row = header;
        foreach (var r in rules)
        {
            ws.Cell(row, 1).Value = r.Ref;
            ws.Cell(row, 2).Value = r.Rule;
            ws.Cell(row, 3).Value = r.Penalty;
            ws.Cell(row, 4).Value = r.Basis;
            ws.Range(row, 1, row, columns).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 44;
            row++;
        }

        StyleTable(ws, header, row - 1, columns);
        SetColumnWidths(ws, 30, 66, 34, 60);
        AddFooterNote(ws, row + 1, columns, FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"), string.Empty));
    }
    // =====================================================================
    //  ورقة المتأخرين عن الدوام (المادة 118/ج): أسبوع بلغ 60 دقيقة أو أكثر
    // =====================================================================

    private static void AddWeekly60Sheet(
        XLWorkbook workbook,
        IReadOnlyList<DeparturesWeeklyLateness> weeks60,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 14;

        double totalDays = Math.Round(weeks60.Sum(w => w.DeductionDays), 2);
        int employees = weeks60.Select(w => w.JobNumber).Distinct(StringComparer.Ordinal).Count();

        var ws = CreateReportSheet(
            workbook, "المتأخرون 60 دقيقة - 118-ج",
            "تقرير المتأخرين عن الدوام — بلوغ 60 دقيقة تأخيراً في الأسبوع (المادة 118/ج)",
            $"{PeriodLabel(period)}  |  الأسابيع المخالفة: {weeks60.Count}  |  الموظفون: {employees}  |  إجمالي أيام الخصم من الإجازات: {totalDays:0.##}",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "بداية الأسبوع (الاثنين)", "نهاية الأسبوع",
            "السنة", "الشهر", "عدد المغادرات", "دقائق التأخير داخل الدوام", "المدة (ساعات ودقائق)",
            "بلغت 60 دقيقة", "أيام الخصم", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var w in weeks60)
        {
            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = w.JobNumber;
            ws.Cell(row, 3).Value = w.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, w.JobNumber);
            ws.Cell(row, 5).Value = w.WeekStart.ToString("yyyy-MM-dd");
            ws.Cell(row, 6).Value = w.WeekEnd.ToString("yyyy-MM-dd");
            ws.Cell(row, 7).Value = w.Year;
            ws.Cell(row, 8).Value = w.Month;
            ws.Cell(row, 9).Value = w.DepartureCount;
            ws.Cell(row, 10).Value = w.LateMinutes;
            ws.Cell(row, 11).Value = FormatMinutes(w.LateMinutes);
            ws.Cell(row, 12).Value = w.Exceeds60Minutes ? "نعم" : "لا";
            ws.Cell(row, 13).Value = w.DeductionDays;
            ws.Cell(row, 14).Value = Ref118c;
            row++;
        }

        int lastData = row - 1;

        if (weeks60.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد أسابيع بلغت 60 دقيقة في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 9).Value = weeks60.Sum(w => w.DepartureCount);
        ws.Cell(row, 10).Value = weeks60.Sum(w => w.LateMinutes);
        ws.Cell(row, 13).Value = totalDays;
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 13);
        if (lastData >= header)
        {
            ws.Range(header, 10, lastData, 13).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(header, 9, lastData, 10).Style.NumberFormat.Format = "#,##0";
        }

        SetColumnWidths(ws, 5, 14, 24, 26, 15, 13, 8, 8, 11, 14, 18, 11, 10, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| كل أسبوع بلغ فيه مجموع التأخير والانصراف المبكر داخل الدوام الرسمي 60 دقيقة = خصم يوم كامل من رصيد الإجازات."));
    }
    /// <summary>ورقة مخالفات المادة 118/ب: الاستئذانات التي تجاوزت 4 ساعات.</summary>
    private static void AddLegalOver4Sheet(
        XLWorkbook workbook,
        IReadOnlyList<LegalItem> over4,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 12;

        var ws = CreateReportSheet(
            workbook, "الاستئذان فوق 4 ساعات - 118-ب",
            "تقرير مخالفات المادة 118/ب — الاستئذان الذي تجاوز 4 ساعات في يوم واحد",
            $"{PeriodLabel(period)}  |  عدد الطلبات المخالفة: {over4.Count}  |  " +
            $"الموظفون: {over4.Select(i => i.Row.JobNumber).Distinct(StringComparer.Ordinal).Count()}  |  " +
            $"إجمالي أيام الخصم: {over4.Count * LegalRules.Art118b_EquivalentDays:0.##}",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "رقم الطلب", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "تاريخ المغادرة",
            "من وقت", "إلى وقت", "المدة (نص)", "المدة (دقيقة)", "أيام الخصم", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var item in over4)
        {
            var r = item.Row;

            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = r.RequestNumber ?? string.Empty;
            ws.Cell(row, 3).Value = r.JobNumber;
            ws.Cell(row, 4).Value = r.EmployeeName ?? string.Empty;
            ws.Cell(row, 5).Value = Administration(administrations, r.JobNumber);
            ws.Cell(row, 6).Value = item.Date.ToString("yyyy-MM-dd");
            ws.Cell(row, 7).Value = r.FromTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 8).Value = r.ToTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 9).Value = r.DurationText ?? string.Empty;
            ws.Cell(row, 10).Value = r.DurationMinutes;
            ws.Cell(row, 11).Value = LegalRules.Art118b_EquivalentDays;
            ws.Cell(row, 12).Value = Ref118b;
            row++;
        }

        int lastData = row - 1;

        if (over4.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد استئذانات تجاوزت 4 ساعات في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 4).Value = "الإجمالي";
        ws.Cell(row, 10).Value = over4.Sum(i => i.Row.DurationMinutes);
        ws.Cell(row, 11).Value = Math.Round(over4.Count * LegalRules.Art118b_EquivalentDays, 2);
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 11);
        if (lastData >= header)
        {
            ws.Range(header, 10, lastData, 11).Style.NumberFormat.Format = "#,##0.00";
        }

        SetColumnWidths(ws, 5, 12, 14, 24, 26, 14, 10, 10, 22, 13, 10, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| كل استئذان تجاوز 240 دقيقة = خصم يوم كامل من رصيد الإجازة السنوية، ويُستثنى من التجميع الأسبوعي (118/ج) ومن عدّ التأخير الصباحي (المادة 7)."));
    }
    // =====================================================================
    //  ورقة التأخير الصباحي المتكرر (المادة 7) وورقة خصم المكافأة (15 يوماً)
    // =====================================================================

    /// <summary>تسمية الإجراء التأديبي وفق المادة 7.</summary>
    private static string Article7Label(DisciplinaryActionType action) => action switch
    {
        DisciplinaryActionType.WrittenWarning => "تنبيه خطي (3 تأخيرات)",
        DisciplinaryActionType.WrittenCaution => "إنذار خطي (4 تأخيرات)",
        DisciplinaryActionType.TwoDaysSalaryDeduction => "حسم يومين من الراتب (أكثر من 4)",
        _ => "لا إجراء تأديبي"
    };

    private static void AddMorningLatenessSheet(
        XLWorkbook workbook,
        IReadOnlyList<MorningLateRow> rows,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 10;

        double salaryDays = rows.Count(r => r.Action == DisciplinaryActionType.TwoDaysSalaryDeduction) * 2.0;

        var ws = CreateReportSheet(
            workbook, "التأخير الصباحي - المادة 7",
            "تقرير التأخير الصباحي المتكرر — العقوبات التأديبية الشهرية (المادة 7)",
            $"{PeriodLabel(period)}  |  الحالات الشهرية: {rows.Count}  |  " +
            $"الموظفون: {rows.Select(r => r.JobNumber).Distinct(StringComparer.Ordinal).Count()}  |  " +
            $"إجمالي أيام الحسم من الراتب: {salaryDays:0.##}",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "السنة", "الشهر",
            "عدد أيام التأخير الصباحي", "الإجراء التأديبي (المادة 7)", "أيام الحسم من الراتب", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var m in rows)
        {
            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = m.JobNumber;
            ws.Cell(row, 3).Value = m.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, m.JobNumber);
            ws.Cell(row, 5).Value = m.Year;
            ws.Cell(row, 6).Value = m.Month;
            ws.Cell(row, 7).Value = m.LateDays;
            ws.Cell(row, 8).Value = Article7Label(m.Action);
            ws.Cell(row, 9).Value = m.Action == DisciplinaryActionType.TwoDaysSalaryDeduction
                ? 2.0
                : 0.0;
            ws.Cell(row, 10).Value = RefArticle7;
            row++;
        }

        int lastData = row - 1;

        if (rows.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد حالات تأخير صباحي متكرر في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 7).Value = rows.Sum(r => r.LateDays);
        ws.Cell(row, 9).Value = salaryDays;
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 9);
        if (lastData >= header)
        {
            ws.Range(header, 9, lastData, 9).Style.NumberFormat.Format = "#,##0.00";
        }

        SetColumnWidths(ws, 5, 14, 24, 26, 8, 8, 16, 26, 14, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| التأخير الصباحي = استئذان يبدأ عند 08:30 أو قبله وينتهي بعده في اليوم نفسه ولم يتجاوز 4 ساعات؛ 3 تأخيرات = تنبيه، 4 = إنذار، وأكثر من 4 = حسم يومين من الراتب."));
    }
    /// <summary>ورقة خصم المكافأة الشهرية عند تجاوز 15 يوم غياب.</summary>
    private static void AddBonusSheet(
        XLWorkbook workbook,
        IReadOnlyList<DeparturesReportReview> rows,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 13;

        var ws = CreateReportSheet(
            workbook, "خصم المكافأة - تجاوز 15 يوماً",
            "تقرير خصم المكافأة الشهرية — تجاوز إجمالي الغياب 15 يوماً",
            $"{PeriodLabel(period)}  |  الحالات (موظف/شهر): {rows.Count}  |  " +
            $"الموظفون: {rows.Select(r => r.JobNumber).Distinct(StringComparer.Ordinal).Count()}  |  " +
            $"الأثر: خصم 50% من المكافأة الشهرية",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "السنة", "الشهر",
            "إجازة سنوية (يوم)", "إجازة مرضية (يوم)", "أيام المادة 118/ب", "أيام المادة 118/ج",
            "إجمالي الغياب (يوم)", "نسبة خصم المكافأة %", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = r.JobNumber;
            ws.Cell(row, 3).Value = r.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, r.JobNumber);
            ws.Cell(row, 5).Value = r.Year;
            ws.Cell(row, 6).Value = r.Month;
            ws.Cell(row, 7).Value = Math.Round(r.AnnualLeaveDays, 2);
            ws.Cell(row, 8).Value = Math.Round(r.SickLeaveDays, 2);
            ws.Cell(row, 9).Value = Math.Round(r.Article118bDays, 2);
            ws.Cell(row, 10).Value = Math.Round(r.Article118cDays, 2);
            ws.Cell(row, 11).Value = Math.Round(r.TotalAbsenceDays, 2);
            ws.Cell(row, 12).Value = r.BonusDeductionPercent * 100;
            ws.Cell(row, 13).Value = RefBonus;
            row++;
        }

        int lastData = row - 1;

        if (rows.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد حالات تجاوزت 15 يوم غياب في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 7).Value = Math.Round(rows.Sum(r => r.AnnualLeaveDays), 2);
        ws.Cell(row, 8).Value = Math.Round(rows.Sum(r => r.SickLeaveDays), 2);
        ws.Cell(row, 9).Value = Math.Round(rows.Sum(r => r.Article118bDays), 2);
        ws.Cell(row, 10).Value = Math.Round(rows.Sum(r => r.Article118cDays), 2);
        ws.Cell(row, 11).Value = Math.Round(rows.Sum(r => r.TotalAbsenceDays), 2);
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 12);
        if (lastData >= header)
        {
            ws.Range(header, 7, lastData, 11).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(header, 12, lastData, 12).Style.NumberFormat.Format = "#,##0.00";
        }

        SetColumnWidths(ws, 5, 14, 24, 26, 8, 8, 13, 13, 13, 13, 14, 15, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| إجمالي الغياب = الإجازة السنوية + المرضية + أيام 118/ب + أيام 118/ج، وإذا تجاوز 15 يوماً فيُخصم 50% من مكافأة ذلك الشهر."));
    }
    // =====================================================================
    //  ورقة الإجازات المرضية (المادة 112) وورقة رصيد الإجازة السنوية
    // =====================================================================

    private static void AddSickLeaveSheet(
        XLWorkbook workbook,
        IReadOnlyList<SickLeaveRow> rows,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 13;

        int reduced = rows.Count(r => r.Days > LegalRules.Sick_Tier1_MaxDays);

        var ws = CreateReportSheet(
            workbook, "الإجازات المرضية - المادة 112",
            "تقرير الإجازات المرضية وتدرّج نسبة الراتب (المادة 112)",
            $"{PeriodLabel(period)}  |  الحالات (موظف/سنة): {rows.Count}  |  " +
            $"انخفضت نسبتها عن 100%: {reduced} حالة  |  " +
            $"إجمالي الأيام المرضية: {Math.Round(rows.Sum(r => r.Days), 2):0.##}",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "السنة", "عدد النوبات المرضية",
            "إجمالي الأيام المرضية", "100% (حتى 120 يوم)", "75% (من 121 إلى 240)",
            "50% (من 241 إلى 360)", "بلا راتب (بعد 360)", "النسبة المستحقة %", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var s in rows)
        {
            double days = s.Days;

            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = s.JobNumber;
            ws.Cell(row, 3).Value = s.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, s.JobNumber);
            ws.Cell(row, 5).Value = s.Year;
            ws.Cell(row, 6).Value = s.Spells;
            ws.Cell(row, 7).Value = days;
            ws.Cell(row, 8).Value = Math.Round(Math.Min(days, LegalRules.Sick_Tier1_MaxDays), 2);
            ws.Cell(row, 9).Value = Math.Round(Math.Clamp(days - LegalRules.Sick_Tier1_MaxDays, 0, 120), 2);
            ws.Cell(row, 10).Value = Math.Round(Math.Clamp(days - LegalRules.Sick_Tier2_MaxDays, 0, 120), 2);
            ws.Cell(row, 11).Value = Math.Round(Math.Max(0, days - LegalRules.Sick_Tier3_MaxDays), 2);
            ws.Cell(row, 12).Value = LegalRules.SickSalaryPercentage((int)Math.Ceiling(days));
            ws.Cell(row, 13).Value = Ref112;
            row++;
        }

        int lastData = row - 1;

        if (rows.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد إجازات مرضية في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 6).Value = rows.Sum(r => r.Spells);
        ws.Cell(row, 7).Value = Math.Round(rows.Sum(r => r.Days), 2);
        ws.Cell(row, 8).Value = Math.Round(rows.Sum(r => Math.Min(r.Days, LegalRules.Sick_Tier1_MaxDays)), 2);
        ws.Cell(row, 11).Value = Math.Round(rows.Sum(r => Math.Max(0, r.Days - LegalRules.Sick_Tier3_MaxDays)), 2);
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        if (lastData >= header)
        {
            ws.Range(header, 7, lastData, 11).Style.NumberFormat.Format = "#,##0.00";
            var reducedRange = ws.Range(header, 12, lastData, 12);
            reducedRange.Style.NumberFormat.Format = "#,##0";
        }

        SetColumnWidths(ws, 5, 14, 24, 26, 8, 15, 15, 15, 16, 16, 15, 13, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| التدرّج: 100% حتى 120 يوماً، 75% من 121 إلى 240، 50% من 241 إلى 360، وبعد 360 يوماً لا يُصرف راتب، ولا تُرحَّل الأيام المرضية إلى السنة التالية (المادة 112)."));
    }
    private static void AddAnnualBalanceSheet(
        XLWorkbook workbook,
        IReadOnlyList<LeaveUsageRow> rows,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 11;

        int lowBalance = rows.Count(r =>
            LegalRules.AnnualLeaveFullEntitlement - r.AnnualDays < LowBalanceThresholdDays);

        var ws = CreateReportSheet(
            workbook, "رصيد الإجازة السنوية",
            "تقرير استخدام الإجازة السنوية والرصيد المتبقي (المواد 100/د و101)",
            $"{PeriodLabel(period)}  |  الحالات (موظف/سنة): {rows.Count}  |  " +
            $"الاستحقاق السنوي المعياري: {LegalRules.AnnualLeaveFullEntitlement} يوماً  |  " +
            $"رصيد منخفض (< {LowBalanceThresholdDays:0.#} يوم): {lowBalance} حالة",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية", "السنة",
            "الاستحقاق السنوي (يوم)", "الإجازة السنوية المستخدمة (يوم)", "الرصيد المتبقي (يوم)",
            "إجازة مرضية (يوم)", "حالة الرصيد", "المرجع القانوني");

        int row = header;
        int index = 1;

        foreach (var u in rows)
        {
            double remaining = Math.Round(LegalRules.AnnualLeaveFullEntitlement - u.AnnualDays, 2);
            bool low = remaining < LowBalanceThresholdDays;

            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = u.JobNumber;
            ws.Cell(row, 3).Value = u.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, u.JobNumber);
            ws.Cell(row, 5).Value = u.Year;
            ws.Cell(row, 6).Value = LegalRules.AnnualLeaveFullEntitlement;
            ws.Cell(row, 7).Value = u.AnnualDays;
            ws.Cell(row, 8).Value = remaining;
            ws.Cell(row, 9).Value = u.SickDays;
            ws.Cell(row, 10).Value = remaining < 0
                ? "تجاوز الاستحقاق السنوي (رصيد سالب)"
                : low ? "رصيد منخفض — يستوجب المراجعة" : "ضمن الحد";
            ws.Cell(row, 11).Value = Ref100;
            row++;
        }

        int lastData = row - 1;

        if (rows.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد بيانات إجازات سنوية في الملف المعالج.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 7).Value = Math.Round(rows.Sum(r => r.AnnualDays), 2);
        ws.Cell(row, 8).Value = Math.Round(rows.Sum(r => LegalRules.AnnualLeaveFullEntitlement - r.AnnualDays), 2);
        ws.Cell(row, 9).Value = Math.Round(rows.Sum(r => r.SickDays), 2);
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 10);
        if (lastData >= header)
        {
            ws.Range(header, 7, lastData, 9).Style.NumberFormat.Format = "#,##0.00";
        }

        SetColumnWidths(ws, 5, 14, 24, 26, 8, 14, 17, 15, 14, 24, 30);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| الرصيد المتبقي محسوب على الاستحقاق السنوي المعياري (30 يوماً) مطروحاً منه أيام الإجازة السنوية المعتمدة في التقرير؛ أما الاحتساب التناسبي للموظف الجديد وسقف الترحيل فيتطلبان تاريخ التعيين وبيانات الرصيد المُرحَّل."));
    }
    // =====================================================================
    //  ورقة كشف الخصومات المُجمَّع + ورقة منهجية الاحتساب
    // =====================================================================

    private static void AddDeductionLedgerSheet(
        XLWorkbook workbook,
        IReadOnlyList<DeductionLedgerRow> rows,
        IReadOnlyDictionary<string, string> administrations,
        ReportPeriod period,
        DateTime generatedAt)
    {
        const int columns = 13;

        double totalLeaveDays = Math.Round(rows.Sum(r => r.Days118b + r.Days118c), 2);
        double totalSalaryDays = Math.Round(rows.Sum(r => r.SalaryDays), 2);

        var ws = CreateReportSheet(
            workbook, "كشف الخصومات المُجمَّع",
            "كشف الخصومات المُجمَّع لكل موظف — للتسوية مع الإجازات والرواتب",
            $"{PeriodLabel(period)}  |  الموظفون ذوو الخصومات: {rows.Count}  |  " +
            $"إجمالي أيام الخصم من الإجازات: {totalLeaveDays:0.##}  |  " +
            $"إجمالي أيام الحسم من الراتب: {totalSalaryDays:0.##}",
            columns);

        int header = WriteHeader(ws, 4,
            "م", "الرقم الوظيفي", "الموظف", "الإدارة الرئيسية",
            "أسابيع ≥ 60 دقيقة (118/ج)", "أيام خصم 118/ج", "طلبات > 4 ساعات (118/ب)", "أيام خصم 118/ب",
            "إجمالي أيام الخصم من الإجازات", "مرات حسم الراتب (المادة 7)", "أيام الحسم من الراتب",
            "أشهر خصم المكافأة (50%)", "ملاحظات");

        int row = header;
        int index = 1;

        foreach (var x in rows)
        {
            double leaveDays = Math.Round(x.Days118b + x.Days118c, 2);
            var notes = new List<string>(3);

            if (x.Days118c > 0)
            {
                notes.Add($"{x.WeeksOver60} أسبوع بلغ 60 دقيقة (118/ج)");
            }

            if (x.Days118b > 0)
            {
                notes.Add($"{x.Over4Requests} استئذان تجاوز 4 ساعات (118/ب)");
            }

            if (x.SalaryDays > 0)
            {
                notes.Add($"{x.PenaltyMonths} شهر أكثر من 4 تأخيرات صباحية (المادة 7)");
            }

            if (x.BonusMonths > 0)
            {
                notes.Add($"{x.BonusMonths} شهر تجاوز 15 يوم غياب (خصم 50% من المكافأة)");
            }

            ws.Cell(row, 1).Value = index++;
            ws.Cell(row, 2).Value = x.JobNumber;
            ws.Cell(row, 3).Value = x.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = Administration(administrations, x.JobNumber);
            ws.Cell(row, 5).Value = x.WeeksOver60;
            ws.Cell(row, 6).Value = x.Days118c;
            ws.Cell(row, 7).Value = x.Over4Requests;
            ws.Cell(row, 8).Value = x.Days118b;
            ws.Cell(row, 9).Value = leaveDays;
            ws.Cell(row, 10).Value = x.PenaltyMonths;
            ws.Cell(row, 11).Value = x.SalaryDays;
            ws.Cell(row, 12).Value = x.BonusMonths;
            ws.Cell(row, 13).Value = string.Join(" | ", notes);
            row++;
        }

        int lastData = row - 1;

        if (rows.Count == 0)
        {
            ws.Cell(row, 1).Value = "لا توجد خصومات مستحقة في البيانات المعالجة.";
            row++;
        }

        ws.Cell(row, 3).Value = "الإجمالي";
        ws.Cell(row, 5).Value = rows.Sum(r => r.WeeksOver60);
        ws.Cell(row, 6).Value = Math.Round(rows.Sum(r => r.Days118c), 2);
        ws.Cell(row, 7).Value = rows.Sum(r => r.Over4Requests);
        ws.Cell(row, 8).Value = Math.Round(rows.Sum(r => r.Days118b), 2);
        ws.Cell(row, 9).Value = totalLeaveDays;
        ws.Cell(row, 10).Value = rows.Sum(r => r.PenaltyMonths);
        ws.Cell(row, 11).Value = totalSalaryDays;
        ws.Cell(row, 12).Value = rows.Sum(r => r.BonusMonths);
        int totalRow = row;

        StyleTable(ws, header, Math.Max(lastData, header), columns);
        StyleTotalRow(ws, totalRow, columns);
        HighlightWarnings(ws, header, lastData, 9);
        HighlightWarnings(ws, header, lastData, 11);
        if (lastData >= header)
        {
            var days457 = ws.Range(header, 6, lastData, 6);
            days457.Style.NumberFormat.Format = "#,##0.00";
            var days87 = ws.Range(header, 8, lastData, 9);
            days87.Style.NumberFormat.Format = "#,##0.00";
            var salary = ws.Range(header, 11, lastData, 11);
            salary.Style.NumberFormat.Format = "#,##0.00";
        }

        SetColumnWidths(ws, 5, 14, 24, 18, 13, 12, 13, 12, 14, 13, 13, 13, 46);
        AddFooterNote(ws, totalRow + 2, columns,
            FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"),
                "| عمود «إجمالي أيام الخصم من الإجازات» = أيام 118/ب + أيام 118/ج (تُخصم من رصيد الإجازات)، وعمودا الحسم من الراتب والمكافأة يُسوَّيان في كشف الرواتب."));
    }
    /// <summary>ورقة منهجية الاحتساب: كيف حُوِّلت صفوف التقرير إلى نتائج، وحدود البيانات المتاحة.</summary>
    private static void AddMethodologySheet(XLWorkbook workbook, DateTime generatedAt)
    {
        const int columns = 2;

        var ws = CreateReportSheet(
            workbook, "منهجية الاحتساب",
            "منهجية الاحتساب القانوني وحدود البيانات",
            "خطوات المعالجة ومصادر القيم في كل تقرير، وحالات البيانات التي تتطلب تدقيقاً بشرياً قبل التسوية النهائية.",
            columns);

        int header = WriteHeader(ws, 4, "المحور", "البيان");

        (string Axis, string Text)[] items =
        {
            ("المصدر",
             "ملف «تقرير المغادرات» (تصدير الطلبات العربية) المستورد إلى جدول المرحلة، أو الصفوف المسحوبة مباشرةً من قاعدة البيانات عند تفعيل الربط المباشر."),
            ("الطلبات المعتبرة",
             "الطلبات بحالة «مقبول» فقط، مع وجود تاريخ (تاريخ المغادرة وإلا تاريخ الطلب)؛ وما عدا ذلك يُستثنى من كل التقارير."),
            ("دور الدوام الرسمي",
             "08:30 – 15:30 (7 ساعات = 420 دقيقة)، وتُحتسب الدقائق الواقعة داخل النافذة فقط لغرض التجميع الأسبوعي."),
            ("التجميع الأسبوعي (118/ج)",
             "لكل موظف ولكل أسبوع ISO (الاثنين–الأحد) يُجمع مجموع دقائق الاستئذانات الواقعة داخل الدوام؛ فإذا بلغ 60 دقيقة أو أكثر استحق خصم يوم كامل. تُستثنى الطلبات التي تجاوزت 4 ساعات لعدم الازدواج مع المادة 118/ب، وتُحتسب الطلبات ذات الأوقات المتاحة فقط."),
            ("نسبة الأسبوع إلى الشهر",
             "تُنسب نتيجة الأسبوع إلى شهر أول مغادرة محتسبة داخل الأسبوع، وهذا هو أساس بناء الملخّص الشهري."),
            ("المادة 118/ب",
             "كل استئذان مقبول يتجاوز 240 دقيقة (4 ساعات) في اليوم الواحد = خصم يوم كامل من رصيد الإجازة السنوية."),
            ("المادة 7 (التأخير الصباحي)",
             "يُعدّ التأخير الصباحي من الاستئذانات التي تبدأ عند 08:30 أو قبله وتنتهي بعده في اليوم نفسه ولم تتجاوز 4 ساعات، ويُحسب عدد الأيام المختلفة لكل شهر: 3 = تنبيه خطي، 4 = إنذار خطي، وأكثر من 4 = حسم يومين من الراتب."),
            ("قاعدة الـ 15 يوماً",
             "إجمالي الغياب الشهري = الإجازة السنوية + المرضية + أيام 118/ب + أيام 118/ج؛ وإذا تجاوز 15 يوماً يُخصم 50% من مكافأة ذلك الشهر."),
            ("المادة 112",
             "تُجمع أيام الإجازة المرضية لكل موظف في السنة المالية ثم يُطبَّق التدرّج: 100% حتى 120 يوماً، 75% من 121 إلى 240، 50% من 241 إلى 360، وبلا راتب بعد 360 يوماً، ولا تُرحَّل إلى السنة التالية."),
            ("المواد 100/د و101 و105",
             "الاحتساب التناسبي للموظف الجديد ومنع التراكم لأكثر من سنتين وسقف تعويض نهاية الخدمة (60 يوماً) تتطلب تاريخ التعيين وأرصدة الإجازات المُرحَّلة وبيانات انتهاء الخدمة، وهي غير متوفرة في تقرير المغادرات؛ لذا تُدرَج في «دليل القواعد القانونية» للمرجعية فقط."),
            ("حدود البيانات",
             "التقرير لا يحتوي على بصمات الحضور والانصراف الفعلية ولا على الرصيد المُرحَّل ولا التعويضات؛ لذا يمثّل التأخير الصباحي المرصود في هذا الملف ما هو مُوثَّق بالطلبات فقط."),
            ("المراجعة البشرية",
             "النتائج محسوبة آلياً وفق القواعد أعلاه، ويُوصى بمراجعتها من الجهة المختصة (الشؤون القانونية / الموارد البشرية) قبل التسوية النهائية للرواتب والإجازات."),
            ("مصدر كل ورقة",
             "«المتأخرون 60 دقيقة» و«الاستئذان فوق 4 ساعات» و«التأخير الصباحي» و«الإجازات المرضية» و«رصيد الإجازة» تُبنى من جدول المرحلة، و«خصم المكافأة» و«كشف الخصومات» من نتائج المراجعة المحفوظة، و«المتأخرون 60 دقيقة» من جدول التجميع الأسبوعي.")
        };

        int row = header;
        foreach (var item in items)
        {
            ws.Cell(row, 1).Value = item.Axis;
            ws.Cell(row, 2).Value = item.Text;
            ws.Range(row, 1, row, columns).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 44;
            row++;
        }

        StyleTable(ws, header, row - 1, columns);
        SetColumnWidths(ws, 30, 110);
        AddFooterNote(ws, row + 1, columns, FooterSignature(generatedAt.ToString("yyyy-MM-dd HH:mm"), string.Empty));
    }
}

