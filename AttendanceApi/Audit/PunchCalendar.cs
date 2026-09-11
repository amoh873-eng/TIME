using AttendanceApi.Domain;

namespace AttendanceApi.Audit;

/// <summary>
/// عطلة واحدة في التقويم المعتمد لتحليل الحضور والانصراف
/// (التاريخ + الاسم + التصنيف + مصدر القيد).
/// </summary>
public sealed record PunchHoliday(DateOnly Date, string Name, HolidayKind Kind, string Source)
{
    /// <summary>نص تصنيف العطلة للعرض في التقارير.</summary>
    public string KindText => PunchCalendar.KindText(Kind);
}

/// <summary>
/// تقويم العطل المعتمد في تحليل بصمات الحضور والانصراف:
/// يدمج كتالوج العطل المدمج (العطل الرسمية والدينية المعتمدة في الأردن) مع العطل
/// المسجّلة في جدول OfficialHolidays، ويحدّد أيام عطلة نهاية الأسبوع
/// (الجمعة والسبت افتراضياً، قابلة للتخصيص من الإعدادات).
/// القاعدة: أي يوم عطلة (رسمية/دينية/نهاية أسبوع) لا يُحتسب غياباً ولا مخالفة،
/// ويُعرض الدوام فيه للعلم فقط (ساعات إضافية محتملة).
/// </summary>
public sealed class PunchCalendar
{
    /// <summary>أيام عطلة نهاية الأسبوع الافتراضية في النظام (الجمعة والسبت).</summary>
    public static readonly IReadOnlyList<DayOfWeek> DefaultWeekendDays =
        new[] { DayOfWeek.Friday, DayOfWeek.Saturday };

    private readonly Dictionary<DateOnly, PunchHoliday> _holidays;
    private readonly HashSet<DayOfWeek> _weekend;

    private PunchCalendar(Dictionary<DateOnly, PunchHoliday> holidays, HashSet<DayOfWeek> weekend)
    {
        _holidays = holidays;
        _weekend = weekend;
    }

    /// <summary>عدد أيام العطل (الرسمية والدينية) في التقويم.</summary>
    public int HolidayCount => _holidays.Count;

    /// <summary>أيام العطلة الأسبوعية المعتمدة.</summary>
    public IReadOnlyCollection<DayOfWeek> WeekendDays => _weekend;

    /// <summary>كل أيام العطل الرسمية والدينية مرتّبة بالتاريخ.</summary>
    public IReadOnlyList<PunchHoliday> Holidays =>
        _holidays.Values.OrderBy(h => h.Date).ToList();

    /// <summary>البحث عن عطلة رسمية/دينية في تاريخ محدّد.</summary>
    public bool TryGetHoliday(DateOnly date, out PunchHoliday holiday) =>
        _holidays.TryGetValue(date, out holiday!);

    /// <summary>عطلة التاريخ إن وُجدت.</summary>
    public PunchHoliday? Holiday(DateOnly date) => _holidays.TryGetValue(date, out var h) ? h : null;

    /// <summary>هل التاريخ يوم عطلة أسبوعية؟</summary>
    public bool IsWeekend(DateOnly date) => _weekend.Contains(date.DayOfWeek);

    /// <summary>هل التاريخ عطلة أسبوعية فقط (غير مسجّل كعطلة رسمية/دينية)؟</summary>
    public bool IsWeekendOnly(DateOnly date) => IsWeekend(date) && !_holidays.ContainsKey(date);

    /// <summary>هل التاريخ يوم عطلة (أسبوعية أو رسمية/دينية) لا يُحتسب فيه الغياب؟</summary>
    public bool IsNonWorkingDay(DateOnly date) => IsWeekend(date) || _holidays.ContainsKey(date);

    /// <summary>اسم يوم الأسبوع بالعربية.</summary>
    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "الأحد",
        DayOfWeek.Monday => "الاثنين",
        DayOfWeek.Tuesday => "الثلاثاء",
        DayOfWeek.Wednesday => "الأربعاء",
        DayOfWeek.Thursday => "الخميس",
        DayOfWeek.Friday => "الجمعة",
        _ => "السبت"
    };

    /// <summary>
    /// بناء التقويم المعتمد من عطل قاعدة البيانات + الكتالوج المدمج + إعدادات النظام.
    /// العطل المسجّلة في قاعدة البيانات تتقدّم على المدمجة عند تطابق التاريخ،
    /// والتواريخ المستبعدة صراحةً في قاعدة البيانات تُحذف من الكتالوج المدمج أيضاً.
    /// </summary>
    public static PunchCalendar Build(
        IEnumerable<PunchHoliday>? databaseHolidays,
        PunchAnalysisOptions? options,
        IEnumerable<DateOnly>? cancelledDates = null)
    {
        var excluded = new HashSet<DateOnly>(cancelledDates ?? Enumerable.Empty<DateOnly>());
        var merged = new Dictionary<DateOnly, PunchHoliday>();

        if (options?.UseBuiltInHolidayCalendar ?? true)
        {
            foreach (var builtIn in JordanHolidayCatalog.Holidays)
            {
                if (!excluded.Contains(builtIn.Date))
                {
                    merged[builtIn.Date] = builtIn;
                }
            }
        }

        if (databaseHolidays is not null)
        {
            foreach (var holiday in databaseHolidays)
            {
                if (!string.IsNullOrWhiteSpace(holiday.Name) && !excluded.Contains(holiday.Date))
                {
                    merged[holiday.Date] = holiday;
                }
            }
        }

        var weekend = new HashSet<DayOfWeek>();
        foreach (int value in options?.WeekendDays ?? Array.Empty<int>())
        {
            if (value is >= 0 and <= 6)
            {
                weekend.Add((DayOfWeek)value);
            }
        }

        if (weekend.Count == 0)
        {
            foreach (var day in DefaultWeekendDays)
            {
                weekend.Add(day);
            }
        }

        return new PunchCalendar(merged, weekend);
    }

    /// <summary>بناء تقويم افتراضي (الكتالوج المدمج + نهاية الأسبوع الافتراضية).</summary>
    public static PunchCalendar Default() => Build(null, null);

    /// <summary>نص تصنيف العطلة.</summary>
    public static string KindText(HolidayKind kind) => kind switch
    {
        HolidayKind.Official => "عطلة رسمية",
        HolidayKind.ReligiousIslamic => "عطلة دينية إسلامية",
        HolidayKind.ReligiousChristian => "عطلة دينية مسيحية",
        HolidayKind.NationalOccasion => "مناسبة وطنية",
        _ => "غير محدّد"
    };

    /// <summary>نص أيام نهاية الأسبوع للعرض (مثال: الجمعة والسبت).</summary>
    public string WeekendDaysText =>
        string.Join(" و", _weekend
            .OrderBy(d => d == DayOfWeek.Sunday ? 7 : (int)d)
            .Select(DayName));
}
