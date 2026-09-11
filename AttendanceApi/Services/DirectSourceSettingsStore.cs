using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceApi.Domain;

namespace AttendanceApi.Services;

/// <summary>
/// تخزين إعدادات «الربط المباشر بقاعدة البيانات» في ملف JSON بجوار التطبيق
/// (<c>direct-source.json</c> في جذر المشروع). كلمة المرور تُشفَّر بـ Windows DPAPI
/// (نطاق الجهاز + مفتاح خاص بالتطبيق)، ومع تعذّر ذلك تُخزَّن مُرمَّزة بـ Base64 مع تنبيه في السجل.
/// </summary>
public sealed class DirectSourceSettingsStore
{
    private const string FileName = "direct-source.json";
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AttendanceApi.DirectSource.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<DirectSourceSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    /// <summary>وقت آخر تعديل للملف كما قرأناه (لكشف التعديل اليدوي أو الحذف خارج التطبيق).</summary>
    private DateTime _stampUtc;

    /// <summary>هل كان الملف موجوداً عند آخر قراءة؟</summary>
    private bool _seen;

    public DirectSourceSettingsStore(IHostEnvironment env, ILogger<DirectSourceSettingsStore> logger)
    {
        _logger = logger;
        FilePath = Path.Combine(env.ContentRootPath, FileName);
        Current = ReadAndTrack();
    }

    /// <summary>مسار ملف الإعدادات (يُعرض في الواجهة ليتمكّن المشغّل من نسخه احتياطياً).</summary>
    public string FilePath { get; }

    /// <summary>الإعدادات الحالية في الذاكرة (تتحدّث بعد كل حفظ أو تعديل يدوي للملف).</summary>
    public DirectSourceFile Current { get; private set; }

    /// <summary>قراءة الإعدادات الحالية (مع إعادة القراءة من القرص إن عُدِّل الملف يدوياً).</summary>
    public DirectSourceSettings Load() => Refresh().Settings;

    /// <summary>قراءة حالة آخر مزامنة.</summary>
    public DirectSourceState LoadState() => Refresh().State;

    /// <summary>
    /// إعادة قراءة ملف الإعدادات إذا تغيّر على القرص (تعديل يدوي/حذف/استرجاع نسخة احتياطية)،
    /// حتى لا تبقى قيم قديمة في الذاكرة. الحفظ عبر <see cref="SaveAsync"/> يُحدِّث البصمة فلا يُعاد القراءة.
    /// </summary>
    private DirectSourceFile Refresh()
    {
        bool exists;
        DateTime stamp;
        try
        {
            exists = File.Exists(FilePath);
            stamp = exists ? File.GetLastWriteTimeUtc(FilePath) : default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر فحص ملف إعدادات الربط المباشر {File}.", FilePath);
            return Current;
        }

        lock (_sync)
        {
            if (exists == _seen && stamp == _stampUtc)
            {
                return Current;
            }

            _logger.LogInformation("تغيّر ملف إعدادات الربط المباشر {File} على القرص — أُعيدت قراءته.", FilePath);
            return Current = ReadAndTrack();
        }
    }

    /// <summary>قراءة الملف مع تسجيل بصمة التعديل الحالية.</summary>
    private DirectSourceFile ReadAndTrack()
    {
        var file = Read();
        Track();
        return file;
    }

    /// <summary>تسجيل بصمة الملف الحالية لمنع إعادة القراءة بلا داعٍ.</summary>
    private void Track()
    {
        try
        {
            _seen = File.Exists(FilePath);
            _stampUtc = _seen ? File.GetLastWriteTimeUtc(FilePath) : default;
        }
        catch
        {
            _seen = false;
            _stampUtc = default;
        }
    }

