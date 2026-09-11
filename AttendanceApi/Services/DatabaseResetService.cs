using System.Diagnostics;
using AttendanceApi.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>نطاق التنظيف: بيانات التشغيل والتحليل، أو مسح شامل يضم الجداول المرجعية.</summary>
public static class ResetScopes
{
    /// <summary>تفريغ بيانات التشغيل والتحليل مع الإبقاء على البيانات المرجعية وإعدادات المنظومة.</summary>
    public const string Data = "data";

    /// <summary>مسح شامل: بيانات التشغيل + البيانات المرجعية (الموظفون والإدارات والعطل وأنواع الإجازات).</summary>
    public const string All = "all";

    /// <summary>توحيد قيمة النطاق القادمة من الواجهة (أي قيمة غير «all» تُعتبر «data»).</summary>
    public static string Normalize(string? scope) =>
        string.Equals(scope?.Trim(), All, StringComparison.OrdinalIgnoreCase) ? All : Data;

    /// <summary>وصف النطاق بالعربية (يظهر في رسائل التنظيف والواجهة).</summary>
    public static string Label(string scope) => scope == All
        ? "مسح شامل (بيانات التشغيل + البيانات المرجعية)"
        : "تنظيف بيانات التشغيل والتحليل";
}

/// <summary>وصف جدول واحد داخل كتالوج التنظيف: اسمه، وصفه العربي، نطاقه، وعدد صفوفه الحالي.</summary>
public sealed record ResetTableInfo(string Table, string Label, string Scope, long Rows);

/// <summary>مجموعة جداول متجانسة تُعرض في الواجهة كقسم واحد (البصمات، الورديات، المغادرات…).</summary>
public sealed record ResetGroupInfo(string Key, string Title, string Note, string Scope, IReadOnlyList<ResetTableInfo> Tables);

/// <summary>حالة البيانات الحالية في قاعدة البيانات: المجموعات + إجماليات كل نطاق + حالة بصمة التحليل.</summary>
public sealed record ResetStatus(
    string Database,
    DateTime GeneratedAtUtc,
    IReadOnlyList<ResetGroupInfo> Groups,
    long DataRows,
    int DataTables,
    long ReferenceRows,
    int ReferenceTables,
    long AllRows,
    int AllTables,
    DateTime? LastAnalyzedAtUtc,
    bool WeeklyRuleCustomized);

/// <summary>نتيجة التنظيف الفعلية: ما حُذف لكل جدول + ملاحظات التشغيل + حالة البيانات بعد التنظيف.</summary>
public sealed record ResetResult(
    string Scope,
    int TablesCleared,
    long RowsDeleted,
    double ElapsedSeconds,
    IReadOnlyList<ResetTableInfo> Cleared,
    bool AnalysisMarkCleared,
    bool WeeklyRuleReset,
    IReadOnlyList<string> Notes,
    ResetStatus Status,
    DateTime RunAtUtc,
    bool DryRun = false);

/// <summary>
/// تنظيف بيانات المنظومة لاستقبال بيانات جديدة — بلا حذف للمخطط:
///   • «data» : تفريغ جداول التشغيل (البصمات الخام ونتائج التحليل والورديات والمغادرات والإجازات
///              والتصاريح والعقوبات والسجلّات) مع الإبقاء على الإدارات والموظفين والعطل وأنواع الإجازات
///              وعلى إعدادات المنظومة (ملفات الإعدادات + إعدادات المادة 118/ج).
///   • «all»  : ما سبق + الجداول المرجعية + أي جداول أخرى موجودة في قاعدة البيانات.
/// الـ Views والإجراءات المخزّنة وهيكل الجداول تبقى كما هي، ويُصفَّر عدّاد الهوية (Identity) للجداول المُفرَّغة.
/// </summary>
public sealed class DatabaseResetService
{
    private readonly AttendanceDbContext _db;
    private readonly PunchWeeklyRuleStore _weeklyRules;
    private readonly ILogger<DatabaseResetService> _logger;

