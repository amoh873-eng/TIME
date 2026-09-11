using AttendanceApi.Audit;
using AttendanceApi.Data;
using AttendanceApi.Domain;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// استيراد ومراجعة «تقرير المغادرات» (تصدير الطلبات العربية):
/// تحويل الأعمدة العربية إلى حقول مُطبَّعة، ثم تطبيق المادة 118/ب وقاعدة المكافأة (حد الـ 15 يوماً).
/// </summary>
public sealed partial class DeparturesReportService
{
    private const int InsertBatchSize = 5_000;

    private readonly AttendanceDbContext _db;
    private readonly DatabaseInitializer _initializer;
    private readonly ILogger<DeparturesReportService> _logger;

    public DeparturesReportService(
        AttendanceDbContext db,
        DatabaseInitializer initializer,
        ILogger<DeparturesReportService> logger)
    {
        _db = db;
        _initializer = initializer;
        _logger = logger;
    }

    /// <summary>رفع ملف «تقرير المغادرات» إلى جدول المرحلة مع تحويل الأعمدة العربية.</summary>
    public async Task<DeparturesImportResult> ImportAsync(
        Stream stream,
        string fileName,
        bool replaceExisting = true,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);
        _logger.LogInformation("بدء استيراد تقرير المغادرات: {File}", fileName);

        var importedAt = DateTime.UtcNow;
        var buffer = new List<DeparturesReportRow>(InsertBatchSize);
        long imported = 0, skipped = 0;
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // تفريغ جدول المرحلة يتم عند أول صف صالح فعلياً من الملف (وليس قبل قراءته)
        // حمايةً لمحتوى الجدول السابق إذا فشلت القراءة أو كان الملف بلا صفوف صالحة.
        var replaced = !replaceExisting;

        foreach (var item in MiniExcelLibs.MiniExcel.Query(stream, useHeaderRow: true))
        {
            ct.ThrowIfCancellationRequested();

            if (item is not IDictionary<string, object> source)
            {
                skipped++;
                continue;
            }

            // توحيد نوع الصف (قيم قابلة للعدم) ليطابق مسار الاستيراد المباشر من قاعدة البيانات.
            var row = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
            foreach (var pair in source)
            {
                row[pair.Key] = pair.Value;
            }

            var mapped = MapRow(row, fileName, importedAt, out var typeText);
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

        _logger.LogInformation("اكتمل استيراد التقرير: {Imported} صفاً (متجاهَل {Skipped}).", imported, skipped);

        return new DeparturesImportResult(fileName, imported, skipped, typeCounts, ReplacedExisting: replaced);
    }

    /// <summary>تحويل صف واحد (من Excel أو من قاعدة بيانات مباشرة) إلى كيان مرحلة مع الحقول المُطبَّعة.</summary>
    private static DeparturesReportRow? MapRow(
        IReadOnlyDictionary<string, object?> row, string fileName, DateTime importedAt, out string? typeText)
    {
        var jobNumber = Pick(row, ReportHeaders.JobNumber, 1)?.Trim();
        typeText = Pick(row, ReportHeaders.RequestType, 9);

        if (string.IsNullOrWhiteSpace(jobNumber))
        {
            return null;
        }

        var fromTime = ParseTime(Pick(row, ReportHeaders.FromTime, 3));
        var toTime = ParseTime(Pick(row, ReportHeaders.ToTime, 4));
        var fromDate = ParseDate(Pick(row, ReportHeaders.FromDate, 5));
        var toDate = ParseDate(Pick(row, ReportHeaders.ToDate, 6));
        var durationText = Pick(row, ReportHeaders.Duration, 7)?.Trim();

        // المدة: تُحسب من فارق الأوقات للطلبات ذات اليوم الواحد (الأدق)،
        // وإلا فمن نص «المدة» (المغادرات الممتدة عبر عدة أيام).
        bool sameDay = !fromDate.HasValue || !toDate.HasValue || fromDate.Value == toDate.Value;

        var minutes = sameDay && fromTime.HasValue && toTime.HasValue
            ? (int)Math.Max(0, (toTime.Value.ToTimeSpan() - fromTime.Value.ToTimeSpan()).TotalMinutes)
            : ParseDurationMinutes(durationText);

        var days = DaysBetween(fromDate, toDate) ?? ParseDurationDays(durationText);
        var category = Classify(typeText);

        return new DeparturesReportRow
        {
            RequestNumber = Pick(row, ReportHeaders.RequestNumber, 0)?.Trim(),
            JobNumber = jobNumber,
            EmployeeName = Pick(row, ReportHeaders.EmployeeName, 2)?.Trim(),
            FromTime = fromTime,
            ToTime = toTime,
            FromDate = fromDate,
            ToDate = toDate,
            DurationText = durationText,
            Status = Pick(row, ReportHeaders.Status, 8)?.Trim(),
            RequestType = typeText?.Trim(),
            RequestDate = ParseDate(Pick(row, ReportHeaders.RequestDate, 10)),
            DepartmentName = Pick(row, ReportHeaders.Department, 11)?.Trim(),
            MainDepartmentName = Pick(row, ReportHeaders.MainDepartment, 12)?.Trim(),
            DurationMinutes = minutes,
            DaysCount = days,
            IsAuthorization = category == ReportLeaveCategory.Authorization,
            Exceeds4Hours = category == ReportLeaveCategory.Authorization
                            && LegalRules.IsExceeding4Hours(minutes),
            Category = category,
            SourceFile = fileName,
            ImportedAtUtc = importedAt
        };
    }
}
