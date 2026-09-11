namespace AttendanceApi.Domain;

/// <summary>
/// إعدادات «الربط المباشر بقاعدة البيانات»: تُحفظ في ملف <c>direct-source.json</c> بجوار التطبيق
/// (انظر <see cref="Services.DirectSourceSettingsStore"/>).
/// عند التفعيل تُقرأ بيانات تقرير المغادرات من قاعدة البيانات مباشرة (بدون رفع ملف Excel)
/// ثم تُستورد إلى جدول المرحلة وتُراجَع قانونياً وتظهر نتائجها في اللوحة.
/// </summary>
public sealed record DirectSourceSettings
{
    /// <summary>وضع الاتصال: <c>app</c> = قاعدة بيانات النظام الحالية، <c>ext</c> = خادم SQL Server خارجي.</summary>
    public string Mode { get; init; } = DirectSourceModes.AppDatabase;

    /// <summary>هل الربط المباشر مفعّل؟</summary>
    public bool Enabled { get; init; }

    /// <summary>اسم الخادم (مثال: <c>.\SQLEXPRESS</c> أو <c>10.0.0.5,1433</c>) — لوضع «خادم خارجي».</summary>
    public string Server { get; init; } = "";

    /// <summary>اسم قاعدة البيانات — لوضع «خادم خارجي».</summary>
    public string Database { get; init; } = "";

    /// <summary>طريقة المصادقة: <c>windows</c> أو <c>sql</c>.</summary>
    public string Auth { get; init; } = DirectSourceAuths.Windows;

    /// <summary>اسم مستخدم SQL (وضع <c>sql</c>).</summary>
    public string? UserId { get; init; }

    /// <summary>كلمة المرور بصيغة مُخزّنة (<c>dpapi:…</c> أو <c>plain:…</c>) — لا تُعاد أبداً إلى المتصفح.</summary>
    public string? PasswordToken { get; init; }

    public bool TrustServerCertificate { get; init; } = true;

    public bool Encrypt { get; init; } = true;

    /// <summary>مهلة تنفيذ الاستعلام بالثواني.</summary>
    public int CommandTimeoutSeconds { get; init; } = 300;

    /// <summary>سلسلة اتصال كاملة (تتجاوز الحقول أعلاه عند تعبئتها) — لوضع «خادم خارجي».</summary>
    public string? ConnectionString { get; init; }

    /// <summary>استعلام SELECT الذي يعيد صفوف التقرير (بالأعمدة العربية أو بترتيب أعمدة التقرير).</summary>
    public string Query { get; init; } = DefaultQuery;

    /// <summary>أقصى عدد صفوف يُقرأ في المزامنة الواحدة (حماية من الاستعلامات المفتوحة).</summary>
    public int MaxRows { get; init; } = 100_000;

    /// <summary>هل يُستبدل محتوى جدول المرحلة قبل الاستيراد؟</summary>
    public bool ReplaceExisting { get; init; } = true;

    /// <summary>هل تُنفَّذ المزامنة تلقائياً عند فتح اللوحة؟</summary>
    public bool AutoSyncOnLoad { get; init; } = true;

    /// <summary>مزامنة دورية كل عدد دقائق (0 = معطّل).</summary>
    public int AutoSyncMinutes { get; init; }

    /// <summary>وصف المصدر يظهر في اللوحة/التقرير (مثال: «نظام الموارد البشرية»).</summary>
    public string? SourceLabel { get; init; }

    /// <summary>الاستعلام الافتراضي: نموذج يعدّله المستخدم ليطابق جدول/عرض نظامه.</summary>
    public const string DefaultQuery = """
-- اكتب استعلاماً يُرجع صفوف «تقرير المغادرات» بالأعمدة العربية نفسها:
--   [رقم الطلب] [الرقم الوظيفي] [الموظف] [من وقت] [إلى وقت] [من تاريخ] [إلى تاريخ]
--   [المدة] [حالة الطلب] [نوع الطلب] [تاريخ الطلب] [الإدارة] [الإدارة الرئيسية]
-- (يُسمح باستعلامات SELECT/WITH فقط؛ وإن اختلفت الأسماء تُقرأ الأعمدة بالترتيب نفسه.)
SELECT TOP (50000) *
FROM dbo.DeparturesRequests;
""";
}

