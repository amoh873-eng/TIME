using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AttendanceApi.Audit;
using AttendanceApi.Domain;

namespace AttendanceApi.Services;

/// <summary>
/// تخزين قاعدة المادة 118/ج الأسبوعية (<see cref="PunchWeeklyRule"/>) في ملف JSON بجوار التطبيق
/// (<c>punch-analysis.json</c>) لتغيير حدّ الدقائق الأسبوعي وسقف الناتج المحتسب وأيام الخصم
/// وبقية خيارات الدمج <b>في وقت التشغيل</b> بلا إعادة بناء ولا إعادة تشغيل.
/// القيم الافتراضية تُقرأ من <c>appsettings.json → PunchAnalysis</c> (انظر <see cref="PunchAnalysisOptions"/>)
/// ويُعاد إليها بزر «إعادة الافتراضي» (حذف الملف).
/// </summary>
public sealed class PunchWeeklyRuleStore
{
    private const string FileName = "punch-analysis.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<PunchWeeklyRuleStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    /// <summary>وقت آخر تعديل للملف كما قرأناه (لكشف التعديل اليدوي أو الحذف خارج التطبيق).</summary>
    private DateTime _stampUtc;

    /// <summary>هل كان الملف موجوداً عند آخر قراءة؟</summary>
    private bool _seen;

    public PunchWeeklyRuleStore(
        IHostEnvironment env,
        PunchAnalysisOptions defaults,
        ILogger<PunchWeeklyRuleStore> logger)
    {
        _logger = logger;
        FilePath = Path.Combine(env.ContentRootPath, FileName);
        Defaults = PunchWeeklyRule.Normalize(BuildDefaults(defaults));
        CurrentFile = ReadAndTrack();
    }

    /// <summary>مسار ملف الإعدادات (يُعرض في الواجهة للنسخ الاحتياطي).</summary>
    public string FilePath { get; }

    /// <summary>القاعدة الافتراضية المستمدّة من <c>appsettings.json → PunchAnalysis</c>.</summary>
    public PunchWeeklyRule Defaults { get; }

    /// <summary>محتويات الملف الحالية في الذاكرة.</summary>
    private PunchWeeklyRuleFile CurrentFile { get; set; }

    /// <summary>هل توجد إعدادات محفوظة على القرص (أي أن الافتراضي مُتجاوَز)؟</summary>
    public bool IsCustomized => File.Exists(FilePath);

    /// <summary>القاعدة السارية الآن (المحفوظة إن وُجد ملف، وإلا الافتراضية من appsettings).</summary>
    public PunchWeeklyRule Load() => Effective(Refresh());

    /// <summary>وقت آخر حفظ للإعدادات.</summary>
    public DateTime? LastSavedUtc => Refresh().SavedAtUtc;

    /// <summary>القاعدة التي طُبِّقت فعلياً في آخر تحليل (للتدقيق ومعرفة إن كانت النتائج بحاجة إعادة تحليل).</summary>
    public PunchWeeklyRule? AppliedRule => Refresh().AppliedRule;

    /// <summary>وقت آخر تحليل قانوني للبصمات.</summary>
    public DateTime? LastAnalyzedAtUtc => Refresh().LastAnalyzedAtUtc;

    /// <summary>هل تغيّرت الإعدادات بعد آخر تحليل (فتحتاج النتائج إلى إعادة تحليل)؟</summary>
    public bool PendingReanalysis
    {
        get
        {
            var file = Refresh();
            return IsCustomized && (file.AppliedRule is null || !file.AppliedRule.SameAs(Effective(file)));
        }
    }

