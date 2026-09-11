using AttendanceApi.Audit;
using AttendanceApi.Domain;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// التقارير القانونية الاحترافية من بصمات الحضور والانصراف:
/// ورقة مستقلة لكل قاعدة (المادة 7، المادة 118/ب، المادة 118/ج، قاعدة الـ 15 يوماً، المادة 112)
/// + ملخص تنفيذي ودليل قواعد وكشف خصومات مُجمَّع ولوحة التزام الإدارات وورقة منهجية وجودة بيانات.
/// </summary>
public sealed partial class PunchReportService
{
    // ---- الهوية البصرية ----
    private static readonly XLColor PunchBrandFill = XLColor.FromHtml("#12314F");
    private static readonly XLColor PunchHeaderFill = XLColor.FromHtml("#1B4B7A");
    private static readonly XLColor PunchStripFill = XLColor.FromHtml("#F7F9FC");
    private static readonly XLColor PunchTotalFill = XLColor.FromHtml("#DCE6F1");
    private static readonly XLColor PunchWarnFill = XLColor.FromHtml("#FCE4E4");
    private static readonly XLColor PunchWarnFont = XLColor.FromHtml("#B00020");
    private static readonly XLColor PunchGoodFont = XLColor.FromHtml("#1B7A3D");
    private static readonly XLColor PunchMutedFont = XLColor.FromHtml("#5A6B7B");
    private static readonly XLColor PunchBorderLine = XLColor.FromHtml("#B8C4D0");

    private const string PunchSourceLabel = "تقرير الحضور والانصراف (البصمات الخام) — نظام الدخول والخروج";

    /// <summary>بناء ملف Excel للتقارير القانونية (16 ورقة) من نتائج تحليل البصمات.</summary>
    public async Task<byte[]> BuildLegalReportsWorkbookAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var summary = await GetAnalysisSummaryAsync(ct);

        if (!summary.HasData)
        {
            throw new InvalidOperationException(
                "لا توجد نتائج تحليل محفوظة — استورد ملف البصمات ثم شغّل التحليل أولاً.");
        }

        var daily = await _db.PunchDailyResults.AsNoTracking().ToListAsync(ct);
        var weekly = await _db.PunchWeeklyResults.AsNoTracking().ToListAsync(ct);
        var monthly = await _db.PunchMonthlyResults.AsNoTracking().ToListAsync(ct);
        var compliance = await GetComplianceByDepartmentAsync(1, ct);
        var staging = await GetStagingSummaryAsync(ct);
        var calendar = await LoadCalendarAsync(ct);

        var generatedAt = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
        // قاعدة المادة 118/ج السارية (مرنة): تُستخدم في ترويسات الأوراق وأعمدة التقارير ودليل القواعد.
        var rule = WeeklyRule;

        using var workbook = new XLWorkbook();