    public DatabaseResetService(
        AttendanceDbContext db,
        PunchWeeklyRuleStore weeklyRules,
        ILogger<DatabaseResetService> logger)
    {
        _db = db;
        _weeklyRules = weeklyRules;
        _logger = logger;
    }

    /// <summary>تعريف مجموعة جداول: المفتاح، العنوان، الشرح، النطاق، وجداولها (اسم الجدول + وصفه العربي).</summary>
    private sealed record GroupDef(string Key, string Title, string Note, string Scope, (string Table, string Label)[] Tables);

    /// <summary>
    /// كتالوج الجداول المُدارة: ما يُفرَّغ عند التنظيف وما يبقى.
    /// الأسماء مطابقة لأسماء الجداول الفعلية في قاعدة البيانات <c>AttendanceAudit</c>.
    /// </summary>
    private static readonly GroupDef[] Catalog =
    {
        new("punch", "البصمات ونتائج التحليل",
            "ملف البصمات المستورد ونتائج التحليل القانوني اليومية والأسبوعية والشهرية، وتصاريح الإضافي والدوام المرن.",
            ResetScopes.Data, new[]
            {
                ("StagingPunchRecords", "بصمات الحضور الخام (جدول المرحلة)"),
                ("PunchDailyResults", "نتائج التحليل اليومية"),
                ("PunchWeeklyResults", "نتائج التحليل الأسبوعية"),
                ("PunchMonthlyResults", "نتائج التحليل الشهرية"),
                ("PunchWorkApprovals", "تصاريح العمل الإضافي والدوام المرن")
            }),
        new("shifts", "جدول الورديات",
            "الجدول الشهري للورديات (الحراسة وبقية الإدارات ذات الورديات) ودفعات استيراده.",
            ResetScopes.Data, new[]
            {
                ("ShiftScheduleEntries", "بنود جدول الورديات الشهري"),
                ("ShiftScheduleBatches", "دفعات استيراد الورديات")
            }),
        new("departures", "المغادرات وتقريرها",
            "تقرير المغادرات المستورد ونتائج مراجعته القانونية ومخالفات التأخير الأسبوعي وسجلّات المغادرات وتجميعها الأسبوعي.",
            ResetScopes.Data, new[]
            {
                ("StagingDeparturesReport", "تقرير المغادرات (جدول المرحلة)"),
                ("DeparturesReportReviews", "نتائج المراجعة القانونية للمغادرات"),
                ("DeparturesWeeklyLateness", "مخالفات التأخير الأسبوعي (المادة 118/ج)"),
                ("Departures", "المغادرات المسجّلة"),
                ("DepartureWeekAggregates", "التجميع الأسبوعي للمغادرات")
            }),
        new("attendance", "الحضور والإجازات",
            "سجلات الحضور المستوردة جماعياً، وطلبات الإجازات وأرصدتها وتفاصيل الإجازات المرضية.",
            ResetScopes.Data, new[]
            {
                ("AttendanceRecords", "سجلات الحضور (الرفع الجماعي)"),
                ("StagingAttendance", "جدول مرحلة الرفع الجماعي"),
                ("LeaveRequests", "طلبات الإجازات"),
                ("LeaveBalances", "أرصدة الإجازات"),
                ("SickLeaveDetails", "تفاصيل الإجازات المرضية")
            }),
        new("penalties", "الجزاءات والمكافآت ونهاية الخدمة",
            "العقوبات التأديبية وقرارات اللجنة الطبية وسجلات العمل الإضافي ومكافآت نهاية الخدمة.",
            ResetScopes.Data, new[]
            {
                ("DisciplinaryActions", "العقوبات التأديبية"),
                ("MedicalCommitteeDecisions", "قرارات اللجنة الطبية"),
                ("OvertimeRecords", "سجلات العمل الإضافي"),
                ("EndOfServiceCompensations", "مكافآت نهاية الخدمة")
            }),
        new("logs", "سجلّات التشغيل",
            "سجلّات دورات محرك المراجعة (للتتبّع فقط — لا تؤثر في النتائج).",
            ResetScopes.Data, new[]
            {
                ("AuditRunLogs", "سجلّات دورات المراجعة")
            }),
        new("reference", "البيانات المرجعية (تُمسح في المسح الشامل فقط)",
            "الموظفون والإدارات والفئات الوظيفية وجداول الدوام وأنواع الإجازات والعطل المعتمدة في التحليل.",
            ResetScopes.All, new[]
            {
                ("Employees", "الموظفون"),
                ("Departments", "الإدارات"),
                ("JobCategories", "الفئات الوظيفية"),
                ("WorkSchedules", "جداول الدوام"),
                ("LeaveTypes", "أنواع الإجازات"),
                ("OfficialHolidays", "العطل الرسمية والدينية")
            })
    };

