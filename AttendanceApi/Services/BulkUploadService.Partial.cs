using System.Data;
using Microsoft.Data.SqlClient;

namespace AttendanceApi.Services;

public sealed partial class BulkUploadService
{
    private async Task<long> UploadExcelAsync(
        SqlConnection connection, Stream stream, string stagingTable, CancellationToken ct)
    {
        // قراءة متدفقة عبر MiniExcel (لا تُحمَّل الملف كاملاً في الذاكرة)
        long total = 0;
        using var dt = BuildDataTable(stagingTable);

        foreach (var row in MiniExcelLibs.MiniExcel.Query(stream, useHeaderRow: true))
        {
            ct.ThrowIfCancellationRequested();
            var dict = row as IDictionary<string, object>;

            dt.Rows.Add(
                Convert.ToInt32(dict?["EmployeeId"] ?? 0),
                Convert.ToDateTime(dict?["CheckInDate"] ?? DateTime.MinValue),
                TimeSpan.TryParse(dict?["CheckInTime"]?.ToString(), out var t) ? t : TimeSpan.Zero,
                TimeSpan.TryParse(dict?["CheckOutTime"]?.ToString(), out var o) ? o : TimeSpan.Zero,
                Convert.ToInt32(dict?["LateMinutes"] ?? 0),
                Convert.ToInt32(dict?["EarlyDepartureMinutes"] ?? 0));

            if (dt.Rows.Count >= 10_000)
            {
                await BulkCopyAsync(connection, dt, stagingTable, ct);
                total += dt.Rows.Count;
                dt.Rows.Clear();
            }
        }

        if (dt.Rows.Count > 0)
        {
            await BulkCopyAsync(connection, dt, stagingTable, ct);
            total += dt.Rows.Count;
        }

        return total;
    }

    private async Task<long> UploadCsvAsync(
        SqlConnection connection, Stream stream, string stagingTable, CancellationToken ct)
    {
        long total = 0;
        using var reader = new StreamReader(stream);
        using var dt = BuildDataTable(stagingTable);

        string? line;
        bool header = true;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (header)
            {
                header = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cols = line.Split(',');
            if (cols.Length < 2)
            {
                continue;
            }

            dt.Rows.Add(
                int.TryParse(cols[0], out var empId) ? empId : 0,
                DateTime.TryParse(cols[1], out var date) ? date : DateTime.MinValue,
                TimeSpan.TryParse(cols.Length > 2 ? cols[2] : null, out var t) ? t : TimeSpan.Zero,
                TimeSpan.TryParse(cols.Length > 3 ? cols[3] : null, out var o) ? o : TimeSpan.Zero,
                int.TryParse(cols.Length > 4 ? cols[4] : null, out var late) ? late : 0,
                int.TryParse(cols.Length > 5 ? cols[5] : null, out var early) ? early : 0);

            if (dt.Rows.Count >= 10_000)
            {
                await BulkCopyAsync(connection, dt, stagingTable, ct);
                total += dt.Rows.Count;
                dt.Rows.Clear();
            }
        }

        if (dt.Rows.Count > 0)
        {
            await BulkCopyAsync(connection, dt, stagingTable, ct);
            total += dt.Rows.Count;
        }

        return total;
    }

    private static DataTable BuildDataTable(string stagingTable)
    {
        var dt = new DataTable(stagingTable);
        dt.Columns.Add("EmployeeId", typeof(int));
        dt.Columns.Add("CheckInDate", typeof(DateTime));
        dt.Columns.Add("CheckInTime", typeof(TimeSpan));
        dt.Columns.Add("CheckOutTime", typeof(TimeSpan));
        dt.Columns.Add("LateMinutes", typeof(int));
        dt.Columns.Add("EarlyDepartureMinutes", typeof(int));
        return dt;
    }
}