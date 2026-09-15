namespace TH.Server.Game;

// 필드 좌표. 룸 시뮬레이션의 위치를 나타내는 불변 값 타입(박싱 없음).
public readonly record struct Position(float X, float Y, float Z);
