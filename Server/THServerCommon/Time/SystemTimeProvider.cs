namespace TH.Common.Time;

public sealed class SystemTimeProvider : ITimeProvider
{
    private static readonly TimeZoneInfo _kstZone = LoadKstZone();

    private static TimeZoneInfo LoadKstZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"); }
        catch (TimeZoneNotFoundException) { }

        throw new InvalidOperationException(
            "KST timezone을 찾을 수 없습니다. 'Asia/Seoul' 또는 'Korea Standard Time' ID가 시스템에 존재해야 합니다.");
    }

    public THDateTime NowKst()
    {
        var kst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _kstZone);
        return new THDateTime(kst.Year, kst.Month, kst.Day,
                              kst.Hour, kst.Minute, kst.Second, kst.Millisecond);
    }

    public DateTime UtcNow() => DateTime.UtcNow;

    public long UnixMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
