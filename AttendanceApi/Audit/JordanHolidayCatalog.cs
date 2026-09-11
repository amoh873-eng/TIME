using System.Globalization;
using AttendanceApi.Domain;

namespace AttendanceApi.Audit;

/// <summary>
/// الكتالوج المدمج لتقويم العطل الرسمية والدينية في الأردن (2023 — 2025)
/// يُستخدم كأساس لتقويم العطل في تحليل بصمات الحضور والانصراف، ويمكن للمشغّل
/// تعديله أو إضافة عطل جديدة من واجهة «تقويم العطل» (تُحفظ في جدول OfficialHolidays
/// وتتقدّم على هذا الكتالوج عند تطابق التاريخ).
/// ملاحظة قانونية: تواريخ العطل الدينية (الهجرية والقبطية) تُبنى على الإعلان الرسمي
/// لكل سنة، ويجب تحديثها سنوياً من الواجهة عند صدور الإعلان.
/// </summary>
public static class JordanHolidayCatalog
{
    private const string Source = "كتالوج النظام (الأردن)";

    /// <summary>عطل السنة 2023 (رسمية ودينية).</summary>
    private static readonly PunchHoliday[] Year2023 =
    {
        H("2023-01-01", "رأس السنة الميلادية", HolidayKind.Official),
        H("2023-02-18", "ليلة الإسراء والمعراج", HolidayKind.ReligiousIslamic),
        H("2023-04-21", "عيد الفطر السعيد — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2023-04-22", "عيد الفطر السعيد — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2023-04-23", "عيد الفطر السعيد — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2023-05-01", "عيد العمال", HolidayKind.Official),
        H("2023-05-25", "عيد الاستقلال", HolidayKind.Official),
        H("2023-06-28", "عيد الأضحى المبارك — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2023-06-29", "عيد الأضحى المبارك — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2023-06-30", "عيد الأضحى المبارك — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2023-07-01", "عيد الأضحى المبارك — اليوم الرابع", HolidayKind.ReligiousIslamic),
        H("2023-07-19", "رأس السنة الهجرية", HolidayKind.ReligiousIslamic),
        H("2023-09-27", "المولد النبوي الشريف", HolidayKind.ReligiousIslamic),
        H("2023-12-25", "عيد الميلاد المجيد", HolidayKind.ReligiousChristian)
    };

    /// <summary>عطل السنة 2024 (رسمية ودينية).</summary>
    private static readonly PunchHoliday[] Year2024 =
    {
        H("2024-01-01", "رأس السنة الميلادية", HolidayKind.Official),
        H("2024-02-08", "ليلة الإسراء والمعراج", HolidayKind.ReligiousIslamic),
        H("2024-04-10", "عيد الفطر السعيد — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2024-04-11", "عيد الفطر السعيد — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2024-04-12", "عيد الفطر السعيد — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2024-05-01", "عيد العمال", HolidayKind.Official),
        H("2024-05-25", "عيد الاستقلال", HolidayKind.Official),
        H("2024-06-16", "عيد الأضحى المبارك — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2024-06-17", "عيد الأضحى المبارك — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2024-06-18", "عيد الأضحى المبارك — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2024-06-19", "عيد الأضحى المبارك — اليوم الرابع", HolidayKind.ReligiousIslamic),
        H("2024-07-07", "رأس السنة الهجرية", HolidayKind.ReligiousIslamic),
        H("2024-09-16", "المولد النبوي الشريف", HolidayKind.ReligiousIslamic),
        H("2024-12-25", "عيد الميلاد المجيد", HolidayKind.ReligiousChristian)
    };

    /// <summary>عطل السنة 2025 (رسمية ودينية).</summary>
    private static readonly PunchHoliday[] Year2025 =
    {
        H("2025-01-01", "رأس السنة الميلادية", HolidayKind.Official),
        H("2025-01-27", "ليلة الإسراء والمعراج", HolidayKind.ReligiousIslamic),
        H("2025-03-30", "عيد الفطر السعيد — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2025-03-31", "عيد الفطر السعيد — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2025-04-01", "عيد الفطر السعيد — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2025-05-01", "عيد العمال", HolidayKind.Official),
        H("2025-05-25", "عيد الاستقلال", HolidayKind.Official),
        H("2025-06-06", "عيد الأضحى المبارك — اليوم الأول", HolidayKind.ReligiousIslamic),
        H("2025-06-07", "عيد الأضحى المبارك — اليوم الثاني", HolidayKind.ReligiousIslamic),
        H("2025-06-08", "عيد الأضحى المبارك — اليوم الثالث", HolidayKind.ReligiousIslamic),
        H("2025-06-09", "عيد الأضحى المبارك — اليوم الرابع", HolidayKind.ReligiousIslamic),
        H("2025-06-26", "رأس السنة الهجرية", HolidayKind.ReligiousIslamic),
        H("2025-09-04", "المولد النبوي الشريف", HolidayKind.ReligiousIslamic),
        H("2025-12-25", "عيد الميلاد المجيد", HolidayKind.ReligiousChristian)
    };

    /// <summary>كل عطل الكتالوج المدمج (2023 — 2025) مرتّبة بالتاريخ.</summary>
    public static readonly IReadOnlyList<PunchHoliday> Holidays =
        Year2023.Concat(Year2024).Concat(Year2025)
            .OrderBy(h => h.Date)
            .ToList();

    /// <summary>أول تاريخ في الكتالوج المدمج.</summary>
    public static DateOnly FirstDate => Holidays[0].Date;

    /// <summary>آخر تاريخ في الكتالوج المدمج.</summary>
    public static DateOnly LastDate => Holidays[^1].Date;

    /// <summary>عطل الكتالوج المدمج الواقعة في نطاق تاريخي (للتقارير والاستعراض).</summary>
    public static IReadOnlyList<PunchHoliday> InRange(DateOnly from, DateOnly to) =>
        Holidays.Where(h => h.Date >= from && h.Date <= to).ToList();

    private static PunchHoliday H(string isoDate, string name, HolidayKind kind) =>
        new(DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), name, kind, Source);
}
