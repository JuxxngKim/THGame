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
}
