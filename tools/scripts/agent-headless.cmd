@echo off
rem MCP server SWORD (stdio) for agents: builds Sword.Headless quietly (build output goes to stderr, stdout is the
rem protocol) into a fresh folder of its own, artifacts\agent-bin\headless\<run>, then starts it with --agent. A running
rem agent never locks the normal bin\ that dotnet build and verify.ps1 write, nor the folder of the next agent session.
rem A failed build is a failure: the agent must not silently run an older build than the working tree.
rem Folders of finished sessions are removed: a folder written to in the last 15 minutes is left alone (another launch
rem may be building into it right now), and of the older ones only those whose exe is not running (a running exe is
rem locked). Every command here keeps stdout clean: del prints the name of a locked file to stdout, which would break
rem the protocol.
rem Used by .mcp.json; see docs/agent-playbook.md.
setlocal
set "ROOT=%~dp0..\.."
set "BASE=%ROOT%\artifacts\agent-bin\headless"
if exist "%BASE%" powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$fresh = (Get-Date).AddMinutes(-15); Get-ChildItem -LiteralPath $env:BASE -Directory | ForEach-Object { if ($_.LastWriteTime -gt $fresh) { return }; $exe = Join-Path $_.FullName 'Sword.Headless.exe'; try { if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -ErrorAction Stop } } catch { return }; Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }" >nul 2>&1
rem Former layout (files straight in headless\): removed the same way, only when its exe is not running.
if exist "%BASE%\Sword.Headless.exe" del /q "%BASE%\Sword.Headless.exe" >nul 2>&1
if exist "%BASE%\Sword.Headless.dll" if not exist "%BASE%\Sword.Headless.exe" del /q "%BASE%\*.*" >nul 2>&1
:pick
set "OUT=%BASE%\%RANDOM%%RANDOM%"
if exist "%OUT%" goto pick
dotnet build "%ROOT%\game\Sword.Game.Headless\Sword.Game.Headless.csproj" -c Debug -nologo -v q -o "%OUT%" 1>&2
if errorlevel 1 (
    echo [agent] build failed: not starting an older build ^(fix the build, then reconnect the MCP server^) 1>&2
    rd /s /q "%OUT%" >nul 2>&1
    exit /b 1
)
"%OUT%\Sword.Headless.exe" --agent %*