    /// <summary>حفظ قاعدة جديدة (مع تقييد القيم) وإرجاع القاعدة السارية بعد الحفظ.</summary>
    public async Task<PunchWeeklyRule> SaveAsync(PunchWeeklyRule rule, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var normalized = PunchWeeklyRule.Normalize(rule);
            var file = Refresh() with { Rule = normalized, SavedAtUtc = DateTime.UtcNow };

            await WriteAsync(file, ct);
            _logger.LogInformation(
                "حُفظت إعدادات المادة 118/ج (الحدّ {Threshold} دقيقة، سقف {Cap}، خصم {Days} يوم/أسبوع، الملف {File}).",
                normalized.ThresholdMinutes, normalized.EffectiveCapMinutes, normalized.DeductionDaysText(), FilePath);

            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>إعادة القاعدة إلى الافتراضي (حذف ملف الإعدادات).</summary>
    public async Task<PunchWeeklyRule> ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "تعذّر حذف ملف إعدادات المادة 118/ج {File}.", FilePath);
            }

            lock (_sync)
            {
                CurrentFile = new PunchWeeklyRuleFile(Defaults);
            }

            Track();
            _logger.LogInformation("أُعيدت قاعدة المادة 118/ج إلى القيم الافتراضية من appsettings.");
            return Defaults;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// تصفير بصمة آخر تحليل (تاريخ التحليل والقاعدة المُطبَّقة) مع الإبقاء على القاعدة المحفوظة —
    /// يُستخدم عند تنظيف البيانات ليعود النظام إلى حالة «لا نتائج بعد» بلا فقدان إعدادات المادة 118/ج.
    /// </summary>
    public async Task<bool> ClearAnalysisMarkAsync(CancellationToken ct = default)
    {
        if (!IsCustomized)
        {
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            await WriteAsync(Refresh() with { AppliedRule = null, LastAnalyzedAtUtc = null }, ct);
            _logger.LogInformation("صُفِّرت بصمة آخر تحليل في ملف إعدادات المادة 118/ج {File}.", FilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر تصفير بصمة آخر تحليل في {File}.", FilePath);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }


    /// <summary>
    /// تسجيل القاعدة التي طُبِّقت في التحليل الأخير (مع بصمة الوقت) لتتبّع القيم قانونياً.
    /// لا يُنشئ ملفاً إن لم تكن هناك إعدادات محفوظة (الافتراضي معروف من appsettings نفسه)،
    /// ولا يُفشل التحليل إن تعذّرت الكتابة.
    /// </summary>
    public async Task MarkAnalyzedAsync(PunchWeeklyRule appliedRule, CancellationToken ct = default)
    {
        if (!IsCustomized)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var file = Refresh() with
            {
                AppliedRule = PunchWeeklyRule.Normalize(appliedRule),
                LastAnalyzedAtUtc = DateTime.UtcNow
            };

            await WriteAsync(file, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر تسجيل القاعدة المطبَّقة في ملف إعدادات المادة 118/ج.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>بناء القاعدة الافتراضية من إعدادات appsettings.</summary>
    private static PunchWeeklyRule BuildDefaults(PunchAnalysisOptions options) => new()
    {
        Enabled = true,
        MergeEarlyDeparture = true,
        MergeMidDayGaps = true,
        ThresholdMinutes = options.WeeklyLateMinutesThreshold,
        ViolationWhenExceededOnly = false,
        CapCountedMinutes = options.CapWeeklyCountedMinutes,
        CapMinutes = 0,
        DeductionDaysPerWeek = LegalRules.Art118c_WeeklyDeductionDays,
        UseCountedInMonthlyRollup = true,
        MorningGraceMinutes = options.MorningGraceMinutes,
        DepartmentThresholdMinutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        DepartmentExceptions = new Dictionary<string, PunchDepartmentException>(StringComparer.OrdinalIgnoreCase),
        Shift = PunchShiftRule.Normalize(new PunchShiftRule
        {
            Enabled = options.ShiftSystemEnabled,
            CycleHours = options.ShiftCycleHours,
            ExemptFromWeeklyRule = options.ShiftExemptFromWeeklyRule,
            ExemptFromMorningLateness = options.ShiftExemptFromMorningLateness,
            ExemptFromEarlyDeparture = options.ShiftExemptFromEarlyDeparture,
            RestDaysNotAbsence = options.ShiftRestDaysNotAbsence,
            MissingScheduleExempts = options.ShiftMissingScheduleExempts,
            MissingPunchIsAbsence = options.ShiftMissingPunchIsAbsence,
            DutyGraceMinutes = options.ShiftDutyGraceMinutes,
            Departments = (options.ShiftDepartments ?? Array.Empty<string>()).ToList()
        }),
        Flexible = PunchFlexibleRule.Normalize(new PunchFlexibleRule
        {
            Enabled = options.FlexibleEnabled,
            EarliestArrival = options.FlexibleEarliestArrival,
            LatestArrival = options.FlexibleLatestArrival,
            CompleteDailyMinutes = options.FlexibleCompleteDailyMinutes,
            RequireApproval = options.FlexibleRequireApproval,
            EarlyArrivalCountsAsOvertime = options.FlexibleEarlyArrivalCountsAsOvertime,
            Departments = (options.FlexibleDepartments ?? Array.Empty<string>()).ToList(),
            Employees = (options.FlexibleEmployees ?? Array.Empty<string>()).ToList()
        }),
        Overtime = PunchOvertimeRule.Normalize(new PunchOvertimeRule
        {
            Enabled = options.OvertimeEnabled,
            MaxMinutesPerDay = options.OvertimeMaxMinutesPerDay,
            MaxMinutesPerMonth = options.OvertimeMaxMinutesPerMonth,
            MinMinutesPerDay = options.OvertimeMinMinutesPerDay,
            CompensateLateness = options.OvertimeCompensateLateness,
            CountEarlyArrival = options.OvertimeCountEarlyArrival,
            CountOnWeekends = options.OvertimeCountOnWeekends,
            CountOnHolidays = options.OvertimeCountOnHolidays,
            RegularRate = options.OvertimeRegularRate,
            WeekendRate = options.OvertimeWeekendRate,
            HolidayRate = options.OvertimeHolidayRate,
            RoundToMinutes = options.OvertimeRoundToMinutes,
            RequireApproval = options.OvertimeRequireApproval,
            Departments = (options.OvertimeDepartments ?? Array.Empty<string>()).ToList(),
            Employees = (options.OvertimeEmployees ?? Array.Empty<string>()).ToList()
        })
    };

    /// <summary>القاعدة السارية من ملف محفوظ مع تقييد القيم داخل الحدود المنطقية.</summary>
    private PunchWeeklyRule Effective(PunchWeeklyRuleFile file) =>
        PunchWeeklyRule.Normalize(file.Rule ?? Defaults);

    /// <summary>
    /// إعادة قراءة ملف الإعدادات إذا تغيّر على القرص (تعديل يدوي/حذف/استرجاع نسخة احتياطية)
    /// حتى لا تبقى قيم قديمة في الذاكرة.
    /// </summary>
    private PunchWeeklyRuleFile Refresh()
    {
        bool exists;
        DateTime stamp;
        try
        {
            exists = File.Exists(FilePath);
            stamp = exists ? File.GetLastWriteTimeUtc(FilePath) : default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر فحص ملف إعدادات المادة 118/ج {File}.", FilePath);
            return CurrentFile;
        }

        lock (_sync)
        {
            if (exists == _seen && stamp == _stampUtc)
            {
                return CurrentFile;
            }

            _logger.LogInformation("تغيّر ملف إعدادات المادة 118/ج {File} على القرص — أُعيدت قراءته.", FilePath);
            return CurrentFile = ReadAndTrack();
        }
    }

    /// <summary>قراءة الملف مع تسجيل بصمة التعديل الحالية.</summary>
    private PunchWeeklyRuleFile ReadAndTrack()
    {
        var file = Read();
        Track();
        return file;
    }

    /// <summary>تسجيل بصمة الملف الحالية لمنع إعادة القراءة بلا داعٍ.</summary>
    private void Track()
    {
        try
        {
            _seen = File.Exists(FilePath);
            _stampUtc = _seen ? File.GetLastWriteTimeUtc(FilePath) : default;
        }
        catch
        {
            _seen = false;
            _stampUtc = default;
        }
    }

    /// <summary>قراءة الملف من القرص (تُرجع الافتراضي عند غياب الملف أو تلفه).</summary>
    private PunchWeeklyRuleFile Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new PunchWeeklyRuleFile(Defaults);
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<PunchWeeklyRuleFile>(json, JsonOptions);
            return file is null || file.Rule is null ? new PunchWeeklyRuleFile(Defaults) : file;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّرت قراءة ملف إعدادات المادة 118/ج {File} — ستُستخدم القيم الافتراضية.", FilePath);
            return new PunchWeeklyRuleFile(Defaults);
        }
    }

    /// <summary>كتابة الملف كتابة ذرّية (ملف مؤقت ثم استبدال) وتحديث البصمة.</summary>
    private async Task WriteAsync(PunchWeeklyRuleFile file, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = FilePath + ".tmp";

        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct);
        File.Move(temp, FilePath, overwrite: true);

        lock (_sync)
        {
            CurrentFile = file;
            Track();
        }
    }
}
