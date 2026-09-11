using AttendanceApi.Audit;
using AttendanceApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceApi.Services;

/// <summary>
/// «الدوام المرن» و«العمل الإضافي» وفق قانون الخدمة المدنية:
/// حساب نافذة الدوام الفعّالة لكل يوم للموظفين المصرَّح لهم بالدوام المرن،
/// واحتساب المدة التي تزيد على نهاية الدوام (أو على كامل الدوام في أيام الراحة والعطل)
/// عملاً إضافياً بحدّ أعلى يومي وشهري وأقل مدة تُحتسب ونسب تعويض،
/// وإدارة «تصاريح العمل الإضافي والدوام المرن» لكل موظف في وقت التشغيل.
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>نافذة الدوام الفعّالة ليوم واحد بعد تطبيق الدوام المرن.</summary>
    internal sealed record PunchFlexibleDay(TimeOnly Start, TimeOnly End, int ExtensionMinutes, string Source);

    /// <summary>نتيجة احتساب العمل الإضافي ليوم واحد (المدة الفعلية + المحتسبة + المستبعدة + النسبة).</summary>
    internal sealed record PunchOvertimeDay(
        bool Eligible,
        OvertimeDayKind Kind,
        double Rate,
        int RawMinutes,
        int CountedMinutes,
        int ExcludedMinutes,
        bool NeedsApproval,
        string Source)
    {
        public static readonly PunchOvertimeDay None =
            new(false, OvertimeDayKind.None, 0, 0, 0, 0, false, "لا توجد مدة خارج الدوام");
    }

    /// <summary>
    /// تحديد نافذة الدوام الفعّالة لليوم وفق «الدوام المرن»:
    /// يُطبَّق إذا كان الموظف/الإدارة مصرَّحاً له وكان حضوره بعد بدء الدوام وداخل نافذة المرونة،
    /// فتتحرّك نهاية دوامه بقدر تأخّره لإكمال ساعات الدوام اليومية.
    /// </summary>
    internal static PunchFlexibleDay? ResolveFlexibleDay(
        PunchFlexibleRule rule,
        PunchWorkApprovalIndex? approvals,
        string jobNumber,
        string? department,
        DateOnly date,
        TimeOnly? clockIn,
        TimeOnly workdayStart,
        TimeOnly workdayEnd)
    {
        if (!rule.Enabled || clockIn is null)
        {
            return null;
        }

        // الحضور في الوقت أو قبله: نافذة الدوام الرسمي كما هي (لا مرونة مطلوبة).
        if (clockIn.Value <= workdayStart)
        {
            return null;
        }

        // تأخّر خارج نافذة المرونة المسموحة: تُطبَّق القواعد العامة.
        if (clockIn.Value > rule.LatestArrivalTime)
        {
            return null;
        }

        if (!rule.AppliesTo(jobNumber, department, date, approvals))
        {
            return null;
        }

        var end = clockIn.Value.AddMinutes(rule.EffectiveDailyMinutes);
        int extension = Math.Max(0, (int)Math.Round(
            end.ToTimeSpan().TotalMinutes - workdayEnd.ToTimeSpan().TotalMinutes));

        var source = approvals?.Find(jobNumber, WorkApprovalKind.Flexible, date) is { } approval
            ? $"تصريح دوام مرن ساري حتى {approval.ToDate:yyyy/MM/dd}"
            : "مشمول بالدوام المرن وفق الإعدادات والقوائم";

        return new PunchFlexibleDay(clockIn.Value, end, extension, source);
    }


    /// <summary>
    /// احتساب العمل الإضافي ليوم واحد:
    /// المدة الفعلية = ما يزيد على نهاية الدوام الفعّالة (بعد تعويض التأخّر إن كانت الإعدادات تشترط ذلك)،
    /// وفي أيام الراحة والعطل = كامل مدة الدوام الفعلي، ثم يُقيَّد بالحدّ اليومي والمدة الدنيا والتصريح الساري.
    /// </summary>
    internal static PunchOvertimeDay ComputeOvertime(
        PunchOvertimeRule rule,
        PunchWorkApprovalIndex? approvals,
        string jobNumber,
        string? department,
        DateOnly date,
        bool worksOnHoliday,
        bool worksOnWeekend,
        bool worksOnOffShiftDay,
        bool isShiftDutyDay,
        bool hasPunch,
        int workedMinutes,
        TimeOnly? clockIn,
        TimeOnly? clockOut,
        TimeOnly effectiveEnd,
        int latenessMinutes,
        TimeOnly workdayStart,
        bool countEarlyArrival)
    {
        if (!rule.Enabled || isShiftDutyDay || !hasPunch || clockIn is null || clockOut is null)
        {
            return PunchOvertimeDay.None;
        }

        // العطلة الرسمية تتقدّم على نهاية الأسبوع، ودوام الراحة/الإجازة لموظفي الورديات كنهاية أسبوع.
        OvertimeDayKind kind;
        int raw;

        if (worksOnHoliday)
        {
            kind = OvertimeDayKind.Holiday;
            raw = Math.Max(0, workedMinutes);
        }
        else if (worksOnWeekend || worksOnOffShiftDay)
        {
            kind = OvertimeDayKind.Weekend;
            raw = Math.Max(0, workedMinutes);
        }
        else
        {
            kind = OvertimeDayKind.Regular;

            int after = Math.Max(0, (int)Math.Round(
                clockOut.Value.ToTimeSpan().TotalMinutes - effectiveEnd.ToTimeSpan().TotalMinutes));
            if (rule.CompensateLateness)
            {
                // التأخّر الصباحي يُعوَّض بما بقي بعد نهاية الدوام فلا يُحتسب مرتين.
                after = Math.Max(0, after - Math.Max(0, latenessMinutes));
            }

            int before = countEarlyArrival
                ? Math.Max(0, (int)Math.Round(
                    workdayStart.ToTimeSpan().TotalMinutes - clockIn.Value.ToTimeSpan().TotalMinutes))
                : 0;

            raw = after + before;
        }

        raw = RoundDown(raw, rule.RoundToMinutes);

        if (raw <= 0)
        {
            return PunchOvertimeDay.None;
        }

        bool kindAllowed = kind switch
        {
            OvertimeDayKind.Holiday => rule.CountOnHolidays,
            OvertimeDayKind.Weekend => rule.CountOnWeekends,
            _ => true
        };

        var eligibility = rule.EligibleFor(jobNumber, department, date, approvals);
        bool eligible = eligibility.Eligible && kindAllowed;

        int maxPerDay = eligibility.MaxMinutesPerDay > 0
            ? eligibility.MaxMinutesPerDay
            : rule.EffectiveMaxMinutesPerDay;

        int counted = eligible ? raw : 0;
        if (maxPerDay > 0)
        {
            counted = Math.Min(counted, maxPerDay);
        }

        if (counted > 0 && counted < rule.MinMinutesPerDay)
        {
            counted = 0;
        }

        var source = !eligibility.Eligible
            ? eligibility.Source
            : !kindAllowed
                ? (kind == OvertimeDayKind.Holiday
                    ? "الدوام في العطل الرسمية والدينية غير محتسب إضافياً وفق الإعدادات"
                    : "الدوام في نهاية الأسبوع غير محتسب إضافياً وفق الإعدادات")
                : counted == 0 && raw > 0
                    ? $"المدة أقل من الحدّ الأدنى المحتسب ({rule.MinMinutesPerDay} دقيقة)"
                    : eligibility.Source;

        return new PunchOvertimeDay(
            eligible,
            kind,
            rule.RateFor(kind),
            raw,
            counted,
            raw - counted,
            NeedsApproval: !eligible && kindAllowed && eligibility.NeedsApproval,
            source);
    }


    /// <summary>الدقائق المعادلة بعد تطبيق نسبة التعويض (ساعات معادلة للصرف/التعويض).</summary>
    internal static int EquivalentOvertime(int minutes, double rate) =>
        minutes <= 0 ? 0 : (int)Math.Round(minutes * (rate <= 0 ? 1 : rate));

    /// <summary>تدوير الدقائق للأسفل وفق مضاعف محدّد (0 = بلا تدوير).</summary>
    internal static int RoundDown(int minutes, int step) =>
        step <= 0 ? minutes : minutes / step * step;

    /// <summary>
    /// تطبيق سقوف العمل الإضافي بعد بناء أيام الشهر كاملاً:
    /// الحدّ الأعلى الشهري، والحدّ الإجمالي للتصريح الساري (إن وُجد)،
    /// مع تسجيل الدقائق المستبعدة في النتيجة اليومية وملاحظتها للتدقيق.
    /// </summary>
    internal static void ApplyOvertimeCaps(
        IReadOnlyList<PunchDailyResult> days,
        PunchOvertimeRule rule,
        PunchWorkApprovalIndex? approvals)
    {
        if (!rule.Enabled || days.Count == 0)
        {
            return;
        }

        int monthlyCap = rule.EffectiveMaxMinutesPerMonth;
        var approvalRemaining = new Dictionary<long, int>();
        int currentMonth = int.MinValue;
        int monthUsed = 0;

        foreach (var day in days.Where(d => d.OvertimeMinutes > 0).OrderBy(d => d.WorkDate))
        {
            int key = day.Year * 100 + day.Month;
            if (key != currentMonth)
            {
                currentMonth = key;
                monthUsed = 0;
            }

            int allowed = day.OvertimeMinutes;
            var reasons = new List<string>(2);

            if (approvals?.Find(day.JobNumber, WorkApprovalKind.Overtime, day.WorkDate) is { } approval
                && approval.MaxMinutesTotal is > 0)
            {
                if (!approvalRemaining.TryGetValue(approval.Id, out int remaining))
                {
                    remaining = approval.MaxMinutesTotal!.Value;
                }

                int forApproval = Math.Min(allowed, Math.Max(0, remaining));
                if (forApproval < allowed)
                {
                    reasons.Add($"الحدّ الإجمالي للتصريح ({PunchFlexibleRule.HoursText(approval.MaxMinutesTotal.Value)})");
                }

                allowed = forApproval;
                approvalRemaining[approval.Id] = Math.Max(0, remaining - forApproval);
            }

            if (monthlyCap > 0)
            {
                int forMonth = Math.Min(allowed, Math.Max(0, monthlyCap - monthUsed));
                if (forMonth < allowed)
                {
                    reasons.Add($"الحدّ الشهري ({PunchFlexibleRule.HoursText(monthlyCap)})");
                }

                allowed = forMonth;
            }

            if (allowed != day.OvertimeMinutes)
            {
                int excluded = day.OvertimeMinutes - allowed;
                day.OvertimeExcludedMinutes += excluded;
                day.OvertimeMinutes = allowed;
                day.EquivalentOvertimeMinutes = EquivalentOvertime(allowed, day.OvertimeRate);

                var note = $"استُبعدت {MinutesLabel(excluded)} من العمل الإضافي لهذا اليوم ({string.Join(" و", reasons)})";
                day.Notes = Truncate(day.Notes is { Length: > 0 } ? $"{day.Notes} | {note}" : note, 390);
            }

            monthUsed += allowed;
        }
    }


    // =====================================================================
    //  تصاريح العمل الإضافي والدوام المرن (تُدار من الواجهة في وقت التشغيل)
    // =====================================================================

    /// <summary>
    /// تحميل تصاريح العمل الإضافي والدوام المرن لتمريرها إلى محرّك التحليل
    /// (تعذّر القراءة لا يوقف التحليل — يُتابع بالقوائم العامة في الإعدادات).
    /// </summary>
    internal async Task<PunchWorkApprovalIndex> LoadWorkApprovalsAsync(CancellationToken ct = default)
    {
        try
        {
            var approvals = await _db.PunchWorkApprovals.AsNoTracking().ToListAsync(ct);
            return new PunchWorkApprovalIndex(approvals);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة «تصاريح العمل الإضافي والدوام المرن» — سيُتابع التحليل وفق القوائم العامة.");
            return PunchWorkApprovalIndex.Empty;
        }
    }

    /// <summary>عرض «التصاريح والموافقات»: القواعد السارية + المؤشرات + التصاريح المسجّلة.</summary>
    public async Task<PunchWorkApprovalsView> GetWorkApprovalsAsync(CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var rule = WeeklyRule;

        try
        {
            var approvals = await _db.PunchWorkApprovals.AsNoTracking()
                .OrderByDescending(a => a.IsActive)
                .ThenByDescending(a => a.FromDate)
                .ThenBy(a => a.JobNumber)
                .ToListAsync(ct);

            var items = approvals.Select(a => new PunchWorkApprovalRow(
                Id: a.Id,
                Kind: a.Kind == WorkApprovalKind.Flexible ? "دوام مرن" : "عمل إضافي",
                JobNumber: a.JobNumber,
                EmployeeName: a.EmployeeName,
                DepartmentName: a.DepartmentName,
                FromText: a.FromDate.ToString("yyyy/MM/dd"),
                ToText: a.ToDate.ToString("yyyy/MM/dd"),
                Days: a.Days,
                MaxMinutesPerDay: a.MaxMinutesPerDay,
                MaxMinutesTotal: a.MaxMinutesTotal,
                IsActive: a.IsActive,
                Source: string.IsNullOrWhiteSpace(a.Source) ? "إدخال يدوي" : a.Source!,
                Note: a.Note)).ToList();

            return new PunchWorkApprovalsView(
                Flexible: rule.Flexible,
                Overtime: rule.Overtime,
                FlexibleSummary: rule.Flexible.Summary(),
                OvertimeSummary: rule.Overtime.Summary(),
                Total: items.Count,
                OvertimeCount: items.Count(i => i.Kind == "عمل إضافي"),
                FlexibleCount: items.Count(i => i.Kind == "دوام مرن"),
                ActiveCount: items.Count(i => i.IsActive),
                EmployeesCount: items.Select(i => i.JobNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                HasApprovals: items.Count > 0,
                PeriodFrom: items.Count > 0 ? approvals.Min(a => a.FromDate) : null,
                PeriodTo: items.Count > 0 ? approvals.Max(a => a.ToDate) : null,
                Items: items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذّر قراءة «تصاريح العمل الإضافي والدوام المرن» من قاعدة البيانات.");

            return new PunchWorkApprovalsView(
                Flexible: rule.Flexible,
                Overtime: rule.Overtime,
                FlexibleSummary: rule.Flexible.Summary(),
                OvertimeSummary: rule.Overtime.Summary(),
                Total: 0,
                OvertimeCount: 0,
                FlexibleCount: 0,
                ActiveCount: 0,
                EmployeesCount: 0,
                HasApprovals: false,
                PeriodFrom: null,
                PeriodTo: null,
                Items: Array.Empty<PunchWorkApprovalRow>());
        }
    }


    /// <summary>
    /// حفظ (إضافة/تعديل) تصريح عمل إضافي أو دوام مرن لموظف.
    /// يُرجع null عند نجاح الحفظ أو رسالة خطأ عند رفض القيم.
    /// </summary>
    public async Task<string?> SaveWorkApprovalAsync(
        PunchWorkApproval approval,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var error = ValidateWorkApproval(approval);
        if (error is not null)
        {
            return error;
        }

        if (approval.Id > 0)
        {
            var existing = await _db.PunchWorkApprovals.FirstOrDefaultAsync(a => a.Id == approval.Id, ct);
            if (existing is null)
            {
                return "لا يوجد تصريح بهذا المعرّف.";
            }

            existing.Kind = approval.Kind;
            existing.JobNumber = approval.JobNumber;
            existing.EmployeeName = approval.EmployeeName;
            existing.DepartmentName = approval.DepartmentName;
            existing.FromDate = approval.FromDate;
            existing.ToDate = approval.ToDate;
            existing.MaxMinutesPerDay = approval.MaxMinutesPerDay;
            existing.MaxMinutesTotal = approval.MaxMinutesTotal;
            existing.Note = approval.Note;
            existing.IsActive = approval.IsActive;
            existing.Source = approval.Source;
        }
        else
        {
            approval.CreatedAtUtc = DateTime.UtcNow;
            approval.Source = string.IsNullOrWhiteSpace(approval.Source) ? "إدخال يدوي" : approval.Source;
            _db.PunchWorkApprovals.Add(approval);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "حُفظ تصريح {Kind} للموظف {Job} من {From} إلى {To}.",
            approval.Kind == WorkApprovalKind.Flexible ? "دوام مرن" : "عمل إضافي",
            approval.JobNumber,
            approval.FromDate,
            approval.ToDate);

        return null;
    }

    /// <summary>حذف تصريح (للتصحيحات)، ويُفضَّل الإيقاف بدل الحذف للتدقيق.</summary>
    public async Task<bool> DeleteWorkApprovalAsync(long id, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var approval = await _db.PunchWorkApprovals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return false;
        }

        _db.PunchWorkApprovals.Remove(approval);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("حُذف التصريح {Id} الخاص بالموظف {Job}.", id, approval.JobNumber);
        return true;
    }

    /// <summary>التحقق من صحة تصريح قبل الحفظ (يُعيد رسالة الخطأ أو null).</summary>
    public static string? ValidateWorkApproval(PunchWorkApproval approval)
    {
        if (string.IsNullOrWhiteSpace(approval.JobNumber))
        {
            return "الرقم الوظيفي مطلوب في التصريح.";
        }

        if (approval.ToDate < approval.FromDate)
        {
            return "تاريخ نهاية التصريح يجب أن يكون بعد تاريخ بدايته.";
        }

        if (approval.MaxMinutesPerDay is < 0 or > PunchOvertimeRule.MaxDailyMinutesLimit)
        {
            return $"الحدّ اليومي للتصريح يجب أن يكون بين 0 و{PunchOvertimeRule.MaxDailyMinutesLimit} دقيقة.";
        }

        if (approval.MaxMinutesTotal is < 0 or > PunchOvertimeRule.MaxMonthlyMinutesLimit)
        {
            return $"إجمالي التصريح يجب أن يكون بين 0 و{PunchOvertimeRule.MaxMonthlyMinutesLimit} دقيقة.";
        }

        return null;
    }


    /// <summary>
    /// إضافة تصاريح من قائمة نصية (سطر لكل تصريح):
    /// الرقم الوظيفي | من | إلى | ساعات/يوم | إجمالي الساعات | النوع (إضافي/مرن) | ملاحظة.
    /// </summary>
    public async Task<PunchWorkApprovalBulkResult> AddWorkApprovalsFromTextAsync(
        string? text,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var added = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseWorkApprovalLine(line, out var approval, out var error))
            {
                skipped++;
                errors.Add($"{Truncate(line, 60)} — {error}");
                continue;
            }

            approval!.CreatedAtUtc = DateTime.UtcNow;
            approval.Source = "استيراد نصي";
            _db.PunchWorkApprovals.Add(approval);
            added++;
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("إضافة تصاريح من قائمة نصية: {Added} تصريحاً، {Skipped} سطراً مرفوضاً.", added, skipped);

        return new PunchWorkApprovalBulkResult(added, skipped, errors.Take(20).ToList());
    }

    /// <summary>تحليل سطر تصريح نصي (يفصل الحقول بـ | أو , أو تبويب).</summary>
    internal static bool TryParseWorkApprovalLine(
        string line,
        out PunchWorkApproval? approval,
        out string error)
    {
        approval = null;
        error = string.Empty;

        var parts = line.Split(new[] { '|', '\t', ',', '،' })
            .Select(p => p.Trim())
            .ToList();

        if (parts.Count < 3)
        {
            error = "الصيغة المطلوبة: الرقم الوظيفي | من تاريخ | إلى تاريخ | ساعات/يوم | إجمالي الساعات | النوع | ملاحظة";
            return false;
        }

        var jobNumber = parts[0];
        if (jobNumber.Length == 0)
        {
            error = "الرقم الوظيفي مطلوب";
            return false;
        }

        if (!DateOnly.TryParse(parts[1], out var from))
        {
            error = $"تاريخ البداية غير صالح: {parts[1]}";
            return false;
        }

        if (!DateOnly.TryParse(parts[2], out var to))
        {
            error = $"تاريخ النهاية غير صالح: {parts[2]}";
            return false;
        }

        if (to < from)
        {
            (from, to) = (to, from);
        }

        var kindText = parts.Count > 5 ? parts[5] : string.Empty;
        var kind = kindText.Contains("مرن", StringComparison.Ordinal)
                   || kindText.Contains("flex", StringComparison.OrdinalIgnoreCase)
            ? WorkApprovalKind.Flexible
            : WorkApprovalKind.Overtime;

        approval = new PunchWorkApproval
        {
            Kind = kind,
            JobNumber = jobNumber,
            FromDate = from,
            ToDate = to,
            MaxMinutesPerDay = ParseApprovalMinutes(parts.Count > 3 ? parts[3] : null),
            MaxMinutesTotal = ParseApprovalMinutes(parts.Count > 4 ? parts[4] : null),
            Note = parts.Count > 6 ? parts[6] : null,
            IsActive = true
        };

        return true;
    }

    /// <summary>تحويل قيمة ساعات/دقائق نصية إلى دقائق (الأرقام العربية مقبولة، و«دقيقة» تُميَّز عن الساعات).</summary>
    internal static int? ParseApprovalMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        bool minutes = value.Contains("دقيق", StringComparison.Ordinal)
                       || value.Contains("min", StringComparison.OrdinalIgnoreCase);

        var digits = new string(value.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (digits.Length == 0
            || !double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number))
        {
            return null;
        }

        int rounded = (int)Math.Round(minutes ? number : number * 60);

        return rounded <= 0 ? null : Math.Clamp(rounded, 1, PunchOvertimeRule.MaxMonthlyMinutesLimit);
    }

    /// <summary>
    /// ترحيل إجمالي ساعات العمل الإضافي الشهرية المحتسبة من البصمات إلى سجل «العمل الإضافي» (OvertimeRecords)
    /// ليتكامل مع تقرير العمل الإضافي القائم (<c>/api/v1/reports/overtime</c>):
    /// يُسجَّل للموظف قيد واحد لكل شهر بقيمة ساعات العمل الإضافي المحتسبة (بتاريخ آخر يوم من الشهر).
    /// </summary>
    /// <param name="year">سنة محدّدة (اختياري).</param>
    /// <param name="month">شهر محدّد (اختياري، يُستخدم مع السنة).</param>
    /// <param name="replacePeriod">استبدال قيود الفترة المرحَّلة سابقاً بدل تكرارها.</param>
    public async Task<PunchOvertimeSyncResult> SyncOvertimeRecordsAsync(
        int? year = null,
        int? month = null,
        bool replacePeriod = true,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct);

        var query = _db.PunchMonthlyResults.AsNoTracking().Where(m => m.OvertimeMinutes > 0);

        if (year.HasValue)
        {
            query = query.Where(m => m.Year == year.Value);
        }

        if (month is >= 1 and <= 12)
        {
            query = query.Where(m => m.Month == month.Value);
        }

        var monthly = await query.ToListAsync(ct);

        if (monthly.Count == 0)
        {
            return new PunchOvertimeSyncResult(0, 0, 0, null, null,
                "لا توجد ساعات عمل إضافي محتسبة في النطاق المطلوب — شغّل التحليل أولاً.");
        }

        var jobNumbers = monthly.Select(m => m.JobNumber).Distinct(StringComparer.Ordinal).ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => jobNumbers.Contains(e.JobNumber))
            .Select(e => new { e.Id, e.JobNumber })
            .ToListAsync(ct);

        var employeeIds = employees
            .GroupBy(e => e.JobNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var entries = new List<OvertimeRecord>(monthly.Count);
        int skipped = 0;

        foreach (var m in monthly)
        {
            if (!employeeIds.TryGetValue(m.JobNumber, out int employeeId))
            {
                skipped++;
                continue;
            }

            entries.Add(new OvertimeRecord
            {
                EmployeeId = employeeId,
                WorkDate = new DateOnly(m.Year, m.Month, DateTime.DaysInMonth(m.Year, m.Month)),
                OvertimeHours = Math.Round(m.OvertimeMinutes / 60.0, 2)
            });
        }

        if (replacePeriod && entries.Count > 0)
        {
            var from = new DateOnly(monthly.Min(m => m.Year), monthly.Min(m => m.Month), 1);
            var to = new DateOnly(monthly.Max(m => m.Year), monthly.Max(m => m.Month),
                DateTime.DaysInMonth(monthly.Max(m => m.Year), monthly.Max(m => m.Month)));

            var existing = await _db.OvertimeRecords
                .Where(o => o.WorkDate >= from && o.WorkDate <= to)
                .ToListAsync(ct);

            if (existing.Count > 0)
            {
                _db.OvertimeRecords.RemoveRange(existing);
            }
        }

        _db.OvertimeRecords.AddRange(entries);
        await _db.SaveChangesAsync(ct);

        double totalHours = Math.Round(entries.Sum(e => e.OvertimeHours), 2);

        _logger.LogInformation(
            "تُرجمت ساعات العمل الإضافي إلى سجل العمل الإضافي: {Count} قيداً ({Hours} ساعة)، وتُخطّي {Skipped} موظفاً غير معروف في سجل الموظفين.",
            entries.Count, totalHours, skipped);

        var hoursText = $"{totalHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} ساعة";
        var summary = entries.Count > 0
            ? $"تُرجمت {entries.Count} قيداً بإجمالي {hoursText}."
            : "لم يُرحَّل أي قيد: أرقام الموظفين المحتسبة غير موجودة في سجل الموظفين (جدول Employees) — "
              + "أضف الموظفين إلى سجل الموظفين أو اعتمد تقرير «العمل الإضافي والدوام المرن» من تقارير البصمات.";

        if (entries.Count > 0 && skipped > 0)
        {
            summary += $" وتُخطّي {skipped} موظفاً غير موجود في سجل الموظفين.";
        }

        return new PunchOvertimeSyncResult(
            entries.Count,
            skipped,
            totalHours,
            entries.Count > 0 ? entries.Min(e => e.WorkDate) : null,
            entries.Count > 0 ? entries.Max(e => e.WorkDate) : null,
            summary);
    }
}

/// <summary>نتيجة ترحيل ساعات العمل الإضافي الشهرية إلى سجل العمل الإضافي (OvertimeRecords).</summary>
public sealed record PunchOvertimeSyncResult(
    int Records,
    int SkippedEmployees,
    double TotalHours,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    string Message);
