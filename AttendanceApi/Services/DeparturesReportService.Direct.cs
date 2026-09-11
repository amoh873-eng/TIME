using AttendanceApi.Data;
using AttendanceApi.Domain;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// «الربط المباشر بقاعدة البيانات»: استيراد صفوف قادمة من المصدر (قاعدة بيانات) إلى جدول المرحلة
/// بنفس منطق تحويل أعمدة Excel، وبناء ملخص اللوحة من قاعدة البيانات وحدها (بلا ملف مرفوع).
/// </summary>
public sealed partial class DeparturesReportService
{
    /// <summary>
    /// استيراد صفوف جاهزة بصيغة «اسم العمود → القيمة» (من قاعدة بيانات مباشرة) إلى جدول المرحلة،
    /// مع تطبيق نفس تحويل الأعمدة العربية والمُطبَّعة المستخدم في استيراد ملفات Excel.
    /// </summary>
    public async Task<DeparturesImportResult> ImportRowsAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        string sourceLabel,
        bool replaceExisting = true,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);
        _logger.LogInformation("بدء الاستيراد المباشر من المصدر: {Source}", sourceLabel);

        var importedAt = DateTime.UtcNow;
        var buffer = new List<DeparturesReportRow>(InsertBatchSize);
        long imported = 0, skipped = 0;
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // تفريغ جدول المرحلة يتم بعد نجاح قراءة أول صف من المصدر (وليس قبل القراءة)
        // حمايةً لبيانات اللوحة السابقة إذا فشل الاتصال أو كان الاستعلام غير صحيح/بلا نتائج.
        var replaced = !replaceExisting;

        await foreach (var row in rows.WithCancellation(ct))
        {
            var mapped = MapRow(row, sourceLabel, importedAt, out var typeText);
            if (mapped is null)
            {
                skipped++;
                continue;
            }

            if (!replaced)
            {
                await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE StagingDeparturesReport;", ct);
                replaced = true;
            }

            var key = typeText?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                typeCounts[key] = typeCounts.GetValueOrDefault(key) + 1;
            }

            buffer.Add(mapped);

            if (buffer.Count >= InsertBatchSize)
            {
                await _db.BulkInsertAsync(buffer, cancellationToken: ct);
                imported += buffer.Count;
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await _db.BulkInsertAsync(buffer, cancellationToken: ct);
            imported += buffer.Count;
        }

        _logger.LogInformation(
            "اكتمل الاستيراد المباشر من {Source}: {Imported} صفاً (متجاهَل {Skipped}).",
            sourceLabel, imported, skipped);

        return new DeparturesImportResult(sourceLabel, imported, skipped, typeCounts, ReplacedExisting: replaced);
    }

    /// <summary>
    /// بناء ملخص اللوحة من قاعدة بيانات النظام مباشرة (جدول المرحلة + نتائج المراجعة + التجميع الأسبوعي)
    /// بحيث تُعرض النتائج في اللوحة دون رفع ملف أو إعادة مراجعة.
    /// </summary>
    public async Task<DeparturesReviewSummary> GetReviewSummaryAsync(CancellationToken ct = default)
    {
        var rows = _db.DeparturesReportRows.AsNoTracking();
        long total = await rows.CountAsync(ct);

        if (total == 0)
        {
            return new DeparturesReviewSummary(
                TotalRequests: 0,
                AcceptedRequests: 0,
                EmployeesReviewed: 0,
                EmployeesWithViolations: 0,
                EmployeesOver4Hours: 0,
                TotalArticle118bDays: 0,
                EmployeesExceeding15Days: 0,
                WeeksOver60Minutes: 0,
                EmployeesOver60Minutes: 0,
                TotalWeeklyLateMinutes: 0,
                TotalArticle118cDays: 0,
                LastImportUtc: null,
                SourceLabel: null,
                RequestTypes: new Dictionary<string, int>(StringComparer.Ordinal),
                Reviews: 0,
                HasData: false);
        }

        var reviews = await _db.DeparturesReportReviews.AsNoTracking().ToListAsync(ct);
        var weekly = await _db.WeeklyLateness.AsNoTracking().ToListAsync(ct);

        var typeRows = await rows
            .GroupBy(r => r.RequestType)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var types = typeRows
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key!, x => x.Count, StringComparer.Ordinal);

        var sources = await rows
            .Where(r => r.SourceFile != null)
            .GroupBy(r => r.SourceFile!)
            .Select(g => new { Source = g.Key, LastUtc = g.Max(r => r.ImportedAtUtc) })
            .OrderByDescending(x => x.LastUtc)
            .Take(5)
            .Select(x => x.Source)
            .ToListAsync(ct);

        var lastImport = await rows.MaxAsync(r => (DateTime?)r.ImportedAtUtc, ct);

        long accepted = await rows.CountAsync(
            r => r.Status == null || r.Status == "" || r.Status.Contains("مقبول"), ct);

        return new DeparturesReviewSummary(
            TotalRequests: total,
            AcceptedRequests: accepted,
            EmployeesReviewed: reviews.Select(r => r.JobNumber).Distinct().Count(),
            EmployeesWithViolations: reviews.Count(r => r.HasViolation),
            EmployeesOver4Hours: reviews.Count(r => r.AuthorizationOver4hCount > 0),
            TotalArticle118bDays: reviews.Sum(r => r.Article118bDays),
            EmployeesExceeding15Days: reviews.Count(r => r.Exceeds15Days),
            WeeksOver60Minutes: weekly.Count(w => w.Exceeds60Minutes),
            EmployeesOver60Minutes: weekly
                .Where(w => w.Exceeds60Minutes)
                .Select(w => w.JobNumber)
                .Distinct()
                .Count(),
            TotalWeeklyLateMinutes: weekly.Sum(w => w.LateMinutes),
            TotalArticle118cDays: weekly.Sum(w => w.DeductionDays),
            LastImportUtc: lastImport,
            SourceLabel: sources.Count == 0 ? null : string.Join(" + ", sources),
            RequestTypes: types,
            Reviews: reviews.Count,
            HasData: true);
    }
}