    /// <summary>قراءة عدد صفوف كل جدول في قاعدة البيانات (sys.partitions) وربطها بكتالوج التنظيف.</summary>
    public async Task<ResetStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var counts = await ReadCountsAsync(ct);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ResetGroupInfo>();
        long dataRows = 0, referenceRows = 0, otherRows = 0;
        int dataTables = 0, referenceTables = 0, otherTables = 0;

        foreach (var def in Catalog)
        {
            var tables = def.Tables
                .Where(t => counts.ContainsKey(t.Table))
                .Select(t => { known.Add(t.Table); return new ResetTableInfo(t.Table, t.Label, def.Scope, counts[t.Table]); })
                .ToList();

            if (tables.Count == 0)
            {
                continue;
            }

            groups.Add(new ResetGroupInfo(def.Key, def.Title, def.Note, def.Scope, tables));

            if (def.Scope == ResetScopes.All)
            {
                referenceRows += tables.Sum(t => t.Rows);
                referenceTables += tables.Count;
            }
            else
            {
                dataRows += tables.Sum(t => t.Rows);
                dataTables += tables.Count;
            }
        }

        /* أي جدول في قاعدة البيانات ليس ضمن الكتالوج: يُعرض للعلم ويُمسح في «المسح الشامل» فقط */
        var extras = counts.Keys
            .Where(k => !known.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extras.Count > 0)
        {
            var tables = extras
                .Select(k => new ResetTableInfo(k, "جدول إضافي غير مصنَّف", ResetScopes.All, counts[k]))
                .ToList();

            groups.Add(new ResetGroupInfo("other", "جداول أخرى في قاعدة البيانات",
                "جداول موجودة في قاعدة البيانات وليست ضمن كتالوج المنظومة — تُمسح في «المسح الشامل» فقط.",
                ResetScopes.All, tables));

            otherRows = tables.Sum(t => t.Rows);
            otherTables = tables.Count;
        }

