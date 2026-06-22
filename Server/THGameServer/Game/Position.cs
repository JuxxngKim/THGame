namespace TH.Server.Game;

// 필드 좌표 — 룸 시뮬레이션의 위치 표현. 값 타입(박싱 없음, 불변).
public readonly record struct Position(float X, float Y, float Z);
