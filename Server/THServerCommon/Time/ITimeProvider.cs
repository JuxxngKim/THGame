namespace TH.Common.Time;

public interface ITimeProvider
{
    /// <summary>한국 시간(KST, UTC+9) 기준 현재 시각.</summary>
    THDateTime NowKst();

    /// <summary>UTC 현재 시각. 저장/직렬화/로깅용.</summary>
    DateTime UtcNow();

    /// <summary>1970-01-01 UTC 이후 경과 ms. 단조 증가 보장 아님 (NTP 동기화 영향).</summary>
    long UnixMillis();
}
