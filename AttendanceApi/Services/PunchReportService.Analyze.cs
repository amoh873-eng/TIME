using AttendanceApi.Audit;
using AttendanceApi.Domain;

namespace AttendanceApi.Services;

/// <summary>
/// محرّك التحليل القانوني لبصمات الحضور والانصراف:
/// تحويل الصفوف الخام إلى نتائج يومية (تأخير صباحي، انصراف مبكر، غياب عن الدوام، المادة 118/ب)،
/// ثم تجميع أسبوعي (المادة 118/ج) ونتيجة شهرية (المادة 7، الإجازات، خصم المكافأة).
/// </summary>
public sealed partial class PunchReportService
{
    /// <summary>حالات اليوم التي تُعدّ أيام عمل رسمية (يُحتسب فيها التأخير والانصراف المبكر).</summary>
    private static readonly HashSet<PunchDayStatus> WorkingStatuses = new()
    {
        PunchDayStatus.Complete,
        PunchDayStatus.Incomplete,
        PunchDayStatus.Absent,
        PunchDayStatus.EmergencyPermission,
        PunchDayStatus.MedicalPermission,
        // يوم وردية دوام: يوم عمل فعلي لكن لا يُقارَن بأوقات الدوام الرسمي (نظام الورديات).
        PunchDayStatus.ShiftDuty
    };

