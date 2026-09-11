using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>
/// مراجعة «تقرير المغادرات» (تصدير الطلبات العربية): الاستيراد، التدقيق القانوني، النتائج، والتصدير.
/// </summary>
[ApiController]
[Route("api/v1/departures")]
public sealed class DeparturesController : ControllerBase
{
    private readonly DeparturesReportService _service;

    public DeparturesController(DeparturesReportService service) => _service = service;

    /// <summary>رفع ملف تقرير المغادرات (.xlsx) وتحويل الأعمدة العربية إلى جدول المرحلة.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> Import(
        IFormFile file,
        [FromQuery] bool replace = true,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "الملف فارغ أو غير موجود." });
        }

        await using var stream = file.OpenReadStream();
        var result = await _service.ImportAsync(stream, file.FileName, replace, ct);

        return Ok(new
        {
            result.FileName,
            result.RowsImported,
            result.RowsSkipped,
            result.RequestTypes,
            message = "تم استيراد تقرير المغادرات إلى جدول المرحلة."
        });
    }

    /// <summary>تشغيل مراجعة التقرير (المادة 118/ب + المادة 118/ج + قاعدة المكافأة).</summary>
    [HttpPost("review")]
    public async Task<IActionResult> Review(CancellationToken ct)
    {
        var result = await _service.ReviewAsync(ct);
        return Ok(new
        {
            result.TotalRequests,
            result.AcceptedRequests,
            result.EmployeesReviewed,
            result.EmployeesWithViolations,
            result.EmployeesOver4Hours,
            result.TotalArticle118bDays,
            result.WeeksOver60Minutes,
            result.EmployeesOver60Minutes,
            result.TotalWeeklyLateMinutes,
            result.TotalArticle118cDays,
            result.EmployeesExceeding15Days,
            result.RunAtUtc,
            message = "اكتملت مراجعة تقرير المغادرات."
        });
    }

    /// <summary>ملخص جدول المرحلة (العدد بحسب التصنيف).</summary>
    [HttpGet("staging-summary")]
    public async Task<IActionResult> StagingSummary(CancellationToken ct)
        => Ok(await _service.GetStagingSummaryAsync(ct));

    /// <summary>نتائج المراجعة الشهرية لكل موظف.</summary>
    [HttpGet("review-results")]
    public async Task<IActionResult> ReviewResults(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool onlyViolations = false,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] string? jobNumber = null,
        CancellationToken ct = default)
        => Ok(await _service.GetReviewsAsync(page, pageSize, onlyViolations, year, month, jobNumber, ct));

    /// <summary>الطلبات التي تجاوزت 4 ساعات (مخالفات المادة 118/ب).</summary>
    [HttpGet("over-4-hours")]
    public async Task<IActionResult> Over4Hours([FromQuery] int take = 1000, CancellationToken ct = default)
        => Ok(await _service.GetOver4HoursAsync(take, ct));

    /// <summary>
    /// التجميع الأسبوعي لدقائق التأخير/الانصراف المبكر داخل الدوام الرسمي (08:30–15:30)
    /// لكل موظف — المادة 118/ج: بلوغ 60 دقيقة في الأسبوع = خصم يوم كامل من الإجازات.
    /// </summary>
    [HttpGet("weekly-late")]
    public async Task<IActionResult> WeeklyLate(
        [FromQuery] bool onlyViolations = true,
        [FromQuery] string? jobNumber = null,
        [FromQuery] int take = 2000,
        CancellationToken ct = default)
        => Ok(await _service.GetWeeklyLatenessAsync(onlyViolations, jobNumber, take, ct));

    /// <summary>
    /// لوحة التزام الإدارات/المديريات بالقوانين: نسبة الموظفين الملتزمين (بلا أي مخالفة 118/ب أو 118/ج أو تجاوز 15 يوماً)
    /// لكل إدارة، مرتّبة تصاعدياً بالمخالفات (الأكثر التزاماً أولاً) مع الترتيب وأيام الخصم لكل مجموعة.
    /// </summary>
    [HttpGet("compliance-by-administration")]
    public async Task<IActionResult> ComplianceByAdministration(
        [FromQuery] bool byMainDepartment = true,
        [FromQuery] int minEmployees = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetComplianceByAdministrationAsync(byMainDepartment, minEmployees, ct));

    /// <summary>
    /// ملخص النتائج المحفوظة في قاعدة البيانات (جدول المرحلة + المراجعة + التجميع الأسبوعي)
    /// لعرض اللوحة مباشرةً من قاعدة البيانات دون رفع ملف أو إعادة مراجعة.
    /// </summary>
    [HttpGet("review-summary")]
    public async Task<IActionResult> ReviewSummary(CancellationToken ct)
        => Ok(await _service.GetReviewSummaryAsync(ct));

    /// <summary>تنزيل ملف Excel بنتائج المراجعة (5 أوراق).</summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel(CancellationToken ct)
    {
        var bytes = await _service.BuildReviewWorkbookAsync(ct);
        var fileName = $"مراجعة-المغادرات-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>
    /// تنزيل «التقارير القانونية الاحترافية» (10 أوراق): تقرير مستقل لكل قاعدة في الوثيقة
    /// (المتأخرون 60 دقيقة — 118/ج، الاستئذان فوق 4 ساعات — 118/ب، التأخير الصباحي المتكرر — المادة 7،
    /// خصم المكافأة — 15 يوماً، الإجازات المرضية — المادة 112، رصيد الإجازة السنوية)
    /// مع ملخص تنفيذي ودليل قواعد وكشف خصومات مُجمَّع وورقة منهجية.
    /// </summary>
    [HttpGet("export-legal-reports")]
    public async Task<IActionResult> ExportLegalReports(CancellationToken ct)
    {
        var bytes = await _service.BuildLegalReportsWorkbookAsync(ct);
        var fileName = $"التقارير-القانونية-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
