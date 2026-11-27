@echo off
setlocal

rem ============================================================
rem  WpsAutostartKiller - one click build  (repo layout)
rem  Only needs the .NET Framework 4.x compiler shipped with Windows.
rem  Notes are kept ASCII on purpose: cmd.exe parses .cmd in the OEM
rem  codepage, so non-ASCII text inside a UTF-8 saved script breaks it.
rem
rem  Layout expected by this script:
rem     src\WpsAutostartKiller.cs     <- compiled
rem     src\WpsLaunchGuard.cs         <- must exist
rem     assets\wps.ico                <- copied into the exe as its icon
rem     dist\WpsAutostartKiller.exe   <- output
rem ============================================================

set "HERE=%~dp0"
set "ROOT=%~dp0.."
set "OUTDIR=%ROOT%\dist"
set "OUTEXE=%OUTDIR%\WpsAutostartKiller.exe"
set "ICON=%ROOT%\assets\wps.ico"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x
    exit /b 1
)
echo [1/4] compiler: "%CSC%"

if not exist "%HERE%WpsAutostartKiller.cs" (
    echo [ERROR] missing src\WpsAutostartKiller.cs
    exit /b 1
)
if not exist "%HERE%WpsLaunchGuard.cs" (
    echo [ERROR] missing src\WpsLaunchGuard.cs
    exit /b 1
)

if exist "%ICON%" (echo [2/4] icon: "%ICON%") else (echo [2/4] [WARN] missing assets\wps.ico - the exe will have no icon)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
if exist "%OUTEXE%" del /q "%OUTEXE%"

echo [3/4] compiling
if not exist "%ICON%" goto noicon

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll ^
  /win32icon:"%ICON%" /out:"%OUTEXE%" "%HERE%WpsAutostartKiller.cs"
goto after

:noicon
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll ^
  /out:"%OUTEXE%" "%HERE%WpsAutostartKiller.cs"

:after
if errorlevel 1 (
    echo.
    echo [FAILED] compile error
    exit /b 1
)
if not exist "%OUTEXE%" (
    echo [FAILED] no exe produced
    exit /b 1
)

echo [4/4] BUILD OK
for %%F in ("%OUTEXE%") do echo     path: %%~fF
for %%F in ("%OUTEXE%") do echo     size: %%~zF bytes
echo.
echo selftest: "%OUTEXE%" --selftest
echo.
echo tip: commit dist\SHA256SUMS.txt together with the exe:
echo      certutil -hashfile "%OUTEXE%" SHA256
exit /b 0
