using System.IO;
using UnrealBuildTool;

// protoc 생성 코드(.pb.cpp) 컴파일 격리 모듈 — 경고/예외/PCH 특수 설정을 게임 모듈(THClient)에 오염시키지 않는다
public class THProtocol : ModuleRules
{
	public THProtocol(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.NoPCHs;   // 공유 PCH 의 UE/Windows 매크로가 protobuf 생성 코드에 새는 것 방지
		bUseUnity = false;                // 생성 파일 unity 병합 시 매크로 충돌 방지
		bEnableExceptions = true;         // libprotobuf 는 /EHsc 로 빌드됨 — 예외 경계 일치

		CppCompileWarningSettings.ShadowVariableWarningLevel = WarningLevel.Off;
		CppCompileWarningSettings.UndefinedIdentifierWarningLevel = WarningLevel.Off;

		PublicDependencyModuleNames.AddRange(new string[] { "Core", "Protobuf" });

		// "enum.pb.h" / "protocol.pb.h" 직접 include 허용 (게임 코드는 THProtocolWrapper.h 만 사용할 것)
		PublicIncludePaths.Add(Path.Combine(ModuleDirectory, "Public", "Generated"));
	}
}
