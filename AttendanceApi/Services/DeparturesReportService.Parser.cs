using System.Globalization;
using System.Text.RegularExpressions;
using AttendanceApi.Domain;

namespace AttendanceApi.Services;

/// <summary>
/// تحليل قيم «تقرير المغادرات» العربية: الأعمدة، الأوقات، التواريخ، ونص المدة.
/// </summary>
public sealed partial class DeparturesReportService
{
    // ---- أسماء الأعمدة كما ترد في ملف التقرير (مع ترتيبها كبديل احتياطي) ----
    private static class ReportHeaders
    {
        public const string RequestNumber = "رقم الطلب";
        public const string JobNumber = "الرقم الوظيفي";
        public const string EmployeeName = "الموظف";
        public const string FromTime = "من وقت";
        public const string ToTime = "إلى وقت";
        public const string FromDate = "من تاريخ";
        public const string ToDate = "إلى تاريخ";
        public const string Duration = "المدة";
        public const string Status = "حالة الطلب";
        public const string RequestType = "نوع الطلب";
        public const string RequestDate = "تاريخ الطلب";
        public const string Department = "الإدارة";
        public const string MainDepartment = "الإدارة الرئيسية";
    }

    private static readonly Regex HoursRx = new(@"(\d+)\s*ساع", RegexOptions.Compiled);
    private static readonly Regex MinutesRx = new(@"(\d+)\s*دقيق", RegexOptions.Compiled);
    private static readonly Regex DaysRx = new(@"(\d+)\s*(أيام|ايام|يوم)", RegexOptions.Compiled);

    /// <summary>قراءة قيمة عمود بالاسم العربي، مع الرجوع إلى الترتيب عند اختلاف الترويسة.</summary>
    private static string? Pick(IReadOnlyDictionary<string, object?> row, string header, int ordinal)
    {
        foreach (var pair in row)
        {
            if (string.Equals(pair.Key?.Trim(), header, StringComparison.Ordinal))
            {
                return pair.Value?.ToString();
            }
        }

        var fallback = row.ElementAtOrDefault(ordinal);
        return fallback.Value?.ToString();
    }

    /// <summary>تحويل «7 ساعات / 30 دقيقة» أو «يوم» إلى دقائق (0 عند التعذّر).</summary>
    internal static int ParseDurationMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int hours = ExtractNumber(HoursRx, text);
        int minutes = ExtractNumber(MinutesRx, text);
        return (hours * 60) + minutes;
    }

    /// <summary>استخراج عدد الأيام من نص المدة (يوم/أيام)، وإلا تقديرها من الساعات (7 ساعات = يوم عمل).</summary>
    internal static double ParseDurationDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int days = ExtractNumber(DaysRx, text);
        if (days > 0)
        {
            return days;
        }

        int minutes = ParseDurationMinutes(text);
        if (minutes <= 0)
        {
            return 0;
        }

        // يوم العمل المعياري = 7 ساعات (كما في التقرير: 08:30 - 15:30).
        return Math.Round(minutes / (7.0 * 60.0), 2);
    }

    private static int ExtractNumber(Regex rx, string text)
    {
        var m = rx.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var value) ? value : 0;
    }

    /// <summary>عدد الأيام بين تاريخين (شامل الطرفين)، أو null عند نقص البيانات.</summary>
    internal static double? DaysBetween(DateOnly? from, DateOnly? to)
    {
        if (from is null || to is null)
        {
            return null;
        }

        int diff = to.Value.DayNumber - from.Value.DayNumber;
        return diff < 0 ? null : diff + 1;
    }

    /// <summary>قراءة وقت بصيغة HH:mm أو HH:mm:ss أو DateTime كامل.</summary>
    internal static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return TimeOnly.FromDateTime(dt);
        }

        return TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out t) ? t : null;
    }

    /// <summary>قراءة تاريخ بصيغ متعددة (yyyy-MM-dd هو الشائع في التقرير).</summary>
    internal static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return DateOnly.FromDateTime(dt);
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt)
            ? DateOnly.FromDateTime(dt)
            : null;
    }

    /// <summary>تصنيف «نوع الطلب» إلى فئة قانونية.</summary>
    internal static ReportLeaveCategory Classify(string? requestType)
    {
        if (string.IsNullOrWhiteSpace(requestType))
        {
            return ReportLeaveCategory.Unknown;
        }

        var t = requestType.Trim();

        if (t.Contains("استئذان", StringComparison.Ordinal))
        {
            return ReportLeaveCategory.Authorization;
        }

        if (t.Contains("سنوية", StringComparison.Ordinal))
        {
            return ReportLeaveCategory.AnnualLeave;
        }

        if (t.Contains("مرضية", StringComparison.Ordinal))
        {
            return ReportLeaveCategory.SickLeave;
        }

        if (t.Contains("مهمة", StringComparison.Ordinal)
            || t.Contains("انتداب", StringComparison.Ordinal)
            || t.Contains("تدريب", StringComparison.Ordinal)
            || t.Contains("رسمية", StringComparison.Ordinal))
        {
            return ReportLeaveCategory.OfficialDuty;
        }

        return ReportLeaveCategory.Other;
    }

    /// <summary>هل الطلب مقبول؟ (الحالات غير المقبولة لا تُحتسب في المراجعة).</summary>
    internal static bool IsAccepted(string? status) =>
        string.IsNullOrWhiteSpace(status) || status.Contains("مقبول", StringComparison.Ordinal);
}