    /// <summary>
    /// سياق «نظام الورديات» لموظف واحد: هل يعمل بالورديات؟ وما هي إعفاءاته السارية
    /// (الورديات أو استثناء إداري خاص)؟ وما هي قيود جدوله الشهري؟
    /// </summary>
    internal sealed record PunchShiftContext(
        bool IsShiftEmployee,
        bool ExemptFromWeeklyRule,
        bool ExemptAllWeeks,
        bool ExemptScheduledWeeks,
        bool ExemptFromMorningLateness,
        bool ExemptFromEarlyDeparture,
        bool ExemptFromAbsence,
        IReadOnlyDictionary<DateOnly, ShiftScheduleEntry> Schedule)
    {
        public static readonly PunchShiftContext None = new(
            IsShiftEmployee: false,
            ExemptFromWeeklyRule: false,
            ExemptAllWeeks: false,
            ExemptScheduledWeeks: false,
            ExemptFromMorningLateness: false,
            ExemptFromEarlyDeparture: false,
            ExemptFromAbsence: false,
            Schedule: new Dictionary<DateOnly, ShiftScheduleEntry>());

        /// <summary>قيد جدول الورديات لهذا اليوم (أو null إن لم يوجد قيد).</summary>
        public ShiftScheduleEntry? For(DateOnly date) =>
            Schedule.TryGetValue(date, out var entry) ? entry : null;

        /// <summary>هل يشمل الجدول الشهري أي يوم من مدى معيّن (يُستخدم لإعفاء الأسابيع المشمولة بالجدول)؟</summary>
        public bool Covers(DateOnly from, DateOnly to)
        {
            if (Schedule.Count == 0)
            {
                return false;
            }

            foreach (var date in Schedule.Keys)
            {
                if (date >= from && date <= to)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// بناء سياق الورديات لموظف واحد: الموظف يُعدّ عامل ورديات إن كان له قيود في الجدول الشهري
    /// أو كانت إدارته ضمن إدارات الورديات/استثناء «نظام ورديات»، ثم تُحدَّد إعفاءاته السارية.
    /// </summary>
    /// <param name="employeeEntries">قيود الجدول الشهري الخاصة بهذا الموظف (مفلترة مسبقاً).</param>
    internal static PunchShiftContext BuildShiftContext(
        string? department,
        PunchWeeklyRule rule,
        IReadOnlyList<ShiftScheduleEntry>? employeeEntries)
    {
        Dictionary<DateOnly, ShiftScheduleEntry> schedule = new();

        if (employeeEntries is not null)
        {
            foreach (var entry in employeeEntries)
            {
                if (!schedule.ContainsKey(entry.DutyDate))
                {
                    schedule[entry.DutyDate] = entry;
                }
            }
        }

        var exception = rule.ExceptionFor(department);
        bool shiftEmployee = rule.Shift.Enabled
            && (schedule.Count > 0 || rule.UsesShiftSystem(department));

        // الإعفاء الإداري يشمل كل الأسابيع، أما إعفاء الورديات فيقتصر على الأسابيع المشمولة بالجدول الشهري
        // حتى لا تُسقط أشهراً لا يغطيها الجدول المستورد.
        bool exemptAllWeeks = exception?.ExemptFromWeeklyRule == true;
        bool exemptScheduledWeeks = !exemptAllWeeks && shiftEmployee && rule.Shift.ExemptFromWeeklyRule;

        return new PunchShiftContext(
            IsShiftEmployee: shiftEmployee,
            ExemptFromWeeklyRule: exemptAllWeeks || exemptScheduledWeeks,
            ExemptAllWeeks: exemptAllWeeks,
            ExemptScheduledWeeks: exemptScheduledWeeks,
            ExemptFromMorningLateness: (shiftEmployee && rule.Shift.ExemptFromMorningLateness) || exception?.ExemptFromMorningLateness == true,
            ExemptFromEarlyDeparture: (shiftEmployee && rule.Shift.ExemptFromEarlyDeparture) || exception?.ExemptFromEarlyDeparture == true,
            ExemptFromAbsence: exception?.ExemptFromAbsence == true,
            Schedule: schedule);
    }

    /// <summary>أولوية دمج الصفوف المتكرّرة لنفس اليوم (الأعلى حكماً أولاً).</summary>
    private static int PriorityOf(PunchDayStatus status) => status switch
    {
        PunchDayStatus.Complete => 0,
        PunchDayStatus.Incomplete => 1,
        PunchDayStatus.Absent => 2,
        PunchDayStatus.EmergencyPermission => 3,
        PunchDayStatus.MedicalPermission => 4,
        PunchDayStatus.AnnualLeave => 5,
        PunchDayStatus.SickLeave => 6,
        PunchDayStatus.CompensatoryLeave => 7,
        PunchDayStatus.BereavementLeave => 8,
        PunchDayStatus.OfficialMission => 9,
        PunchDayStatus.Secondment => 10,
        PunchDayStatus.Training => 11,
        _ => 12
    };

    /// <summary>حزمة نتائج التحليل الكاملة.</summary>
    internal sealed record PunchAnalysisBundle(
        List<PunchDailyResult> Daily,
        List<PunchWeeklyResult> Weekly,
        List<PunchMonthlyResult> Monthly);

    /// <summary>
    /// تنفيذ التحليل الكامل (يومي + أسبوعي + شهري) على صفوف جدول المرحلة مع تقويم العطل المعتمد.
    /// </summary>
    /// <param name="weeklyRule">
    /// قاعدة المادة 118/ج المرنة (الحدّ الأسبوعي، سقف الناتج المحتسب، أيام الخصم، خيارات الدمج،
    /// وتجاوزات الإدارات) — تُقرأ من إعدادات التحليل وقت التشغيل (انظر <c>PunchWeeklyRuleStore</c>).
    /// </param>
    internal PunchAnalysisBundle BuildAnalysis(
        IReadOnlyList<PunchRecord> records,
        int graceMinutes,
        PunchCalendar calendar,
        PunchWeeklyRule weeklyRule,
        IReadOnlyList<ShiftScheduleEntry>? shiftSchedule = null,
        PunchWorkApprovalIndex? workApprovals = null)
    {
        var daily = new List<PunchDailyResult>(records.Count);
        var weekly = new List<PunchWeeklyResult>();
        var monthly = new List<PunchMonthlyResult>();

        // قيود «جدول الورديات الشهري» مجمَّعة بالموظف لتفادي البحث المتكرّر داخل حلقة الأيام.
        var shiftsByEmployee = (shiftSchedule ?? Array.Empty<ShiftScheduleEntry>())
            .GroupBy(e => e.JobNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ShiftScheduleEntry>)g.ToList(), StringComparer.Ordinal);

        foreach (var employee in records.GroupBy(r => r.JobNumber, StringComparer.Ordinal))
        {
            var employeeName = MostFrequent(employee.Select(r => r.EmployeeName));
            var department = MostFrequentDepartment(employee.Select(r => r.DepartmentName));

            var shift = BuildShiftContext(
                department,
                weeklyRule,
                shiftsByEmployee.GetValueOrDefault(employee.Key));

            var days = BuildDaily(employee, employeeName, department, graceMinutes, calendar, daily, weeklyRule, shift, workApprovals);

            // سقوف العمل الإضافي (الحدّ الشهري والحدّ الإجمالي للتصاريح) تُطبَّق على أيام الشهر مجتمعةً.
            ApplyOvertimeCaps(days, weeklyRule.Overtime, workApprovals);

            // الإعفاء من قاعدة المادة 118/ج: إعفاء كامل (استثناء إداري) أو إعفاء الورديات
            // للأسابيع المشمولة بجدولها الشهري فقط.
            var weeks = BuildWeekly(days, employeeName, department, weeklyRule);

            if (shift.ExemptAllWeeks)
            {
                weeks = new List<PunchWeeklyResult>();
            }
            else if (shift.ExemptScheduledWeeks)
            {
                weeks = weeks.Where(w => !shift.Covers(w.WeekStart, w.WeekEnd)).ToList();
            }

            weekly.AddRange(weeks);
            monthly.AddRange(BuildMonthly(days, weeks, employee.Key, employeeName, department, weeklyRule, shift));
        }

        return new PunchAnalysisBundle(daily, weekly, monthly);
    }

    /// <summary>
    /// بناء النتائج اليومية لموظف واحد مع دمج الصفوف المتكرّرة لنفس التاريخ،
    /// وتطبيق تقويم العطل: أي يوم نهاية أسبوع أو عطلة رسمية/دينية لا يُحتسب غياباً ولا مخالفة.
    /// </summary>
    private static List<PunchDailyResult> BuildDaily(
        IEnumerable<PunchRecord> employeeRecords,
        string? employeeName,
        string? department,
        int graceMinutes,
        PunchCalendar calendar,
        List<PunchDailyResult> sink,
        PunchWeeklyRule rule,
        PunchShiftContext shift,
        PunchWorkApprovalIndex? approvals)
    {
        var jobNumber = employeeRecords.First().JobNumber;
        var days = new List<PunchDailyResult>();

        foreach (var group in employeeRecords.GroupBy(r => r.WorkDate).OrderBy(g => g.Key))
        {
            var rows = group.ToList();
            var inTimes = rows.Where(r => r.ClockIn.HasValue).Select(r => r.ClockIn!.Value).ToList();
            var outTimes = rows.Where(r => r.ClockOut.HasValue).Select(r => r.ClockOut!.Value).ToList();

            TimeOnly? clockIn = inTimes.Count > 0 ? inTimes.Min() : null;
            TimeOnly? clockOut = outTimes.Count > 0 ? outTimes.Max() : null;

            // ---- تقويم العطل المعتمد: نهاية الأسبوع + العطل الرسمية والدينية ----
            bool calendarHoliday = calendar.TryGetHoliday(group.Key, out var holiday);
            bool calendarWeekend = calendar.IsWeekend(group.Key);
            bool fileWeekend = rows.Any(r => r.Status == PunchDayStatus.Weekend);
            bool fileHoliday = rows.Any(r => r.Status == PunchDayStatus.OfficialHoliday);

            // العطلة الرسمية/الدينية تتقدّم على تصنيف نهاية الأسبوع عند تداخل اليومين.
            bool isHoliday = calendarHoliday || fileHoliday;
            bool isWeekend = calendarWeekend || fileWeekend;
            bool isOffDay = isHoliday || isWeekend;

            var ranked = rows
                .Where(r => r.Status is not (PunchDayStatus.NoData or PunchDayStatus.Weekend or PunchDayStatus.OfficialHoliday))
                .ToList();

            var primary = ranked.Count > 0
                ? ranked.OrderBy(r => PriorityOf(r.Status)).Select(r => (PunchDayStatus?)r.Status).First()
                : null;

            PunchDayStatus status;
            if (primary is null)
            {
                // لا بصمات: يوم عطلة (أسبوعية أو رسمية/دينية) أم يوم بلا بيانات؟
                status = isHoliday
                    ? PunchDayStatus.OfficialHoliday
                    : isWeekend
                        ? PunchDayStatus.Weekend
                        : PunchDayStatus.NoData;
            }
            else if (primary is PunchDayStatus.Complete or PunchDayStatus.Incomplete)
            {
                // بصمات فعلية في يوم عطلة تُعرض للعلم (ساعات إضافية) ولا تُحتسب تأخيراً/انصرافاً مبكراً.
                status = isHoliday
                    ? PunchDayStatus.HolidayWork
                    : isWeekend
                        ? PunchDayStatus.WeekendWork
                        : primary.Value;
            }
            else if (primary == PunchDayStatus.Absent && isOffDay)
            {
                // غياب مُسجَّل في يوم عطلة (نهاية أسبوع أو عطلة رسمية/دينية) — يُستثنى من المخالفات.
                status = isHoliday ? PunchDayStatus.OfficialHoliday : PunchDayStatus.Weekend;
            }
            else
            {
                status = primary.Value;
            }

            bool worksOnHoliday = status is PunchDayStatus.WeekendWork or PunchDayStatus.HolidayWork;
            bool absenceRecordedOnOffDay = primary == PunchDayStatus.Absent && isOffDay;

            // ---- نظام الورديات: يُحكم على اليوم من «جدول الورديات الشهري» لا من أوقات الدوام الرسمي ----
            var shiftEntry = shift.IsShiftEmployee ? shift.For(group.Key) : null;
            bool isShiftDay = shiftEntry is not null;
            bool shiftDutyMissingPunch = false;

            if (isShiftDay)
            {
                status = ShiftStatusFor(shiftEntry!.Kind, primary, rule.Shift.MissingPunchIsAbsence, out shiftDutyMissingPunch);
                worksOnHoliday = worksOnHoliday
                    || (status == PunchDayStatus.ShiftDuty && isOffDay)
                    || status == PunchDayStatus.ShiftHoliday;
                absenceRecordedOnOffDay = false;
            }

            // يوم بلا قيد في الجدول الشهري مع تفعيل «الإعفاء عند غياب القيد»: لا حكم عام عليه.
            bool scheduleExemptDay = !isShiftDay
                && shift.IsShiftEmployee
                && shift.Schedule.Count > 0
                && rule.Shift.MissingScheduleExempts;

            bool hasPunchDay = status is PunchDayStatus.Complete or PunchDayStatus.Incomplete;
            bool skipStandardComparison = isShiftDay || scheduleExemptDay;
            int lateness = 0, early = 0, absence = 0, worked = 0, gapMinutes = 0;
            int rawLateness = 0, rawEarly = 0;

            if (hasPunchDay && clockIn.HasValue)
            {
                rawLateness = Math.Max(0, (int)Math.Round(
                    (clockIn.Value.ToTimeSpan() - LegalRules.WorkdayStart.ToTimeSpan()).TotalMinutes));
            }

            if (status == PunchDayStatus.Complete && clockIn.HasValue && clockOut.HasValue)
            {
                rawEarly = Math.Max(0, (int)Math.Round(
                    (LegalRules.WorkdayEnd.ToTimeSpan() - clockOut.Value.ToTimeSpan()).TotalMinutes));
            }

            if (isShiftDay && !shift.ExemptFromMorningLateness && rule.Shift.DutyGraceMinutes > 0)
            {
                // احتساب تأخير الوردية بسماح الإعدادات (حين لا يكون الإعفاء من التأخير مُفعّلاً).
                lateness = Math.Max(0, rawLateness - rule.Shift.DutyGraceMinutes);
            }
            else
            {
                lateness = skipStandardComparison || shift.ExemptFromMorningLateness ? 0 : rawLateness;
            }

            early = skipStandardComparison || shift.ExemptFromEarlyDeparture ? 0 : rawEarly;

            // ---- الدوام المرن: نافذة دوام فردية لهذا اليوم (استحقاق الموظف/الإدارة أو تصريح ساري) ----
            // تتحرّك نهاية الدوام بقدر التأخّر داخل النافذة المسموحة، فلا تأخير صباحي،
            // ويُحتسب النقص إن لم يُكمل الموظف ساعات الدوام اليومية.
            TimeOnly effectiveEnd = LegalRules.WorkdayEnd;
            PunchFlexibleDay? flexibleDay = null;

            if (!skipStandardComparison && hasPunchDay)
            {
                flexibleDay = ResolveFlexibleDay(
                    rule.Flexible,
                    approvals,
                    jobNumber,
                    department,
                    group.Key,
                    clockIn,
                    LegalRules.WorkdayStart,
                    LegalRules.WorkdayEnd);
            }

            if (flexibleDay is not null)
            {
                effectiveEnd = flexibleDay.End;
                lateness = 0;

                if (status == PunchDayStatus.Complete && clockOut.HasValue)
                {
                    early = Math.Max(0, (int)Math.Round(
                        effectiveEnd.ToTimeSpan().TotalMinutes - clockOut.Value.ToTimeSpan().TotalMinutes));
                }
            }

            // دوام فعلي في يوم راحة/إجازة/عطلة لموظف ورديات (يُحتسب إضافياً ولو لم يكن يوم وردية).
            bool offShiftDayWork = shift.IsShiftEmployee
                && isShiftDay
                && primary is PunchDayStatus.Complete or PunchDayStatus.Incomplete
                && shiftEntry!.Kind is ShiftDayKind.Rest or ShiftDayKind.Leave
                    or ShiftDayKind.Holiday or ShiftDayKind.Training;

            // الدقائق الفعلية تُحتسب أيضاً للدوام في العطل ونهاية الأسبوع وأيام الورديات (للإضافي).
            if ((status == PunchDayStatus.Complete
                    || worksOnHoliday
                    || status == PunchDayStatus.ShiftDuty
                    || offShiftDayWork)
                && clockIn.HasValue
                && clockOut.HasValue)
            {
                worked = Math.Max(0, (int)Math.Round(
                    (clockOut.Value.ToTimeSpan() - clockIn.Value.ToTimeSpan()).TotalMinutes));
            }

            // المغادرات أثناء الدوام: الفجوات بين جلسات البصمات (انصراف ثم حضور لاحق)
            // تُحتسب «مغادرة» ضمن المادتين 118/ب و118/ج، مع تجاهل الفجوات القصيرة (بصمة مكرّرة).
            if (hasPunchDay && !skipStandardComparison)
            {
                var sessions = rows
                    .Where(r => r.Status is PunchDayStatus.Complete or PunchDayStatus.Incomplete)
                    .Where(r => r.ClockIn.HasValue || r.ClockOut.HasValue)
                    .Select(r => (In: r.ClockIn, Out: r.ClockOut))
                    .OrderBy(s => s.In ?? s.Out ?? TimeOnly.MinValue)
                    .ToList();

                for (int i = 1; i < sessions.Count; i++)
                {
                    var previous = sessions[i - 1];
                    var current = sessions[i];
                    if (previous.Out is null || current.In is null)
                    {
                        continue; // بصمة ناقصة: الفجوة غير معروفة فلا تُحتسب
                    }

                    int gap = (int)Math.Round(
                        (current.In.Value.ToTimeSpan() - previous.Out.Value.ToTimeSpan()).TotalMinutes);

                    if (gap > LegalRules.PunchNoiseMinutes)
                    {
                        gapMinutes += gap;
                    }
                }
            }

            int rawAbsence = 0;
            if (hasPunchDay && !skipStandardComparison)
            {
                rawAbsence = LegalRules.WorkdayAbsenceMinutes(lateness + gapMinutes, early);
                absence = shift.ExemptFromAbsence ? 0 : rawAbsence;
            }

            bool counts118b = status == PunchDayStatus.Complete && absence > LegalRules.Art118b_MinutesThreshold;

            // ---- العمل الإضافي: المدة خارج نافذة الدوام (وكامل الدوام في أيام الراحة والعطل) ----
            // يُحتسب للموظفين المصرَّح لهم، بحدّ أعلى يومي (وسقوف شهرية تُطبَّق لاحقاً على الشهر كاملاً).
            bool holidayWork = status == PunchDayStatus.HolidayWork
                || (isHoliday && primary is PunchDayStatus.Complete or PunchDayStatus.Incomplete);

            var overtime = ComputeOvertime(
                rule.Overtime,
                approvals,
                jobNumber,
                department,
                group.Key,
                worksOnHoliday: holidayWork,
                worksOnWeekend: isWeekend && !isHoliday,
                worksOnOffShiftDay: offShiftDayWork,
                isShiftDutyDay: status == PunchDayStatus.ShiftDuty,
                hasPunch: primary is PunchDayStatus.Complete or PunchDayStatus.Incomplete,
                workedMinutes: worked,
                clockIn: clockIn,
                clockOut: clockOut,
                effectiveEnd: effectiveEnd,
                latenessMinutes: lateness,
                workdayStart: LegalRules.WorkdayStart,
                countEarlyArrival: rule.Overtime.CountEarlyArrival
                    || (flexibleDay is not null && rule.Flexible.EarlyArrivalCountsAsOvertime));

            var notes = new List<string>(5);
            if (rows.Count > 1)
            {
                notes.Add($"دُمجت {rows.Count} صفوف لنفس اليوم");
            }

            if (status == PunchDayStatus.Incomplete)
            {
                notes.Add("بصمة حضور بلا انصراف — يحتاج تسوية");
            }

            // ---- ملاحظات نظام الورديات (الجدول الشهري، الراحة/الإجازة، والإعفاءات السارية) ----
            if (isShiftDay)
            {
                var code = string.IsNullOrWhiteSpace(shiftEntry!.ShiftCode) ? "—" : shiftEntry.ShiftCode;
                notes.Add(status switch
                {
                    PunchDayStatus.ShiftRest => $"راحة وفق جدول الورديات (الرمز «{code}») — لا تُحتسب غياباً",
                    PunchDayStatus.ShiftLeave => $"إجازة وفق جدول الورديات (الرمز «{code}»)",
                    PunchDayStatus.ShiftHoliday => $"عطلة وفق جدول الورديات (الرمز «{code}»)",
                    PunchDayStatus.ShiftTraining => $"دورة/تدريب وفق جدول الورديات (الرمز «{code}») — لا تُحتسب غياباً",
                    _ => $"يوم وردية ({rule.Shift.CycleHoursText()} ساعة) وفق جدول الورديات (الرمز «{code}»)"
                });

                if (shiftDutyMissingPunch)
                {
                    notes.Add("وردية بلا بصمة حضور — تُحال إلى مسؤول الورديات للمراجعة");
                }
            }
            else if (scheduleExemptDay)
            {
                notes.Add("لا قيد لهذا اليوم في جدول الورديات — مستثنى من الحكم العام وفق الإعدادات");
            }

            if (!isShiftDay && shift.ExemptFromMorningLateness && rawLateness >= graceMinutes && rawLateness > 0)
            {
                notes.Add("إعفاء ساري: لا يُحتسب التأخير الصباحي على هذه الإدارة/الفئة");
            }

            if (!skipStandardComparison && shift.ExemptFromEarlyDeparture && rawEarly > 0)
            {
                notes.Add("إعفاء ساري: لا يُحتسب الانصراف المبكر على هذه الإدارة/الفئة");
            }

            if (shift.ExemptFromAbsence && rawAbsence > 0)
            {
                notes.Add("إعفاء ساري: لا تُحتسب ساعات الغياب والمادة 118/ب على هذه الإدارة");
            }

            if (gapMinutes > 0)
            {
                notes.Add($"مغادرة أثناء الدوام {MinutesLabel(gapMinutes)}");
            }

            // ---- ملاحظات الدوام المرن والعمل الإضافي (للتدقيق القانوني) ----
            if (flexibleDay is not null)
            {
                notes.Add($"دوام مرن: نافذة {PunchTimeText.Format(flexibleDay.Start)} — {PunchTimeText.Format(flexibleDay.End)}"
                    + (flexibleDay.ExtensionMinutes > 0 ? $" (تأخير معوَّض {MinutesLabel(flexibleDay.ExtensionMinutes)})" : string.Empty)
                    + $" — {flexibleDay.Source}");
            }

            if (overtime.RawMinutes > 0)
            {
                var kindText = overtime.Kind switch
                {
                    OvertimeDayKind.Holiday => " (عطلة رسمية/دينية)",
                    OvertimeDayKind.Weekend => " (نهاية أسبوع/راحة)",
                    _ => string.Empty
                };

                notes.Add(overtime.CountedMinutes > 0
                    ? $"عمل إضافي محتسب {MinutesLabel(overtime.CountedMinutes)}{kindText} بنسبة {PunchOvertimeRule.RateText(overtime.Rate)} — {overtime.Source}"
                    : $"مدة خارج الدوام {MinutesLabel(overtime.RawMinutes)}{kindText} غير محتسبة إضافياً — {overtime.Source}");
            }

            if (worksOnHoliday)
            {
                notes.Add(calendarHoliday
                    ? $"دوام في عطلة رسمية/دينية: {holiday.Name}"
                    : fileHoliday
                        ? "دوام في عطلة رسمية (وفق ملف البصمات)"
                        : "دوام في عطلة نهاية الأسبوع");
            }
            else if (absenceRecordedOnOffDay)
            {
                notes.Add("غياب مُسجَّل في يوم عطلة (نهاية أسبوع أو عطلة رسمية/دينية) — مستثنى من المخالفات");
            }
            else if (isOffDay && !hasPunchDay)
            {
                notes.Add(calendarHoliday ? $"يوم عطلة: {holiday.Name}" : "عطلة نهاية الأسبوع");
            }

            if (ranked.Count > 0 && ranked.Count != rows.Count)
            {
                notes.Add("تعارض: تسجيل إجازة/عطلة مع وجود بصمة حضور");
            }

            var result = new PunchDailyResult
            {
                JobNumber = jobNumber,
                EmployeeName = employeeName,
                DepartmentName = department,
                WorkDate = group.Key,
                Status = status,
                ClockIn = clockIn,
                ClockOut = clockOut,
                LatenessMinutes = lateness,
                EarlyDepartureMinutes = early,
                GapMinutes = gapMinutes,
                AbsenceMinutes = absence,
                WorkedMinutes = worked,
                SourceRows = rows.Count,
                IsWorkingDay = WorkingStatuses.Contains(status),
                IsMorningLate = hasPunchDay && LegalRules.IsMorningLateness(lateness, graceMinutes),
                IsAbsent = status == PunchDayStatus.Absent,
                IsIncomplete = status == PunchDayStatus.Incomplete,
                CountsFor118b = counts118b,
                Article118bDays = counts118b ? Math.Round(LegalRules.Article118bDaysFromAbsence(absence), 2) : 0,
                IsWeekendDay = isWeekend,
                IsCalendarHoliday = isHoliday,
                HolidayKind = calendarHoliday ? holiday.Kind : HolidayKind.Official,
                HolidayName = calendarHoliday
                    ? Truncate(holiday.Name, 200)
                    : fileHoliday
                        ? "عطلة رسمية (وفق ملف البصمات)"
                        : null,
                IsWorkOnHoliday = worksOnHoliday,
                IsShiftDay = isShiftDay,
                ShiftKind = isShiftDay ? shiftEntry!.Kind : ShiftDayKind.Unknown,
                ShiftCode = isShiftDay ? Truncate(shiftEntry!.ShiftCode, 40) : null,
                IsFlexibleWork = flexibleDay is not null,
                FlexibleStart = flexibleDay?.Start,
                FlexibleEnd = flexibleDay?.End,
                FlexibleMinutes = flexibleDay?.ExtensionMinutes ?? 0,
                IsOvertimeEligible = overtime.Eligible,
                RawOvertimeMinutes = overtime.RawMinutes,
                OvertimeMinutes = overtime.CountedMinutes,
                OvertimeExcludedMinutes = overtime.ExcludedMinutes,
                OvertimeNeedsApproval = overtime.NeedsApproval,
                OvertimeKind = overtime.RawMinutes > 0 ? overtime.Kind : OvertimeDayKind.None,
                OvertimeRate = overtime.RawMinutes > 0 ? overtime.Rate : 0,
                EquivalentOvertimeMinutes = EquivalentOvertime(overtime.CountedMinutes, overtime.Rate),
                WorkWeekStart = LegalRules.StartOfWorkWeek(group.Key),
                Year = group.Key.Year,
                Month = group.Key.Month,
                Notes = notes.Count > 0 ? Truncate(string.Join(" | ", notes), 390) : null
            };

            days.Add(result);
            sink.Add(result);
        }

        return days;
    }

    /// <summary>تقصير النصوص لتطابق حدود أعمدة قاعدة البيانات.</summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// ملاحظات النتيجة الشهرية: نقص بصمة الانصراف + نظام الورديات (أيام الجدول) + الإعفاءات السارية،
    /// لتكون الاستثناءات ظاهرة في التدقيق ولا تبدو الخصومات ناقصة بلا سبب.
    /// </summary>
    private static string? MonthlyNotes(
        int incompleteDays,
        int shiftDutyDays,
        int shiftRestDays,
        int shiftLeaveDays,
        PunchShiftContext shift,
        int flexibleDays,
        int overtimeMinutes,
        int overtimeExcludedMinutes,
        int overtimeNeedsApprovalDays,
        int overtimeNeedsApprovalMinutes,
        bool overtimeCapReached,
        PunchOvertimeRule overtimeRule)
    {
        var notes = new List<string>(7);

        if (incompleteDays > 0)
        {
            notes.Add($"{incompleteDays} يوم عمل بلا بصمة انصراف (تسوية مطلوبة)");
        }

        if (flexibleDays > 0)
        {
            notes.Add($"دوام مرن: {flexibleDays} يوماً وفق نافذة الحضور المعتمدة");
        }

        if (overtimeMinutes > 0 || overtimeExcludedMinutes > 0)
        {
            notes.Add($"عمل إضافي: {PunchFlexibleRule.HoursText(overtimeMinutes)} محتسبة"
                + (overtimeExcludedMinutes > 0 ? $" — استُبعد {PunchFlexibleRule.HoursText(overtimeExcludedMinutes)} وفق الحدود المعتمدة" : string.Empty)
                + (overtimeCapReached
                    ? $" (بلغ الحدّ الشهري {PunchFlexibleRule.HoursText(overtimeRule.EffectiveMaxMinutesPerMonth)})"
                    : string.Empty));
        }

        if (overtimeNeedsApprovalMinutes > 0 && overtimeMinutes == 0)
        {
            notes.Add($"لا يُحتسب عمل إضافي بلا موافقة مسبقة: {overtimeNeedsApprovalDays} يوماً بمقدار "
                + $"{PunchFlexibleRule.HoursText(overtimeNeedsApprovalMinutes)} معلَّقة بانتظار تصريح ساري"
                + (overtimeRule.RequireApproval ? " (الاشتراط مُفعَّل)" : string.Empty));
        }

        if (shiftDutyDays + shiftRestDays + shiftLeaveDays > 0)
        {
            notes.Add($"نظام الورديات وفق الجدول الشهري: {shiftDutyDays} وردية، {shiftRestDays} راحة، {shiftLeaveDays} إجازة");
        }

        if (shift.IsShiftEmployee)
        {
            notes.Add("موظف بنظام الورديات — لا تُقارَن أيامه بأوقات الدوام الرسمي (08:30 — 15:30)");
        }

        if (shift.ExemptFromWeeklyRule)
        {
            notes.Add(shift.ExemptAllWeeks
                ? "إعفاء من المادة 118/ج (استثناء إداري خاص)"
                : "إعفاء من المادة 118/ج (نظام الورديات — للأسابيع المشمولة بالجدول الشهري)");
        }

        if (shift.ExemptFromMorningLateness)
        {
            notes.Add("إعفاء من التأخير الصباحي وعقوبة المادة 7");
        }

        if (shift.ExemptFromAbsence)
        {
            notes.Add("إعفاء من احتساب الغياب والمادة 118/ب");
        }

        return notes.Count > 0 ? Truncate(string.Join(" | ", notes), 900) : null;
    }

    /// <summary>
    /// تحديد حالة اليوم وفق «جدول الورديات الشهري»:
    /// وردية دوام (ببصمة أو بلا بصمة)، راحة، إجازة، عطلة، دورة/تدريب —
    /// مع إبقاء حالات الإجازات/الاستئذانات المسجّلة في ملف البصمات كما هي.
    /// </summary>
    private static PunchDayStatus ShiftStatusFor(
        ShiftDayKind kind,
        PunchDayStatus? primary,
        bool missingPunchIsAbsence,
        out bool dutyMissingPunch)
    {
        dutyMissingPunch = false;

        switch (kind)
        {
            case ShiftDayKind.Duty:
                if (primary is PunchDayStatus.Complete or PunchDayStatus.Incomplete)
                {
                    return PunchDayStatus.ShiftDuty;
                }

                if (primary is null or PunchDayStatus.NoData or PunchDayStatus.Weekend or PunchDayStatus.OfficialHoliday)
                {
                    // وردية بلا أي بصمة: غياب (إن كانت الإعدادات تشترط ذلك) أو ملاحظة للمراجعة فقط.
                    dutyMissingPunch = !missingPunchIsAbsence;
                    return missingPunchIsAbsence ? PunchDayStatus.Absent : PunchDayStatus.ShiftDuty;
                }

                // إجازة/استئذان/مهمة مسجّلة في ملف البصمات تحكم على اليوم.
                return primary.Value;

            case ShiftDayKind.Rest:
                return PunchDayStatus.ShiftRest;

            case ShiftDayKind.Leave:
                return PunchDayStatus.ShiftLeave;

            case ShiftDayKind.Holiday:
                return PunchDayStatus.ShiftHoliday;

            case ShiftDayKind.Training:
                return PunchDayStatus.ShiftTraining;

            default:
                return primary ?? PunchDayStatus.NoData;
        }
    }

    /// <summary>
    /// التجميع الأسبوعي للمادة 118/ج وفق القاعدة المرنة السارية:
    /// دمج (التأخير الصباحي + الانصراف المبكر + المغادرة أثناء الدوام — بحسب خيارات الدمج) في ناتج واحد،
    /// ثم تحديد المخالفة عند بلوغ/تجاوز الحدّ المعتمد، وتقييد الناتج المحتسب بسقف الإعدادات،
    /// واحتساب أيام الخصم المخصّصة لكل أسبوع مخالف. المجموع الفعلي يُحفظ دائماً للتفصيل والتدقيق.
    /// </summary>
    private static List<PunchWeeklyResult> BuildWeekly(
        IReadOnlyList<PunchDailyResult> days,
        string? employeeName,
        string? department,
        PunchWeeklyRule rule)
    {
        // تعطيل القاعدة من الإعدادات: لا تجميع أسبوعي ولا خصم (تبقى النتائج اليومية للتدقيق).
        if (!rule.Enabled)
        {
            return new List<PunchWeeklyResult>();
        }

        int threshold = rule.ThresholdFor(department);
        int cap = rule.CapFor(department);

        return days
            .Where(d => d.IsWorkingDay)
            .GroupBy(d => d.WorkWeekStart)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                int lateness = g.Sum(d => d.LatenessMinutes);
                int early = rule.MergeEarlyDeparture ? g.Sum(d => d.EarlyDepartureMinutes) : 0;
                int gaps = rule.MergeMidDayGaps ? g.Sum(d => d.GapMinutes) : 0;
                // دمج (التأخير الصباحي + الانصراف المبكر + المغادرة أثناء الدوام) في مجموع أسبوعي واحد.
                int combined = LegalRules.TotalWeeklyLateMinutes(lateness, early) + gaps;
                // الناتج المحتسب: المجموع الفعلي مقيَّداً بسقف الإعدادات (أو كما هو عند تعطيل التقييد).
                int counted = LegalRules.CappedWeeklyMinutes(combined, cap, rule.CapCountedMinutes);
                bool violation = LegalRules.WeeklyThresholdReached(combined, threshold, rule.ViolationWhenExceededOnly);
                double deductionDays = LegalRules.WeeklyDeductionDays(violation, rule.DeductionDaysPerWeek);

                var details = string.Join(" | ", g
                    .Where(d => d.LatenessMinutes > 0 || d.EarlyDepartureMinutes > 0 || d.GapMinutes > 0)
                    .OrderBy(d => d.WorkDate)
                    .Select(d => $"{d.WorkDate:yyyy-MM-dd} (+{d.LatenessMinutes}ت/-{d.EarlyDepartureMinutes}ص/م{d.GapMinutes})"));

                // ملاحظة السقف + ملاحظة القاعدة المطبَّقة (الحدّ وأيام الخصم) للتدقيق القانوني.
                var capNote = combined > counted
                    ? $"المجموع الفعلي {combined} دقيقة — الناتج المحتسب {counted} دقيقة (سقف المادة 118/ج = {cap} دقيقة)"
                    : null;

                var ruleNote = $"قاعدة 118/ج: {(rule.ViolationWhenExceededOnly ? "تجاوز" : "بلوغ")} {threshold} دقيقة = خصم {rule.DeductionDaysText()} يوم"
                    + (threshold != rule.ThresholdMinutes ? $" (حدّ خاص للإدارة بدل {rule.ThresholdMinutes})" : string.Empty)
                    + (rule.CapCountedMinutes ? string.Empty : " | بلا تقييد للناتج")
                    + (rule.MergeEarlyDeparture ? string.Empty : " | بلا دمج الانصراف المبكر")
                    + (rule.MergeMidDayGaps ? string.Empty : " | بلا دمج المغادرة أثناء الدوام");

                // الترتيب: ملاحظة السقف ثم تفصيل أيام الأسبوع ثم القاعدة المطبَّقة (الأقل أهمية يُقصّ عند طول النص).
                var notes = string.Join(" | ", new[] { capNote, details, ruleNote }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

                return new PunchWeeklyResult
                {
                    JobNumber = g.First().JobNumber,
                    EmployeeName = employeeName,
                    DepartmentName = department,
                    WeekStart = g.Key,
                    WeekEnd = LegalRules.EndOfWorkWeek(g.Key),
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    DaysCounted = g.Count(),
                    LatenessMinutes = lateness,
                    EarlyDepartureMinutes = early,
                    GapMinutes = gaps,
                    TotalMinutes = combined,
                    CountedMinutes = counted,
                    Exceeds60Minutes = violation,
                    DeductionDays = deductionDays,
                    Notes = notes.Length > 0 ? Truncate(notes, 390) : null
                };
            })
            .ToList();
    }

    /// <summary>
    /// النتيجة الشهرية: عقوبة التأخير الصباحي (المادة 7)، الغياب، الإجازات،
    /// أيام المادتين 118/ب و118/ج، وخصم المكافأة عند تجاوز 15 يوماً.
    /// </summary>
    private static List<PunchMonthlyResult> BuildMonthly(
        IReadOnlyList<PunchDailyResult> days,
        IReadOnlyList<PunchWeeklyResult> weeks,
        string jobNumber,
        string? employeeName,
        string? department,
        PunchWeeklyRule rule,
        PunchShiftContext shift)
    {
        var weeksByMonth = weeks
            .GroupBy(w => (w.Year, w.Month))
            .ToDictionary(g => g.Key, g => g.ToList());

        var daysByMonth = days
            .GroupBy(d => (d.Year, d.Month))
            .ToDictionary(g => g.Key, g => g.ToList());

        // تُحتسب الأشهر من أيام التحليل ومن أسابيع المادة 118/ج معاً؛
        // فالأسابيع التي تبدأ في آخر شهر وتمتد إلى الشهر التالي لا تُفقد نتائجها.
        var months = daysByMonth.Keys
            .Concat(weeksByMonth.Keys)
            .Distinct()
            .OrderBy(k => k.Year)
            .ThenBy(k => k.Month)
            .ToList();

        var results = new List<PunchMonthlyResult>();

        foreach (var month in months)
        {
            var list = daysByMonth.GetValueOrDefault(month) ?? new List<PunchDailyResult>();
            var monthWeeks = weeksByMonth.GetValueOrDefault(month) ?? new List<PunchWeeklyResult>();

            int lateIncidents = list.Count(d => d.IsMorningLate);
            var penalty = LegalRules.MonthlyLatePenalty(lateIncidents);

            int absentDays = list.Count(d => d.IsAbsent);
            int incompleteDays = list.Count(d => d.IsIncomplete);

            // أيام العطل المستثناة (نهاية الأسبوع + العطل الرسمية والدينية) والدوام الفعلي فيها.
            int weekendDays = list.Count(d => d.IsWeekendDay && !d.IsCalendarHoliday);
            int holidayDays = list.Count(d => d.IsCalendarHoliday);
            int weekendWorkDays = list.Count(d => d.Status == PunchDayStatus.WeekendWork);
            int holidayWorkDays = list.Count(d => d.Status == PunchDayStatus.HolidayWork);

            // ---- نظام الورديات: أيام الورديات والراحة والإجازة وفق الجدول الشهري ----
            int shiftDutyDays = list.Count(d => d.Status == PunchDayStatus.ShiftDuty);
            int shiftRestDays = list.Count(d => d.Status == PunchDayStatus.ShiftRest);
            int shiftLeaveDays = list.Count(d => d.Status == PunchDayStatus.ShiftLeave);

            double annual = list.Count(d => d.Status == PunchDayStatus.AnnualLeave);
            double sick = list.Count(d => d.Status == PunchDayStatus.SickLeave);
            double compensatory = list.Count(d => d.Status == PunchDayStatus.CompensatoryLeave);
            double bereavement = list.Count(d => d.Status == PunchDayStatus.BereavementLeave);
            double emergency = list.Count(d => d.Status == PunchDayStatus.EmergencyPermission);
            double medical = list.Count(d => d.Status == PunchDayStatus.MedicalPermission);
            double officialDuty = list.Count(d => d.Status is PunchDayStatus.OfficialMission
                                                  or PunchDayStatus.Secondment
                                                  or PunchDayStatus.Training);

            // ---- الدوام المرن والعمل الإضافي: إجمالي الشهر وفق السقوف المطبَّقة ----
            int flexibleDays = list.Count(d => d.IsFlexibleWork);
            int flexibleMinutes = list.Sum(d => d.FlexibleMinutes);
            int overtimeMinutes = list.Sum(d => d.OvertimeMinutes);
            int overtimeRaw = list.Sum(d => d.RawOvertimeMinutes);
            int overtimeExcluded = list.Sum(d => d.OvertimeExcludedMinutes);
            int overtimeDays = list.Count(d => d.OvertimeMinutes > 0);
            int overtimeWeekendMinutes = list.Where(d => d.OvertimeKind == OvertimeDayKind.Weekend).Sum(d => d.OvertimeMinutes);
            int overtimeHolidayMinutes = list.Where(d => d.OvertimeKind == OvertimeDayKind.Holiday).Sum(d => d.OvertimeMinutes);
            int equivalentOvertimeMinutes = list.Sum(d => d.EquivalentOvertimeMinutes);
            int needsApprovalDays = list.Count(d => d.OvertimeNeedsApproval);
            int needsApprovalMinutes = list.Where(d => d.OvertimeNeedsApproval).Sum(d => d.RawOvertimeMinutes);
            bool overtimeCapReached = rule.Overtime.EffectiveMaxMinutesPerMonth > 0
                && overtimeMinutes >= rule.Overtime.EffectiveMaxMinutesPerMonth
                && overtimeExcluded > 0;

            double article118b = Math.Round(list.Sum(d => d.Article118bDays), 2);
            double article118c = Math.Round(monthWeeks.Sum(w => w.DeductionDays), 2);
            // الناتج الأسبوعي المُرحَّل للشهر: إمّا الناتج المحتسب (المقيَّد بسقف الإعدادات) أو المجموع الفعلي.
            int weeklyMinutes = rule.UseCountedInMonthlyRollup
                ? monthWeeks.Sum(w => w.CountedMinutes)
                : monthWeeks.Sum(w => w.TotalMinutes);
            int weeksOverThreshold = monthWeeks.Count(w => w.Exceeds60Minutes);

            // قاعدة الـ 15 يوماً: الغياب غير المبرّر + الإجازة السنوية + المرضية + أيام 118/ب و118/ج.
            double totalAbsence = Math.Round(absentDays + annual + sick + article118b + article118c, 2);
            double bonusPercent = LegalRules.BonusDeductionPercent(totalAbsence);
            double article7Days = LegalRules.Article7SalaryDeductionDays(penalty);

            results.Add(new PunchMonthlyResult
            {
                JobNumber = list.Count > 0 ? list[0].JobNumber : jobNumber,
                EmployeeName = employeeName,
                DepartmentName = department,
                Year = month.Year,
                Month = month.Month,
                WorkingDays = list.Count(d => d.IsWorkingDay),
                CompleteDays = list.Count(d => d.Status == PunchDayStatus.Complete),
                WeekendDays = weekendDays,
                HolidayDays = holidayDays,
                WeekendWorkDays = weekendWorkDays,
                HolidayWorkDays = holidayWorkDays,
                LateIncidents = lateIncidents,
                Penalty = penalty,
                PenaltyText = LegalRules.DisciplinaryActionText(penalty),
                Article7SalaryDeductionDays = article7Days,
                AbsentDays = absentDays,
                IncompleteDays = incompleteDays,
                AnnualLeaveDays = annual,
                SickLeaveDays = sick,
                CompensatoryLeaveDays = compensatory,
                BereavementLeaveDays = bereavement,
                EmergencyPermissionDays = emergency,
                MedicalPermissionDays = medical,
                OfficialDutyDays = officialDuty,
                Article118bDays = article118b,
                Article118cDays = article118c,
                WeeklyLateMinutes = weeklyMinutes,
                WeeksOver60Minutes = weeksOverThreshold,
                ShiftDutyDays = shiftDutyDays,
                ShiftRestDays = shiftRestDays,
                ShiftLeaveDays = shiftLeaveDays,
                FlexibleDays = flexibleDays,
                FlexibleMinutes = flexibleMinutes,
                OvertimeDays = overtimeDays,
                OvertimeMinutes = overtimeMinutes,
                OvertimeHours = Math.Round(overtimeMinutes / 60.0, 2),
                RawOvertimeMinutes = overtimeRaw,
                OvertimeExcludedMinutes = overtimeExcluded,
                OvertimeNeedsApprovalDays = needsApprovalDays,
                OvertimeNeedsApprovalMinutes = needsApprovalMinutes,
                OvertimeWeekendMinutes = overtimeWeekendMinutes,
                OvertimeHolidayMinutes = overtimeHolidayMinutes,
                EquivalentOvertimeMinutes = equivalentOvertimeMinutes,
                EquivalentOvertimeHours = Math.Round(equivalentOvertimeMinutes / 60.0, 2),
                OvertimeCapReached = overtimeCapReached,
                TotalAbsenceDays = totalAbsence,
                BonusDeductionPercent = bonusPercent,
                Exceeds15Days = totalAbsence > LegalRules.Bonus_AbsenceDaysThreshold,
                TotalSalaryDeductionDays = Math.Round(absentDays + article7Days, 2),
                AnnualLeaveBalanceUsageDays = article118b,
                Notes = MonthlyNotes(
                    incompleteDays, shiftDutyDays, shiftRestDays, shiftLeaveDays, shift,
                    flexibleDays, overtimeMinutes, overtimeExcluded, needsApprovalDays, needsApprovalMinutes,
                    overtimeCapReached, rule.Overtime)
            });
        }

        return results;
    }

    /// <summary>أكثر قيمة تكراراً (لتوحيد اسم الموظف).</summary>
    private static string? MostFrequent(IEnumerable<string?> values) =>
        values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>الإدارة الأكثر تكراراً للموظف مع تجاهل القيم الفارغة و«_» و«-».</summary>
    private static string? MostFrequentDepartment(IEnumerable<string?> values) =>
        MostFrequent(values.Where(v =>
        {
            var t = Normalize(v);
            return t.Length > 0 && t != "_" && t != "-";
        }));
}
