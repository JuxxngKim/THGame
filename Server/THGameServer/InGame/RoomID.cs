namespace TH.Server.Logic;

// 룸 식별자 — long 을 래핑한 타입 안전 ID. 원시 long 혼용으로 인한 인자 뒤바뀜을 컴파일 타임에 차단.
// 신규 코드 네이밍 규약: 모든 ID 식별자는 대문자 ID 표기(RoomID).
public readonly record struct RoomID(long Value)
{
    public override string ToString() => Value.ToString();
}
