@echo off
setlocal enabledelayedexpansion
title DRPS STATUS
color 0A
set SYS32=%WINDIR%\System32
for /f "tokens=1-2 delims= " %%a in ('time /t') do set STARTUP_TIME=%%a %%b
echo   [STATUS CHECK] %DATE% %STARTUP_TIME%
echo.
echo   DRPS  ^|  OctoLux AI
echo   --------------------------------
echo.

set REPO=%~dp0

:: -- Backup (safe, read-only w.r.t. DRPS itself) --
echo   [BACKUP]  Syncing source to D:\Backups\Source\DRPS...
"%SYS32%\robocopy.exe" "C:\Users\kent\source\repos\DRPS" "D:\Backups\Source\DRPS" /MIR /XD ".git" "bin" "obj" "publish" /XF "*.user" /NFL /NDL /NJH /NJS >nul
if "%ERRORLEVEL%" LEQ "7" (
    echo   [BACKUP]  Done.
) else (
    echo   [BACKUP]  WARNING: robocopy exited with code %ERRORLEVEL% - check D:\Backups\Source\
)
echo.

:: -- Git status (uncommitted work check) --
echo   [GIT]     Checking for uncommitted changes...
pushd "%REPO%"
"%SYS32%\where.exe" git >nul 2>&1
if errorlevel 1 (
    echo   [GIT]     git not found on PATH - skipping.
) else (
    for /f %%i in ('git status --porcelain ^| find /c /v ""') do set DIRTY_COUNT=%%i
    if "!DIRTY_COUNT!"=="0" (
        echo   [GIT]     Working tree clean.
    ) else (
        echo   [GIT]     WARNING: !DIRTY_COUNT! uncommitted change^(s^) - review before proceeding.
    )
)
popd
echo.

:: -- Scheduled task check (Ingestion/Calculator/Gate) --
echo   [SCHEDULED TASKS]
"%SYS32%\schtasks.exe" /query /TN "DRPS-Ingestion" >nul 2>&1
if errorlevel 1 (echo   [WARN]    DRPS-Ingestion task not found.) else (echo   [OK]      DRPS-Ingestion task exists.)
"%SYS32%\schtasks.exe" /query /TN "DRPS-Calculator" >nul 2>&1
if errorlevel 1 (echo   [WARN]    DRPS-Calculator task not found.) else (echo   [OK]      DRPS-Calculator task exists.)
"%SYS32%\schtasks.exe" /query /TN "DRPS-Gate" >nul 2>&1
if errorlevel 1 (echo   [WARN]    DRPS-Gate task not found.) else (echo   [OK]      DRPS-Gate task exists.)
echo.

:: -- Is Adjuster/Execution currently running? --
:: NOTE (fixed 2026-08-04): the original WINDOWTITLE-based tasklist filter never matches a
:: plain `dotnet run --project X` process, since that neither sets nor inherits a custom
:: console window title - confirmed empirically (started Drps.Execution for real, the old
:: check still reported [STOPPED]). Replaced with a command-line substring match via
:: PowerShell's Get-CimInstance Win32_Process, which sees the real argv regardless of window
:: title and matches both the `dotnet.exe ... run --project Drps.Execution` launcher process
:: and its child apphost (Drps.Execution.exe), or a directly-run published .exe.
::
:: Filter is restricted to Name='dotnet.exe' (or the published exe's own name) rather than
:: matching the substring against every process's command line - also confirmed empirically
:: necessary: an unrestricted CommandLine-only match matches the PowerShell query's OWN
:: command line (which itself contains the literal text "Drps.Adjuster"/"Drps.Execution"),
:: producing a guaranteed false-positive RUNNING result on every single check regardless of
:: whether anything is actually running.
echo   [RUNNING PROCESSES]
"%SYS32%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "if (Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*Drps.Adjuster*') -or ($_.Name -like 'Drps.Adjuster*') }) { exit 0 } else { exit 1 }"
if errorlevel 1 (echo   [STOPPED] Drps.Adjuster is not running.) else (echo   [RUNNING] Drps.Adjuster appears to be running.)
"%SYS32%\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "if (Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*Drps.Execution*') -or ($_.Name -like 'Drps.Execution*') }) { exit 0 } else { exit 1 }"
if errorlevel 1 (echo   [STOPPED] Drps.Execution is not running.) else (echo   [RUNNING] Drps.Execution appears to be running.)
echo.

:: -- IsDryRunEnabled / KillSwitchMaxOpensPerDay, read directly from appsettings.json --
echo   [EXECUTION CONFIG]  Drps.Execution/appsettings.json
set CONFIG=%REPO%Drps.Execution\appsettings.json
if exist "%CONFIG%" (
    "%SYS32%\findstr.exe" /i "IsDryRunEnabled" "%CONFIG%"
    "%SYS32%\findstr.exe" /i "KillSwitchMaxOpensPerDay" "%CONFIG%"
) else (
    echo   [WARN]    %CONFIG% not found - skipping.
)
echo.

echo   --------------------------------
echo   This script does NOT start Drps.Adjuster or Drps.Execution.
echo   Run those manually, deliberately, after reviewing the above:
echo     dotnet run --project Drps.Adjuster
echo     dotnet run --project Drps.Execution
echo   --------------------------------
endlocal
pause
