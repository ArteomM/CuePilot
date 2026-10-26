@echo off
rem Builds build\installer\CuePilot-Setup-<version>.exe:
rem   1) publishes CuePilotDesktop and DesktopCapture self-contained (no .NET install needed on the target PC),
rem   2) adds whisper.cpp (the speech model is downloaded later from the app Settings),
rem   3) packs everything into payload.zip and embeds it into the setup stub (build\setup-builder).
rem secrets.txt is never packed: the installed app reads the key from Settings or %AppData%\CuePilot\secrets.txt.
setlocal EnableExtensions
cd /d "%~dp0.."

if exist "%USERPROFILE%\.dotnet8" set "PATH=%USERPROFILE%\.dotnet8;%PATH%"
where dotnet >nul 2>nul || (echo [build] .NET SDK not found. & goto :fail)

set "VERSION=1.0.5"
set "ROOT=%CD%"
set "STAGE=%ROOT%\build\installer\stage"
set "PAYLOAD=%ROOT%\build\setup-builder\payload.zip"
set "SETUP=%ROOT%\build\installer\CuePilot-Setup-%VERSION%.exe"

echo [build] 1/6 Cleaning
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%PAYLOAD%" del /f /q "%PAYLOAD%"

echo [build] 2/6 Publishing CuePilotDesktop (self-contained, win-x64)
dotnet publish "apps\CuePilotDesktop\CuePilotDesktop.csproj" -c Release -r win-x64 --self-contained true -o "%STAGE%" -v q --nologo || goto :fail

echo [build] 3/6 Publishing DesktopCapture (self-contained, win-x64)
dotnet publish "apps\DesktopCapture\DesktopCapture.csproj" -c Release -r win-x64 --self-contained true -o "%STAGE%" -v q --nologo || goto :fail
del /f /q "%STAGE%\*.pdb" >nul 2>nul

echo [build] 4/6 Adding whisper.cpp
if not exist "tools\whisper.cpp\Release\whisper-cli.exe" (echo [build] tools\whisper.cpp\Release\whisper-cli.exe is missing. & goto :fail)
rem Speech models are not packed: the app downloads them from Settings into %LocalAppData%\CuePilot\models.
mkdir "%STAGE%\tools\whisper.cpp\Release"
copy /y "tools\whisper.cpp\Release\whisper-cli.exe" "%STAGE%\tools\whisper.cpp\Release\" >nul || goto :fail
copy /y "tools\whisper.cpp\Release\whisper-server.exe" "%STAGE%\tools\whisper.cpp\Release\" >nul || goto :fail
copy /y "tools\whisper.cpp\Release\*.dll" "%STAGE%\tools\whisper.cpp\Release\" >nul || goto :fail

echo [build] 5/6 Packing payload.zip
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory($env:STAGE, $env:PAYLOAD, [IO.Compression.CompressionLevel]::Optimal, $false)" || goto :fail

echo [build] 6/6 Building the setup
dotnet build "build\setup-builder\SetupCuePilot.csproj" -c Release -v q --nologo || goto :fail
copy /y "build\setup-builder\bin\Release\net48\SetupCuePilot.exe" "%SETUP%" >nul || goto :fail

echo.
echo [build] Done: %SETUP%
exit /b 0

:fail
echo.
echo [build] FAILED
exit /b 1
