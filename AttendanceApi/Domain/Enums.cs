namespace AttendanceApi.Domain;

/// <summary>تصنيف البطاقة/المجموعة الوظيفية (المادة 100).</summary>
public enum ClassGroup
{
    First = 1,
    Second = 2,
    Third = 3,
    Fourth = 4
}

/// <summary>نوع عقد الموظف.</summary>
public enum ContractType
{
    Permanent = 1,
    Temporary = 2,
    Contractual = 3
}

/// <summary>نمط العمل (حضوري/عن بُعد/مختلط).</summary>
public enum WorkMode
{
    OnSite = 1,
    Remote = 2,
    Hybrid = 3
}

/// <summary>نوع جدول العمل.</summary>
public enum ScheduleType
{
    Standard = 1,
    Shift = 2,
    PartTime = 3
}

/// <summary>نوع الوجبات التعويضية للمغادرة/الانصراف.</summary>
public enum DepartureType
{
    /// <summary>استئذان (المادة 118/ب).</summary>
    Authorization = 1,
    /// <summary>انصراف مبكر.</summary>
    EarlyDeparture = 2,
    /// <summary>خروج رسمي.</summary>
    OfficialDeparture = 3
}

/// <summary>نوع الإجراء التأديبي (المادة 7 - تعليمات 2020).</summary>
public enum DisciplinaryActionType
{
    None = 0,
    /// <summary>تنبيه خطي عند بلوغ 3 تأخيرات صباحية في الشهر.</summary>
    WrittenWarning = 1,
    /// <summary>إنذار خطي عند بلوغ 4 تأخيرات صباحية في الشهر.</summary>
    WrittenCaution = 2,
    /// <summary>حسم يومين من الراتب عند تجاوز 4 تأخيرات صباحية في الشهر.</summary>
    TwoDaysSalaryDeduction = 3
}

/// <summary>نوع قرار اللجنة الطبية (المادة 112).</summary>
public enum MedicalDecisionType
{
    Reexamination = 1,
    Prolongation = 2,
    Return = 3
}

/// <summary>نوع الموعد/الفترة في التسجيل.</summary>
public enum AttendanceSession
{
    Morning = 1,
    Evening = 2
}

/// <summary>حالة تشغيل محرك المراجعة.</summary>
public enum AuditRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>
/// تصنيف العطلة في تقويم العطل المعتمد لتحليل الحضور والانصراف:
/// العطل الرسمية، العطل الدينية الإسلامية، والعطل الدينية المسيحية.
/// تُستهلك هذه التصنيفات في استثناء أيام العطل من احتساب الغياب والمخالفات.
/// </summary>
public enum HolidayKind
{
    /// <summary>عطلة رسمية عامة (رأس السنة الميلادية، عيد العمال، عيد الاستقلال...).</summary>
    Official = 1,
    /// <summary>عطلة دينية إسلامية (عيد الفطر، عيد الأضحى، المولد النبوي، الإسراء والمعراج، رأس السنة الهجرية).</summary>
    ReligiousIslamic = 2,
    /// <summary>عطلة دينية مسيحية (عيد الميلاد المجيد، عيد الفصح).</summary>
    ReligiousChristian = 3,
    /// <summary>مناسبة وطنية أو إدارية خاصة (تُدرج يدوياً عند الحاجة).</summary>
    NationalOccasion = 4
}
