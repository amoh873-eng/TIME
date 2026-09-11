using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Data;

/// <summary>
/// تهيئة قاعدة البيانات: إنشاء المخطط من نموذج EF ثم تنفيذ ملفات SQL المضمّنة
/// (Views + Stored Procedures) بعد تقسيمها على دفعات GO.
/// </summary>
public sealed class DatabaseInitializer
{
    private const string SqlResourceMarker = ".Data.Sql.";

    private readonly AttendanceDbContext _db;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(AttendanceDbContext db, ILogger<DatabaseInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>إنشاء قاعدة البيانات/الجداول ثم تطبيق السكربتات المضمّنة.</summary>
    public async Task<DatabaseInitResult> InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("تهيئة قاعدة البيانات...");

        bool created = await _db.Database.EnsureCreatedAsync(ct);

        var assembly = typeof(DatabaseInitializer).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(SqlResourceMarker, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int scripts = 0, batches = 0;

        foreach (var resource in resources)
        {
            ct.ThrowIfCancellationRequested();

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);

            foreach (var batch in SplitOnGo(sql))
            {
                if (string.IsNullOrWhiteSpace(batch))
                {
                    continue;
                }

                try
                {
                    await _db.Database.ExecuteSqlRawAsync(batch, ct);
                    batches++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "فشل تنفيذ دفعة من السكربت {Script} — سيتم المتابعة.", resource);
                }
            }

            scripts++;
        }

        _logger.LogInformation(
            "اكتملت تهيئة قاعدة البيانات (أنشئت={Created}, سكربتات={Scripts}, دفعات={Batches}).",
            created, scripts, batches);

        return new DatabaseInitResult(created, scripts, batches, resources.Count);
    }

    /// <summary>تقسيم سكربت T-SQL إلى دفعات حسب فاصل GO (لا يفهمه SqlClient).</summary>
    internal static IEnumerable<string> SplitOnGo(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n").Replace("\r", "\n");
        var buffer = new System.Text.StringBuilder();

        foreach (var line in normalized.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                yield return buffer.ToString();
                buffer.Clear();
                continue;
            }

            buffer.AppendLine(line);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}

/// <summary>نتيجة تهيئة قاعدة البيانات.</summary>
public sealed record DatabaseInitResult(
    bool DatabaseCreated,
    int ScriptsExecuted,
    int BatchesExecuted,
    int ScriptsFound);
