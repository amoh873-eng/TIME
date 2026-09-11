using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AttendanceApi.Domain;
using ClosedXML.Excel;

namespace AttendanceApi.Services;

/// <summary>
/// تحليل «تقرير الحضور والانصراف» الخام (البصمات):
/// تحديد صف الترويسة العربي، قراءة الأعمدة بالاسم (مع الرجوع إلى الترتيب)،
/// وتوحيد حالات التحضير العربية إلى تصنيفات <see cref="PunchDayStatus"/>.
/// </summary>
public sealed partial class PunchReportService
{
    // ---- أسماء الأعمدة كما ترد في ملف النظام (بعد التوحيد: ة→ه، أ/إ→ا) ----
    private static class PunchHeaders
    {
        public const string JobNumber = "الرقم الوظيفي";
        public const string EmployeeName = "الاسم";
        public const string WorkDate = "التاريخ";
        public const string Status = "حاله التحضير";
        public const string ClockIn = "توقيت الحضور";
        public const string Location = "مكان الحضور";
        public const string ClockOut = "توقيت الانصراف";
        public const string Department = "الاداره";
    }

    /// <summary>العلامات الخفية (RTL/LTR) التي تُلصق بالتواريخ والأرقام في تصدير النظام.</summary>
    private static readonly char[] InvisibleMarks = { '\u200E', '\u200F', '\u200B', '\u200C', '\u200D', '\u00A0', '\u202A', '\u202B', '\u202C' };

    private static readonly Regex PeriodRx = new(
        @"من\s*:?\s*([0-9]{4}[/\-][0-9]{1,2}[/\-][0-9]{1,2})\s*(?:الى|إلى|الي)\s*([0-9]{4}[/\-][0-9]{1,2}[/\-][0-9]{1,2})",
        RegexOptions.Compiled);

