@echo off
rem Builds UltrawidePatcher.exe with the C# compiler that ships with .NET Framework 4.x
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:src\icon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:dist\UltrawidePatcher.exe src\UltrawidePatcher.cs
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Built dist\UltrawidePatcher.exe
pause
