using System.Data;
using AttendanceApi.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// تصدير التقارير إلى ملف Excel متعدد الأوراق (6+ أوراق)
/// عبر ClosedXML مع تدفق مخرجات منخفض الذاكرة.
/// </summary>
public sealed class ExcelExportService
{
    private readonly AttendanceDbContext _db;
    private readonly ILogger<ExcelExportService> _logger;

    public ExcelExportService(AttendanceDbContext db, ILogger<ExcelExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>إنشاء ملف Excel متعدد الأوراق لكل التقارير.</summary>
    public async Task<byte[]> BuildWorkbookAsync(CancellationToken ct = default)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook();

        await AddSheetAsync(workbook, "v_employees_low_leave_balance", "الرصيد المنخفض", ct);
        await AddSheetAsync(workbook, "v_monthly_attendance_percentage", "نسبة الحضور الشهري", ct);
        await AddSheetAsync(workbook, "v_weekly_lateness_violations", "مخالفات التأخير الأسبوعي", ct);
        await AddSheetAsync(workbook, "v_monthly_disciplinary_actions", "العقوبات التأديبية", ct);
        await AddSheetAsync(workbook, "v_low_absence_bonus_eligibility", "أهلية المكافأة", ct);
        await AddSheetAsync(workbook, "v_annual_sick_leave_report", "تقرير الإجازات المرضية", ct);
        await AddSheetAsync(workbook, "v_overtime_report", "العمل الإضافي", ct);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private async Task AddSheetAsync(
        ClosedXML.Excel.XLWorkbook workbook,
        string viewName,
        string sheetTitle,
        CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        var rows = await conn.QueryAsync(
            new CommandDefinition($"SELECT * FROM {viewName}", cancellationToken: ct));

        var sheet = workbook.Worksheets.Add(sheetTitle);
        var list = rows as IEnumerable<IDictionary<string, object?>> ?? rows.Cast<IDictionary<string, object?>>().ToList();

        var columns = list.FirstOrDefault()?.Keys.ToList() ?? new List<string>();

        for (int c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c];
        }

        int r = 2;
        foreach (var row in list)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                var val = row[columns[c]];
                if (val is not null)
                {
                    sheet.Cell(r, c + 1).Value = val.ToString();
                }
            }

            r++;
        }

        sheet.Columns().AdjustToContents();
        _logger.LogInformation($"ورقة {sheetTitle}: {list.Count()} صف");
    }
}