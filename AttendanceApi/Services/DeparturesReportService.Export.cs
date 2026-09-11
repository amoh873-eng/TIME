using AttendanceApi.Domain;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

public sealed partial class DeparturesReportService
{
    /// <summary>
    /// بناء ملف Excel لمراجعة تقرير المغادرات
    /// (5 أوراق: ملخص المراجعة، التزام الإدارات، مخالفات 118/ب، مخالفات 118/ج الأسبوعية، تفصيل الطلبات).
    /// </summary>
    public async Task<byte[]> BuildReviewWorkbookAsync(CancellationToken ct = default)
    {
        var reviews = await _db.DeparturesReportReviews
            .AsNoTracking()
            .OrderBy(r => r.JobNumber)
            .ThenBy(r => r.Year)
            .ThenBy(r => r.Month)
            .ToListAsync(ct);

        var over4 = await GetOver4HoursAsync(20_000, ct);

        var weekly = await _db.WeeklyLateness
            .AsNoTracking()
            .OrderByDescending(w => w.Exceeds60Minutes)
            .ThenByDescending(w => w.LateMinutes)
            .ThenBy(w => w.JobNumber)
            .ThenBy(w => w.WeekStart)
            .ToListAsync(ct);

        var details = await _db.DeparturesReportRows
            .AsNoTracking()
            .OrderBy(r => r.JobNumber)
            .ThenBy(r => r.FromDate)
            .ToListAsync(ct);

        // لوحة التزام الإدارات الرئيسية (المديريات) — الأكثر التزاماً أولاً.
        var compliance = await GetComplianceByAdministrationAsync(
            byMainDepartment: true, minEmployees: 1, ct: ct);

        using var workbook = new XLWorkbook();

        AddReviewsSheet(workbook, reviews);
        AddComplianceSheet(workbook, compliance);
        AddOver4Sheet(workbook, over4);
        AddWeeklyLateSheet(workbook, weekly);
        AddDetailsSheet(workbook, details);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// ورقة «التزام الإدارات»: نسبة الموظفين الملتزمين لكل إدارة رئيسية (مديرية)
    /// مرتّبة تصاعدياً بالمخالفات، مع أيام المادة 118/ب و118/ج وإجمالي التأخير الأسبوعي.
    /// </summary>
    private static void AddComplianceSheet(XLWorkbook workbook, DeparturesComplianceResult compliance)
    {
        var ws = workbook.Worksheets.Add("التزام الإدارات");
        ws.RightToLeft = true;

        string[] headers =
        {
            "الترتيب", "الإدارة الرئيسية", "عدد الموظفين", "الموظفون الملتزمون", "الموظفون المخالفون",
            "نسبة الالتزام %", "نسبة المخالفات %", "استئذان > 4 ساعات (موظف)", "أيام المادة 118/ب",
            "أسابيع ≥ 60 دقيقة (موظف)", "أيام المادة 118/ج", "دقائق التأخير الأسبوعي",
            "تجاوز 15 يوماً (موظف)"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        int row = 2;
        foreach (var x in compliance.Items)
        {
            ws.Cell(row, 1).Value = x.Rank;
            ws.Cell(row, 2).Value = x.Administration;
            ws.Cell(row, 3).Value = x.Employees;
            ws.Cell(row, 4).Value = x.CompliantEmployees;
            ws.Cell(row, 5).Value = x.ViolatingEmployees;
            ws.Cell(row, 6).Value = x.CompliancePercent;
            ws.Cell(row, 7).Value = x.ViolationRatePercent;
            ws.Cell(row, 8).Value = x.EmployeesOver4Hours;
            ws.Cell(row, 9).Value = x.Article118bDays;
            ws.Cell(row, 10).Value = x.EmployeesOver60Minutes;
            ws.Cell(row, 11).Value = x.Article118cDays;
            ws.Cell(row, 12).Value = x.WeeklyLateMinutes;
            ws.Cell(row, 13).Value = x.EmployeesExceeding15Days;
            row++;
        }

        // سطر الإجمالي العام أسفل الجدول.
        ws.Cell(row, 2).Value = "الإجمالي العام";
        ws.Cell(row, 3).Value = compliance.TotalEmployees;
        ws.Cell(row, 4).Value = compliance.CompliantEmployees;
        ws.Cell(row, 5).Value = compliance.ViolatingEmployees;
        ws.Cell(row, 6).Value = compliance.OverallCompliancePercent;
        ws.Cell(row, 7).Value = Math.Round(100.0 - compliance.OverallCompliancePercent, 2);
        ws.Cell(row, 9).Value = compliance.TotalArticle118bDays;
        ws.Cell(row, 11).Value = compliance.TotalArticle118cDays;
        ws.Cell(row, 12).Value = compliance.TotalWeeklyLateMinutes;
        ws.Cell(row, 13).Value = compliance.EmployeesExceeding15Days;
        ws.Row(row).Style.Font.Bold = true;

        Finalize(ws, headers.Length);
    }

    private static void AddReviewsSheet(XLWorkbook workbook, List<DeparturesReportReview> reviews)
    {
        var ws = workbook.Worksheets.Add("ملخص المراجعة");
        ws.RightToLeft = true;

        string[] headers =
        {
            "الرقم الوظيفي", "الموظف", "السنة", "الشهر", "عدد الاستئذانات",
            "استئذان > 4 ساعات", "أيام المادة 118/ب", "أسابيع ≥ 60 دقيقة (118/ج)",
            "أيام المادة 118/ج", "دقائق التأخير الأسبوعي", "إجازة سنوية", "إجازة مرضية",
            "مهمة رسمية", "أخرى", "إجمالي أيام الغياب", "خصم المكافأة %", "تجاوز 15 يوماً", "ملاحظات"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        int row = 2;
        foreach (var x in reviews)
        {
            ws.Cell(row, 1).Value = x.JobNumber;
            ws.Cell(row, 2).Value = x.EmployeeName ?? string.Empty;
            ws.Cell(row, 3).Value = x.Year;
            ws.Cell(row, 4).Value = x.Month;
            ws.Cell(row, 5).Value = x.AuthorizationCount;
            ws.Cell(row, 6).Value = x.AuthorizationOver4hCount;
            ws.Cell(row, 7).Value = x.Article118bDays;
            ws.Cell(row, 8).Value = x.WeeksOver60Minutes;
            ws.Cell(row, 9).Value = x.Article118cDays;
            ws.Cell(row, 10).Value = x.WeeklyLateMinutes;
            ws.Cell(row, 11).Value = x.AnnualLeaveDays;
            ws.Cell(row, 12).Value = x.SickLeaveDays;
            ws.Cell(row, 13).Value = x.OfficialDutyDays;
            ws.Cell(row, 14).Value = x.OtherLeaveDays;
            ws.Cell(row, 15).Value = x.TotalAbsenceDays;
            ws.Cell(row, 16).Value = x.BonusDeductionPercent;
            ws.Cell(row, 17).Value = x.Exceeds15Days ? "نعم" : "لا";
            ws.Cell(row, 18).Value = x.Notes ?? string.Empty;
            row++;
        }

        Finalize(ws, headers.Length);
    }

    private static void Finalize(IXLWorksheet ws, int columns)
    {
        ws.Range(1, 1, 1, columns).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    private static void AddOver4Sheet(XLWorkbook workbook, IReadOnlyList<DeparturesReportRow> rows)
    {
        var ws = workbook.Worksheets.Add("مخالفات 118-ب");
        ws.RightToLeft = true;

        string[] headers =
        {
            "رقم الطلب", "الرقم الوظيفي", "الموظف", "نوع الطلب", "من تاريخ", "إلى تاريخ",
            "من وقت", "إلى وقت", "المدة (نص)", "المدة (دقيقة)", "الإدارة", "الإدارة الرئيسية"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        int row = 2;
        foreach (var x in rows)
        {
            ws.Cell(row, 1).Value = x.RequestNumber ?? string.Empty;
            ws.Cell(row, 2).Value = x.JobNumber;
            ws.Cell(row, 3).Value = x.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = x.RequestType ?? string.Empty;
            ws.Cell(row, 5).Value = x.FromDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 6).Value = x.ToDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 7).Value = x.FromTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 8).Value = x.ToTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 9).Value = x.DurationText ?? string.Empty;
            ws.Cell(row, 10).Value = x.DurationMinutes;
            ws.Cell(row, 11).Value = x.DepartmentName ?? string.Empty;
            ws.Cell(row, 12).Value = x.MainDepartmentName ?? string.Empty;
            row++;
        }

        Finalize(ws, headers.Length);
    }

    /// <summary>ورقة مخالفات المادة 118/ج: أسابيع بلغ فيها مجموع التأخير/الانصراف المبكر 60 دقيقة أو أكثر.</summary>
    private static void AddWeeklyLateSheet(XLWorkbook workbook, IReadOnlyList<DeparturesWeeklyLateness> rows)
    {
        var ws = workbook.Worksheets.Add("مخالفات 118-ج الأسبوعية");
        ws.RightToLeft = true;

        string[] headers =
        {
            "الرقم الوظيفي", "الموظف", "بداية الأسبوع (الاثنين)", "نهاية الأسبوع",
            "السنة", "الشهر", "عدد المغادرات", "دقائق التأخير داخل الدوام",
            "بلغت 60 دقيقة", "أيام الخصم", "التفصيل"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        int row = 2;
        foreach (var x in rows)
        {
            ws.Cell(row, 1).Value = x.JobNumber;
            ws.Cell(row, 2).Value = x.EmployeeName ?? string.Empty;
            ws.Cell(row, 3).Value = x.WeekStart.ToString("yyyy-MM-dd");
            ws.Cell(row, 4).Value = x.WeekEnd.ToString("yyyy-MM-dd");
            ws.Cell(row, 5).Value = x.Year;
            ws.Cell(row, 6).Value = x.Month;
            ws.Cell(row, 7).Value = x.DepartureCount;
            ws.Cell(row, 8).Value = x.LateMinutes;
            ws.Cell(row, 9).Value = x.Exceeds60Minutes ? "نعم" : "لا";
            ws.Cell(row, 10).Value = x.DeductionDays;
            ws.Cell(row, 11).Value = x.Notes ?? string.Empty;
            row++;
        }

        Finalize(ws, headers.Length);
    }

    private static void AddDetailsSheet(XLWorkbook workbook, IReadOnlyList<DeparturesReportRow> rows)
    {
        var ws = workbook.Worksheets.Add("تفصيل الطلبات");
        ws.RightToLeft = true;

        string[] headers =
        {
            "رقم الطلب", "الرقم الوظيفي", "الموظف", "من وقت", "إلى وقت", "من تاريخ",
            "إلى تاريخ", "المدة", "حالة الطلب", "نوع الطلب", "تاريخ الطلب", "الإدارة",
            "الإدارة الرئيسية", "التصنيف", "عدد الأيام", "المدة (دقيقة)", "تجاوز 4 ساعات"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        int row = 2;
        foreach (var x in rows)
        {
            ws.Cell(row, 1).Value = x.RequestNumber ?? string.Empty;
            ws.Cell(row, 2).Value = x.JobNumber;
            ws.Cell(row, 3).Value = x.EmployeeName ?? string.Empty;
            ws.Cell(row, 4).Value = x.FromTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 5).Value = x.ToTime?.ToString("HH\\:mm") ?? string.Empty;
            ws.Cell(row, 6).Value = x.FromDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 7).Value = x.ToDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 8).Value = x.DurationText ?? string.Empty;
            ws.Cell(row, 9).Value = x.Status ?? string.Empty;
            ws.Cell(row, 10).Value = x.RequestType ?? string.Empty;
            ws.Cell(row, 11).Value = x.RequestDate?.ToString("yyyy-MM-dd") ?? string.Empty;
            ws.Cell(row, 12).Value = x.DepartmentName ?? string.Empty;
            ws.Cell(row, 13).Value = x.MainDepartmentName ?? string.Empty;
            ws.Cell(row, 14).Value = CategoryLabel(x.Category);
            ws.Cell(row, 15).Value = x.DaysCount;
            ws.Cell(row, 16).Value = x.DurationMinutes;
            ws.Cell(row, 17).Value = x.Exceeds4Hours ? "نعم" : "لا";
            row++;
        }

        Finalize(ws, headers.Length);
    }

    /// <summary>تسمية عربية لفئة الطلب.</summary>
    internal static string CategoryLabel(ReportLeaveCategory category) => category switch
    {
        ReportLeaveCategory.Authorization => "استئذان",
        ReportLeaveCategory.AnnualLeave => "إجازة سنوية",
        ReportLeaveCategory.SickLeave => "إجازة مرضية",
        ReportLeaveCategory.OfficialDuty => "مهمة رسمية",
        ReportLeaveCategory.Other => "أخرى",
        _ => "غير محدد"
    };
}
