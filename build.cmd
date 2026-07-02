@echo off
REM Build LAME v4 (64-bit) with VS2022 Build Tools.
REM The stock Makefile.MSVC hardcodes /machine:I386 (line 102); MSVCVER=Win64 leaves
REM the librarian on x86, so we override MACHINE explicitly.
setlocal
set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" (
  echo ERROR: vcvars64.bat not found at "%VCVARS%"
  exit /b 1
)
call "%VCVARS%" >nul 2>&1
cd /d "%~dp0"
nmake -f Makefile.MSVC MSVCVER=Win64 ASM=NO "MACHINE=/machine:x64" %*
