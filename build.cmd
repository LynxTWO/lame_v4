@echo off
REM Build LAME v4 (64-bit) with VS2022 Build Tools.
REM The stock Makefile.MSVC hardcodes /machine:I386 (line 102); MSVCVER=Win64 leaves
REM the librarian on x86, so we override MACHINE explicitly.
REM
REM IMPORTANT: Makefile.MSVC does NOT track header dependencies. After editing a widely-included
REM header (e.g. util.h, lame_global_flags.h), run a CLEAN build or you get stale objects with
REM mismatched struct layouts -> silent memory corruption (encode ok, crash on exit):
REM     build.cmd clean  &&  build.cmd
setlocal
set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" (
  echo ERROR: vcvars64.bat not found at "%VCVARS%"
  exit /b 1
)
call "%VCVARS%" >nul 2>&1
cd /d "%~dp0"
nmake -f Makefile.MSVC MSVCVER=Win64 ASM=NO "MACHINE=/machine:x64" %*