    /// <summary>
    /// حفظ الإعدادات. <paramref name="passwordProvided"/> = true يعني أن الطلب يحمل قراراً
    /// بشأن كلمة المرور (نص جديد أو تفريغها)؛ و=false يعني إبقاء المُخزَّنة كما هي.
    /// </summary>
    public async Task<DirectSourceSettings> SaveAsync(
        DirectSourceSettings settings,
        string? plainPassword,
        bool passwordProvided,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var token = settings.PasswordToken;

            if (passwordProvided)
            {
                token = string.IsNullOrEmpty(plainPassword) ? null : Protect(plainPassword!);
            }

            var saved = settings with { PasswordToken = token };
            lock (_sync)
            {
                Current = Current with { Settings = saved };
            }

            await WriteAsync(Current, ct);

            _logger.LogInformation(
                "حُفظت إعدادات الربط المباشر (مفعّل={Enabled}, الوضع={Mode}, الملف={File}).",
                saved.Enabled, saved.Mode, FilePath);

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>تحديث حالة آخر مزامنة وحفظها (بلا مسّ الإعدادات).</summary>
    public async Task<DirectSourceState> SaveStateAsync(DirectSourceState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                Current = Current with { State = state };
            }

            await WriteAsync(Current, ct);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>كلمة مرور مفكوكة التشفير (تُستخدم عند بناء سلسلة الاتصال فقط).</summary>
    public string? UnprotectPassword(DirectSourceSettings settings) =>
        string.IsNullOrEmpty(settings.PasswordToken) ? null : Unprotect(settings.PasswordToken!);

    /// <summary>تحويل الإعدادات إلى نسخة آمنة للمتصفح (بلا كلمة مرور).</summary>
    public static DirectSourceSettingsView ToView(
        DirectSourceSettings s,
        string appConnectionTarget,
        string settingsFile,
        DirectSourceState state)
        => new(
            Enabled: s.Enabled,
            Mode: s.Mode,
            Server: s.Server,
            Database: s.Database,
            Auth: s.Auth,
            UserId: s.UserId,
            HasPassword: !string.IsNullOrEmpty(s.PasswordToken),
            TrustServerCertificate: s.TrustServerCertificate,
            Encrypt: s.Encrypt,
            CommandTimeoutSeconds: s.CommandTimeoutSeconds,
            HasConnectionString: !string.IsNullOrWhiteSpace(s.ConnectionString),
            ConnectionStringHint: MaskConnectionString(s.ConnectionString),
            Query: s.Query,
            MaxRows: s.MaxRows,
            ReplaceExisting: s.ReplaceExisting,
            AutoSyncOnLoad: s.AutoSyncOnLoad,
            AutoSyncMinutes: s.AutoSyncMinutes,
            SourceLabel: s.SourceLabel,
            AppConnectionTarget: appConnectionTarget,
            SettingsFile: settingsFile,
            State: state);

    /// <summary>تشفير كلمة المرور (DPAPI على ويندوز، وإلا Base64 مع تنبيه في السجل).</summary>
    private string Protect(string plain)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
                return DpapiPrefix + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "تعذّر تشفير كلمة مرور المصدر بـ DPAPI — ستُخزَّن مُرمَّزة بـ Base64 فقط.");
            }
        }

        return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    }

    /// <summary>فك تشفير كلمة المرور، مع إرجاع null عند أي خطأ دون إسقاط الطلب.</summary>
    private string? Unprotect(string token)
    {
        try
        {
            if (token.StartsWith(DpapiPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(token[DpapiPrefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(bytes);
            }

            if (token.StartsWith(PlainPrefix, StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(token[PlainPrefix.Length..]));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر فك تشفير كلمة مرور المصدر — ستُعامَل كأنها غير مضبوطة.");
        }

        return null;
    }

    /// <summary>إخفاء كلمة المرور قبل عرض سلسلة اتصال كاملة في الواجهة.</summary>
    private static string? MaskConnectionString(string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? null
            : Regex.Replace(connectionString, @"(Password|Pwd)\s*=\s*[^;]*", "$1=••••••", RegexOptions.IgnoreCase);

    private DirectSourceFile Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new DirectSourceFile();
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<DirectSourceFile>(json, JsonOptions) ?? new DirectSourceFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّرت قراءة ملف إعدادات الربط المباشر {File} — ستُستخدم القيم الافتراضية.", FilePath);
            return new DirectSourceFile();
        }
    }

    private async Task WriteAsync(DirectSourceFile file, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = FilePath + ".tmp";

        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct);
        File.Move(temp, FilePath, overwrite: true);

        // تحديث البصمة بعد الكتابة حتى لا تُعيد Refresh قراءة ما كتبناه للتو.
        lock (_sync)
        {
            Track();
        }
    }
}
