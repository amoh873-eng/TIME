using AttendanceApi.Domain;
using AttendanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceApi.Controllers;

/// <summary>
/// إعدادات المنظومة — أهمّها «الربط المباشر بقاعدة البيانات»: الحفظ، اختبار الاتصال،
/// معاينة الأعمدة، ومزامنة النتائج إلى اللوحة مباشرة من قاعدة البيانات.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly DirectSourceSettingsStore _store;
    private readonly DirectSourceService _source;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        DirectSourceSettingsStore store,
        DirectSourceService source,
        ILogger<SettingsController> logger)
    {
        _store = store;
        _source = source;
        _logger = logger;
    }

    /// <summary>قراءة إعدادات الربط المباشر الحالية (بلا كلمة المرور) + حالة آخر مزامنة.</summary>
    [HttpGet("direct-source")]
    public IActionResult Get() => Ok(View());

    /// <summary>حفظ إعدادات الربط المباشر (تفعيل/تعطيل + بيانات الاتصال + الاستعلام + الأتمتة).</summary>
    [HttpPut("direct-source")]
    public async Task<IActionResult> Save([FromBody] DirectSourceRequest request, CancellationToken ct)
    {
        var merged = Merge(_store.Load(), request);

        var error = Validate(merged);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        await _store.SaveAsync(merged, request.Password, request.Password is not null, ct);

        return Ok(new
        {
            message = merged.Enabled
                ? "تم حفظ الإعدادات وتفعيل الربط المباشر بقاعدة البيانات."
                : "تم حفظ الإعدادات (الربط المباشر غير مفعّل).",
            view = View()
        });
    }

    /// <summary>اختبار الاتصال بالمصدر (بالإعدادات المرسلة، أو المحفوظة إن لم يُرسل جسم).</summary>
    [HttpPost("direct-source/test")]
    public async Task<IActionResult> Test([FromBody] DirectSourceRequest? request, CancellationToken ct)
    {
        var settings = request is null ? _store.Load() : Merge(_store.Load(), request);
        return Ok(await _source.TestAsync(settings, ct));
    }

    /// <summary>معاينة أول صفوف المصدر (لعرض الأعمدة والتأكد من الاستعلام قبل المزامنة).</summary>
    [HttpPost("direct-source/preview")]
    public async Task<IActionResult> Preview(
        [FromBody] DirectSourceRequest? request,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var settings = request is null ? _store.Load() : Merge(_store.Load(), request);
        return Ok(await _source.PreviewAsync(settings, take, ct));
    }

    /// <summary>مزامنة الآن: قراءة المصدر ← الاستيراد ← المراجعة ← ملخص اللوحة (بالإعدادات المرسلة).</summary>
    [HttpPost("direct-source/sync")]
    public async Task<IActionResult> Sync([FromBody] DirectSourceRequest? request, CancellationToken ct)
    {
        var settings = request is null ? _store.Load() : Merge(_store.Load(), request);

        var error = Validate(settings);
        if (error is not null)
        {
            return BadRequest(new { message = error });
        }

        var result = await _source.SyncAsync(settings, ct);
        _logger.LogInformation("مزامنة المصدر المباشر: {Ok} — {Message}", result.Ok, result.Message);

        return Ok(new { result, view = View() });
    }

    /// <summary>تعطيل الربط المباشر (تبقى بيانات الاتصال محفوظة للاستخدام لاحقاً).</summary>
    [HttpPost("direct-source/disable")]
    public async Task<IActionResult> Disable(CancellationToken ct)
    {
        var current = _store.Load();
        await _store.SaveAsync(current with { Enabled = false }, plainPassword: null, passwordProvided: false, ct);

        return Ok(new { message = "تم تعطيل الربط المباشر.", view = View() });
    }

    /// <summary>استعلام المصدر الافتراضي (نموذج يعدّله المشغّل في نافذة الإعدادات).</summary>
    [HttpGet("direct-source/default-query")]
    public IActionResult DefaultQuery() => Ok(new { query = DirectSourceSettings.DefaultQuery });

    /// <summary>النسخة الآمنة من الإعدادات + حالة آخر مزامنة.</summary>
    private DirectSourceSettingsView View() =>
        DirectSourceSettingsStore.ToView(
            _store.Load(),
            _source.AppDatabaseName(),
            _store.FilePath,
            _store.LoadState());

    /// <summary>دمج الطلب الجزئي على الإعدادات الحالية (الحقول غير المرسلة تبقى كما هي).</summary>
    private static DirectSourceSettings Merge(DirectSourceSettings current, DirectSourceRequest r) => current with
    {
        Enabled = r.Enabled ?? current.Enabled,
        Mode = string.IsNullOrWhiteSpace(r.Mode) ? current.Mode : r.Mode!.Trim(),
        Server = r.Server ?? current.Server,
        Database = r.Database ?? current.Database,
        Auth = string.IsNullOrWhiteSpace(r.Auth) ? current.Auth : r.Auth!.Trim(),
        UserId = r.UserId ?? current.UserId,
        TrustServerCertificate = r.TrustServerCertificate ?? current.TrustServerCertificate,
        Encrypt = r.Encrypt ?? current.Encrypt,
        CommandTimeoutSeconds = r.CommandTimeoutSeconds ?? current.CommandTimeoutSeconds,
        ConnectionString = string.IsNullOrWhiteSpace(r.ConnectionString) ? null : r.ConnectionString!.Trim(),
        Query = string.IsNullOrWhiteSpace(r.Query) ? current.Query : r.Query,
        MaxRows = r.MaxRows ?? current.MaxRows,
        ReplaceExisting = r.ReplaceExisting ?? current.ReplaceExisting,
        AutoSyncOnLoad = r.AutoSyncOnLoad ?? current.AutoSyncOnLoad,
        AutoSyncMinutes = r.AutoSyncMinutes ?? current.AutoSyncMinutes,
        SourceLabel = r.SourceLabel ?? current.SourceLabel
    };

    /// <summary>التحقق من الإعدادات قبل الحفظ/المزامنة.</summary>
    private static string? Validate(DirectSourceSettings s)
    {
        try
        {
            DirectSourceService.EnsureReadOnlyQuery(s.Query);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (s.Mode != DirectSourceModes.AppDatabase && s.Mode != DirectSourceModes.External)
        {
            return "وضع الاتصال غير معروف — اختر «قاعدة بيانات النظام» أو «خادم خارجي».";
        }

        if (s.Mode == DirectSourceModes.External
            && string.IsNullOrWhiteSpace(s.ConnectionString)
            && (string.IsNullOrWhiteSpace(s.Server) || string.IsNullOrWhiteSpace(s.Database)))
        {
            return "أدخل اسم الخادم واسم قاعدة البيانات (أو سلسلة اتصال كاملة).";
        }

        if (s.CommandTimeoutSeconds is < 15 or > 3600)
        {
            return "مهلة تنفيذ الاستعلام يجب أن تكون بين 15 و3600 ثانية.";
        }

        if (s.MaxRows is < 1 or > 5_000_000)
        {
            return "أقصى عدد صفوف يجب أن يكون بين 1 و5,000,000.";
        }

        if (s.AutoSyncMinutes is < 0 or > 1440)
        {
            return "فترة المزامنة الدورية يجب أن تكون بين 0 و1440 دقيقة.";
        }

        return null;
    }
}

/// <summary>جسم طلب الإعدادات القادم من نافذة الإعدادات (كل الحقول اختيارية).</summary>
public sealed class DirectSourceRequest
{
    public bool? Enabled { get; set; }
    public string? Mode { get; set; }
    public string? Server { get; set; }
    public string? Database { get; set; }
    public string? Auth { get; set; }
    public string? UserId { get; set; }

    /// <summary>كلمة مرور نصية: null = إبقاء المخزّنة، "" = حذفها، وغير ذلك = تعيينها.</summary>
    public string? Password { get; set; }

    public bool? TrustServerCertificate { get; set; }
    public bool? Encrypt { get; set; }
    public int? CommandTimeoutSeconds { get; set; }
    public string? ConnectionString { get; set; }
    public string? Query { get; set; }
    public int? MaxRows { get; set; }
    public bool? ReplaceExisting { get; set; }
    public bool? AutoSyncOnLoad { get; set; }
    public int? AutoSyncMinutes { get; set; }
    public string? SourceLabel { get; set; }
}
