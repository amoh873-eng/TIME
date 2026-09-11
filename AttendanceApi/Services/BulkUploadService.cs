using System.Data;
using Microsoft.Data.SqlClient;

namespace AttendanceApi.Services;

/// <summary>
/// الرفع الجماعي لملفات الحضور (.xlsx / .csv) إلى جداول مرحلية (Staging)
/// ثم نقلها إلى الجداول الأساسية عبر SqlBulkCopy — قراءة متدفقة منخفضة الذاكرة.
/// </summary>
public sealed partial class BulkUploadService
{
    private readonly string _connectionString;
    private readonly ILogger<BulkUploadService> _logger;

    public BulkUploadService(IConfiguration configuration, ILogger<BulkUploadService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection غير معرفة.");
        _logger = logger;
    }

    /// <summary>رفع ملف (xlsx/csv) إلى جدول المرحلة المحدد.</summary>
    public async Task<BulkUploadResult> UploadAsync(
        Stream fileStream,
        string fileName,
        string stagingTable,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var isCsv = extension is ".csv" or ".txt";

        _logger.LogInformation("بدء رفع {File} إلى جدول {Table}", fileName, stagingTable);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await EnsureStagingTableAsync(connection, stagingTable, ct);
        long rows = 0;

        if (isCsv)
        {
            rows = await UploadCsvAsync(connection, fileStream, stagingTable, ct);
        }
        else
        {
            rows = await UploadExcelAsync(connection, fileStream, stagingTable, ct);
        }

        _logger.LogInformation("اكتمل الرفع: {Rows} صفاً إلى {Table}", rows, stagingTable);

        return new BulkUploadResult(fileName, stagingTable, rows);
    }

    private static async Task BulkCopyAsync(
        SqlConnection connection, DataTable dt, string stagingTable, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity, null)
        {
            DestinationTableName = stagingTable,
            BatchSize = 50_000,
            BulkCopyTimeout = 600
        };

        foreach (DataColumn col in dt.Columns)
        {
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }

        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task EnsureStagingTableAsync(
        SqlConnection connection, string stagingTable, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            IF OBJECT_ID('{stagingTable}', 'U') IS NULL
            BEGIN
                CREATE TABLE {stagingTable} (
                    EmployeeId INT NOT NULL,
                    CheckInDate DATE NOT NULL,
                    CheckInTime TIME NULL,
                    CheckOutTime TIME NULL,
                    LateMinutes INT NOT NULL DEFAULT 0,
                    EarlyDepartureMinutes INT NOT NULL DEFAULT 0
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>نتيجة الرفع الجماعي.</summary>
public sealed record BulkUploadResult(string FileName, string StagingTable, long RowsUploaded);