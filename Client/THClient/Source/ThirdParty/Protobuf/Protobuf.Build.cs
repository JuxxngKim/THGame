using System.IO;
using UnrealBuildTool;

// protobuf 3.21.12 정적 라이브러리 External 모듈 (MSVC x64 Release, /MD)
// protoc.exe(Common/Tool/ProtocolGenerator) / 생성 코드(THProtocol/Public/Generated)와 버전 락 — 항상 세트로 업그레이드할 것
public class Protobuf : ModuleRules
{
	public Protobuf(ReadOnlyTargetRules Target) : base(Target)
	{
		Type = ModuleType.External;

		// 시스템 include 로 등록해 protobuf 헤더의 경고가 게임 코드 경고 레벨에 걸리지 않게 함
		PublicSystemIncludePaths.Add(Path.Combine(ModuleDirectory, "include"));

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			// UE 는 Development/DebugGame/Shipping 모두 릴리스 CRT(/MD)를 링크하므로 Release lib 하나로 충분
			PublicAdditionalLibraries.Add(Path.Combine(ModuleDirectory, "lib", "Win64", "Release", "libprotobuf.lib"));
		}

		// UE 는 RTTI 비활성(/GR-) — lib 빌드와 동일하게 정의를 맞춰 인라인 코드 경로 일치
		PublicDefinitions.Add("GOOGLE_PROTOBUF_NO_RTTI=1");
		// 정적 링크이므로 PROTOBUF_USE_DLLS 는 정의하지 않음
	}
}
