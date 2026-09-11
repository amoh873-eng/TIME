using AttendanceApi.Audit;
using AttendanceApi.Domain;
using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>
/// تحليل بصمات الحضور والانصراف (تقرير الحضور والانصراف الخام):
/// الاستيراد، التحليل القانوني (المواد 7 و112 و118/ب و118/ج + قاعدة الـ 15 يوماً)،
/// المؤشرات، التفاصيل، وتصدير التقارير القانونية.
/// </summary>
[ApiController]
[Route("api/v1/punch")]
public sealed class PunchController : ControllerBase
{
    private readonly PunchReportService _service;

    public PunchController(PunchReportService service) => _service = service;

    /// <summary>رفع ملف «تقرير الحضور والانصراف» (.xlsx) إلى جدول المرحلة.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(300 * 1024 * 1024)]
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
            result.Employees,
            result.FirstDate,
            result.LastDate,
            message = "تم استيراد ملف البصمات إلى جدول المرحلة."
        });
    }

    /// <summary>
    /// تشغيل التحليل القانوني وتخزين النتائج (يومية/أسبوعية/شهرية).
    /// </summary>
    /// <param name="graceMinutes">حدّ السماح الصباحي بالدقائق (افتراضياً من الإعدادات).</param>
    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze(
        [FromQuery] int? graceMinutes = null,
        CancellationToken ct = default)
    {
        var summary = await _service.AnalyzeAsync(graceMinutes, ct);
        return Ok(new { summary, message = "اكتمل التحليل القانوني لبصمات الحضور." });
    }

    /// <summary>ملخص جدول المرحلة (جودة البيانات قبل التحليل).</summary>
    [HttpGet("staging-summary")]
    public async Task<IActionResult> StagingSummary(CancellationToken ct)
        => Ok(await _service.GetStagingSummaryAsync(ct));

    /// <summary>ملخص نتائج التحليل القانوني (لوحة المؤشرات).</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
        => Ok(await _service.GetAnalysisSummaryAsync(ct));

    /// <summary>
    /// إعدادات قاعدة المادة 118/ج الأسبوعية السارية (مرنة): حدّ الدقائق، سقف الناتج المحتسب،
    /// أيام الخصم عن كل أسبوع مخالف، خيارات الدمج، حدّ السماح الصباحي، وتجاوزات الإدارات.
    /// </summary>
    [HttpGet("analysis-settings")]
    public IActionResult GetAnalysisSettings() => Ok(_service.GetWeeklyRuleView());

    /// <summary>
    /// حفظ إعدادات المادة 118/ج (يُطبَّق فوراً على التحليل التالي بلا إعادة تشغيل الخدمة).
    /// كل الحقول اختيارية: ما لا يُرسل يبقى على قيمته الحالية.
    /// </summary>
    [HttpPut("analysis-settings")]
    public async Task<IActionResult> SaveAnalysisSettings(
        [FromBody] PunchWeeklyRuleRequest request,
        CancellationToken ct)
    {
        var current = _service.GetWeeklyRuleView().Rule;
        var merged = request.Merge(current);

        var error = PunchReportService.ValidateWeeklyRule(merged);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var view = await _service.SaveWeeklyRuleAsync(merged, ct);

        return Ok(new
        {
            message = "حُفظت إعدادات المادة 118/ج — أعد تشغيل التحليل لتطبيقها على النتائج.",
            settings = view
        });
    }

    /// <summary>إعادة إعدادات المادة 118/ج إلى القيم الافتراضية المعتمدة في appsettings.json.</summary>
    [HttpPost("analysis-settings/reset")]
    public async Task<IActionResult> ResetAnalysisSettings(CancellationToken ct)
    {
        var view = await _service.ResetWeeklyRuleAsync(ct);

        return Ok(new
        {
            message = "أُعيدت إعدادات المادة 118/ج إلى الافتراضي (appsettings.json) — أعد تشغيل التحليل لتطبيقها.",
            settings = view
        });
    }

    /// <summary>معاينة أثر إعدادات القاعدة على بيانات حالية قبل حفظها (بلا تعديل أي شيء).</summary>
    [HttpPost("analysis-settings/preview")]
    public async Task<IActionResult> PreviewAnalysisSettings(
        [FromBody] PunchWeeklyRuleRequest request,
        CancellationToken ct)
    {
        var current = _service.GetWeeklyRuleView().Rule;
        var merged = request.Merge(current);

        var error = PunchReportService.ValidateWeeklyRule(merged);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        return Ok(await _service.PreviewWeeklyRuleAsync(merged, ct));
    }

    /// <summary>النتائج اليومية (التأخير/الانصراف المبكر/الغياب/118/ب).</summary>
    [HttpGet("daily")]
    public async Task<IActionResult> Daily(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool onlyViolations = false,
        [FromQuery] string? jobNumber = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        CancellationToken ct = default)
        => Ok(await _service.GetDailyAsync(page, pageSize, onlyViolations, jobNumber, year, month, null, ct));

    /// <summary>التجميع الأسبوعي للمادة 118/ج.</summary>
    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool onlyViolations = true,
        [FromQuery] string? jobNumber = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        CancellationToken ct = default)
        => Ok(await _service.GetWeeklyAsync(page, pageSize, onlyViolations, jobNumber, year, month, ct));

    /// <summary>النتائج الشهرية (المادة 7 + الإجازات + الخصومات).</summary>
    [HttpGet("monthly")]
    public async Task<IActionResult> Monthly(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool onlyViolations = false,
        [FromQuery] string? jobNumber = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        CancellationToken ct = default)
        => Ok(await _service.GetMonthlyAsync(page, pageSize, onlyViolations, jobNumber, year, month, ct));

    /// <summary>لوحة التزام الإدارات والمديريات من البصمات.</summary>
    [HttpGet("compliance-by-department")]
    public async Task<IActionResult> Compliance(
        [FromQuery] int minEmployees = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetComplianceByDepartmentAsync(minEmployees, ct));

    /// <summary>سجل أيام موظف واحد (تحليل الحالة الفردية).</summary>
    [HttpGet("employees/{jobNumber}/timeline")]
    public async Task<IActionResult> Timeline(
        string jobNumber,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken ct = default)
        => Ok(await _service.GetEmployeeTimelineAsync(jobNumber, from, to, ct));

    /// <summary>تنزيل «التقارير القانونية» من البصمات (16 ورقة).</summary>
    [HttpGet("export-legal-reports")]
    public async Task<IActionResult> ExportLegalReports(CancellationToken ct)
    {
        var bytes = await _service.BuildLegalReportsWorkbookAsync(ct);
        var fileName = $"التقارير-القانونية-البصمات-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    // =====================================================================
    //  قائمة تقارير الحضور والانصراف (تقرير Excel مستقل لكل قاعدة/مؤشر)
    // =====================================================================

    /// <summary>قائمة تقارير الحضور والانصراف المتاحة مع عدد الصفوف في كل تقرير.</summary>
    [HttpGet("reports")]
    public async Task<IActionResult> Reports(CancellationToken ct)
        => Ok(await _service.GetReportsCatalogAsync(ct));

    /// <summary>معاينة مختصرة لتقرير واحد (أول صفحة من صفوفه).</summary>
    [HttpGet("reports/{key}/preview")]
    public async Task<IActionResult> ReportPreview(
        string key,
        [FromQuery] int pageSize = 15,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? jobNumber = null,
        CancellationToken ct = default)
    {
        if (!PunchReportService.IsKnownReportKey(key))
        {
            return BadRequest(new { message = $"تقرير غير معروف: {key}" });
        }

        var filter = new PunchReportFilter(from, to, jobNumber);
        return Ok(await _service.GetReportPreviewAsync(key, pageSize, filter, ct));
    }

    /// <summary>تنزيل تقرير Excel مستقل من قائمة تقارير الحضور والانصراف.</summary>
    [HttpGet("reports/{key}/export")]
    public async Task<IActionResult> ReportExport(
        string key,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? jobNumber = null,
        [FromQuery] int maxRows = 0,
        CancellationToken ct = default)
    {
        if (!PunchReportService.IsKnownReportKey(key))
        {
            return BadRequest(new { message = $"تقرير غير معروف: {key}" });
        }

        var filter = new PunchReportFilter(from, to, jobNumber);
        var bytes = await _service.BuildReportWorkbookAsync(key, filter, maxRows, ct);
        var fileName = $"تقرير-{key}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    // =====================================================================
    //  تقويم العطل الرسمية والدينية (إدارة العطل المعتمدة في التحليل)
    // =====================================================================

    /// <summary>عرض تقويم العطل المعتمد (الكتالوج المدمج + العطل المسجّلة).</summary>
    [HttpGet("calendar")]
    public async Task<IActionResult> Calendar(CancellationToken ct)
        => Ok(await _service.GetCalendarAsync(ct));

    /// <summary>تنزيل تقويم العطل الرسمية والدينية (Excel).</summary>
    [HttpGet("calendar/export")]
    public async Task<IActionResult> CalendarExport(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        CancellationToken ct = default)
    {
        var bytes = await _service.BuildHolidayCalendarWorkbookAsync(from, to, ct);
        var fileName = $"تقويم-العطل-الرسمية-والدينية-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>إضافة عطلة أو تعديل عطلة قائمة (تُعاد العطلة المستبعدة إلى التقويم).</summary>
    [HttpPost("calendar/holidays")]
    public async Task<IActionResult> SaveHoliday(
        [FromBody] PunchHolidayRequest request,
        CancellationToken ct)
    {
        var kind = PunchReportService.TryParseHolidayKind(request.Kind, out var parsed)
            ? parsed
            : PunchReportService.ParseHolidayKind(request.KindText);

        bool saved = await _service.SaveHolidayAsync(
            request.Date,
            request.Name,
            kind,
            string.IsNullOrWhiteSpace(request.Source) ? "إدخال يدوي" : request.Source,
            ct);

        return saved
            ? Ok(new
            {
                message = $"تم اعتماد عطلة {request.Date:yyyy/MM/dd}.",
                kind = PunchCalendar.KindText(kind)
            })
            : BadRequest(new { message = "اسم العطلة مطلوب." });
    }

    /// <summary>إضافة مجموعة عطل من قائمة نصية (سطر لكل عطلة: التاريخ | الاسم | النوع).</summary>
    [HttpPost("calendar/holidays/bulk")]
    public async Task<IActionResult> SaveHolidays(
        [FromBody] PunchHolidayBulkRequest request,
        CancellationToken ct)
    {
        int saved = await _service.AddHolidaysFromTextAsync(request.Text ?? string.Empty, ct);
        return Ok(new { saved, message = $"أُضيفت {saved} عطلة من القائمة النصية." });
    }

    /// <summary>استيراد تقويم العطل من ملف Excel (أعمدة: التاريخ، الاسم/المناسبة، النوع).</summary>
    [HttpPost("calendar/holidays/import")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> ImportHolidays(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "الملف فارغ أو غير موجود." });
        }

        await using var stream = file.OpenReadStream();
        int imported = await _service.ImportHolidaysAsync(stream, file.FileName, ct);

        return Ok(new { imported, message = $"استُوردت {imported} عطلة من الملف." });
    }

    /// <summary>حذف عطلة مسجّلة أو استبعاد عطلة من الكتالوج المدمج (لا تُحتسب بعدها عطلة).</summary>
    [HttpDelete("calendar/holidays/{date}")]
    public async Task<IActionResult> DeleteHoliday(DateOnly date, CancellationToken ct)
    {
        bool removed = await _service.RemoveHolidayAsync(date, ct);
        return removed
            ? Ok(new { message = $"أُلغيت عطلة {date:yyyy/MM/dd} من التقويم المعتمد." })
            : NotFound(new { message = $"لا توجد عطلة مسجّلة في {date:yyyy/MM/dd}." });
    }

    /// <summary>استعادة كتالوج العطل المدمج في قاعدة البيانات (لسنة محدّدة أو للكل).</summary>
    [HttpPost("calendar/holidays/defaults")]
    public async Task<IActionResult> RestoreDefaultHolidays(
        [FromQuery] int? year = null,
        CancellationToken ct = default)
    {
        int saved = await _service.RestoreDefaultHolidaysAsync(year, ct);
        return Ok(new { saved, message = $"استُعيدت {saved} عطلة من كتالوج النظام إلى قاعدة البيانات." });
    }

    // =====================================================================
    //  نظام الورديات: جدول الورديات الشهري الصادر من مسؤول الورديات
    //  (الحراسة وبقية الإدارات ذات الورديات 24 ساعة) + الاستثناءات الخاصة
    // =====================================================================

    /// <summary>عرض «جدول الورديات الشهري» المستورد: المؤشرات + دفعات الاستيراد + عيّنة القيود.</summary>
    [HttpGet("shifts")]
    public async Task<IActionResult> Shifts(
        [FromQuery] string? batchKey = null,
        [FromQuery] int sampleSize = 60,
        CancellationToken ct = default)
        => Ok(await _service.GetShiftScheduleAsync(batchKey, sampleSize, ct));

    /// <summary>تنزيل «قالب جدول الورديات الشهري» (.xlsx) ليعبّئه مسؤول الورديات ثم يُعاد استيراده.</summary>
    [HttpGet("shifts/template")]
    public async Task<IActionResult> ShiftTemplate(
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] string? department = null,
        CancellationToken ct = default)
    {
        int y = year is >= 2000 and <= 2100 ? year.Value : DateTime.Now.Year;
        int m = month is >= 1 and <= 12 ? month.Value : DateTime.Now.Month;

        var bytes = await _service.BuildShiftTemplateWorkbookAsync(y, m, department, ct);
        var fileName = $"قالب-جدول-الورديات-{y:0000}-{m:00}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>
    /// استيراد «جدول الورديات الشهري» من ملف Excel:
    /// شبكة شهرية (عمود لكل يوم) أو جدول طويل (الرقم الوظيفي | التاريخ | الوردية).
    /// </summary>
    [HttpPost("shifts/import")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> ImportShiftSchedule(
        IFormFile file,
        [FromQuery] string? batchKey = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] bool replace = true,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "الملف فارغ أو غير موجود." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _service.ImportShiftScheduleAsync(
                stream, file.FileName, batchKey, year, month, replace, ct);

            return Ok(new
            {
                result.FileName,
                result.BatchId,
                result.BatchKey,
                result.Format,
                result.FirstDate,
                result.LastDate,
                result.Rows,
                result.Employees,
                result.DutyDays,
                result.RestDays,
                result.LeaveDays,
                result.Replaced,
                message = $"استُورد جدول الورديات ({result.Format}): {result.Rows} قيداً لـ {result.Employees} موظفاً"
                    + $" — ورديات: {result.DutyDays}، راحة: {result.RestDays}، إجازات: {result.LeaveDays}."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>تنزيل «كشف جدول الورديات المستورد» (.xlsx) للتدقيق.</summary>
    [HttpGet("shifts/export")]
    public async Task<IActionResult> ShiftExport(
        [FromQuery] string? batchKey = null,
        CancellationToken ct = default)
    {
        var bytes = await _service.BuildShiftScheduleWorkbookAsync(batchKey, ct);
        var fileName = $"جدول-الورديات-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>حذف دفعة استيراد جدول ورديات كاملة (لتصحيح ملف خاطئ أو شهر ملغى).</summary>
    [HttpDelete("shifts/batches/{id:long}")]
    public async Task<IActionResult> DeleteShiftBatch(long id, CancellationToken ct)
    {
        bool deleted = await _service.DeleteShiftBatchAsync(id, ct);

        return deleted
            ? Ok(new { message = "حُذفت دفعة جدول الورديات وقيودها من التحليل." })
            : NotFound(new { message = "لا توجد دفعة استيراد بهذا المعرّف." });
    }

    // =====================================================================
    //  الدوام المرن والعمل الإضافي: القواعد السارية + تصاريح الموظفين
    // =====================================================================

    /// <summary>
    /// عرض «تصاريح العمل الإضافي والدوام المرن»: القواعد السارية (الحدود والنطاق) + التصاريح المسجّلة.
    /// </summary>
    [HttpGet("work-approvals")]
    public async Task<IActionResult> WorkApprovals(CancellationToken ct)
        => Ok(await _service.GetWorkApprovalsAsync(ct));

    /// <summary>
    /// إضافة/تعديل تصريح عمل إضافي أو دوام مرن لموظف (يُطبَّق على التحليل التالي فوراً بلا إعادة تشغيل).
    /// </summary>
    [HttpPost("work-approvals")]
    public async Task<IActionResult> SaveWorkApproval(
        [FromBody] PunchWorkApprovalRequest request,
        CancellationToken ct)
    {
        var approval = request.ToEntity();

        var error = await _service.SaveWorkApprovalAsync(approval, ct);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var view = await _service.GetWorkApprovalsAsync(ct);

        return Ok(new
        {
            message = $"حُفظ تصريح {(approval.Kind == WorkApprovalKind.Flexible ? "الدوام المرن" : "العمل الإضافي")}"
                + $" للموظف {approval.JobNumber} — أعد تشغيل التحليل لتطبيقه على النتائج.",
            approvals = view
        });
    }

    /// <summary>حذف تصريح (للتصحيحات)؛ للحفاظ على التدقيق يُفضَّل إيقافه بدل حذفه.</summary>
    [HttpDelete("work-approvals/{id:long}")]
    public async Task<IActionResult> DeleteWorkApproval(long id, CancellationToken ct)
    {
        bool deleted = await _service.DeleteWorkApprovalAsync(id, ct);
        return deleted
            ? Ok(new { message = "حُذف التصريح." })
            : NotFound(new { message = "لا يوجد تصريح بهذا المعرّف." });
    }

    /// <summary>
    /// إضافة تصاريح من قائمة نصية (سطر لكل تصريح):
    /// الرقم الوظيفي | من | إلى | ساعات/يوم | إجمالي الساعات | النوع (إضافي/مرن) | ملاحظة.
    /// </summary>
    [HttpPost("work-approvals/bulk")]
    public async Task<IActionResult> SaveWorkApprovals(
        [FromBody] PunchWorkApprovalsBulkRequest request,
        CancellationToken ct)
    {
        var result = await _service.AddWorkApprovalsFromTextAsync(request.Text, ct);
        var view = await _service.GetWorkApprovalsAsync(ct);

        return Ok(new
        {
            result.Added,
            result.Skipped,
            result.Errors,
            approvals = view,
            message = $"أُضيف {result.Added} تصريحاً" + (result.Skipped > 0 ? $" ورُفض {result.Skipped} سطراً." : ".")
        });
    }

    /// <summary>
    /// ترحيل إجمالي ساعات العمل الإضافي الشهرية المحتسبة (لكل موظف) إلى سجل «العمل الإضافي»
    /// ليتكامل مع تقرير العمل الإضافي القائم <c>/api/v1/reports/overtime</c>.
    /// </summary>
    [HttpPost("work-approvals/sync-overtime")]
    public async Task<IActionResult> SyncOvertime(
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] bool replace = true,
        CancellationToken ct = default)
        => Ok(await _service.SyncOvertimeRecordsAsync(year, month, replace, ct));
}

/// <summary>طلب تعديل إعدادات المادة 118/ج — كل الحقول اختيارية (ما لا يُرسل يبقى على قيمته).</summary>
public sealed class PunchWeeklyRuleRequest
{
    public bool? Enabled { get; set; }
    public bool? MergeEarlyDeparture { get; set; }
    public bool? MergeMidDayGaps { get; set; }
    public int? ThresholdMinutes { get; set; }
    public bool? ViolationWhenExceededOnly { get; set; }
    public bool? CapCountedMinutes { get; set; }
    public int? CapMinutes { get; set; }
    public double? DeductionDaysPerWeek { get; set; }
    public bool? UseCountedInMonthlyRollup { get; set; }
    public int? MorningGraceMinutes { get; set; }

    /// <summary>حدود إدارات خاصة: تُرسل كاملة (استبدال) — وإرسالها فارغة يحذف التجاوزات.</summary>
    public Dictionary<string, int>? DepartmentThresholdMinutes { get; set; }

    /// <summary>
    /// إعدادات «نظام الورديات» (الحراسة وبقية الإدارات ذات الورديات): تُرسل كاملة (استبدال)،
    /// وعدم إرسالها يُبقي الإعدادات الحالية كما هي.
    /// </summary>
    public PunchShiftRule? Shift { get; set; }

    /// <summary>
    /// إعدادات «الدوام المرن» (نافذة الحضور وساعات الإكمال والنطاق المصرَّح له):
    /// تُرسل كاملة (استبدال)، وعدم إرسالها يُبقي الإعدادات الحالية.
    /// </summary>
    public PunchFlexibleRule? Flexible { get; set; }

    /// <summary>
    /// إعدادات «العمل الإضافي» (الحدود اليومية والشهرية وأقل مدة ونسب التعويض والنطاق المصرَّح له):
    /// تُرسل كاملة (استبدال)، وعدم إرسالها يُبقي الإعدادات الحالية.
    /// </summary>
    public PunchOvertimeRule? Overtime { get; set; }

    /// <summary>
    /// استثناءات الإدارات/المديريات الخاصة (إعفاء من 118/ج أو التأخير أو الانصراف أو الغياب،
    /// أو حدّ/سقف مختلف، أو معاملتها بنظام الورديات): تُرسل كاملة (استبدال)، وإرسالها فارغة يحذف الاستثناءات.
    /// </summary>
    public Dictionary<string, PunchDepartmentException>? DepartmentExceptions { get; set; }

    /// <summary>دمج الطلب مع القاعدة الحالية (الحقول غير المُرسَلة تُبقى كما هي).</summary>
    public PunchWeeklyRule Merge(PunchWeeklyRule current) => new()
    {
        Enabled = Enabled ?? current.Enabled,
        MergeEarlyDeparture = MergeEarlyDeparture ?? current.MergeEarlyDeparture,
        MergeMidDayGaps = MergeMidDayGaps ?? current.MergeMidDayGaps,
        ThresholdMinutes = ThresholdMinutes ?? current.ThresholdMinutes,
        ViolationWhenExceededOnly = ViolationWhenExceededOnly ?? current.ViolationWhenExceededOnly,
        CapCountedMinutes = CapCountedMinutes ?? current.CapCountedMinutes,
        CapMinutes = CapMinutes ?? current.CapMinutes,
        DeductionDaysPerWeek = DeductionDaysPerWeek ?? current.DeductionDaysPerWeek,
        UseCountedInMonthlyRollup = UseCountedInMonthlyRollup ?? current.UseCountedInMonthlyRollup,
        MorningGraceMinutes = MorningGraceMinutes ?? current.MorningGraceMinutes,
        DepartmentThresholdMinutes = DepartmentThresholdMinutes ?? current.DepartmentThresholdMinutes,
        Shift = Shift ?? current.Shift,
        Flexible = Flexible ?? current.Flexible,
        Overtime = Overtime ?? current.Overtime,
        DepartmentExceptions = DepartmentExceptions ?? current.DepartmentExceptions
    };
}

/// <summary>طلب إضافة/تعديل عطلة (Kind رقمي 1-4 أو KindText نصي).</summary>
public sealed record PunchHolidayRequest(
    DateOnly Date,
    string Name,
    int? Kind = null,
    string? KindText = null,
    string? Source = null);

/// <summary>طلب إضافة مجموعة عطل من قائمة نصية.</summary>
public sealed record PunchHolidayBulkRequest(string? Text);

/// <summary>طلب إضافة/تعديل تصريح عمل إضافي أو دوام مرن لموظف.</summary>
public sealed class PunchWorkApprovalRequest
{
    /// <summary>معرّف التصريح (0 أو عدم الإرسال = تصريح جديد).</summary>
    public long Id { get; set; }

    /// <summary>الرقم الوظيفي.</summary>
    public string JobNumber { get; set; } = string.Empty;

    public string? EmployeeName { get; set; }
    public string? DepartmentName { get; set; }

    /// <summary>بداية سريان التصريح.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>نهاية سريان التصريح (عدم الإرسال = يوم واحد).</summary>
    public DateOnly? ToDate { get; set; }

    /// <summary>النوع: «مرن» أو «flexible» للدوام المرن، وغير ذلك = عمل إضافي.</summary>
    public string? Kind { get; set; }

    /// <summary>الحدّ اليومي بالساعات (0 أو عدم الإرسال = يتبع الإعدادات العامة).</summary>
    public double? MaxHoursPerDay { get; set; }

    /// <summary>إجمالي ساعات التصريح (0 أو عدم الإرسال = يتبع السقف الشهري العام).</summary>
    public double? MaxHoursTotal { get; set; }

    public string? Note { get; set; }

    /// <summary>إيقاف التصريح بلا حذف (الافتراضي: ساري).</summary>
    public bool? IsActive { get; set; }

    /// <summary>تحويل الطلب إلى كيان تصريح.</summary>
    public PunchWorkApproval ToEntity()
    {
        var kindText = Kind ?? string.Empty;
        var kind = kindText.Contains("مرن", StringComparison.Ordinal)
                   || kindText.Contains("flex", StringComparison.OrdinalIgnoreCase)
            ? WorkApprovalKind.Flexible
            : WorkApprovalKind.Overtime;

        var from = FromDate == default ? DateOnly.FromDateTime(DateTime.Today) : FromDate;

        return new PunchWorkApproval
        {
            Id = Id,
            Kind = kind,
            JobNumber = (JobNumber ?? string.Empty).Trim(),
            EmployeeName = string.IsNullOrWhiteSpace(EmployeeName) ? null : EmployeeName!.Trim(),
            DepartmentName = string.IsNullOrWhiteSpace(DepartmentName) ? null : DepartmentName!.Trim(),
            FromDate = from,
            ToDate = ToDate.HasValue && ToDate.Value >= from ? ToDate.Value : from,
            MaxMinutesPerDay = ToMinutes(MaxHoursPerDay),
            MaxMinutesTotal = ToMinutes(MaxHoursTotal),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note!.Trim(),
            IsActive = IsActive ?? true
        };
    }

    /// <summary>تحويل الساعات إلى دقائق (لا تُقبل القيم الصفرية أو السالبة).</summary>
    private static int? ToMinutes(double? hours) =>
        hours is > 0 ? (int)Math.Round(hours.Value * 60) : null;
}

/// <summary>طلب إضافة تصاريح من قائمة نصية.</summary>
public sealed record PunchWorkApprovalsBulkRequest(string? Text);
