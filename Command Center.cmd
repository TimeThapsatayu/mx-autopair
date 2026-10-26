@echo off
rem Starts the MxAutoPair command center (this PC only) and opens it in your browser.
rem Close this window to stop it.
title MxAutoPair Command Center
dotnet run --project "%~dp0commander" -c Release
pause
