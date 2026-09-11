using AttendanceApi.Data;
using AttendanceApi.Domain;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Audit;

/// <summary>
/// محرك المراجعة — يطبّق كل القواعد القانونية (118/ب، 118/ج، 7، 112، مكافأة 15 يوماً)
/// بمعالجة دفعات كبيرة (50,000) وبعمليات Bulk لتحقيق أداء على ملايين السجلات.
/// </summary>
public sealed partial class AuditEngine
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditEngineOptions _options;
    private readonly ILogger<AuditEngine> _logger;

    public AuditEngine(IServiceScopeFactory scopeFactory, AuditEngineOptions options, ILogger<AuditEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>تشغيل دورة مراجعة كاملة على الملايين من السجلات.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("بدء دورة المراجعة...");

        // 1) المادة 118/ب: مخالفات الاستئذان > 4 ساعات
        await ProcessArticle118bAsync(ct);

        // 2) المادة 118/ج: الخصم الأسبوعي التراكمي (>= 60 دقيقة)
        await ProcessArticle118cAsync(ct);

        // 3) المادة 7: العقوبات التأديبية الشهرية للتأخير
        await ProcessArticle7Async(ct);

        // 4) المادة 112: نسب الإجازة المرضية
        await ProcessArticle112Async(ct);

        // 5) قاعدة المكافأة الشهرية (حد الـ 15 يوماً)
        await ProcessMonthlyBonusAsync(ct);

        _logger.LogInformation("اكتملت دورة المراجعة.");
    }

    // ---------------------------------------------------------------
    //  المادة 118/ب — خصم يوم كامل للاستئذان الذي يتجاوز 4 ساعات
    // ---------------------------------------------------------------
    private async Task ProcessArticle118bAsync(CancellationToken ct)
    {
        _logger.LogInformation("118/ب: معالجة مخالفات الاستئذان (> 4 ساعات)...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        int total = 0;
        await foreach (var deps in QueryDepartureBatchesAsync(db, ct))
        {
            foreach (var d in deps)
            {
                bool exceeds = LegalRules.IsExceeding4Hours(d.DurationMinutes);
                d.IsExceeding4Hours = exceeds;
                d.EquivalentDaysDeduction = exceeds ? LegalRules.Art118b_EquivalentDays : 0.0;
            }

            await db.BulkUpdateAsync(deps, config =>
            {
                config.BatchSize = _options.BatchSize;
            }, cancellationToken: ct);

            total += deps.Count;
            _logger.LogInformation("  118/ب: تمت معالجة {Count} سجل", deps.Count);
        }

        _logger.LogInformation("118/ب: اكتمل ({Total} سجل)", total);
    }

    private static async IAsyncEnumerable<List<Departure>> QueryDepartureBatchesAsync(
        AttendanceDbContext db, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long lastId = 0;
        while (true)
        {
            var batch = await db.Departures
                .AsNoTracking()
                .Where(d => d.Id > lastId)
                .OrderBy(d => d.Id)
                .Take(50_000)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                yield break;
            }

            lastId = batch[^1].Id;
            yield return batch;
        }
    }

    // ---------------------------------------------------------------
    //  المادة 118/ج — الخصم الأسبوعي التراكمي للتأخير (>= 60 دقيقة)
    // ---------------------------------------------------------------
    private async Task ProcessArticle118cAsync(CancellationToken ct)
    {
        _logger.LogInformation("118/ج: تجميع التأخير الأسبوعي لكل موظف...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        var groups = await db.AttendanceRecords
            .AsNoTracking()
            .GroupBy(a => new { a.EmployeeId, a.WeekNumber, a.Year })
            .Select(g => new
            {
                g.Key.EmployeeId,
                g.Key.WeekNumber,
                g.Key.Year,
                TotalLate = g.Sum(x => x.LateMinutes + x.EarlyDepartureMinutes)
            })
            .ToListAsync(ct);

        var aggregates = groups
            .Select(g => new DepartureWeekAggregate
            {
                EmployeeId = g.EmployeeId,
                WeekNumber = g.WeekNumber,
                Year = g.Year,
                TotalLateMinutes = g.TotalLate,
                DeductionDays = LegalRules.WeeklyLateDeductionDays(g.TotalLate)
            })
            .ToList();

        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE DepartureWeekAggregates;", ct);
        await db.BulkInsertAsync(aggregates, config =>
        {
            config.BatchSize = _options.BatchSize;
        }, cancellationToken: ct);

        _logger.LogInformation("118/ج: اكتمل ({Count} مجموعة أسبوعية)", aggregates.Count);
    }
}