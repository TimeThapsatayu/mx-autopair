@echo off
rem Logs what Windows has for paired MX devices (changes nothing), then opens the log.
set "EXE=%LOCALAPPDATA%\MxAutoPair\MxAutoPair.exe"
if not exist "%EXE%" set "EXE=%~dp0MxAutoPair.exe"
start "" /wait "%EXE%" --diagnose
start "" notepad "%LOCALAPPDATA%\MxAutoPair\MxAutoPair.log"
