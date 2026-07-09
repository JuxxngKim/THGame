@echo off
chcp 65001 >nul
REM Base path is this batch file's directory
SET BASE_DIR=%~dp0
SET PROTO_DIR=%BASE_DIR%protos
SET OUTPUT_DIR=%BASE_DIR%generated
SET PROTOC_CMD=%BASE_DIR%protoc.exe

echo Base Directory: %BASE_DIR%
echo Proto Source Directory: %PROTO_DIR%
echo C# Output Directory: %OUTPUT_DIR%

if not exist "%OUTPUT_DIR%" (
    echo Creating output directory: %OUTPUT_DIR%
    mkdir "%OUTPUT_DIR%"
)

echo Running protoc...

"%PROTOC_CMD%" -I="%PROTO_DIR%" --csharp_out="%OUTPUT_DIR%" --csharp_opt=file_extension=.g.cs "%PROTO_DIR%\enum.proto" "%PROTO_DIR%\protocol.proto" "%PROTO_DIR%\sprotocol.proto"

if not %errorlevel% == 0 (
    echo C# protoc failed with error code %errorlevel%
    exit /b %errorlevel%
)
echo C# generation completed: %OUTPUT_DIR%

REM C++ generation for UE client. sprotocol.proto is server-internal, excluded.
REM NOTE: protoc.exe / libprotobuf (Client ThirdParty) / generated code are version-locked (3.21.12).
REM       Always upgrade them together.
SET CPP_OUTPUT_DIR=%BASE_DIR%..\..\..\Client\THClient\Source\THProtocol\Public\Generated

if not exist "%CPP_OUTPUT_DIR%" (
    echo Creating output directory: %CPP_OUTPUT_DIR%
    mkdir "%CPP_OUTPUT_DIR%"
)

"%PROTOC_CMD%" -I="%PROTO_DIR%" --cpp_out="%CPP_OUTPUT_DIR%" "%PROTO_DIR%\enum.proto" "%PROTO_DIR%\protocol.proto"
if not %errorlevel% == 0 (
    echo C++ protoc failed with error code %errorlevel%
    exit /b %errorlevel%
)

REM Rename .pb.cc to .pb.cpp so UnrealBuildTool picks them up
for %%f in ("%CPP_OUTPUT_DIR%\*.pb.cc") do move /Y "%%f" "%%~dpnf.cpp" >nul

echo C++ generation completed: %CPP_OUTPUT_DIR%

REM pause