    /// <summary>تنظيف النص: إزالة العلامات الخفية، توحيد المسافات، وتوحيد الهمزات والتاء المربوطة.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Array.IndexOf(InvisibleMarks, ch) >= 0)
            {
                continue;
            }

            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => ch
            });
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>توحيد الأرقام العربية-الهندية إلى أرقام لاتينية.</summary>
    internal static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                >= '\u0660' and <= '\u0669' => (char)('0' + (ch - '\u0660')),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + (ch - '\u06F0')),
                _ => ch
            });
        }

        return sb.ToString();
    }

    /// <summary>تصنيف حالة التحضير العربية إلى تصنيف قانوني.</summary>
    internal static PunchDayStatus ClassifyStatus(string? rawText)
    {
        var text = Normalize(rawText);

        // «-» أو «_ -» أو الفراغ = لا توجد بيانات للنظام في ذلك اليوم.
        if (text.Where(ch => char.IsLetter(ch)).Any() is false)
        {
            return PunchDayStatus.NoData;
        }

        if (text.Contains("غياب", StringComparison.Ordinal))
        {
            return PunchDayStatus.Absent;
        }

        if (text.Contains("مكتمله", StringComparison.Ordinal))
        {
            // «غير مكتملة» تحتوي «مكتملة» — الفحص المتقدم يمنع الالتباس.
            return text.Contains("غير مكتمله", StringComparison.Ordinal)
                ? PunchDayStatus.Incomplete
                : PunchDayStatus.Complete;
        }

        if (text.Contains("غير مكتمله", StringComparison.Ordinal))
        {
            return PunchDayStatus.Incomplete;
        }

        if (text.Contains("مغادره", StringComparison.Ordinal))
        {
            if (text.Contains("استيذان", StringComparison.Ordinal))
            {
                return text.Contains("طارئ", StringComparison.Ordinal)
                    ? PunchDayStatus.EmergencyPermission
                    : PunchDayStatus.MedicalPermission;
            }

            if (text.Contains("مرضيه", StringComparison.Ordinal))
            {
                return PunchDayStatus.SickLeave;
            }

            if (text.Contains("سنويه", StringComparison.Ordinal))
            {
                return PunchDayStatus.AnnualLeave;
            }

            if (text.Contains("تعويضيه", StringComparison.Ordinal))
            {
                return PunchDayStatus.CompensatoryLeave;
            }

            if (text.Contains("وفاه", StringComparison.Ordinal))
            {
                return PunchDayStatus.BereavementLeave;
            }

            if (text.Contains("مهمه عمل", StringComparison.Ordinal))
            {
                return PunchDayStatus.OfficialMission;
            }

            if (text.Contains("انتداب", StringComparison.Ordinal))
            {
                return PunchDayStatus.Secondment;
            }

            if (text.Contains("تدريب", StringComparison.Ordinal))
            {
                return PunchDayStatus.Training;
            }

            return PunchDayStatus.Unknown;
        }

        if (text.Contains("عطله الاسبوع", StringComparison.Ordinal))
        {
            return PunchDayStatus.Weekend;
        }

        if (text.Contains("عطله", StringComparison.Ordinal))
        {
            return PunchDayStatus.OfficialHoliday;
        }

        return PunchDayStatus.Unknown;
    }

    /// <summary>قراءة قيمة تاريخ من خلية Excel (قيمة زمنية حقيقية أو نص بأرقام عربية).</summary>
    internal static DateOnly? ReadDate(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        if (cell.DataType == XLDataType.DateTime)
        {
            return DateOnly.FromDateTime(cell.GetDateTime());
        }

        var text = NormalizeDigits(Normalize(cell.GetFormattedString())).Replace('-', '/');
        if (text.Length == 0 || text == "-" || text == "_")
        {
            return null;
        }

        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
    }

    /// <summary>قراءة قيمة وقت من خلية Excel (نص HH:mm أو قيمة زمنية رقمية).</summary>
    internal static TimeOnly? ReadTime(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        if (cell.DataType == XLDataType.TimeSpan)
        {
            var span = cell.Value.GetTimeSpan();
            var within = TimeSpan.FromHours(span.Hours) + TimeSpan.FromMinutes(span.Minutes) + TimeSpan.FromSeconds(span.Seconds);
            return TimeOnly.FromTimeSpan(within);
        }

        if (cell.DataType == XLDataType.DateTime)
        {
            return TimeOnly.FromDateTime(cell.GetDateTime());
        }

        if (cell.DataType == XLDataType.Number)
        {
            double number = cell.GetDouble();
            double fraction = number - Math.Floor(number);
            if (fraction > 0 && fraction < 1)
            {
                return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Round(fraction * 24 * 60)));
            }
        }

        var text = NormalizeDigits(Normalize(cell.GetFormattedString()));
        if (text.Length == 0 || text == "-" || text == "_")
        {
            return null;
        }

        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? TimeOnly.FromDateTime(dt)
            : null;
    }

    /// <summary>استخراج الفترة المعلنة في ترويسة الملف (من تاريخ ... الى تاريخ) للتحقق من نطاق البيانات.</summary>
    internal static (DateOnly? From, DateOnly? To) ReadDeclaredPeriod(IXLWorksheet ws, int headerRow)
    {
        int last = Math.Max(headerRow, 1);
        for (int r = 1; r <= last; r++)
        {
            int cols = Math.Max(1, ws.LastColumnUsed()?.ColumnNumber() ?? 1);
            for (int c = 1; c <= cols; c++)
            {
                var text = Normalize(ws.Cell(r, c).GetFormattedString());
                if (text.Length == 0)
                {
                    continue;
                }

                var match = PeriodRx.Match(NormalizeDigits(text));
                if (!match.Success)
                {
                    continue;
                }

                DateOnly? from = DateOnly.TryParse(match.Groups[1].Value.Replace('/', '-'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
                DateOnly? to = DateOnly.TryParse(match.Groups[2].Value.Replace('/', '-'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
                if (from.HasValue || to.HasValue)
                {
                    return (from, to);
                }
            }
        }

        return (null, null);
    }
}

