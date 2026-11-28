@echo off
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [Error] .NET Framework 4.x csc.exe not found.
    pause
    exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:KTLanDrop.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll *.cs
if %errorlevel% neq 0 (
    echo [Error] Build failed.
    pause
    exit /b 1
)
echo [OK] Built KTLanDrop.exe successfully!
