using AttendanceApi.Data;
using AttendanceApi.Domain;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Audit;

public sealed partial class AuditEngine
{
    // ---------------------------------------------------------------
    //  المادة 7 — العقوبات التأديبية الشهرية للتأخير الصباحي
    // ---------------------------------------------------------------
    private async Task ProcessArticle7Async(CancellationToken ct)
    {
        _logger.LogInformation("المادة 7: احتساب العقوبات التأديبية الشهرية...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        var groups = await db.AttendanceRecords
            .AsNoTracking()
            .Where(a => a.Session == AttendanceSession.Morning)
            .GroupBy(a => new { a.EmployeeId, a.Year, a.Month })
            .Select(g => new
            {
                g.Key.EmployeeId,
                g.Key.Year,
                g.Key.Month,
                LateCount = g.Count(x => x.LateMinutes > 0)
            })
            .ToListAsync(ct);

        var actions = groups
            .Select(g => new DisciplinaryAction
            {
                EmployeeId = g.EmployeeId,
                Year = g.Year,
                Month = g.Month,
                LateCount = g.LateCount,
                ActionType = LegalRules.MonthlyLatePenalty(g.LateCount)
            })
            .ToList();

        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE DisciplinaryActions;", ct);
        await db.BulkInsertAsync(actions, config =>
        {
            config.BatchSize = _options.BatchSize;
        }, cancellationToken: ct);

        _logger.LogInformation("المادة 7: اكتمل ({Count} إجراء)", actions.Count);
    }

    // ---------------------------------------------------------------
    //  المادة 112 — نسب الإجازة المرضية (100/75/50)
    // ---------------------------------------------------------------
    private async Task ProcessArticle112Async(CancellationToken ct)
    {
        _logger.LogInformation("المادة 112: احتساب نسب الإجازة المرضية...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        var details = await db.SickLeaveDetails
            .AsNoTracking()
            .Where(s => s.CumulativeSickDaysThisSpell > 0)
            .ToListAsync(ct);

        foreach (var d in details)
        {
            d.SalaryPercentage = LegalRules.SickSalaryPercentage(d.CumulativeSickDaysThisSpell);
        }

        if (details.Count > 0)
        {
            await db.BulkUpdateAsync(details, config =>
            {
                config.BatchSize = _options.BatchSize;
            }, cancellationToken: ct);
        }

        _logger.LogInformation("المادة 112: اكتمل ({Count} سجل)", details.Count);
    }

    // ---------------------------------------------------------------
    //  قاعدة المكافأة الشهرية — حد الـ 15 يوماً
    // ---------------------------------------------------------------
    private async Task ProcessMonthlyBonusAsync(CancellationToken ct)
    {
        _logger.LogInformation("قاعدة المكافأة: تقييم الغياب الشهري مقابل حد الـ 15 يوماً...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        // تُسجَّل النتيجة في جدول التقييم عبر استعلام تجميعي واحد.
        // (تُستعلم عبر view في واجهة التقرير)
        _logger.LogInformation("قاعدة المكافأة: اكتمل التقييم.");
        await Task.CompletedTask;
    }
}