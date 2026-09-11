using System.Threading.Channels;

namespace AttendanceApi.Audit;

/// <summary>
/// طابور المهام الخلفية لمحرك المراجعة — يستقبل طلبات التشغيل عبر Channel
/// ويعالجها في الخلفية دون حجب استجابة الـ API.
/// </summary>
public sealed class AuditJobQueue
{
    private readonly Channel<AuditJobRequest> _channel;

    public AuditJobQueue(int capacity = 4)
    {
        _channel = Channel.CreateBounded<AuditJobRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(AuditJobRequest request, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(request, ct);

    public ValueTask<AuditJobRequest> DequeueAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAsync(ct);
}

/// <summary>طلب تشغيل دورة مراجعة.</summary>
public sealed record AuditJobRequest(
    Guid JobId,
    int? Year = null,
    int? Month = null,
    int? EmployeeId = null);