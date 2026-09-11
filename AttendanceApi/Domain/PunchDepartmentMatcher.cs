using System.Text;

namespace AttendanceApi.Domain;

/// <summary>
/// مطابقة أسماء الإدارات/المديريات الواردة في ملف البصمات مع الأسماء المكتوبة في الإعدادات
/// (توحيد الهمزات والتاء المربوطة والمسافات، ثم مقارنة مباشرة أو احتواء أحدهما للآخر)
/// حتى لا تتعطّل الاستثناءات الخاصة بسبب اختلاف رسم الاسم بين ملف وآخر.
/// </summary>
public static class PunchDepartmentMatcher
{
    private static readonly char[] InvisibleMarks =
        { '\u200E', '\u200F', '\u200B', '\u200C', '\u200D', '\u00A0', '\u202A', '\u202B', '\u202C' };

    /// <summary>توحيد اسم الإدارة: إزالة العلامات الخفية، توحيد الهمزات/التاء المربوطة/الياء، وتوحيد المسافات.</summary>
    public static string Normalize(string? text)
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
                'ـ' => '\0',
                _ => ch
            });
        }

        var cleaned = sb.ToString().Replace("\0", string.Empty);
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>هل الاسم المكتوب في الإعدادات يطابق إدارة الموظف كما وردت في ملف البصمات؟</summary>
    public static bool Matches(string? configured, string? department)
    {
        var a = Normalize(configured);
        var b = Normalize(department);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // احتواء أحدهما للآخر (مثال: «الحراسة» داخل «مديريه الحراسه والدفاع المدني»).
        return a.Length >= 3 && b.Length >= 3
            && (a.Contains(b, StringComparison.OrdinalIgnoreCase)
                || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>هل توجد أي إدارة من القائمة تطابق إدارة الموظف؟</summary>
    public static bool MatchesAny(IEnumerable<string>? names, string? department)
    {
        if (names is null)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (Matches(name, department))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>مفتاح الاستثناء المطابق لإدارة الموظف (أو null إن لم يوجد استثناء).</summary>
    public static string? FindKey<T>(IReadOnlyDictionary<string, T>? map, string? department)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }

        foreach (var key in map.Keys)
        {
            if (Matches(key, department))
            {
                return key;
            }
        }

        return null;
    }
}
