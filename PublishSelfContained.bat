@echo off
title Publish Self-Contained App
echo Publishing Media Sorter as a self-contained, single-file executable...
cd /d "%~dp0MediaSorterApp"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o ..
del /q "..\*.lib" 2>nul
del /q "..\*.pdb" 2>nul
echo.
echo Publication complete. The app is in the main folder: double-click MediaSorterApp.exe
echo The exe, the Models folder and the DLLs must travel together - no .NET required on the target PC.
pause
