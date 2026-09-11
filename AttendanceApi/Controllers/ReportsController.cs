using AttendanceApi.Data;
using AttendanceApi.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Controllers;

/// <summary>نقطة دخول التقارير — كلها تُقرأ من الـ Views المخزّنة في قاعدة البيانات.</summary>
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly AttendanceDbContext _db;
    private readonly ExcelExportService _excel;

    public ReportsController(AttendanceDbContext db, ExcelExportService excel)
    {
        _db = db;
        _excel = excel;
    }

    /// <summary>تقرير انخفاض رصيد الإجازة السنوية.</summary>
    [HttpGet("low-leave-balance")]
    public async Task<IActionResult> LowLeaveBalance(CancellationToken ct = default) =>
        await ReadView("v_employees_low_leave_balance", ct);

    /// <summary>نسبة الحضور الشهري.</summary>
    [HttpGet("monthly-attendance-percentage")]
    public async Task<IActionResult> MonthlyAttendancePercentage(CancellationToken ct = default) =>
        await ReadView("v_monthly_attendance_percentage", ct);

    /// <summary>مخالفات التأخير الأسبوعي (المادة 118/ج).</summary>
    [HttpGet("weekly-lateness")]
    public async Task<IActionResult> WeeklyLateness(CancellationToken ct = default) =>
        await ReadView("v_weekly_lateness_violations", ct);

    /// <summary>العقوبات التأديبية الشهرية (المادة 7).</summary>
    [HttpGet("disciplinary-penalties")]
    public async Task<IActionResult> DisciplinaryPenalties(CancellationToken ct = default) =>
        await ReadView("v_monthly_disciplinary_actions", ct);

    /// <summary>أهلية المكافأة الشهرية (قاعدة الـ 15 يوماً).</summary>
    [HttpGet("monthly-bonus-evaluation")]
    public async Task<IActionResult> MonthlyBonusEvaluation(CancellationToken ct = default) =>
        await ReadView("v_low_absence_bonus_eligibility", ct);

    /// <summary>تقرير الإجازات المرضية السنوي (لرئيس الوزراء / الوزير).</summary>
    [HttpGet("annual-sick-leave-report")]
    public async Task<IActionResult> AnnualSickLeaveReport(CancellationToken ct = default) =>
        await ReadView("v_annual_sick_leave_report", ct);

    /// <summary>تقرير العمل الإضافي.</summary>
    [HttpGet("overtime")]
    public async Task<IActionResult> Overtime(CancellationToken ct = default) =>
        await ReadView("v_overtime_report", ct);

    /// <summary>تصدير جميع التقارير في ملف Excel متعدد الأوراق.</summary>
    [HttpGet("export-excel")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task<IActionResult> ExportExcel(CancellationToken ct = default)
    {
        var bytes = await _excel.BuildWorkbookAsync(ct);
        var fileName = $"AttendanceReports_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private async Task<IActionResult> ReadView(string viewName, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        var rows = await conn.QueryAsync(new CommandDefinition($"SELECT * FROM {viewName}", cancellationToken: ct));
        return Ok(rows);
    }
}