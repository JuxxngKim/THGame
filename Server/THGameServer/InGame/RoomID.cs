namespace TH.Server.Logic;

// 룸 식별자. long을 감싼 타입 안전 ID라 다른 long과 인자가 뒤바뀌는 실수를 컴파일 시점에 막는다.
// 신규 코드 네이밍 규약: 모든 ID 식별자는 대문자 ID로 쓴다(RoomID).
public readonly record struct RoomID(long Value)
{
    public override string ToString() => Value.ToString();
}
