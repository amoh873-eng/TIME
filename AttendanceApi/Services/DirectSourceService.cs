using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AttendanceApi.Domain;
using Microsoft.Data.SqlClient;

namespace AttendanceApi.Services;

/// <summary>
/// «الربط المباشر بقاعدة البيانات»: اختبار الاتصال، معاينة الأعمدة، وقراءة صفوف تقرير المغادرات
/// من قاعدة البيانات (بثّ صفوف واحداً واحداً) لتمريرها إلى جدول المرحلة ثم المراجعة القانونية.
/// يُسمح باستعلامات <c>SELECT/WITH</c> فقط (قراءة فقط من قاعدة المصدر).
/// </summary>
public sealed class DirectSourceService
{
    private readonly DirectSourceSettingsStore _store;
    private readonly IConfiguration _config;
    private readonly DeparturesReportService _departures;
    private readonly ILogger<DirectSourceService> _logger;

    public DirectSourceService(
        DirectSourceSettingsStore store,
        IConfiguration config,
        DeparturesReportService departures,
        ILogger<DirectSourceService> logger)
    {
        _store = store;
        _config = config;
        _departures = departures;
        _logger = logger;
    }

    /// <summary>وصف المصدر كما يظهر في الواجهة/التقرير.</summary>
    public string DescribeTarget(DirectSourceSettings s)
        => s.Mode == DirectSourceModes.AppDatabase
            ? $"قاعدة بيانات النظام الحالية ({AppDatabaseName()})"
            : $"{s.Server} / {s.Database} ({AuthLabel(s.Auth)})";

    /// <summary>اسم قاعدة بيانات النظام الحالية من سلسلة الاتصال الافتراضية.</summary>
    public string AppDatabaseName()
    {
        try
        {
            var cs = _config.GetConnectionString("DefaultConnection") ?? "";
            var name = new SqlConnectionStringBuilder(cs).InitialCatalog;
            return string.IsNullOrWhiteSpace(name) ? "(غير محدّدة)" : name;
        }
        catch
        {
            return "(غير محدّدة)";
        }
    }

    private static string AuthLabel(string auth) =>
        auth == DirectSourceAuths.Sql ? "مصادقة SQL" : "مصادقة ويندوز";

    /// <summary>بناء سلسلة الاتصال بحسب الوضع (قاعدة النظام، أو خادم خارجي بحقول/سلسلة كاملة).</summary>
    public string ResolveConnectionString(DirectSourceSettings s, string? plainPassword)
    {
        if (s.Mode == DirectSourceModes.AppDatabase)
        {
            var cs = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException("سلسلة اتصال قاعدة بيانات النظام (DefaultConnection) غير مضبوطة.");
            }

            return cs;
        }

        if (!string.IsNullOrWhiteSpace(s.ConnectionString))
        {
            return s.ConnectionString!;
        }

        if (string.IsNullOrWhiteSpace(s.Server) || string.IsNullOrWhiteSpace(s.Database))
        {
            throw new InvalidOperationException("أدخل اسم الخادم واسم قاعدة البيانات (أو سلسلة اتصال كاملة).");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = s.Server.Trim(),
            InitialCatalog = s.Database.Trim(),
            TrustServerCertificate = s.TrustServerCertificate,
            Encrypt = s.Encrypt,
            ConnectTimeout = 20,
            ApplicationName = "AttendanceApi/DirectSource"
        };

        if (s.Auth == DirectSourceAuths.Sql)
        {
            if (string.IsNullOrWhiteSpace(s.UserId))
            {
                throw new InvalidOperationException("أدخل اسم مستخدم SQL.");
            }

            builder.UserID = s.UserId.Trim();
            builder.Password = plainPassword ?? string.Empty;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>قبول استعلامات القراءة فقط (SELECT/WITH) ورفض أي تعديل على قاعدة المصدر.</summary>
    public static string EnsureReadOnlyQuery(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("لم يُضبط استعلام المصدر.");
        }

        var stripped = StripLeadingComments(text);
        var first = stripped.Length >= 2 ? stripped[..2].ToUpperInvariant() : "";

        if (first != "SE" && first != "WI")
        {
            throw new InvalidOperationException("يُسمح باستعلامات القراءة فقط (SELECT أو WITH) على قاعدة المصدر.");
        }

        return text;
    }

    /// <summary>إزالة تعليقات T-SQL البادئة للتحقق من نوع الاستعلام.</summary>
    private static string StripLeadingComments(string sql)
    {
        var text = sql.TrimStart();

        while (text.Length > 0)
        {
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n');
                if (end < 0)
                {
                    return "";
                }

                text = text[(end + 1)..].TrimStart();
                continue;
            }

            if (text.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return "";
                }

                text = text[(end + 2)..].TrimStart();
                continue;
            }