        AddExecutiveSummarySheet(workbook, summary, generatedAt, rule);
        AddRulesSheet(workbook, summary, generatedAt, rule);
        AddArticle7Sheet(workbook, monthly, generatedAt);
        AddDailyViolationsSheet(workbook, daily, generatedAt);
        AddWeekly118cSheet(workbook, weekly, generatedAt, rule);
        AddArticle118bSheet(workbook, daily, generatedAt);
        AddAbsenceSheet(workbook, monthly, generatedAt);
        AddIncompleteSheet(workbook, daily, generatedAt);
        AddAnnualBalanceUsageSheet(workbook, monthly, generatedAt);
        AddBonusSheet(workbook, monthly, generatedAt);
        AddSickLeaveSheet(workbook, monthly, generatedAt);
        AddComplianceSheet(workbook, compliance, generatedAt, rule);
        AddDeductionLedgerSheet(workbook, monthly, weekly, generatedAt, rule);
        await AddHolidayCalendarSheet(workbook, generatedAt, ct);
        AddHolidayWorkSheet(workbook, daily, generatedAt);
        AddOvertimeFlexibleSheet(workbook, monthly, generatedAt, rule);
        AddMethodologySheet(workbook, summary, staging, calendar, generatedAt, rule, _weeklyRules);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);

        _logger.LogInformation(
            "تم بناء التقارير القانونية للبصمات: {Employees} موظفاً، {Daily} حالة يومية، {Weekly} أسبوعاً، {Monthly} شهراً.",
            summary.EmployeesAnalyzed, daily.Count, weekly.Count, monthly.Count);

        return ms.ToArray();
    }

    /// <summary>نص الفترة المنسوبة للتقرير.</summary>
    private static string PeriodLabel(PunchAnalysisSummary summary) =>
        summary.PeriodFrom.HasValue && summary.PeriodTo.HasValue
            ? $"من {summary.PeriodFrom:yyyy/MM/dd} إلى {summary.PeriodTo:yyyy/MM/dd}"
            : "الفترة غير محدّدة";

    /// <summary>نص التوقيع أسفل كل ورقة.</summary>
    private static string Signature(string generatedAt, string rule) =>
        $"المصدر: {PunchSourceLabel} | الأساس القانوني: {rule} | أُنشئ في: {generatedAt}";

    /// <summary>إنشاء ورقة تقرير موحّدة الشكل (عنوان + سطر وصفي).</summary>
    private static IXLWorksheet CreateSheet(
        XLWorkbook workbook, string name, string title, string subtitle, int columns)
    {
        var ws = workbook.Worksheets.Add(name);
        ws.RightToLeft = true;
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 11;

        var titleRange = ws.Range(1, 1, 1, Math.Max(1, columns));
        titleRange.Merge();
        titleRange.Value = title;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 14;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Fill.BackgroundColor = PunchBrandFill;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 26;

        var subtitleRange = ws.Range(2, 1, 2, Math.Max(1, columns));
        subtitleRange.Merge();
        subtitleRange.Value = subtitle;
        subtitleRange.Style.Font.FontSize = 10;
        subtitleRange.Style.Font.FontColor = PunchMutedFont;
        subtitleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Row(2).Height = 18;
        ws.Row(3).Height = 6;

        return ws;
    }

    /// <summary>كتابة صف الترويسة وتنسيقه.</summary>
    private static int WriteHeader(IXLWorksheet ws, int row, params string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(row, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = PunchHeaderFill;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }

        ws.Row(row).Height = 30;
        return row + 1;
    }

    /// <summary>تنسيق جدول البيانات (حدود + تظليل متبادل + تجميد + فلتر).</summary>
    private static void StyleTable(IXLWorksheet ws, int headerRow, int lastRow, int columns)
    {
        var range = ws.Range(headerRow, 1, Math.Max(headerRow, lastRow), columns);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.Style.Border.OutsideBorderColor = PunchBorderLine;

        for (int r = headerRow + 2; r <= lastRow; r += 2)
        {
            ws.Range(r, 1, r, columns).Style.Fill.BackgroundColor = PunchStripFill;
        }

        ws.SheetView.FreezeRows(headerRow);
        if (lastRow > headerRow)
        {
            ws.Range(headerRow, 1, lastRow, columns).SetAutoFilter();
        }
    }

    /// <summary>تظليل خلية تحذيرية.</summary>
    private static void MarkWarning(IXLCell cell)
    {
        cell.Style.Fill.BackgroundColor = PunchWarnFill;
        cell.Style.Font.FontColor = PunchWarnFont;
        cell.Style.Font.Bold = true;
    }

    /// <summary>تذييل الورقة (المصدر + الأساس القانوني + تاريخ الإنشاء).</summary>
    private static void AddFooter(IXLWorksheet ws, int row, int columns, string signature)
    {
        var range = ws.Range(row + 1, 1, row + 1, Math.Max(1, columns));
        range.Merge();
        range.Value = signature;
        range.Style.Font.FontSize = 9;
        range.Style.Font.FontColor = PunchMutedFont;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    /// <summary>عرض الأعمدة دفعة واحدة.</summary>
    private static void SetWidths(IXLWorksheet ws, params double[] widths)
    {
        for (int i = 0; i < widths.Length; i++)
        {
            ws.Column(i + 1).Width = widths[i];
        }
    }

    /// <summary>اسم الشهر بالعربية (١–١٢).</summary>
    private static string MonthName(int month) => month switch
    {
        1 => "كانون الثاني (1)",
        2 => "شباط (2)",
        3 => "آذار (3)",
        4 => "نيسان (4)",
        5 => "أيار (5)",
        6 => "حزيران (6)",
        7 => "تموز (7)",
        8 => "آب (8)",
        9 => "أيلول (9)",
        10 => "تشرين الأول (10)",
        11 => "تشرين الثاني (11)",
        12 => "كانون الأول (12)",
        _ => month.ToString()
    };

    /// <summary>اسم حالة اليوم بالعربية.</summary>
    private static string StatusLabel(PunchDayStatus status) => status switch
    {
        PunchDayStatus.Complete => "مكتملة",
        PunchDayStatus.Incomplete => "غير مكتملة (بلا انصراف)",
        PunchDayStatus.Absent => "غياب",
        PunchDayStatus.NoData => "لا بيانات",
        PunchDayStatus.Weekend => "عطلة الأسبوع",
        PunchDayStatus.OfficialHoliday => "عطلة رسمية",
        PunchDayStatus.AnnualLeave => "إجازة سنوية",
        PunchDayStatus.SickLeave => "إجازة مرضية",
        PunchDayStatus.CompensatoryLeave => "إجازة تعويضية",
        PunchDayStatus.BereavementLeave => "إجازة وفاة",
        PunchDayStatus.EmergencyPermission => "استئذان طارئ",
        PunchDayStatus.MedicalPermission => "استئذان طبي",
        PunchDayStatus.OfficialMission => "مهمة عمل رسمية",
        PunchDayStatus.Secondment => "انتداب رسمي",
        PunchDayStatus.Training => "تدريب رسمي",
        PunchDayStatus.WeekendWork => "دوام في عطلة الأسبوع",
        PunchDayStatus.HolidayWork => "دوام في عطلة رسمية",
        PunchDayStatus.ShiftDuty => "وردية دوام (جدول الورديات)",
        PunchDayStatus.ShiftRest => "راحة (جدول الورديات)",
        PunchDayStatus.ShiftLeave => "إجازة (جدول الورديات)",
        PunchDayStatus.ShiftHoliday => "عطلة (جدول الورديات)",
        PunchDayStatus.ShiftTraining => "دورة/مهمة (جدول الورديات)",
        _ => "غير معروفة"
    };

    /// <summary>تنسيق الوقت (HH:mm) أو «—».</summary>
    private static string TimeLabel(TimeOnly? value) => value.HasValue ? value.Value.ToString("HH\\:mm") : "—";

    /// <summary>تنسيق الدقائق إلى نص مقروء.</summary>
    private static string MinutesLabel(int minutes) =>
        minutes <= 0 ? "—" : minutes < 60 ? $"{minutes} دقيقة" : $"{minutes / 60} س {(minutes % 60 == 0 ? "" : (minutes % 60) + " د")}".Trim();

    /// <summary>ورقة 1: الملخص التنفيذي.</summary>
    private static void AddExecutiveSummarySheet(
        XLWorkbook workbook,
        PunchAnalysisSummary s,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var ws = CreateSheet(workbook, "الملخص التنفيذي",
            "الملخص التنفيذي — المراجعة القانونية لبصمات الحضور والانصراف",
            $"{PeriodLabel(s)} | {PunchSourceLabel}", 3);

        int row = WriteHeader(ws, 4, "المؤشر", "القيمة", "ملاحظات");

        void Add(string label, object value, string note = "")
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = value.ToString();
            ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, 3).Value = note;
            ws.Cell(row, 3).Style.Font.FontColor = PunchMutedFont;
            row++;
        }

        void Section(string title)
        {
            var range = ws.Range(row, 1, row, 3);
            range.Merge();
            range.Value = title;
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = PunchTotalFill;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        Section("نطاق البيانات");
        Add("الفترة المشمولة", $"{s.PeriodFrom:yyyy/MM/dd} — {s.PeriodTo:yyyy/MM/dd}");
        Add("عدد الموظفين المُحلَّلين", s.EmployeesAnalyzed);
        Add("عدد صفوف البصمات المعالجة", s.RecordsAnalyzed);
        Add("حدّ السماح الصباحي المعتمد (دقيقة)", s.MorningGraceMinutes, "التأخير الأقل منه لا يُحتسب إجراءً تأديبياً");
        Add("أيام العمل المحتسبة", s.WorkingDays);
        Add("أيام عمل مكتملة (حضور + انصراف)", s.CompleteDays);
        Add("أيام غياب غير مبرّر", s.AbsentDays);
        Add("أيام عمل بلا بصمة انصراف", s.IncompleteDays);
        Add("أيام بلا بيانات في النظام (مستثناة)", s.NoDataDays, "سجلات ناقصة لا تسمح بالحكم على اليوم");
        Add("أيام عطلة نهاية الأسبوع (مستثناة)", s.WeekendDays, "لا تُحتسب غياباً ولا مخالفة");
        Add("أيام العطل الرسمية والدينية (مستثناة)", s.HolidayDays,
            $"عدد أيام العطل المعتمدة في التقويم: {s.CalendarHolidays}");
        Add("دوام فعلي في نهاية الأسبوع / العطل", $"{s.WeekendWorkDays} / {s.HolidayWorkDays}",
            "يُعرض للعلم (ساعات إضافية محتملة) ولا يُحتسب مخالفة");

        Section("نظام الورديات (الحراسة وبقية الإدارات ذات الورديات)");
        Add("أيام الورديات (الدوام) وفق الجدول الشهري", s.ShiftDutyDays,
            "لا تُقارَن بأوقات الدوام الرسمي (08:30 — 15:30)");
        Add("أيام الراحة وفق الجدول الشهري", s.ShiftRestDays, "لا تُحتسب غياباً");
        Add("أيام الإجازات وفق الجدول الشهري", s.ShiftLeaveDays, "وفق ما ورد في جدول مسؤول الورديات");
        Add("موظفون بنظام الورديات", s.ShiftEmployees,
            "تُحتسب أيامهم من «جدول الورديات الشهري» المستورد، ويُعفون من 118/ج والتأخير الصباحي حسب الإعدادات");

        Section("الالتزام بأوقات الدوام");
        Add("إجمالي دقائق التأخير الصباحي", s.TotalLatenessMinutes);
        Add("إجمالي دقائق الانصراف المبكر", s.TotalEarlyDepartureMinutes);
        Add("إجمالي دقائق المغادرة أثناء الدوام", s.TotalMidDayGapMinutes, "الفجوات بين جلسات البصمات (انصراف ثم حضور لاحق)");
        Add("أيام التأخير المحتسبة (≥ حدّ السماح)", s.LateIncidentDays);
        Add("موظفون لديهم تأخير صباحي", s.EmployeesWithLateIncidents);
        Add("موظفون استحقوا عقوبة المادة 7", s.EmployeesWithArticle7Penalty);
        Add("أيام حسم الراتب — المادة 7", s.TotalArticle7SalaryDays, "تنبيه/إنذار/حسم يومين حسب عدد التأخيرات الشهرية");

        Section($"المادة 118/ج — دمج التأخير الصباحي مع الانصراف المبكر (حدّ {rule.ThresholdMinutes} دقيقة أسبوعياً)");
        Add($"أسابيع بلغت {rule.ThresholdMinutes} دقيقة", s.WeeksOver60Minutes,
            $"المجموع الفعلي (تأخير صباحي + انصراف مبكر + مغادرة) {(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلغ")} {rule.ThresholdMinutes} دقيقة");
        Add("موظفون لديهم أسبوع مخالف", s.EmployeesOver60Minutes);
        Add("إجمالي الناتج المحتسب أسبوعياً", s.TotalWeeklyLateMinutes,
            rule.CapCountedMinutes
                ? $"لا يزيد عن {rule.EffectiveCapMinutes} دقيقة لكل أسبوع (ما زاد على السقف لا يُحتسب)"
                : "المجموع الفعلي بلا تقييد بسقف");
        Add($"أيام الخصم وفق المادة 118/ج ({rule.DeductionDaysText()} يوم/أسبوع)", s.TotalArticle118cDays);

        Section("المادة 118/ب — الغياب عن الدوام أكثر من 4 ساعات");
        Add("أيام تجاوزت 4 ساعات", s.DaysOver4Hours);
        Add("موظفون لديهم أيام > 4 ساعات", s.EmployeesOver4Hours);
        Add("أيام الخصم من الرصيد السنوي (118/ب)", s.TotalArticle118bDays);

        Section("الإجازات والغياب");
        Add("أيام الإجازة السنوية المسجّلة", s.TotalAnnualLeaveDays);
        Add("أيام الإجازة المرضية المسجّلة", s.TotalSickLeaveDays, "تخضع للشرائح 120/240/360 (المادة 112)");
        Add("أيام الاستئذان (طارئ/طبي)", s.TotalEmergencyPermissionDays);
        Add("أيام المهام الرسمية والانتداب والتدريب", s.TotalOfficialDutyDays, "لا تُحتسب غياباً");
        Add("إجمالي أيام الغياب المحتسبة لقاعدة الـ 15 يوماً", s.TotalAbsenceForBonusDays);

        Section("العمل الإضافي (الساعات المحتسبة طوال الشهر)");
        Add("إجمالي الساعات الإضافية المحتسبة", s.OvertimeHours,
            $"الحدّ: {PunchFlexibleRule.HoursText(rule.Overtime.EffectiveMaxMinutesPerDay)} يومياً"
            + $" و{PunchFlexibleRule.HoursText(rule.Overtime.EffectiveMaxMinutesPerMonth)} شهرياً");
        Add("إجمالي الساعات المعادلة (بعد نسب التعويض)", s.EquivalentOvertimeHours,
            $"عادي {PunchOvertimeRule.RateText(rule.Overtime.RegularRate)}"
            + $" — نهاية أسبوع {PunchOvertimeRule.RateText(rule.Overtime.WeekendRate)}"
            + $" — عطلة {PunchOvertimeRule.RateText(rule.Overtime.HolidayRate)}");
        Add("أيام العمل الإضافي المحتسبة", s.OvertimeDays);
        Add("موظفون لديهم عمل إضافي", s.OvertimeEmployees);
        Add("أيام دوام في نهاية الأسبوع / العطل محتسبة إضافياً", $"{s.OvertimeWeekendDays} / {s.OvertimeHolidayDays}");
        Add("دقائق خارج الدوام قبل التقييد (للتدقيق)", s.OvertimeRawMinutes);
        Add("دقائق استُبعدت وفق الحدود المعتمدة", s.OvertimeExcludedMinutes,
            "سقف يومي/شهري، أو بلا تصريح، أو أقل من المدة الدنيا المحتسبة");
        Add("دقائق معلَّقة بانتظار موافقة مسبقة", s.OvertimeNeedsApprovalMinutes,
            $"{s.OvertimeNeedsApprovalDays} يوماً فيها عمل خارج الدوام بلا تصريح ساري"
            + " — لا يُحتسب أي عمل إضافي بلا موافقة مسبقة");
        Add("موظفون بلغوا الحدّ الشهري للعمل الإضافي", s.EmployeesOverMonthlyOvertimeCap);

        Section("الدوام المرن (نافذة الحضور وإكمال الساعات)");
        Add("أيام الدوام المرن المحتسبة", s.FlexibleDays,
            $"{PunchTimeText.Format(rule.Flexible.EarliestArrivalTime)} — {PunchTimeText.Format(rule.Flexible.LatestArrivalTime)}"
            + $" وإكمال {PunchFlexibleRule.HoursText(rule.Flexible.EffectiveDailyMinutes)} يومياً");
        Add("موظفون لديهم دوام مرن", s.FlexibleEmployees);
        Add("إجمالي دقائق المرونة المستخدمة", s.FlexibleMinutes, "مقدار تحرّك نهاية الدوام عن نهاية الدوام الرسمي");

        Section("النتائج النهائية");
        Add("إجمالي أيام حسم الراتب (غياب + المادة 7)", s.TotalSalaryDeductionDays);
        Add("نسبة الشهور المتجاوزة 15 يوماً", $"{s.TotalBonusDeductionPercent}%", "تجاوز 15 يوماً ⇒ خصم 50% من المكافأة");
        Add("موظفون لديهم مخالفات مرصودة", s.EmployeesWithViolations);

        StyleTable(ws, 4, row - 1, 3);
        SetWidths(ws, 52, 26, 48);
        AddFooter(ws, row + 1, 3, Signature(generatedAt, "المواد 7 و112 و118/ب و118/ج + قاعدة الـ 15 يوماً"));
    }

    /// <summary>ورقة 2: دليل القواعد القانونية ومعايير الاحتساب.</summary>
    private static void AddRulesSheet(
        XLWorkbook workbook,
        PunchAnalysisSummary s,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var ws = CreateSheet(workbook, "دليل القواعد القانونية",
            "دليل القواعد القانونية ومعايير الاحتساب المطبَّقة على البصمات",
            $"نظام الخدمة المدنية الأردني وتعليمات الحضور والانصراف 2020 | حدّ السماح الصباحي المعتمد: {s.MorningGraceMinutes} دقيقة", 4);

        int row = WriteHeader(ws, 4, "القاعدة", "المرجع القانوني", "الشرط / العتبة المطبَّقة", "الأثر القانوني");

        void Rule(string name, string reference, string condition, string effect, bool warn = false)
        {
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = reference;
            ws.Cell(row, 3).Value = condition;
            ws.Cell(row, 4).Value = effect;
            if (warn)
            {
                MarkWarning(ws.Cell(row, 4));
            }

            row++;
        }

        Rule("ساعات الدوام الرسمي", "نظام الخدمة المدنية",
            "الحضور 08:30 — الانصراف 15:30 (7 ساعات = 420 دقيقة)",
            "تُحتسب التأخيرات والانصرافات المبكرة بالنسبة لهذه النافذة");
        Rule("التأخير الصباحي", "المادة 7 — تعليمات 2020",
            $"الحضور بعد 08:30 ببلوغ {s.MorningGraceMinutes} دقيقة أو أكثر (قابل للتخصيص من الإعدادات)",
            "تأخيران أو أقل: لا إجراء | 3: تنبيه خطي | 4: إنذار خطي | أكثر من 4: حسم يومين من الراتب");
        Rule("الغياب عن الدوام أكثر من 4 ساعات", "المادة 118/ب",
            "مجموع (التأخير الصباحي + الانصراف المبكر) في اليوم الواحد يزيد على 240 دقيقة",
            "خصم يوم كامل من الرصيد السنوي عن كل يوم");
        Rule("دمج التأخير الصباحي مع الانصراف المبكر", "المادة 118/ج",
            "يُدمج (التأخير الصباحي + الانصراف المبكر + المغادرة أثناء الدوام) في ناتج أسبوعي واحد خلال أسبوع العمل (الأحد — الخميس)",
            rule.CapCountedMinutes
                ? $"الناتج المعتمد للخصم والملخص الشهري لا يزيد عن {rule.EffectiveCapMinutes} دقيقة في الأسبوع"
                : "الناتج المعتمد للخصم والملخص الشهري هو المجموع الفعلي بلا تقييد بسقف", warn: true);
        Rule("المجموع الأسبوعي للتأخير والمغادرة", "المادة 118/ج",
            $"{(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} الناتج الأسبوعي المدمج {rule.ThresholdMinutes} دقيقة خلال أسبوع العمل (الأحد — الخميس)",
            $"خصم {rule.DeductionDaysText()} يوم عن كل أسبوع مخالف (وما زاد على الحدّ لا يُضاعف الخصم)", warn: true);
        Rule("المغادرة أثناء الدوام", "المادتان 118/ب و118/ج",
            $"انصراف ثم حضور لاحق في اليوم نفسه بفجوة تزيد على {LegalRules.PunchNoiseMinutes} دقائق (الفجوات الأصغر تُعدّ تكرار بصمة)",
            "تُضاف إلى دقائق الغياب عن الدوام ويُبنى عليها احتساب المادتين", warn: true);
        Rule("الغياب غير المبرّر", "نظام الخدمة المدنية — واجبات الموظف",
            "يوم عمل رسمي بلا أي بصمة حضور/انصراف ولا يوجد تسجيل إجازة أو عطلة أو مهمة رسمية",
            "حسم يوم من الراتب + المساءلة التأديبية");
        Rule("نقص بصمة الانصراف", "تعليمات الدوام الرسمي",
            "بصمة حضور مسجّلة دون بصمة انصراف (يوم غير مكتمل)",
            "يُدرج للتسوية وإبراز العذر خلال المدة النظامية");
        Rule("خصم المكافأة الشهرية", "تعليمات المكافأة الشهرية",
            "مجموع أيام الغياب الشهري (غياب + إجازة سنوية + مرضية + 118/ب + 118/ج) يزيد على 15 يوماً",
            "خصم 50% من مكافأة الشهر");
        Rule("الإجازة المرضية", "المادة 112",
            "تراكم أيام الإجازة المرضية: حتى 120 بأجر كامل، 121–240 بثلاثة أرباع الأجر، 241–360 بنصف الأجر",
            "ما يتجاوز 360 يوماً بلا أجر");
        Rule("الإجازات والمهام الرسمية", "نظام الخدمة المدنية",
            "إجازة سنوية/تعويضية/وفاة، مهمة عمل، انتداب، تدريب — بموافقة مسبقة",
            "لا تُحتسب غياباً ولا مخالفة (تُعرض للعلم)");
        Rule("أسبوع العمل المعتمد", "تعليمات الدوام الرسمي",
            "الأحد — الخميس، ويُقفل الأسبوع يوم الخميس",
            "يُبنى عليه التجميع الأسبوعي للمادة 118/ج");
        Rule("العطل الرسمية وعطلة الأسبوع", "قانون الخدمة المدنية",
            "الجمعة والسبت والعطل المعلنة",
            "لا تُحتسب أيام عمل؛ ويُعرض الدوام فيها للعلم (ساعات إضافية محتملة)");
        Rule("دمج صفوف اليوم الواحد", "ضبط جودة البيانات",
            "عند تكرار نفس الموظف/التاريخ تُعتمد أبكر بصمة حضور وأحدث بصمة انصراف، وتُرجَّح الحالة الأعلى حكماً",
            "يمنع ازدواج الاحتساب ويحفظ البصمات المتعددة للتدقيق");

        StyleTable(ws, 4, row - 1, 4);
        SetWidths(ws, 30, 32, 62, 52);
        AddFooter(ws, row + 1, 4, Signature(generatedAt, "نظام الخدمة المدنية — المواد 7 و112 و118"));
    }

    /// <summary>ورقة 3: التأخير الصباحي المتكرر (المادة 7).</summary>
    private static void AddArticle7Sheet(XLWorkbook workbook, IReadOnlyList<PunchMonthlyResult> monthly, string generatedAt)
    {
        var items = monthly
            .Where(m => m.LateIncidents > 0)
            .OrderByDescending(m => m.LateIncidents)
            .ThenBy(m => m.JobNumber)
            .ThenBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        var ws = CreateSheet(workbook, "التأخير الصباحي - المادة 7",
            "التأخير الصباحي المتكرر — المادة 7 (تعليمات الحضور والانصراف 2020)",
            $"عدد الحالات: {items.Count} | الإجراء: 3 تأخيرات تنبيه خطي، 4 إنذار خطي، أكثر من 4 حسم يومين من الراتب", 9);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "السنة", "الشهر",
            "أيام العمل المحتسبة", "أيام التأخير الصباحي", "الإجراء التأديبي", "أيام حسم الراتب");

        int penaltyCount = 0;
        foreach (var m in items)
        {
            ws.Cell(row, 1).Value = m.JobNumber;
            ws.Cell(row, 2).Value = m.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = m.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = m.Year;
            ws.Cell(row, 5).Value = MonthName(m.Month);
            ws.Cell(row, 6).Value = m.WorkingDays;
            ws.Cell(row, 7).Value = m.LateIncidents;
            ws.Cell(row, 8).Value = m.PenaltyText ?? LegalRules.DisciplinaryActionText(m.Penalty);
            ws.Cell(row, 9).Value = m.Article7SalaryDeductionDays;

            if (m.LateIncidents >= 3)
            {
                penaltyCount++;
                MarkWarning(ws.Cell(row, 7));
            }

            if (m.Article7SalaryDeductionDays > 0)
            {
                MarkWarning(ws.Cell(row, 9));
            }

            row++;
        }

        StyleTable(ws, 4, row - 1, 9);
        SetWidths(ws, 16, 26, 32, 8, 20, 14, 14, 30, 14);
        AddFooter(ws, row + 1, 9,
            $"{Signature(generatedAt, "المادة 7 — تعليمات 2020")} | حالات استوجبت إجراءً تأديبياً: {penaltyCount}");
    }

    /// <summary>ورقة 4: المخالفات اليومية التفصيلية (تأخير/انصراف مبكر/غياب عن الدوام).</summary>
    private static void AddDailyViolationsSheet(XLWorkbook workbook, IReadOnlyList<PunchDailyResult> daily, string generatedAt)
    {
        var items = daily
            .Where(d => d.IsMorningLate || d.EarlyDepartureMinutes > 0 || d.GapMinutes > 0 || d.IsAbsent || d.IsIncomplete)
            .OrderByDescending(d => d.AbsenceMinutes)
            .ThenBy(d => d.JobNumber)
            .ThenBy(d => d.WorkDate)
            .ToList();

        var ws = CreateSheet(workbook, "المخالفات اليومية",
            "المخالفات اليومية التفصيلية — التأخير والانصراف المبكر والمغادرة أثناء الدوام والغياب",
            $"عدد الحالات: {items.Count} موزّعة على {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً", 12);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "التاريخ", "حالة اليوم", "توقيت الحضور", "توقيت الإنصراف",
            "تأخير (دقيقة)", "انصراف مبكر (دقيقة)", "مغادرة أثناء الدوام (دقيقة)", "الغياب عن الدوام (دقيقة)", "ملاحظات التدقيق");

        foreach (var d in items)
        {
            ws.Cell(row, 1).Value = d.JobNumber;
            ws.Cell(row, 2).Value = d.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = d.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = d.WorkDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy/mm/dd";
            ws.Cell(row, 5).Value = StatusLabel(d.Status);
            ws.Cell(row, 6).Value = TimeLabel(d.ClockIn);
            ws.Cell(row, 7).Value = TimeLabel(d.ClockOut);
            ws.Cell(row, 8).Value = d.LatenessMinutes;
            ws.Cell(row, 9).Value = d.EarlyDepartureMinutes;
            ws.Cell(row, 10).Value = d.GapMinutes;
            ws.Cell(row, 11).Value = d.AbsenceMinutes;
            ws.Cell(row, 12).Value = d.Notes ?? "—";

            if (d.IsAbsent)
            {
                MarkWarning(ws.Cell(row, 5));
            }

            if (d.CountsFor118b)
            {
                MarkWarning(ws.Cell(row, 11));
            }

            if (d.GapMinutes > 0)
            {
                MarkWarning(ws.Cell(row, 10));
            }

            if (d.IsIncomplete)
            {
                MarkWarning(ws.Cell(row, 7));
            }

            row++;
        }

        StyleTable(ws, 4, row - 1, 12);
        SetWidths(ws, 16, 24, 30, 13, 24, 12, 12, 12, 16, 20, 18, 42);
        AddFooter(ws, row + 1, 12, Signature(generatedAt, "المواد 7 و118/ب + ضوابط الدوام الرسمي"));
    }

    /// <summary>ورقة 5: الناتج الأسبوعي (تأخير صباحي + انصراف مبكر) ببلوغ حدّ المادة 118/ج الساري.</summary>
    private static void AddWeekly118cSheet(
        XLWorkbook workbook,
        IReadOnlyList<PunchWeeklyResult> weekly,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var items = weekly
            .Where(w => w.Exceeds60Minutes)
            .OrderByDescending(w => w.TotalMinutes)
            .ThenBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .ToList();

        string capText = rule.CapCountedMinutes
            ? $"الناتج المحتسب لا يزيد عن {rule.EffectiveCapMinutes} دقيقة أسبوعياً"
            : "الناتج المحتسب = المجموع الفعلي بلا تقييد بسقف";

        var ws = CreateSheet(workbook, SafeSheetName($"المتأخرون {rule.ThresholdMinutes} دقيقة - 118ج"),
            $"دمج التأخير الصباحي مع الانصراف المبكر (ومعه المغادرة أثناء الدوام) — المادة 118/ج: "
                + $"{(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} {rule.ThresholdMinutes} دقيقة أسبوعياً، وخصم {rule.DeductionDaysText()} يوم عن كل أسبوع مخالف، {capText}",
            $"عدد الأسابيع المخالفة: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً | "
                + $"إجمالي الناتج المحتسب: {items.Sum(i => (long)i.CountedMinutes)} دقيقة | المخالفة عند {(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} {rule.ThresholdMinutes} دقيقة/أسبوع | أسبوع العمل: الأحد — الخميس", 13);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "بداية الأسبوع", "نهاية الأسبوع", "أيام محتسبة",
            "تأخير صباحي (دقيقة)", "انصراف مبكر (دقيقة)", "مغادرة أثناء الدوام (دقيقة)",
            "المجموع الفعلي (تأخير + انصراف مبكر)",
            rule.CapCountedMinutes ? $"الناتج المحتسب (≤ {rule.EffectiveCapMinutes} دقيقة)" : "الناتج المحتسب (المجموع الفعلي)",
            $"الخصم (أيام) — {rule.DeductionDaysText()}/أسبوع", "تفصيل أيام الأسبوع");

        foreach (var w in items)
        {
            ws.Cell(row, 1).Value = w.JobNumber;
            ws.Cell(row, 2).Value = w.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = w.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = w.WeekStart.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy/mm/dd";
            ws.Cell(row, 5).Value = w.WeekEnd.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 5).Style.DateFormat.Format = "yyyy/mm/dd";
            ws.Cell(row, 6).Value = w.DaysCounted;
            ws.Cell(row, 7).Value = w.LatenessMinutes;
            ws.Cell(row, 8).Value = w.EarlyDepartureMinutes;
            ws.Cell(row, 9).Value = w.GapMinutes;
            ws.Cell(row, 10).Value = w.TotalMinutes;
            ws.Cell(row, 11).Value = w.CountedMinutes;
            ws.Cell(row, 12).Value = w.DeductionDays;
            ws.Cell(row, 13).Value = w.Notes ?? "—";
            MarkWarning(ws.Cell(row, 11));
            MarkWarning(ws.Cell(row, 12));
            row++;
        }

        StyleTable(ws, 4, row - 1, 13);
        SetWidths(ws, 16, 24, 30, 13, 13, 12, 16, 16, 20, 22, 18, 12, 60);
        AddFooter(ws, row + 1, 13, Signature(generatedAt,
            $"المادة 118/ج — دمج التأخير الصباحي مع الانصراف المبكر بحدّ {rule.ThresholdMinutes} دقيقة أسبوعياً وخصم {rule.DeductionDaysText()} يوم"));
    }

    /// <summary>ورقة 6: الأيام التي تجاوز فيها الغياب عن الدوام 4 ساعات (المادة 118/ب).</summary>
    private static void AddArticle118bSheet(XLWorkbook workbook, IReadOnlyList<PunchDailyResult> daily, string generatedAt)
    {
        var items = daily
            .Where(d => d.CountsFor118b)
            .OrderByDescending(d => d.AbsenceMinutes)
            .ThenBy(d => d.JobNumber)
            .ThenBy(d => d.WorkDate)
            .ToList();

        var ws = CreateSheet(workbook, "الاستئذان فوق 4 ساعات - 118ب",
            "الأيام التي تجاوز فيها الغياب عن الدوام 4 ساعات — المادة 118/ب",
            $"عدد الأيام: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً | كل يوم = خصم يوم كامل من الرصيد السنوي", 12);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "التاريخ", "توقيت الحضور", "توقيت الإنصراف",
            "تأخير (دقيقة)", "انصراف مبكر (دقيقة)", "مغادرة أثناء الدوام (دقيقة)", "الغياب عن الدوام (دقيقة)", "الخصم من الرصيد (أيام)", "ملاحظات");

        foreach (var d in items)
        {
            ws.Cell(row, 1).Value = d.JobNumber;
            ws.Cell(row, 2).Value = d.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = d.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = d.WorkDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy/mm/dd";
            ws.Cell(row, 5).Value = TimeLabel(d.ClockIn);
            ws.Cell(row, 6).Value = TimeLabel(d.ClockOut);
            ws.Cell(row, 7).Value = d.LatenessMinutes;
            ws.Cell(row, 8).Value = d.EarlyDepartureMinutes;
            ws.Cell(row, 9).Value = d.GapMinutes;
            ws.Cell(row, 10).Value = d.AbsenceMinutes;
            ws.Cell(row, 11).Value = d.Article118bDays;
            ws.Cell(row, 12).Value = d.Notes ?? "—";
            MarkWarning(ws.Cell(row, 10));
            row++;
        }

        StyleTable(ws, 4, row - 1, 12);
        SetWidths(ws, 16, 24, 30, 13, 12, 12, 12, 16, 20, 18, 18, 42);
        AddFooter(ws, row + 1, 12, Signature(generatedAt, "المادة 118/ب — الغياب عن الدوام > 4 ساعات"));
    }

    /// <summary>ورقة 7: الغياب غير المبرّر شهرياً.</summary>
    private static void AddAbsenceSheet(XLWorkbook workbook, IReadOnlyList<PunchMonthlyResult> monthly, string generatedAt)
    {
        var items = monthly
            .Where(m => m.AbsentDays > 0)
            .OrderByDescending(m => m.AbsentDays)
            .ThenBy(m => m.JobNumber)
            .ThenBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        var ws = CreateSheet(workbook, "الغياب غير المبرر",
            "الغياب غير المبرّر — أيام عمل رسمية بلا بصمة ولا تسجيل إجازة",
            $"عدد الشهور: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً | إجمالي أيام الغياب: {items.Sum(i => i.AbsentDays)} يوم", 9);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "السنة", "الشهر", "أيام الغياب",
            "أيام حسم الراتب", "أيام العمل المحتسبة", "ملاحظات");

        foreach (var m in items)
        {
            ws.Cell(row, 1).Value = m.JobNumber;
            ws.Cell(row, 2).Value = m.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = m.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = m.Year;
            ws.Cell(row, 5).Value = MonthName(m.Month);
            ws.Cell(row, 6).Value = m.AbsentDays;
            ws.Cell(row, 7).Value = m.AbsentDays;
            ws.Cell(row, 8).Value = m.WorkingDays;
            ws.Cell(row, 9).Value = m.Notes ?? "—";
            MarkWarning(ws.Cell(row, 6));
            MarkWarning(ws.Cell(row, 7));
            row++;
        }

        StyleTable(ws, 4, row - 1, 9);
        SetWidths(ws, 16, 26, 32, 8, 20, 12, 14, 18, 40);
        AddFooter(ws, row + 1, 9, Signature(generatedAt, "المادة 118 + تعليمات الدوام الرسمي"));
    }

    /// <summary>ورقة 8: أيام العمل غير المكتملة (نقص بصمة الانصراف).</summary>
    private static void AddIncompleteSheet(XLWorkbook workbook, IReadOnlyList<PunchDailyResult> daily, string generatedAt)
    {
        var items = daily
            .Where(d => d.IsIncomplete)
            .OrderBy(d => d.JobNumber)
            .ThenBy(d => d.WorkDate)
            .ToList();

        var ws = CreateSheet(workbook, "نقص بصمة الانصراف",
            "أيام عمل غير مكتملة — بصمة حضور بلا بصمة انصراف (تحتاج تسوية وإبراز عذر)",
            $"عدد الأيام: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً", 7);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "التاريخ", "بصمة الحضور", "تأخير صباحي (دقيقة)", "الإجراء المطلوب");

        foreach (var d in items)
        {
            ws.Cell(row, 1).Value = d.JobNumber;
            ws.Cell(row, 2).Value = d.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = d.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = d.WorkDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy/mm/dd";
            ws.Cell(row, 5).Value = TimeLabel(d.ClockIn);
            ws.Cell(row, 6).Value = d.LatenessMinutes;
            ws.Cell(row, 7).Value = "تسوية البصمة أو إبراز عذر مقبول";
            MarkWarning(ws.Cell(row, 5));
            row++;
        }

        StyleTable(ws, 4, row - 1, 7);
        SetWidths(ws, 16, 26, 32, 13, 14, 16, 40);
        AddFooter(ws, row + 1, 7, Signature(generatedAt, "تعليمات الدوام الرسمي — ضبط البصمات"));
    }

    /// <summary>ورقة 9: الأيام المستنزفة من رصيد الإجازة السنوية (المادة 118/ب و118/ج).</summary>
    private static void AddAnnualBalanceUsageSheet(XLWorkbook workbook, IReadOnlyList<PunchMonthlyResult> monthly, string generatedAt)
    {
        var items = monthly
            .Where(m => m.Article118bDays > 0 || m.Article118cDays > 0)
            .OrderByDescending(m => m.Article118bDays + m.Article118cDays)
            .ThenBy(m => m.JobNumber)
            .ThenBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        var ws = CreateSheet(workbook, "رصيد الإجازة السنوية",
            "الأيام المستنزفة من رصيد الإجازة السنوية — المادتان 118/ب و118/ج",
            $"عدد الشهور: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً | إجمالي الأيام: {items.Sum(i => i.Article118bDays + i.Article118cDays):0.##} يوم", 10);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "السنة", "الشهر",
            "خصم 118/ب من الرصيد (أيام)", "خصم 118/ج (أيام)", "أسابيع ≥ 60 دقيقة", "إجمالي الأيام المستنزفة", "ملاحظات");

        foreach (var m in items)
        {
            double total = Math.Round(m.Article118bDays + m.Article118cDays, 2);
            ws.Cell(row, 1).Value = m.JobNumber;
            ws.Cell(row, 2).Value = m.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = m.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = m.Year;
            ws.Cell(row, 5).Value = MonthName(m.Month);
            ws.Cell(row, 6).Value = m.Article118bDays;
            ws.Cell(row, 7).Value = m.Article118cDays;
            ws.Cell(row, 8).Value = m.WeeksOver60Minutes;
            ws.Cell(row, 9).Value = total;
            ws.Cell(row, 10).Value = m.Notes ?? "—";
            MarkWarning(ws.Cell(row, 9));
            row++;
        }

        StyleTable(ws, 4, row - 1, 10);
        SetWidths(ws, 16, 26, 32, 8, 20, 20, 16, 16, 20, 40);
        AddFooter(ws, row + 1, 10, Signature(generatedAt, "المادتان 118/ب و118/ج — الخصم من الرصيد السنوي"));
    }

    /// <summary>ورقة 10: خصم المكافأة الشهرية (تجاوز 15 يوماً).</summary>
    private static void AddBonusSheet(XLWorkbook workbook, IReadOnlyList<PunchMonthlyResult> monthly, string generatedAt)
    {
        var items = monthly
            .Where(m => m.Exceeds15Days)
            .OrderByDescending(m => m.TotalAbsenceDays)
            .ThenBy(m => m.JobNumber)
            .ThenBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();

        var ws = CreateSheet(workbook, "خصم المكافأة - 15 يوم",
            "خصم المكافأة الشهرية — تجاوز مجموع أيام الغياب 15 يوماً (خصم 50%)",
            $"عدد الحالات: {items.Count} لـ {items.Select(i => i.JobNumber).Distinct().Count()} موظفاً", 12);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "السنة", "الشهر", "أيام الغياب",
            "إجازة سنوية", "إجازة مرضية", "خصم 118/ب", "خصم 118/ج", "إجمالي أيام الغياب", "نسبة خصم المكافأة");

        foreach (var m in items)
        {
            ws.Cell(row, 1).Value = m.JobNumber;
            ws.Cell(row, 2).Value = m.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = m.DepartmentName ?? "—";
            ws.Cell(row, 4).Value = m.Year;
            ws.Cell(row, 5).Value = MonthName(m.Month);
            ws.Cell(row, 6).Value = m.AbsentDays;
            ws.Cell(row, 7).Value = m.AnnualLeaveDays;
            ws.Cell(row, 8).Value = m.SickLeaveDays;
            ws.Cell(row, 9).Value = m.Article118bDays;
            ws.Cell(row, 10).Value = m.Article118cDays;
            ws.Cell(row, 11).Value = m.TotalAbsenceDays;
            ws.Cell(row, 12).Value = $"{m.BonusDeductionPercent * 100:0.##}%";
            MarkWarning(ws.Cell(row, 11));
            MarkWarning(ws.Cell(row, 12));
            row++;
        }

        StyleTable(ws, 4, row - 1, 12);
        SetWidths(ws, 16, 26, 32, 8, 20, 12, 12, 12, 12, 12, 16, 18);
        AddFooter(ws, row + 1, 12, Signature(generatedAt, "قاعدة الـ 15 يوماً — تعليمات المكافأة الشهرية"));
    }

    /// <summary>ورقة 11: الإجازات المرضية وشرائح الأجر (المادة 112).</summary>
    private static void AddSickLeaveSheet(XLWorkbook workbook, IReadOnlyList<PunchMonthlyResult> monthly, string generatedAt)
    {
        var items = monthly
            .Where(m => m.SickLeaveDays > 0)
            .GroupBy(m => m.JobNumber, StringComparer.Ordinal)
            .Select(g => new
            {
                JobNumber = g.Key,
                EmployeeName = g.Select(m => m.EmployeeName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                Department = g.Select(m => m.DepartmentName).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && d != "_"),
                SickDays = g.Sum(m => m.SickLeaveDays),
                Months = g.Count(),
                First = g.OrderBy(m => m.Year).ThenBy(m => m.Month).First(),
                Last = g.OrderBy(m => m.Year).ThenBy(m => m.Month).Last()
            })
            .OrderByDescending(x => x.SickDays)
            .ThenBy(x => x.JobNumber)
            .ToList();

        var ws = CreateSheet(workbook, "الإجازة المرضية - المادة 112",
            "أيام الإجازة المرضية المتراكمة وشرائح الأجر — المادة 112",
            $"عدد الموظفين: {items.Count} | الشرائح: حتى 120 بأجر كامل، 121–240 بثلاثة أرباع الأجر، 241–360 بنصف الأجر", 7);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "إجمالي أيام الإجازة المرضية",
            "شريحة الأجر المطبَّقة", "الفترة (أول/آخر شهر مسجّل)", "ملاحظات");

        foreach (var item in items)
        {
            decimal percentage = LegalRules.SickSalaryPercentage((int)Math.Ceiling(item.SickDays));
            ws.Cell(row, 1).Value = item.JobNumber;
            ws.Cell(row, 2).Value = item.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = item.Department ?? "—";
            ws.Cell(row, 4).Value = item.SickDays;
            ws.Cell(row, 5).Value = $"{percentage:0.#}% — {LegalRules.SickLeaveTierText(percentage)}";
            ws.Cell(row, 6).Value = $"{MonthName(item.First.Month)} {item.First.Year} → {MonthName(item.Last.Month)} {item.Last.Year}";
            ws.Cell(row, 7).Value = item.SickDays > LegalRules.Sick_Tier1_MaxDays
                ? "يتطلب مراجعة اللجنة الطبية"
                : "داخل الحدّ القانوني";

            if (item.SickDays > LegalRules.Sick_Tier1_MaxDays)
            {
                MarkWarning(ws.Cell(row, 5));
            }

            row++;
        }

        StyleTable(ws, 4, row - 1, 7);
        SetWidths(ws, 16, 26, 32, 22, 46, 34, 26);
        AddFooter(ws, row + 1, 7, Signature(generatedAt, "المادة 112 — الإجازة المرضية"));
    }

    /// <summary>ورقة 12: التزام الإدارات/المديريات بالدوام.</summary>
    private static void AddComplianceSheet(
        XLWorkbook workbook,
        IReadOnlyList<PunchComplianceItem> compliance,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var ws = CreateSheet(workbook, "التزام الإدارات",
            "لوحة التزام الإدارات والمديريات بقواعد الدوام (من البصمات)",
            $"عدد الإدارات: {compliance.Count} | المخالفة = غياب غير مبرّر أو يوم > 4 ساعات أو أسبوع {(rule.ViolationWhenExceededOnly ? "يتجاوز" : "يبلغ")} {rule.ThresholdMinutes} دقيقة", 11);

        int row = WriteHeader(ws, 4,
            "الترتيب", "الإدارة / المديرية", "عدد الموظفين", "الموظفون الملتزمون", "الموظفون المخالفون",
            "نسبة الالتزام", "موظفون لديهم تأخير صباحي", "أيام 118/ب", "أيام 118/ج", "أيام الغياب", "إجمالي أيام حسم الراتب");

        foreach (var item in compliance)
        {
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.Administration;
            ws.Cell(row, 3).Value = item.Employees;
            ws.Cell(row, 4).Value = item.CompliantEmployees;
            ws.Cell(row, 5).Value = item.ViolatingEmployees;
            ws.Cell(row, 6).Value = $"{item.CompliancePercent:0.##}%";
            ws.Cell(row, 7).Value = item.EmployeesWithLateness;
            ws.Cell(row, 8).Value = item.Article118bDays;
            ws.Cell(row, 9).Value = item.Article118cDays;
            ws.Cell(row, 10).Value = item.AbsentDays;
            ws.Cell(row, 11).Value = item.TotalSalaryDeductionDays;

            var percentCell = ws.Cell(row, 6);
            if (item.CompliancePercent >= 90)
            {
                percentCell.Style.Font.FontColor = PunchGoodFont;
                percentCell.Style.Font.Bold = true;
            }
            else if (item.CompliancePercent < 50)
            {
                MarkWarning(percentCell);
            }

            row++;
        }

        StyleTable(ws, 4, row - 1, 11);
        SetWidths(ws, 8, 40, 14, 16, 16, 12, 20, 12, 12, 12, 20);
        AddFooter(ws, row + 1, 11, Signature(generatedAt, "المواد 7 و118 + تعليمات الدوام الرسمي"));
    }

    /// <summary>ورقة 13: كشف الخصومات المُجمَّع لكل موظف.</summary>
    private static void AddDeductionLedgerSheet(
        XLWorkbook workbook,
        IReadOnlyList<PunchMonthlyResult> monthly,
        IReadOnlyList<PunchWeeklyResult> weekly,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var weeklyByEmployee = weekly
            .Where(w => w.Exceeds60Minutes)
            .GroupBy(w => w.JobNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var ledger = monthly
            .GroupBy(m => m.JobNumber, StringComparer.Ordinal)
            .Select(g => new
            {
                JobNumber = g.Key,
                EmployeeName = g.Select(m => m.EmployeeName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                Department = g.Select(m => m.DepartmentName).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && d != "_"),
                LateIncidents = g.Sum(m => m.LateIncidents),
                Article7Days = g.Sum(m => m.Article7SalaryDeductionDays),
                Article7Actions = g.Count(m => m.Penalty != DisciplinaryActionType.None),
                Weeks = weeklyByEmployee.GetValueOrDefault(g.Key),
                Article118cDays = g.Sum(m => m.Article118cDays),
                Article118bDays = g.Sum(m => m.Article118bDays),
                AbsentDays = g.Sum(m => m.AbsentDays),
                IncompleteDays = g.Sum(m => m.IncompleteDays),
                SalaryDays = g.Sum(m => m.TotalSalaryDeductionDays),
                BalanceDays = g.Sum(m => m.AnnualLeaveBalanceUsageDays),
                BonusMonths = g.Count(m => m.Exceeds15Days)
            })
            .Where(x => x.LateIncidents > 0 || x.Article7Days > 0 || x.Weeks > 0 || x.Article118bDays > 0
                        || x.AbsentDays > 0 || x.IncompleteDays > 0)
            .OrderByDescending(x => x.SalaryDays + x.Article118cDays + x.Article118bDays + x.AbsentDays)
            .ThenBy(x => x.JobNumber)
            .ToList();

        var ws = CreateSheet(workbook, "كشف الخصومات المُجمَّع",
            "كشف الخصومات المُجمَّع لكل موظف (المواد 7 و118/ب و118/ج وقاعدة الـ 15 يوماً)",
            $"عدد الموظفين في الكشف: {ledger.Count} | إجمالي أيام حسم الراتب: {ledger.Sum(x => x.SalaryDays):0.##} | إجمالي الخصم من الرصيد السنوي: {ledger.Sum(x => x.Article118bDays):0.##}", 14);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "الموظف", "الإدارة", "أيام التأخير الصباحي", "إجراءات المادة 7",
            "أيام حسم المادة 7", $"أسابيع {(rule.ViolationWhenExceededOnly ? ">" : "≥")} {rule.ThresholdMinutes} دقيقة", "أيام 118/ج", "أيام 118/ب", "أيام الغياب",
            "أيام بلا انصراف", "إجمالي حسم الراتب", "من الرصيد السنوي", "شهور خصم المكافأة");

        foreach (var x in ledger)
        {
            ws.Cell(row, 1).Value = x.JobNumber;
            ws.Cell(row, 2).Value = x.EmployeeName ?? "—";
            ws.Cell(row, 3).Value = x.Department ?? "—";
            ws.Cell(row, 4).Value = x.LateIncidents;
            ws.Cell(row, 5).Value = x.Article7Actions;
            ws.Cell(row, 6).Value = x.Article7Days;
            ws.Cell(row, 7).Value = x.Weeks;
            ws.Cell(row, 8).Value = x.Article118cDays;
            ws.Cell(row, 9).Value = x.Article118bDays;
            ws.Cell(row, 10).Value = x.AbsentDays;
            ws.Cell(row, 11).Value = x.IncompleteDays;
            ws.Cell(row, 12).Value = x.SalaryDays;
            ws.Cell(row, 13).Value = x.BalanceDays;
            ws.Cell(row, 14).Value = x.BonusMonths;

            if (x.SalaryDays > 0)
            {
                MarkWarning(ws.Cell(row, 12));
            }

            row++;
        }

        StyleTable(ws, 4, row - 1, 14);
        SetWidths(ws, 16, 24, 30, 16, 14, 14, 14, 12, 12, 12, 14, 16, 16, 16);
        AddFooter(ws, row + 1, 14, Signature(generatedAt, "المواد 7 و112 و118 + قاعدة الـ 15 يوماً"));
    }

    /// <summary>
    /// ورقة: العمل الإضافي والدوام المرن — إجمالي الساعات الإضافية المحتسبة لكل موظف شهرياً
    /// (الدقائق المحتسبة والساعات المعادلة ودقائق نهاية الأسبوع والعطل والدقائق المستبعدة).
    /// </summary>
    private static void AddOvertimeFlexibleSheet(
        XLWorkbook workbook,
        IReadOnlyList<PunchMonthlyResult> monthly,
        string generatedAt,
        PunchWeeklyRule rule)
    {
        var rows = monthly
            .Where(m => m.OvertimeMinutes > 0 || m.OvertimeExcludedMinutes > 0 || m.FlexibleDays > 0)
            .OrderBy(m => m.Year)
            .ThenBy(m => m.Month)
            .ThenByDescending(m => m.OvertimeMinutes)
            .ToList();

        var ws = CreateSheet(workbook, "العمل الإضافي والدوام المرن",
            "ملخص العمل الإضافي والدوام المرن شهرياً لكل موظف",
            $"{rule.Overtime.Summary()} | {rule.Flexible.Summary()}", 17);

        int row = WriteHeader(ws, 4,
            "الرقم الوظيفي", "اسم الموظف", "الإدارة", "السنة", "الشهر",
            "أيام العمل الإضافي", "دقائق إضافي محتسبة", "ساعات إضافية", "ساعات معادلة",
            "دقائق نهاية الأسبوع", "دقائق العطل", "دقائق مستبعدة", "بلغ الحدّ الشهري؟",
            "أيام دوام مرن", "دقائق المرونة", "أيام معلَّقة بانتظار تصريح", "ملاحظات");

        int overtimeMinutes = 0, equivalentMinutes = 0, excludedMinutes = 0, flexibleDays = 0, pendingDays = 0;

        foreach (var m in rows)
        {
            SetCellValue(ws.Cell(row, 1), m.JobNumber);
            SetCellValue(ws.Cell(row, 2), m.EmployeeName);
            SetCellValue(ws.Cell(row, 3), m.DepartmentName);
            SetCellValue(ws.Cell(row, 4), m.Year);
            SetCellValue(ws.Cell(row, 5), MonthName(m.Month));
            SetCellValue(ws.Cell(row, 6), m.OvertimeDays);
            SetCellValue(ws.Cell(row, 7), m.OvertimeMinutes);
            SetCellValue(ws.Cell(row, 8), m.OvertimeHours);
            SetCellValue(ws.Cell(row, 9), m.EquivalentOvertimeHours);
            SetCellValue(ws.Cell(row, 10), m.OvertimeWeekendMinutes);
            SetCellValue(ws.Cell(row, 11), m.OvertimeHolidayMinutes);
            SetCellValue(ws.Cell(row, 12), m.OvertimeExcludedMinutes);
            SetCellValue(ws.Cell(row, 13), m.OvertimeCapReached ? "نعم" : "لا");
            SetCellValue(ws.Cell(row, 14), m.FlexibleDays);
            SetCellValue(ws.Cell(row, 15), m.FlexibleMinutes);
            SetCellValue(ws.Cell(row, 16), m.OvertimeNeedsApprovalDays);
            SetCellValue(ws.Cell(row, 17), m.Notes);

            overtimeMinutes += m.OvertimeMinutes;
            equivalentMinutes += m.EquivalentOvertimeMinutes;
            excludedMinutes += m.OvertimeExcludedMinutes;
            flexibleDays += m.FlexibleDays;
            pendingDays += m.OvertimeNeedsApprovalDays;
            row++;
        }

        int lastRow = Math.Max(4, row - 1);
        StyleTable(ws, 4, lastRow, 17);
        ws.SheetView.FreezeRows(4);
        SetWidths(ws, 14, 22, 22, 8, 10, 12, 14, 12, 12, 14, 12, 12, 14, 12, 12, 16, 46);

        if (lastRow > 4)
        {
            ws.Range(4, 1, lastRow, 17).SetAutoFilter();
        }

        row = lastRow + 2;
        ws.Cell(row, 1).Value = "الإجمالي";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 7).Value = overtimeMinutes;
        ws.Cell(row, 9).Value = Math.Round(equivalentMinutes / 60.0, 2);
        ws.Cell(row, 12).Value = excludedMinutes;
        ws.Cell(row, 14).Value = flexibleDays;
        ws.Cell(row, 16).Value = pendingDays;
        ws.Range(row, 1, row, 17).Style.Fill.BackgroundColor = PunchTotalFill;

        AddFooter(ws, row + 1, 17,
            Signature(generatedAt, "قانون الخدمة المدنية — حدود العمل الإضافي وضوابط الدوام المرن"));
    }

    /// <summary>
    /// ورقة 14: منهجية الاحتساب وجودة البيانات والملاحظات الفنية.
    /// </summary>
    private static void AddMethodologySheet(
        XLWorkbook workbook,
        PunchAnalysisSummary s,
        PunchStagingSummary staging,
        PunchCalendar calendar,
        string generatedAt,
        PunchWeeklyRule rule,
        PunchWeeklyRuleStore store)
    {
        var defaults = store.Defaults;
        var savedAtUtc = store.LastSavedUtc;
        var settingsFile = store.FilePath;
        var ws = CreateSheet(workbook, "المنهجية وجودة البيانات",
            "منهجية الاحتساب، جودة البيانات، والملاحظات الفنية",
            $"{PeriodLabel(s)} | {staging.SourceFile ?? "—"}", 3);

        int row = WriteHeader(ws, 4, "البند", "القيمة / البيان", "ملاحظات");

        void Add(string label, object value, string note = "")
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = value.ToString();
            ws.Cell(row, 3).Value = note;
            ws.Cell(row, 3).Style.Font.FontColor = PunchMutedFont;
            row++;
        }

        void Section(string title)
        {
            var range = ws.Range(row, 1, row, 3);
            range.Merge();
            range.Value = title;
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = PunchTotalFill;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        Section("مصدر البيانات");
        Add("الملف المستورد", staging.SourceFile ?? "—");
        Add("عدد الصفوف الخام في جدول المرحلة", staging.TotalRows);
        Add("عدد الموظفين", staging.Employees);
        Add("الفترة الفعلية في الملف", $"{staging.FirstDate:yyyy/MM/dd} — {staging.LastDate:yyyy/MM/dd}");
        Add("آخر استيراد", staging.LastImportUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "—");

        Section("توزيع حالات الملف كما وردت (نص المصدر)");
        foreach (var kv in staging.StatusCounts.OrderByDescending(k => k.Value).Take(15))
        {
            Add(kv.Key, kv.Value);
        }

        Section("قواعد المعالجة المطبَّقة");
        Add("دمج صفوف اليوم الواحد", "أبكر بصمة حضور + أحدث بصمة انصراف", "تُرجَّح الحالة الأعلى حكماً عند التعارض");
        Add("المغادرة أثناء الدوام", $"فجوات تزيد على {LegalRules.PunchNoiseMinutes} دقائق بين جلسات البصمة",
            "تُحتسب ضمن دقائق الغياب عن الدوام (المادتان 118/ب و118/ج)");
        Add("أيام «_ -» (بلا بيانات)", $"{s.NoDataDays} يوماً مستثناة", "لا تسمح بالحكم على اليوم فلا تُحتسب مخالفة");
        Add("تقويم العطل المعتمد", $"{s.CalendarHolidays} يوماً (رسمية ودينية)",
            $"عطلة نهاية الأسبوع: {calendar.WeekendDaysText} — تُدار من «تقويم العطل»");
        Add("الدوام في نهاية الأسبوع/العطل", $"{s.WeekendWorkDays + s.HolidayWorkDays} يوماً",
            "يُعرض للعلم ولا يُحتسب مخالفة (ساعات إضافية محتملة)");
        Add("أسبوع العمل", "الأحد — الخميس", "يُبنى عليه التجميع الأسبوعي (المادة 118/ج)");
        Add("دمج التأخير الصباحي مع الانصراف المبكر", "ناتج أسبوعي واحد",
            rule.Description());
        Add("حدّ المخالفة الأسبوعي (المادة 118/ج)",
            rule.Enabled ? $"{rule.ThresholdMinutes} دقيقة أسبوعياً" : "القاعدة معطّلة",
            $"{rule.Summary()} — القيم قابلة للتغيير من «إعدادات المادة 118/ج» بلا إعادة تشغيل"
                + (rule.DepartmentThresholdMinutes.Count > 0
                    ? $" | الإدارات ذات الحدّ الخاص: {string.Join("، ", rule.DepartmentThresholdMinutes.Select(kv => $"{kv.Key}={kv.Value}"))}"
                    : string.Empty));
        if (rule.CapCountedMinutes)
        {
            Add("سقف الناتج المحتسب", $"{rule.EffectiveCapMinutes} دقيقة أسبوعياً",
                "العمود «الناتج المحتسب» = المجموع الفعلي بحدّ أقصى السقف؛ الزائد لا يُحتسب ولا يُضاعف الخصم");
        }
        Add("أيام الخصم لكل أسبوع مخالف", $"{rule.DeductionDaysText()} يوم",
            rule.UseCountedInMonthlyRollup
                ? "الملخص الشهري يجمع الناتج المحتسب (المقيَّد بالسقف)"
                : "الملخص الشهري يجمع المجموع الفعلي (بلا تقييد)");
        Add("ملف إعدادات القاعدة", rule.SameAs(defaults) ? "الافتراضي (appsettings.json)" : "محفوظ (punch-analysis.json)",
            $"الملف: {settingsFile}" + (savedAtUtc.HasValue ? $" | آخر حفظ: {savedAtUtc.Value.ToLocalTime():yyyy/MM/dd HH:mm}" : string.Empty));
        Add("التقويم الشهري", "ميلادي", "الشهر المنسوب لكل أسبوع هو شهر بدايته (الأحد)");
        Add("نافذة الدوام الرسمي", "08:30 — 15:30", "7 ساعات = 420 دقيقة");
        Add("حدّ السماح الصباحي", $"{s.MorningGraceMinutes} دقيقة", "قابل للتغيير من إعدادات النظام (PunchAnalysis)");

        Section("قيود يجب الانتباه إليها");
        Add("الغياب المسجّل في أيام العطل", $"{s.AbsentDaysOnWeekend} يوماً",
            "يُستثنى تلقائياً وفق تقويم العطل المعتمد (نهاية الأسبوع + العطل الرسمية والدينية)");
        Add("مدد الإجازات والاستئذانات", "غير مدرجة في ملف البصمات",
            "تُحتسب أيام الاستئذان كأيام؛ ويُستدل على تجاوز 4 ساعات من أثر البصمات (المادة 118/ب)");
        Add("أرصدة الإجازات السنوية", "غير متوفرة في ملف البصمات",
            "تُعرض الأيام المستنزفة (118/ب و118/ج) دون مقارنتها برصيد الموظف");
        Add("تواريخ التعيين", "غير متوفرة",
            "لا يمكن الاحتساب التناسبي للإجازة السنوية للموظف الجديد (المواد 100/د و101)");
        Add("تحديث تقويم العطل", "مسؤولية المشغّل",
            "تُحدَّث التواريخ الدينية (الهجرية/المسيحية) سنوياً من «تقويم العطل» عند صدور الإعلان الرسمي");
        Add("الأيام غير المكتملة", $"{s.IncompleteDays} يوماً",
            "تتطلب تسوية البصمة أو إبراز عذر، وتُعرض في ورقة مستقلة");

        Section("نتيجة المراجعة");
        Add("عدد الموظفين المُحلَّلين", s.EmployeesAnalyzed);
        Add("موظفون لديهم مخالفات مرصودة", s.EmployeesWithViolations);
        Add("إجمالي حسم الراتب (أيام)", s.TotalSalaryDeductionDays);
        Add("إجمالي الخصم من الرصيد السنوي (أيام)", s.TotalArticle118bDays);
        Add("إجمالي أيام خصم 118/ج", s.TotalArticle118cDays);
        Add("تاريخ إخراج التقرير", generatedAt);

        StyleTable(ws, 4, row - 1, 3);
        SetWidths(ws, 44, 36, 62);
        AddFooter(ws, row + 1, 3,
            $"{Signature(generatedAt, "منهجية احتساب معتمدة")} | تُعرض النتائج للمراجعة القانونية والمالية قبل التنفيذ");
    }
}










