// protobuf 생성 헤더를 포함할 때는 반드시 이 헤더를 사용한다.
// pb.h 를 직접 include 하면 windows.h 매크로(GetMessage 등)와 충돌할 수 있다.
//
// [중요/불변식] protobuf 타입을 사용하는 코드는 전부 THProtocol 모듈 안에만 둔다.
// libprotobuf 가 정적 링크라 에디터(모듈별 DLL) 빌드에서 다른 모듈이 pb 타입을 쓰면
// 런타임 복사본이 두 개 생기고, 내부 전역(fixed_address_empty_string 등) 주소 불일치로
// string 필드 조작 시 타 DLL 정적 메모리를 건드리는 크래시가 난다.
// 다른 모듈에서 pb 타입이 필요해지면 protobuf 를 DLL 빌드(PROTOBUF_USE_DLLS)로 전환하고
// --cpp_out=dllexport_decl=THPROTOCOL_API 로 생성 클래스를 export 할 것.
#pragma once

#include "CoreMinimal.h"

#if PLATFORM_WINDOWS
	// windows.h 매크로와 protobuf 메서드(Reflection::GetMessage 등) 충돌 방지
	#pragma push_macro("GetMessage")
	#pragma push_macro("SendMessage")
	#pragma push_macro("GetClassName")
	#undef GetMessage
	#undef SendMessage
	#undef GetClassName
#endif

THIRD_PARTY_INCLUDES_START
#include "Generated/enum.pb.h"
#include "Generated/protocol.pb.h"
THIRD_PARTY_INCLUDES_END

#if PLATFORM_WINDOWS
	#pragma pop_macro("GetClassName")
	#pragma pop_macro("SendMessage")
	#pragma pop_macro("GetMessage")
#endif
