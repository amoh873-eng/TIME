namespace AttendanceApi.Audit;

/// <summary>خيارات محرك المراجعة (قابلة للتخصيص من appsettings).</summary>
public sealed class AuditEngineOptions
{
    public const string SectionName = "AuditEngine";

    public int BatchSize { get; set; } = 50_000;
    public int BulkTimeoutSeconds { get; set; } = 600;
    public int ChannelCapacity { get; set; } = 4;
    public int Art118b_MinutesThreshold { get; set; } = 240;
    public double Art118b_EquivalentDays { get; set; } = 1.0;
    public int Art118c_WeeklyLateMinutesThreshold { get; set; } = 60;
    public double Bonus_AbsenceDaysThreshold { get; set; } = 15.0;
    public int EndOfService_MaxCompensationDays { get; set; } = 60;
    public int CarryoverYearsMax { get; set; } = 2;
}
