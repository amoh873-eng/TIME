using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// لوحة «التزام الإدارات/المديريات بالقوانين»:
/// يُسنَد كل موظف إلى إدارته (الاسم الأكثر تكراراً في صفوف التقرير)، ثم تُجمَّع نتائج المراجعة
/// (المادة 118/ب + المادة 118/ج + تجاوز 15 يوماً) لكل إدارة، فتُحسب نسبة الالتزام:
/// (عدد الموظفين بلا أي مخالفة ÷ إجمالي الموظفين المراجَعين) × 100.
/// يُرتَّب الناتج **تصاعدياً بعدد المخالفين** أي «الأكثر التزاماً أولاً» ويرافق كل مجموعة ترتيبها.
/// </summary>
public sealed partial class DeparturesReportService
{
    /// <summary>اسم المجموعة المستخدم عندما لا تحمل صفوف الموظف أي اسم إدارة.</summary>
    public const string UnspecifiedAdministration = "(غير محدّد)";

    /// <summary>
    /// تجميع نتائج المراجعة بحسب الإدارة الرئيسية (المديريات) أو الإدارة،
    /// مرتّباً تصاعدياً بعدد المخالفين (الأكثر التزاماً أولاً)، مع عتبة اختيارية لأدنى عدد موظفين.
    /// </summary>
    /// <param name="byMainDepartment">true = «الإدارة الرئيسية» (المديريات)، false = «الإدارة».</param>
    /// <param name="minEmployees">أدنى عدد موظفين لتضمين المجموعة (1 = بلا فلترة).</param>
    public async Task<DeparturesComplianceResult> GetComplianceByAdministrationAsync(
        bool byMainDepartment = true,
        int minEmployees = 1,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rows = await _db.DeparturesReportRows
            .AsNoTracking()
            .Select(r => new AdministrationRow(r.JobNumber, r.DepartmentName, r.MainDepartmentName))
            .ToListAsync(ct);

        var reviews = await _db.DeparturesReportReviews.AsNoTracking().ToListAsync(ct);

        var administrationByJob = ResolveAdministrations(rows, byMainDepartment);

        // ---- نتيجة موحّدة لكل موظف (قد يحمل الموظف أكثر من شهر في جدول المراجعة) ----
        var employees = reviews
            .GroupBy(r => r.JobNumber)
            .Select(g => new EmployeeCompliance(
                JobNumber: g.Key,
                Administration: administrationByJob.GetValueOrDefault(g.Key, UnspecifiedAdministration),
                HasViolation: g.Any(r => r.AuthorizationOver4hCount > 0
                                          || r.WeeksOver60Minutes > 0
                                          || r.Exceeds15Days),
                Over4: g.Any(r => r.AuthorizationOver4hCount > 0),
                Over60: g.Any(r => r.WeeksOver60Minutes > 0),
                Exceeds15: g.Any(r => r.Exceeds15Days),
                Article118bDays: g.Sum(r => r.Article118bDays),
                Article118cDays: g.Sum(r => r.Article118cDays),
                WeeklyLateMinutes: g.Sum(r => r.WeeklyLateMinutes)))
            .ToList();

        // ---- التجميع بحسب الإدارة ----
        int threshold = Math.Max(1, minEmployees);

        var groups = employees
            .GroupBy(e => e.Administration, StringComparer.Ordinal)
            .Select(g => BuildComplianceItem(g.Key, g.ToList()))
            .ToList();

        var items = groups
            .Where(x => x.Employees >= threshold)
            // تصاعدياً بالمخالفات = الأكثر التزاماً أولاً، ثم الأكبر عدداً، ثم الاسم.
            .OrderBy(x => x.ViolationRatePercent)
            .ThenByDescending(x => x.Employees)
            .ThenBy(x => x.Administration, StringComparer.Ordinal)
            .Select((x, i) => x with { Rank = i + 1 })
            .ToList();

        int totalEmployees = employees.Count;
        int totalViolating = employees.Count(e => e.HasViolation);

        var result = new DeparturesComplianceResult(
            GroupBy: byMainDepartment ? "mainDepartment" : "department",
            MinEmployees: threshold,
            TotalGroups: groups.Count,
            TotalEmployees: totalEmployees,
            CompliantEmployees: totalEmployees - totalViolating,
            ViolatingEmployees: totalViolating,
            OverallCompliancePercent: totalEmployees > 0
                ? Math.Round(100.0 - (totalViolating * 100.0 / totalEmployees), 2)
                : 0,
            TotalArticle118bDays: Math.Round(employees.Sum(e => e.Article118bDays), 2),
            TotalArticle118cDays: Math.Round(employees.Sum(e => e.Article118cDays), 2),
            TotalWeeklyLateMinutes: employees.Sum(e => e.WeeklyLateMinutes),
            EmployeesExceeding15Days: employees.Count(e => e.Exceeds15),
            RunAtUtc: DateTime.UtcNow,
            Items: items);

        _logger.LogInformation(
            "اكتملت لوحة التزام الإدارات ({GroupBy}): {Groups} مجموعة، {Employees} موظفاً، الالتزام {Percent:0.##}%.",
            result.GroupBy, result.TotalGroups, result.TotalEmployees, result.OverallCompliancePercent);

        return result;
    }

