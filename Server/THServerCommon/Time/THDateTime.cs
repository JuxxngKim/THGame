namespace TH.Common.Time;

public readonly record struct THDateTime(
    int Year,
    int Month,
    int Day,
    int Hour,
    int Minute,
    int Second,
    int Millisecond)
{
    /// <summary>해당 날짜의 요일.</summary>
    public DayOfWeek DayOfWeek => new DateTime(Year, Month, Day).DayOfWeek;

    /// <summary>KST 기준 DateTime 반환 (DateTimeKind.Unspecified).</summary>
    public DateTime ToDateTime() =>
        new(Year, Month, Day, Hour, Minute, Second, Millisecond, DateTimeKind.Unspecified);

    /// <summary>offset +09:00 명시된 DateTimeOffset 반환.</summary>
    public DateTimeOffset ToDateTimeOffset() =>
        new(Year, Month, Day, Hour, Minute, Second, Millisecond, TimeSpan.FromHours(9));

    /// <summary>"yyyy-MM-dd HH:mm:ss.fff" 형식 문자열.</summary>
    public override string ToString() =>
        $"{Year:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Second:D2}.{Millisecond:D3}";
}