/// <summary>ثوابت أوضاع الاتصال بالمصدر.</summary>
public static class DirectSourceModes
{
    /// <summary>قاعدة بيانات النظام الحالية (AttendanceAudit) — بلا بيانات اعتماد إضافية.</summary>
    public const string AppDatabase = "app";

    /// <summary>خادم SQL Server خارجي (نظام الموارد البشرية أو قاعدة وسيطة).</summary>
    public const string External = "ext";
}

/// <summary>ثوابت طرق المصادقة على SQL Server.</summary>
public static class DirectSourceAuths
{
    public const string Windows = "windows";
    public const string Sql = "sql";
}

/// <summary>حالة آخر مزامنة (تُحفظ مع الإعدادات وتُعرض في اللوحة).</summary>
public sealed record DirectSourceState(
    DateTime? LastSyncUtc = null,
    string? LastMessage = null,
    bool LastSyncOk = false,
    int LastImportedRows = 0,
    int LastSkippedRows = 0,
    long LastElapsedMs = 0,
    bool LastTruncated = false,
    string? LastSourceLabel = null);

/// <summary>الملف المحفوظ على القرص: الإعدادات + حالة آخر مزامنة.</summary>
public sealed record DirectSourceFile
{
    public DirectSourceSettings Settings { get; init; } = new();
    public DirectSourceState State { get; init; } = new();
}

/// <summary>نسخة الإعدادات المُرسَلة إلى المتصفح (بلا كلمة مرور).</summary>
public sealed record DirectSourceSettingsView(
    bool Enabled,
    string Mode,
    string Server,
    string Database,
    string Auth,
    string? UserId,
    bool HasPassword,
    bool TrustServerCertificate,
    bool Encrypt,
    int CommandTimeoutSeconds,
    bool HasConnectionString,
    string? ConnectionStringHint,
    string Query,
    int MaxRows,
    bool ReplaceExisting,
    bool AutoSyncOnLoad,
    int AutoSyncMinutes,
    string? SourceLabel,
    string AppConnectionTarget,
    string SettingsFile,
    DirectSourceState State);

/// <summary>نتيجة اختبار الاتصال بالمصدر.</summary>
public sealed record DirectSourceTestResult(
    bool Ok,
    string Message,
    string? ServerVersion,
    string? Database,
    string? LoginName,
    string? Target,
    long ElapsedMs);

/// <summary>معاينة أول صفوف من المصدر (للتأكد من الأعمدة قبل المزامنة).</summary>
public sealed record DirectSourcePreviewResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> Columns,
    int RowCount,
    bool Truncated,
    long ElapsedMs,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);

/// <summary>إحصاء قراءة المصدر (يُحدَّث أثناء البثّ من <c>ReadRowsAsync</c>).</summary>
public sealed class DirectSourceReadStats
{
    /// <summary>عدد الصفوف التي قُرئت فعلياً من المصدر.</summary>
    public long RowsRead { get; set; }

    /// <summary>هل أُوقفت القراءة عند الحد الأقصى (MaxRows)؟</summary>
    public bool Truncated { get; set; }
}

/// <summary>نتيجة مزامنة المصدر المباشر: الاستيراد + المراجعة + الملخص.</summary>
public sealed record DirectSourceSyncResult(
    bool Ok,
    string Message,
    string SourceLabel,
    string Target,
    long RowsRead,
    long RowsImported,
    long RowsSkipped,
    bool Truncated,
    long ElapsedMs,
    DateTime RanAtUtc,
    DeparturesReviewSummary? Summary);

