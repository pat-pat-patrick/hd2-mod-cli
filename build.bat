@echo off
rem Tests the core, then builds release\hd2qd.exe (the command line) and
rem release\HD2 Mod Quick Deploy.exe (the window). Both need only the .NET
rem Framework 4.8 that Windows 10 (1903+) and 11 have. Building needs the .NET
rem SDK (8 or later): dotnet on PATH, or set DOTNET to its dotnet.exe.
rem
rem Nexus Mods (signing in, downloads, the nxm:// handler, update checks) is in the
rem build where official.txt sits beside this file, or when asked for with
rem --with-nexus. official.txt is not in the repository, so a build made from the
rem source anywhere else leaves Nexus Mods out by default and identifies itself to
rem the API as "(unofficial build)". Signing in needs the client id Nexus Mods
rem issues to a registered application, read from nexus_client_id.txt beside this
rem file (not in the repository either): a build you publish registers its own
rem application, its own name and its own client id.
rem
rem   build.bat                the whole program (with Nexus Mods where
rem                            official.txt is beside this file)
rem   build.bat --no-nexus     without Nexus Mods even here. It lands in
rem                            release\no-nexus\.
rem   build.bat --with-nexus   with Nexus Mods without official.txt: for anyone
rem                            building this themselves, who should put their own
rem                            name in Nexus.AppName and register it.
cd /d "%~dp0"
if "%DOTNET%"=="" set DOTNET=dotnet
set OUT=release
set DEFS=
set NAME=the whole program
if /i "%~1"=="--no-nexus" (
  set OUT=release\no-nexus
  set DEFS=-p:NoNexus=true -p:BaseOutputPath=bin\no-nexus\ -p:BaseIntermediateOutputPath=obj\no-nexus\
  set NAME=without Nexus Mods
)
if /i "%~1"=="--with-nexus" (
  set DEFS=-p:WithNexus=true
  set NAME=with Nexus Mods
)
"%DOTNET%" test tests\Hd2Mod.Tests.csproj || exit /b 1
"%DOTNET%" build cli\hd2qd.csproj -c Release %DEFS% || exit /b 1
"%DOTNET%" build gui\QuickDeploy\QuickDeploy.csproj -c Release %DEFS% || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
if not "%OUT%"=="release\no-nexus" (
  copy /y "cli\bin\Release\net48\hd2qd.exe" "%OUT%\hd2qd.exe" >nul || exit /b 1
  copy /y "gui\QuickDeploy\bin\Release\net48\HD2 Mod Quick Deploy.exe" "%OUT%\HD2 Mod Quick Deploy.exe" >nul || exit /b 1
) else (
  copy /y "cli\bin\no-nexus\Release\net48\hd2qd.exe" "%OUT%\hd2qd.exe" >nul || exit /b 1
  copy /y "gui\QuickDeploy\bin\no-nexus\Release\net48\HD2 Mod Quick Deploy.exe" "%OUT%\HD2 Mod Quick Deploy.exe" >nul || exit /b 1
)
echo.
echo Built (%NAME%): %OUT%\hd2qd.exe and %OUT%\HD2 Mod Quick Deploy.exe
