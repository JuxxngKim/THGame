// protobuf 링크/직렬화 왕복 스모크 테스트
// UBT 링크 및 THProtocolWrapper.h 경유 include 가 정상인지 검증한다.
#include "Misc/AutomationTest.h"
#include "THProtocolWrapper.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FTHProtobufRoundTripTest,
	"THClient.Protocol.ProtobufRoundTrip",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FTHProtobufRoundTripTest::RunTest(const FString& Parameters)
{
	th::COLoginReq Req;
	// proto2 required 필드(CurrentVersion, PID, AuthToken)는 전부 채워야 직렬화 가능
	Req.set_currentversion(1);
	Req.set_pid("smoke-pid");
	Req.set_authtoken("smoke-token");

	TestEqual(TEXT("MessageID 기본값"), (int32)Req.messageid(), (int32)th::CO_LOGIN_REQ);
	TestTrue(TEXT("IsInitialized"), Req.IsInitialized());

	TArray<uint8> Buffer;
	Buffer.SetNumUninitialized((int32)Req.ByteSizeLong());
	TestTrue(TEXT("Serialize"), Req.SerializeToArray(Buffer.GetData(), Buffer.Num()));

	th::COLoginReq Parsed;
	TestTrue(TEXT("Parse"), Parsed.ParseFromArray(Buffer.GetData(), Buffer.Num()));
	TestEqual(TEXT("PID 왕복"), FString(UTF8_TO_TCHAR(Parsed.pid().c_str())), FString(TEXT("smoke-pid")));
	TestEqual(TEXT("CurrentVersion 왕복"), Parsed.currentversion(), 1);
	return true;
}
