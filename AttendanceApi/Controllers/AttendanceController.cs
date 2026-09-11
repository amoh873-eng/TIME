using AttendanceApi.Audit;
using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>نقطة دخول الحضور: الرفع الجماعي وتشغيل محرك المراجعة.</summary>
[ApiController]
[Route("api/v1/attendance")]
public sealed class AttendanceController : ControllerBase
{
    private readonly BulkUploadService _upload;
    private readonly AuditJobQueue _queue;
    private readonly AuditJobState _state;

    public AttendanceController(BulkUploadService upload, AuditJobQueue queue, AuditJobState state)
    {
        _upload = upload;
        _queue = queue;
        _state = state;
    }

    /// <summary>رفع جماعي لملف حضور (.xlsx/.csv) إلى جدول مرحلة.</summary>
    [HttpPost("upload-bulk")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadBulk(
        IFormFile file,
        [FromQuery] string? stagingTable = "StagingAttendance",
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "الملف فارغ أو غير موجود." });
        }

        await using var stream = file.OpenReadStream();
        var result = await _upload.UploadAsync(stream, file.FileName, stagingTable!, ct);

        return Ok(new
        {
            result.FileName,
            result.StagingTable,
            result.RowsUploaded,
            message = "تم تحميل الملف في جدول المرحلة بنجاح."
        });
    }

    /// <summary>تشغيل محرك المراجعة في الخلفية (غير متزامن).</summary>
    [HttpPost("audit-engine")]
    public async Task<IActionResult> TriggerAudit(
        [FromQuery] int? year = null,
        [FromQuery] int? month = null,
        [FromQuery] int? employeeId = null,
        CancellationToken ct = default)
    {
        var job = new AuditJobRequest(Guid.NewGuid(), year, month, employeeId);
        await _queue.EnqueueAsync(job, ct);

        return Accepted(new
        {
            jobId = job.JobId,
            message = "تمت جدولة دورة المراجعة — ستُنفَّذ في الخلفية."
        });
    }

    /// <summary>الاستعلام عن حالة آخر دورة مراجعة.</summary>
    [HttpGet("audit-status")]
    public IActionResult AuditStatus()
    {
        var status = new
        {
            isRunning = _state.IsRunning,
            startedAtUtc = _state.StartedAtUtc,
            finishedAtUtc = _state.FinishedAtUtc,
            recordsProcessed = _state.RecordsProcessed,
            lastError = _state.LastError
        };

        return Ok(status);
    }
}