            break;
        }

        return text;
    }

    /// <summary>اختبار الاتصال بالمصدر (فتح الاتصال + قراءة معلومات الخادم).</summary>
    public async Task<DirectSourceTestResult> TestAsync(DirectSourceSettings s, CancellationToken ct = default)
    {
        var target = DescribeTarget(s);
        var watch = Stopwatch.StartNew();

        try
        {
            EnsureReadOnlyQuery(s.Query);
            var password = _store.UnprotectPassword(s);
            var cs = ResolveConnectionString(s, password);

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = Math.Clamp(s.CommandTimeoutSeconds, 15, 3600);
            cmd.CommandText = """
SELECT
    ServerVersion = CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
    ServerEdition = CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
    DatabaseName  = DB_NAME(),
    LoginName     = SUSER_SNAME(),
    ServerName    = @@SERVERNAME;
""";

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);

            string? version = null, edition = null, database = null, login = null, server = null;
            if (await reader.ReadAsync(ct))
            {
                version = ReadString(reader, 0);
                edition = ReadString(reader, 1);
                database = ReadString(reader, 2);
                login = ReadString(reader, 3);
                server = ReadString(reader, 4);
            }

            watch.Stop();
            _logger.LogInformation("نجح اختبار اتصال المصدر المباشر: {Target}", target);

            return new DirectSourceTestResult(
                Ok: true,
                Message: $"تم الاتصال بنجاح بالمصدر: {target} — قاعدة «{database}» على الخادم «{server}».",
                ServerVersion: string.Join(" — ", new[] { version, edition }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Database: database,
                LoginName: login,
                Target: target,
                ElapsedMs: watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            watch.Stop();
            _logger.LogWarning(ex, "فشل اختبار اتصال المصدر المباشر: {Target}", target);

            return new DirectSourceTestResult(
                Ok: false,
                Message: $"تعذّر الاتصال بالمصدر ({target}): {ex.Message}",
                ServerVersion: null,
                Database: null,
                LoginName: null,
                Target: target,
                ElapsedMs: watch.ElapsedMilliseconds);
        }
    }

    private static string? ReadString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();

    /// <summary>
    /// بثّ صفوف المصدر واحداً واحداً (بحد أقصى <c>MaxRows</c>) بصيغة قواميس
    /// «اسم العمود → القيمة» بعد تطبيع القيم (تواريخ/أوقات كنصوص ISO).
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
        DirectSourceSettings s,
        DirectSourceReadStats stats,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReadOnlyQuery(s.Query);

        int max = s.MaxRows <= 0 ? int.MaxValue : s.MaxRows;
        var password = _store.UnprotectPassword(s);
        var cs = ResolveConnectionString(s, password);

        _logger.LogInformation("بدء القراءة من المصدر المباشر {Target} (حد الصفوف: {Max}).",
            DescribeTarget(s), max);

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = s.Query;
        cmd.CommandTimeout = Math.Clamp(s.CommandTimeoutSeconds, 15, 3600);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        int fields = reader.FieldCount;
        if (fields == 0)
        {
            yield break;
        }

        var names = new string[fields];
        for (int i = 0; i < fields; i++)
        {
            var name = reader.GetName(i);
            names[i] = string.IsNullOrWhiteSpace(name) ? $"عمود{i + 1}" : name.Trim();
        }

        long read = 0;

        while (await reader.ReadAsync(ct))
        {
            if (read >= max)
            {
                stats.Truncated = true;
                yield break;
            }

            var row = new Dictionary<string, object?>(fields, StringComparer.Ordinal);

            for (int i = 0; i < fields; i++)
            {
                var key = names[i];
                if (row.ContainsKey(key))
                {
                    key = $"{key} ({i + 1})";
                }

                row[key] = NormalizeValue(reader.GetValue(i));
            }

            read++;
            stats.RowsRead = read;
            yield return row;
        }
    }

    /// <summary>تطبيع قيمة خام من قاعدة البيانات إلى نوع يفهمه مُحلِّل التقرير.</summary>
    private static object? NormalizeValue(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(),
        bool flag => flag ? "نعم" : "لا",
        byte[] => null,
        string text => text,
        _ => value.ToString()
    };

    /// <summary>معاينة أول صفوف من المصدر (لعرض الأعمدة في نافذة الإعدادات قبل المزامنة).</summary>
    public async Task<DirectSourcePreviewResult> PreviewAsync(
        DirectSourceSettings s,
        int take = 20,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var watch = Stopwatch.StartNew();
        var stats = new DirectSourceReadStats();
        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        bool more = false;

        try
        {
            await foreach (var row in ReadRowsAsync(s, stats, ct))
            {
                if (columns.Count == 0)
                {
                    columns.AddRange(row.Keys);
                }

                rows.Add(row.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.ToString(),
                    StringComparer.Ordinal));

                if (rows.Count >= take)
                {
                    more = true;
                    break;
                }
            }

            watch.Stop();

            if (rows.Count == 0)
            {
                return new DirectSourcePreviewResult(
                    Ok: false,
                    Message: "نُفِّذ الاستعلام بنجاح لكنه لم يُرجع أي صف — تحقق من شرط الاستعلام/اسم الجدول.",
                    Columns: columns,
                    RowCount: 0,
                    Truncated: false,
                    ElapsedMs: watch.ElapsedMilliseconds,
                    Rows: rows);
            }

            return new DirectSourcePreviewResult(
                Ok: true,
                Message: $"تم جلب {rows.Count} صفاً للمعاينة من {columns.Count} عموداً"
                         + (more ? " (توجد صفوف إضافية يتم تجاهلها في المعاينة)." : "."),
                Columns: columns,
                RowCount: rows.Count,
                Truncated: more,
                ElapsedMs: watch.ElapsedMilliseconds,
                Rows: rows);
        }
        catch (Exception ex)
        {
            watch.Stop();
            _logger.LogWarning(ex, "فشلت معاينة المصدر المباشر.");

            return new DirectSourcePreviewResult(
                Ok: false,
                Message: $"تعذّرت معاينة المصدر: {ex.Message}",
                Columns: columns,
                RowCount: rows.Count,
                Truncated: more,
                ElapsedMs: watch.ElapsedMilliseconds,
                Rows: rows);
        }
    }

    /// <summary>
    /// المزامنة الكاملة: قراءة المصدر ← استيراد جدول المرحلة ← المراجعة القانونية ← الملخص،
    /// مع حفظ حالة آخر مزامنة لعرضها في اللوحة.
    /// </summary>
    public async Task<DirectSourceSyncResult> SyncAsync(DirectSourceSettings s, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var target = DescribeTarget(s);
        var label = string.IsNullOrWhiteSpace(s.SourceLabel)
            ? $"قاعدة البيانات المباشرة — {target}"
            : s.SourceLabel!.Trim();
        var stats = new DirectSourceReadStats();

        try
        {
            var import = await _departures.ImportRowsAsync(
                ReadRowsAsync(s, stats, ct), label, s.ReplaceExisting, ct);

            var review = await _departures.ReviewAsync(ct);
            var summary = await _departures.GetReviewSummaryAsync(ct);

            watch.Stop();

            var message = $"تمت المزامنة من {target}: قُرئ {stats.RowsRead} صفاً، واستُورد {import.RowsImported} صفاً"
                          + (import.RowsSkipped > 0 ? $"، وتُجوهل {import.RowsSkipped} بلا رقم وظيفي" : "")
                          + (stats.Truncated ? $" — تنبيه: أُوقفت القراءة عند حد {s.MaxRows} صفاً" : "")
                          + (!import.ReplacedExisting
                              ? " — تنبيه: لم يُرصد أي صف صالح من المصدر فلم يُعدَّل جدول المرحلة (بقيت نتائج اللوحة السابقة)."
                              : "")
                          + $" — {review.EmployeesReviewed} موظفاً، منهم {review.EmployeesWithViolations} لديهم مخالفات.";

            var state = new DirectSourceState(
                LastSyncUtc: DateTime.UtcNow,
                LastMessage: message,
                LastSyncOk: true,
                LastImportedRows: (int)import.RowsImported,
                LastSkippedRows: (int)import.RowsSkipped,
                LastElapsedMs: watch.ElapsedMilliseconds,
                LastTruncated: stats.Truncated,
                LastSourceLabel: label);

            await _store.SaveStateAsync(state, ct);
            _logger.LogInformation("اكتملت مزامنة المصدر المباشر: {Message}", message);

            return new DirectSourceSyncResult(
                Ok: true,
                Message: message,
                SourceLabel: label,
                Target: target,
                RowsRead: stats.RowsRead,
                RowsImported: import.RowsImported,
                RowsSkipped: import.RowsSkipped,
                Truncated: stats.Truncated,
                ElapsedMs: watch.ElapsedMilliseconds,
                RanAtUtc: DateTime.UtcNow,
                Summary: summary);
        }
        catch (Exception ex)
        {
            watch.Stop();
            var message = $"تعذّرت المزامنة من {target}: {ex.Message}";
            _logger.LogError(ex, "فشلت مزامنة المصدر المباشر من {Target}.", target);

            await _store.SaveStateAsync(
                new DirectSourceState(
                    LastSyncUtc: DateTime.UtcNow,
                    LastMessage: message,
                    LastSyncOk: false,
                    LastImportedRows: 0,
                    LastSkippedRows: 0,
                    LastElapsedMs: watch.ElapsedMilliseconds,
                    LastTruncated: stats.Truncated,
                    LastSourceLabel: label),
                CancellationToken.None);

            return new DirectSourceSyncResult(
                Ok: false,
                Message: message,
                SourceLabel: label,
                Target: target,
                RowsRead: stats.RowsRead,
                RowsImported: 0,
                RowsSkipped: 0,
                Truncated: stats.Truncated,
                ElapsedMs: watch.ElapsedMilliseconds,
                RanAtUtc: DateTime.UtcNow,
                Summary: null);
        }
    }
}
