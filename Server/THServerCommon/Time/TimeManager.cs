namespace TH.Common.Time;

public sealed class TimeManager : Singleton<TimeManager>, ITimeProvider
{
    private ITimeProvider _provider = SystemTimeProvider.Instance;

    private TimeManager() { }

    public void SetProvider(ITimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public THDateTime NowKst() => _provider.NowKst();
    public DateTime UtcNow() => _provider.UtcNow();
    public long UnixMillis() => _provider.UnixMillis();

    /// <summary>
    /// tick 스케줄링 전용 monotonic 시간(부팅 이후 경과 ms). _provider 와 무관하며
    /// 시스템 시계 변경(NTP/수동 조정)에 영향받지 않는다. 절대 시각 의미는 없다.
    /// </summary>
    public long TickMillis() => Environment.TickCount64;
}