    /// <summary>
    /// إسناد كل موظف إلى إدارته: الاسم (الإدارة أو الإدارة الرئيسية) الأكثر تكراراً في صفوفه،
    /// ثم الأبجدي عند التعادل، و«(غير محدّد)» عند غياب الاسم من كل صفوفه.
    /// </summary>
    private static Dictionary<string, string> ResolveAdministrations(
        IEnumerable<AdministrationRow> rows,
        bool byMainDepartment)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var employee in rows.GroupBy(r => r.JobNumber, StringComparer.Ordinal))
        {
            var pick = employee
                .Select(r => (byMainDepartment ? r.MainDepartmentName : r.DepartmentName)?.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v!, StringComparer.Ordinal)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            map[employee.Key] = pick?.Name ?? UnspecifiedAdministration;
        }

        return map;
    }

    /// <summary>بناء سطر التزام واحد من موظفي إدارة واحدة.</summary>
    private static DeparturesComplianceItem BuildComplianceItem(
        string administration,
        IReadOnlyList<EmployeeCompliance> employees)
    {
        int total = employees.Count;
        int violating = employees.Count(e => e.HasViolation);
        double rate = total > 0 ? violating * 100.0 / total : 0;

        return new DeparturesComplianceItem(
            Administration: administration,
            Rank: 0,
            Employees: total,
            CompliantEmployees: total - violating,
            ViolatingEmployees: violating,
            ViolationRatePercent: Math.Round(rate, 2),
            CompliancePercent: Math.Round(100.0 - rate, 2),
            EmployeesOver4Hours: employees.Count(e => e.Over4),
            Article118bDays: Math.Round(employees.Sum(e => e.Article118bDays), 2),
            EmployeesOver60Minutes: employees.Count(e => e.Over60),
            Article118cDays: Math.Round(employees.Sum(e => e.Article118cDays), 2),
            WeeklyLateMinutes: employees.Sum(e => e.WeeklyLateMinutes),
            EmployeesExceeding15Days: employees.Count(e => e.Exceeds15));
    }

    /// <summary>صف تقرير بأعمدة الإسناد التنظيمي فقط (لتقليل حجم القراءة من قاعدة البيانات).</summary>
    private sealed record AdministrationRow(string JobNumber, string? DepartmentName, string? MainDepartmentName);

    /// <summary>نتيجة موظف واحدة بعد توحيد أشهره.</summary>
    private sealed record EmployeeCompliance(
        string JobNumber,
        string Administration,
        bool HasViolation,
        bool Over4,
        bool Over60,
        bool Exceeds15,
        double Article118bDays,
        double Article118cDays,
        int WeeklyLateMinutes);
}

/// <summary>سطر «التزام إدارة» واحد في لوحة الالتزام بالقوانين.</summary>
public sealed record DeparturesComplianceItem(
    string Administration,
    int Rank,
    int Employees,
    int CompliantEmployees,
    int ViolatingEmployees,
    double ViolationRatePercent,
    double CompliancePercent,
    int EmployeesOver4Hours,
    double Article118bDays,
    int EmployeesOver60Minutes,
    double Article118cDays,
    int WeeklyLateMinutes,
    int EmployeesExceeding15Days);

/// <summary>
/// نتيجة لوحة «التزام الإدارات بالقوانين» كاملة (مع الإجماليات العامة).
/// <c>GroupBy</c>: معيار التجميع (mainDepartment = الإدارة الرئيسية/المديريات، department = الإدارة)،
/// <c>MinEmployees</c>: عتبة أقل عدد موظفين المطبَّقة، <c>TotalGroups</c>: عدد المجموعات قبل العتبة،
/// <c>Items</c>: المجموعات مرتّبة تصاعدياً بالمخالفات (الأكثر التزاماً أولاً).
/// </summary>
public sealed record DeparturesComplianceResult(
    string GroupBy,
    int MinEmployees,
    int TotalGroups,
    int TotalEmployees,
    int CompliantEmployees,
    int ViolatingEmployees,
    double OverallCompliancePercent,
    double TotalArticle118bDays,
    double TotalArticle118cDays,
    int TotalWeeklyLateMinutes,
    int EmployeesExceeding15Days,
    DateTime RunAtUtc,
    IReadOnlyList<DeparturesComplianceItem> Items);
