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

if %errorlevel% == 0 (
    echo Protoc completed successfully. Generated files are in %OUTPUT_DIR%
) else (
    echo Protoc failed with error code %errorlevel%
)

REM pause
