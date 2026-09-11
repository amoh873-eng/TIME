using AttendanceApi.Data;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Audit;

/// <summary>
/// خدمة خلفية (BackgroundService) تستهلك طلبات المراجعة من الطابور
/// وتنفّذ محرك المراجعة، مع تسجيل الحالة في AuditRunLogs.
/// </summary>
public sealed class AuditEngineHostedService : BackgroundService
{
    private readonly AuditJobQueue _queue;
    private readonly AuditJobState _state;
    private readonly AuditEngineOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditEngineHostedService> _logger;
    private long _activeRunLogId;

    public AuditEngineHostedService(
        AuditJobQueue queue,
        AuditJobState state,
        AuditEngineOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<AuditEngineHostedService> logger)
    {
        _queue = queue;
        _state = state;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("مستمع محرك المراجعة بدأ العمل.");

        while (!stoppingToken.IsCancellationRequested)
        {
            AuditJobRequest? request = null;
            try
            {
                request = await _queue.DequeueAsync(stoppingToken);
                await ProcessAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "فشل تشغيل دورة المراجعة {JobId}", request?.JobId);
                _state.Fail(ex.Message);
                await MarkRunFailedAsync(_activeRunLogId, ex.Message);
            }
        }
    }

    private async Task ProcessAsync(AuditJobRequest request, CancellationToken ct)
    {
        if (!_state.TryStart())
        {
            _logger.LogWarning("دورة مراجعة قيد التشغيل بالفعل — تم تجاهل الطلب {JobId}", request.JobId);
            return;
        }

        _activeRunLogId = await CreateRunLogAsync(request.JobId);

        using var scope = _scopeFactory.CreateScope();
        var engineLogger = scope.ServiceProvider.GetRequiredService<ILogger<AuditEngine>>();
        var engine = new AuditEngine(_scopeFactory, _options, engineLogger);

        try
        {
            await engine.RunAsync(ct);
            _state.Complete();
            await MarkRunSucceededAsync(_activeRunLogId);
            _logger.LogInformation("اكتملت دورة المراجعة {JobId}", request.JobId);
        }
        catch (Exception ex)
        {
            _state.Fail(ex.Message);
            await MarkRunFailedAsync(_activeRunLogId, ex.Message);
            throw;
        }
    }

    private async Task<long> CreateRunLogAsync(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var log = new Domain.AuditRunLog
        {
            StartedAtUtc = DateTime.UtcNow,
            Status = Domain.AuditRunStatus.Running
        };
        db.AuditRunLogs.Add(log);
        await db.SaveChangesAsync();
        return log.Id;
    }

    private async Task MarkRunSucceededAsync(long runLogId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var log = await db.AuditRunLogs.FindAsync(runLogId);
        if (log is not null)
        {
            log.Status = Domain.AuditRunStatus.Succeeded;
            log.FinishedAtUtc = DateTime.UtcNow;
            log.RecordsProcessed = 0;
            await db.SaveChangesAsync();
        }
    }

    private async Task MarkRunFailedAsync(long? runLogId, string error)
    {
        if (runLogId is null)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var log = await db.AuditRunLogs.FindAsync(runLogId);
        if (log is not null)
        {
            log.Status = Domain.AuditRunStatus.Failed;
            log.FinishedAtUtc = DateTime.UtcNow;
            log.ErrorMessage = error;
            await db.SaveChangesAsync();
        }
    }
}