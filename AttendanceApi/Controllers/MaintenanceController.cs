using AttendanceApi.Audit;
using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>
/// صيانة بيانات المنظومة: قراءة حالة البيانات الحالية (أعداد الصفوف لكل جدول) ثم تنظيفها
/// لاستقبال بيانات جديدة — إما بيانات التشغيل والتحليل وحدها، أو مسح شامل يضم البيانات المرجعية.
/// التنظيف يتطلّب تأكيداً نصّياً صريحاً في جسم الطلب لمنع التنفيذ بالخطأ.
/// </summary>
[ApiController]
[Route("api/v1/maintenance")]
public sealed class MaintenanceController : ControllerBase
{
    /// <summary>كلمات التأكيد المقبولة (تكتبها الواجهة قبل التنفيذ لمنع الحذف بالخطأ).</summary>
    private static readonly string[] ConfirmWords = { "حذف", "تنظيف", "تنظيف البيانات", "delete", "reset" };

    private readonly DatabaseResetService _service;
    private readonly AuditJobState _auditState;
    private readonly ILogger<MaintenanceController> _logger;

    public MaintenanceController(
        DatabaseResetService service,
        AuditJobState auditState,
        ILogger<MaintenanceController> logger)
    {
        _service = service;
        _auditState = auditState;
        _logger = logger;
    }

    /// <summary>
    /// حالة البيانات الحالية: مجموعات الجداول وعدد صفوف كل جدول وإجماليات كل نطاق (data / all)
    /// وحالة بصمة آخر تحليل — تُستخدم لعرض ما سيُحذف قبل التنفيذ.
    /// </summary>
    [HttpGet("data-status")]
    public async Task<IActionResult> DataStatus(CancellationToken ct)
        => Ok(await _service.GetStatusAsync(ct));

    /// <summary>
    /// تنظيف البيانات — JSON: { "scope": "data" | "all", "confirm": "حذف", "resetWeeklyRule": false }.
    /// «data» يُفرّغ جداول التشغيل والتحليل ويُبقي البيانات المرجعية والإعدادات، و«all» مسح شامل.
    /// تمرير <c>dryRun=true</c> يُنفِّذ التفريغ داخل معاملة ثم يُلغيها (معاينة بلا حذف).
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(
        [FromBody] ResetDataRequest? request,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (request is null || !IsConfirmed(request.Confirm))
        {
            return BadRequest(new { message = "التأكيد مطلوب: اكتب كلمة «حذف» في حقل التأكيد لتنفيذ عملية التنظيف." });
        }

        /* حماية: لا تنظيف أثناء تنفيذ دورة مراجعة في الخلفية (تعارض كتابة مع حذف) */
        if (_auditState.IsRunning)
        {
            return Conflict(new { message = "توجد دورة مراجعة قيد التنفيذ الآن — انتظر انتهاءها ثم أعد المحاولة." });
        }

        var result = await _service.ResetAsync(request.Scope, request.ResetWeeklyRule, dryRun, ct);
        var scopeLabel = ResetScopes.Label(result.Scope);

        var message = result.DryRun
            ? $"معاينة فقط ({scopeLabel}): كان سيُفرَّغ {result.TablesCleared} جدولاً بها {result.RowsDeleted} صفاً — أُلغيت المعاملة ولم يُحذف أي صف."
            : result.RowsDeleted == 0
                ? $"اكتمل التنفيذ ({scopeLabel}) — لا توجد صفوف لحذفها."
                : $"تم تنظيف البيانات بنجاح ({scopeLabel}): {result.TablesCleared} جدولاً و{result.RowsDeleted} صفاً في {result.ElapsedSeconds} ثانية. النظام جاهز لاستقبال بيانات جديدة.";

        _logger.LogInformation(
            "تنظيف بيانات بطلب من الواجهة ({Scope}{Dry}): {Tables} جدولاً، {Rows} صفاً، {Seconds} ثانية.",
            result.Scope, result.DryRun ? " — معاينة بلا حذف" : string.Empty,
            result.TablesCleared, result.RowsDeleted, result.ElapsedSeconds);

        return Ok(new { message, result });
    }

    /// <summary>التحقق من كلمة التأكيد المرسلة من الواجهة.</summary>
    private static bool IsConfirmed(string? value)
    {
        var v = value?.Trim();
        return !string.IsNullOrEmpty(v) && ConfirmWords.Any(w => string.Equals(w, v, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>جسم طلب تنظيف البيانات القادم من الواجهة.</summary>
public sealed class ResetDataRequest
{
    /// <summary>نطاق التنظيف: «data» (افتراضي: بيانات التشغيل والتحليل) أو «all» (مسح شامل).</summary>
    public string? Scope { get; set; }

    /// <summary>تأكيد صريح مطلوب (كلمة «حذف») — بدونه يُرفض الطلب.</summary>
    public string? Confirm { get; set; }

    /// <summary>إعادة إعدادات المادة 118/ج إلى القيم الافتراضية من appsettings.json مع التنظيف.</summary>
    public bool ResetWeeklyRule { get; set; }
}
