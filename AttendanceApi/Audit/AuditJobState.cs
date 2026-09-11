namespace AttendanceApi.Audit;

/// <summary>حالة تشغيل محرك المراجعة في الذاكرة (Singleton).</summary>
public sealed class AuditJobState
{
    private readonly object _lock = new();
    public bool IsRunning { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public long RecordsProcessed { get; private set; }
    public string? LastError { get; private set; }

    public bool TryStart()
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                return false;
            }

            IsRunning = true;
            StartedAtUtc = DateTime.UtcNow;
            FinishedAtUtc = null;
            RecordsProcessed = 0;
            LastError = null;
            return true;
        }
    }

    public void AddProcessed(long count)
    {
        lock (_lock)
        {
            RecordsProcessed += count;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            IsRunning = false;
            FinishedAtUtc = DateTime.UtcNow;
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            IsRunning = false;
            FinishedAtUtc = DateTime.UtcNow;
            LastError = error;
        }
    }
}