        return new ResetStatus(
            Database: _db.Database.GetDbConnection().Database,
            GeneratedAtUtc: DateTime.UtcNow,
            Groups: groups,
            DataRows: dataRows,
            DataTables: dataTables,
            ReferenceRows: referenceRows,
            ReferenceTables: referenceTables,
            AllRows: dataRows + referenceRows + otherRows,
            AllTables: dataTables + referenceTables + otherTables,
            LastAnalyzedAtUtc: _weeklyRules.LastAnalyzedAtUtc,
            WeeklyRuleCustomized: _weeklyRules.IsCustomized);
    }

    /// <summary>قراءة أعداد صفوف كل الجداول المستخدمة دفعة واحدة (سريع: من sys.partitions).</summary>
    private async Task<Dictionary<string, long>> ReadCountsAsync(CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        var conn = _db.Database.GetDbConnection();
        var rows = await conn.QueryAsync<TableRow>(new CommandDefinition(TableCountsSql, cancellationToken: ct));
        return rows.ToDictionary(r => r.Table, r => r.Rows, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>صف استعلام أعداد الصفوف (تُملأ حقوله عبر Dapper).</summary>
    private sealed class TableRow
    {
        public string Table { get; set; } = string.Empty;
        public long Rows { get; set; }
    }

    private const string TableCountsSql = @"
SELECT t.name AS [Table],
       CAST(ISNULL(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0) AS bigint) AS [Rows]
FROM sys.tables t
LEFT JOIN sys.partitions p ON p.object_id = t.object_id
WHERE t.is_ms_shipped = 0
GROUP BY t.name
ORDER BY t.name;";

    // =====================================================================
    //  التنظيف الفعلي
    // =====================================================================

    /// <summary>
    /// تنفيذ التنظيف داخل معاملة واحدة: تعطيل قيود المفاتيح الأجنبية مؤقتاً، تفريغ الجداول المستهدفة،
    /// تصفير عدّادات الهوية، ثم إعادة تفعيل القيود والتحقّق منها — مع الإبقاء على المخطط والـ Views.
    /// </summary>
    public async Task<ResetResult> ResetAsync(string? scope, bool resetWeeklyRule = false, bool dryRun = false, CancellationToken ct = default)
    {
        var normalized = ResetScopes.Normalize(scope);
        var before = await GetStatusAsync(ct);
        var targets = TargetTables(before, normalized);
        var notes = new List<string>();

        if (targets.Count == 0)
        {
            notes.Add("لا توجد جداول مطابقة للنطاق المطلوب — لم يُحذف أي صف.");
            return new ResetResult(normalized, 0, 0, 0, Array.Empty<ResetTableInfo>(),
                AnalysisMarkCleared: false, WeeklyRuleReset: false, notes, before, DateTime.UtcNow, DryRun: dryRun);
        }

        var stopwatch = Stopwatch.StartNew();
        var cleared = new List<ResetTableInfo>();

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.Database.ExecuteSqlRawAsync(DisableForeignKeysSql, ct);

            foreach (var table in targets)
            {
                ct.ThrowIfCancellationRequested();
                /* في «المعاينة بلا حذف» نحذف عيّنة محدودة من كل جدول: تكفي للتحقق من صلاحية الحذف
                   ومن مسار العملية كاملاً، ثم تُلغى المعاملة — وحذف الجداول الضخمة كاملاً في المعاينة
                   يجعل الإلغاء يطول أو ينتهي مهلته بلا فائدة، لأن النتيجة تبقى بلا أثر. */
                var statement = dryRun
                    ? "DELETE TOP (" + DryRunSampleRows + ") FROM " + Quote(table.Table) + ";"
                    : "DELETE FROM " + Quote(table.Table) + ";";
                await _db.Database.ExecuteSqlRawAsync(statement, ct);
                cleared.Add(table);
            }

            /* تصفير عدّاد الهوية — يُتخطّى في وضع المعاينة لأن DBCC لا يتراجع مع إلغاء المعاملة */
            if (!dryRun)
            {
                await _db.Database.ExecuteSqlRawAsync(ReseedIdentitiesSql(cleared.Select(t => t.Table)), ct);

                /* إعادة تفعيل القيود مع التحقّق منها (WITH CHECK) — تُتخطّى في «المعاينة»
                   لأن إلغاء المعاملة يعيد حالة القيود كما كانت، والتحقّق من كل الصفوف مكلف بلا فائدة. */
                await _db.Database.ExecuteSqlRawAsync(EnableForeignKeysSql, ct);
            }

            /* وضع المعاينة: أُلغِ المعاملة ليعود كل شيء كما كان (فحص بلا أثر) */
            if (dryRun)
            {
                await tx.RollbackAsync(ct);
            }
            else
            {
                await tx.CommitAsync(ct);
            }
        }

        stopwatch.Stop();

        bool markCleared = false;
        bool ruleReset = false;

        if (dryRun)
        {
            notes.Add("معاينة بلا أثر (dry-run): حُذفت عيّنة محدودة (حتى " + DryRunSampleRows +
                " صفاً لكل جدول) داخل معاملة ثم أُلغيت — الأعداد المعروضة هي ما كان سيُحذف فعلاً، ولم يتغيّر أي صف في قاعدة البيانات.");
        }
        else
        {
            /* تصفير بصمة آخر تحليل (تاريخ التحليل والقاعدة المُطبَّقة) ليعود النظام إلى حالة «لا نتائج بعد» */
            markCleared = before.LastAnalyzedAtUtc is not null && await _weeklyRules.ClearAnalysisMarkAsync(ct);

            if (resetWeeklyRule)
            {
                await _weeklyRules.ResetAsync(ct);
                ruleReset = true;
                notes.Add("أُعيدت إعدادات المادة 118/ج إلى القيم الافتراضية (appsettings.json) — أعد ضبطها إن لزم قبل التحليل التالي.");
            }
        }

        if (normalized == ResetScopes.Data && before.ReferenceTables > 0)
        {
            notes.Add($"بقيت البيانات المرجعية كما هي ({before.ReferenceTables} جدولاً: الموظفون والإدارات والعطل وأنواع الإجازات) — استخدم «المسح الشامل» لحذفها أيضاً.");
        }

        if (!dryRun && before.WeeklyRuleCustomized && !ruleReset)
        {
            notes.Add("إعدادات المادة 118/ج المحفوظة بقيت كما هي (لم تُحذف) — القواعد القانونية جاهزة للتحليل التالي.");
        }

        var after = dryRun ? before : await GetStatusAsync(ct);
        var result = new ResetResult(
            Scope: normalized,
            TablesCleared: cleared.Count,
            RowsDeleted: cleared.Sum(t => t.Rows),
            ElapsedSeconds: Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            Cleared: cleared,
            AnalysisMarkCleared: markCleared,
            WeeklyRuleReset: ruleReset,
            Notes: notes,
            Status: after,
            RunAtUtc: DateTime.UtcNow,
            DryRun: dryRun);

        _logger.LogInformation("تنظيف البيانات ({Scope}{Dry}): {Tables} جدولاً، {Rows} صفاً، {Seconds} ثانية.",
            ResetScopes.Label(normalized), dryRun ? " — معاينة بلا حذف" : string.Empty,
            result.TablesCleared, result.RowsDeleted, result.ElapsedSeconds);

        return result;
    }

    /// <summary>الجداول المستهدفة بالنطاق المطلوب (بلا تكرار، مرتّبة بالاسم لتسهيل المتابعة).</summary>
    private static List<ResetTableInfo> TargetTables(ResetStatus status, string scope) =>
        status.Groups
            .Where(g => scope == ResetScopes.All || g.Scope == ResetScopes.Data)
            .SelectMany(g => g.Tables)
            .GroupBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
            .Select(grp => grp.First())
            .OrderBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>تغليف اسم الجدول بين قوسين مربّعين مع تأمين الأقواس الداخلية.</summary>
    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";


    /// <summary>تصفير عدّاد الهوية (Identity) للجداول المُفرَّغة فقط (تلك التي تحتوي عمود هوية).</summary>
    private static string ReseedIdentitiesSql(IEnumerable<string> tables)
    {
        var list = string.Join(",", tables.Select(t => "N'" + t.Replace("'", "''") + "'"));

        return @"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'DBCC CHECKIDENT(''' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N''', RESEED, 0) WITH NO_INFOMSGS;' + CHAR(13)
FROM sys.tables t
JOIN sys.identity_columns ic ON ic.object_id = t.object_id
WHERE t.is_ms_shipped = 0 AND t.name IN (" + list + @");
EXEC sp_executesql @sql;";
    }

    /* تعطيل كل قيود المفاتيح الأجنبية مؤقتاً (تُعاد وتُتحقَّق بعد الحذف) — لتكون عملية التفريغ مستقلة عن الترتيب. */
    /// <summary>عدد الصفوف التي تُحذف من كل جدول في «المعاينة بلا حذف» — عيّنة تكفي للتحقق من الصلاحيات والمسار ثم تُلغى المعاملة.</summary>
    private const int DryRunSampleRows = 500;

    private const string DisableForeignKeysSql = @"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' NOCHECK CONSTRAINT ALL;' + CHAR(13)
FROM sys.tables t WHERE t.is_ms_shipped = 0;
EXEC sp_executesql @sql;";

    /* إعادة تفعيل القيود مع التحقّق منها (WITH CHECK) بعد التنظيف. */
    private const string EnableForeignKeysSql = @"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' WITH CHECK CHECK CONSTRAINT ALL;' + CHAR(13)
FROM sys.tables t WHERE t.is_ms_shipped = 0;
EXEC sp_executesql @sql;";
}

