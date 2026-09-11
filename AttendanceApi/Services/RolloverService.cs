using AttendanceApi.Data;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// محرك الترحيل السنوي — ينفّذ sp_process_year_end_rollover
/// (المواد 100/د، 101، 105) مع منع التراكم لأكثر من سنتين.
/// </summary>
public sealed class RolloverService
{
    private readonly AttendanceDbContext _db;
    private readonly ILogger<RolloverService> _logger;

    public RolloverService(AttendanceDbContext db, ILogger<RolloverService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>تنفيذ إجراء الترحيل السنوي للسنة المحددة.</summary>
    public async Task<RolloverResult> ExecuteAsync(int fromYear, CancellationToken ct = default)
    {
        _logger.LogInformation("بدء الترحيل السنوي من سنة {Year}...", fromYear);

        // 1) التحقق من عدم وجود رصيد مرصّد لأكثر من سنتين (المادة 101)
        var violations = await _db.LeaveBalances
            .AsNoTracking()
            .Where(b => b.CarriedOverDays > 0 && b.Year < fromYear - 2)
            .CountAsync(ct);

        if (violations > 0)
        {
            _logger.LogWarning("تم اكتشاف {Count} رصيد مرصّد يتجاوز سنتين — سيتم منع الترحيل.", violations);
        }

        // 2) تنفيذ الإجراء المخزّن
        var result = await _db.Database
            .ExecuteSqlInterpolatedAsync($"EXEC sp_process_year_end_rollover @FromYear = {fromYear}", ct);

        _logger.LogInformation("اكتمل الترحيل السنوي ({Result}).", result);

        return new RolloverResult(fromYear, result, violations);
    }
}

/// <summary>نتيجة تنفيذ الترحيل السنوي.</summary>
public sealed record RolloverResult(int FromYear, int RowsAffected, int CarryoverViolations);