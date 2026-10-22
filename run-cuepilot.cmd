@echo off
setlocal

REM Always run from repository root (folder of this file).
cd /d "%~dp0"

REM Prefer local .NET 8 SDK path if present.
if exist "%USERPROFILE%\.dotnet8" (
  set "PATH=%USERPROFILE%\.dotnet8;%PATH%"
)

echo [CuePilot] Repo root: %CD%
if exist "secrets.txt" (
  echo [CuePilot] secrets.txt found: API key will be read from it.
) else if not "%POLZA_API_KEY%"=="" (
  echo [CuePilot] POLZA_API_KEY detected in environment.
) else (
  echo [CuePilot] No secrets.txt and no POLZA_API_KEY.
  echo [CuePilot] You can still set the key in app: Profile ^> Settings ^> API key.
)
echo.

REM Avoid file-lock errors when previous app instance is still running.
tasklist /FI "IMAGENAME eq CuePilotDesktop.exe" | find /I "CuePilotDesktop.exe" >nul
if %ERRORLEVEL%==0 (
  echo [CuePilot] Existing CuePilotDesktop process detected. Restarting instance...
  taskkill /IM CuePilotDesktop.exe /F >nul 2>nul
  taskkill /IM DesktopCapture.exe /F >nul 2>nul
  ping 127.0.0.1 -n 2 >nul
)

REM The desktop app starts the prebuilt DesktopCapture.exe, so it must be rebuilt after code changes.
echo [CuePilot] Building DesktopCapture...
dotnet build ".\apps\DesktopCapture\DesktopCapture.csproj" -c Debug -v q --nologo
if not "%ERRORLEVEL%"=="0" (
  echo [CuePilot] DesktopCapture build failed.
  pause
  exit /b 1
)

echo [CuePilot] Starting desktop app...
dotnet run --project ".\apps\CuePilotDesktop\CuePilotDesktop.csproj"
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
  echo.
  echo [CuePilot] Launch failed with code %EXIT_CODE%.
  echo [CuePilot] If app was already running, close it and run again.
  pause
)

exit /b %EXIT_CODE%
