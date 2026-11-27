@echo off
setlocal

rem ============================================================
rem  WpsAutostartKiller - one click build
rem  Only needs the .NET Framework 4.x compiler shipped with Windows.
rem  Notes are kept ASCII on purpose: cmd.exe parses .cmd in the OEM
rem  codepage, so non-ASCII text inside a UTF-8 saved script breaks it.
rem  Chinese documentation lives in the readme (shi-yong-shuo-ming).
rem ============================================================

set "HERE=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x
    exit /b 1
)
echo [1/3] compiler: "%CSC%"

if not exist "%HERE%assets\WpsLaunchGuard.cs" (
    echo [ERROR] missing assets\WpsLaunchGuard.cs
    exit /b 1
)
if not exist "%HERE%assets\wps.ico" (
    echo [WARN] missing assets\wps.ico - the exe will have no icon
)

if exist "%HERE%WpsAutostartKiller.exe" del /q "%HERE%WpsAutostartKiller.exe"

echo [2/3] compiling
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll /win32icon:"%HERE%assets\wps.ico" /out:"%HERE%WpsAutostartKiller.exe" "%HERE%WpsAutostartKiller.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] compile error
    exit /b 1
)

if not exist "%HERE%WpsAutostartKiller.exe" (
    echo [FAILED] no exe produced
    exit /b 1
)

echo [3/3] BUILD OK
for %%F in ("%HERE%WpsAutostartKiller.exe") do echo     path: %%~fF
for %%F in ("%HERE%WpsAutostartKiller.exe") do echo     size: %%~zF bytes
echo.
echo selftest: WpsAutostartKiller.exe --selftest
exit /b 0
