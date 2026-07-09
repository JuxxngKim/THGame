// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class THClient : ModuleRules
{
	public THClient(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
	
		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine", "InputCore", "EnhancedInput" });

		// THProtocol: protobuf 생성 코드 모듈 (THClient 공개 헤더에서 pb 타입을 노출하게 되면 Public 으로 승격)
		PrivateDependencyModuleNames.AddRange(new string[] { "THProtocol" });

		// Uncomment if you are using Slate UI
		// PrivateDependencyModuleNames.AddRange(new string[] { "Slate", "SlateCore" });
		
		// Uncomment if you are using online features
		// PrivateDependencyModuleNames.Add("OnlineSubsystem");

		// To include OnlineSubsystemSteam, add it to the plugins section in your uproject file with the Enabled attribute set to true
	}
}
