using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>نقطة دخول الإجازات: الترحيل السنوي لأرصدة الإجازات.</summary>
[ApiController]
[Route("api/v1/leave")]
public sealed class LeaveController : ControllerBase
{
    private readonly RolloverService _rollover;

    public LeaveController(RolloverService rollover)
    {
        _rollover = rollover;
    }

    /// <summary>تنفيذ sp_process_year_end_rollover للترحيل السنوي.</summary>
    [HttpPost("rollover")]
    public async Task<IActionResult> Rollover([FromQuery] int fromYear, CancellationToken ct = default)
    {
        if (fromYear <= 0)
        {
            return BadRequest(new { message = "fromYear يجب أن يكون سنة صحيحة موجبة." });
        }

        var result = await _rollover.ExecuteAsync(fromYear, ct);

        return Ok(new
        {
            result.FromYear,
            result.RowsAffected,
            result.CarryoverViolations,
            message = "اكتمل الترحيل السنوي بنجاح."
        });
    }
}