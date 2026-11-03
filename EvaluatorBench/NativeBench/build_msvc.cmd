@echo off
rem Builds the native benchmark (bench_msvc.exe) and the Two Plus Two table generator with MSVC (x64, AVX-512).
rem Needs the clones in ..\external (see ..\README.md).
setlocal
cd /d "%~dp0"
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "EXT=..\external"
set "PHE=%EXT%\PokerHandEvaluator\cpp\src"
if not exist obj_msvc mkdir obj_msvc
cl /nologo /O2 /Oi /GL /arch:AVX512 /std:c++17 /EHsc /DNDEBUG /W1 /Foobj_msvc\ ^
   /I"%EXT%\OMPEval" /I"%EXT%\PokerHandEvaluator\cpp\include" /I"%EXT%\senzee" ^
   bench.cpp "%EXT%\OMPEval\omp\HandEvaluator.cpp" ^
   "%PHE%\evaluator7.c" "%PHE%\hash.c" "%PHE%\hashtable.c" "%PHE%\hashtable7.c" "%PHE%\dptables.c" "%PHE%\tables_bitwise.c" ^
   /Fe:bench_msvc.exe /link /LTCG || exit /b 1
set "TPT=%EXT%\TwoPlusTwoHandEvaluator"
cl /nologo /O2 /EHsc /W0 /Foobj_msvc\ "%TPT%\generate_table.cpp" "%TPT%\pokerlib.cpp" "%TPT%\mtrand.cpp" /Fe:generate_table.exe || exit /b 1
