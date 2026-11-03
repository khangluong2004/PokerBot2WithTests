#!/bin/sh
# Builds the native benchmark with GCC (bench_gcc.exe, -O3 -march=native). Run from an MSYS2 UCRT64 shell, or with
# /c/msys64/ucrt64/bin on PATH. Needs the clones in ../external (see ../README.md).
set -e
cd "$(dirname "$0")"
EXT=../external
PHE=$EXT/PokerHandEvaluator/cpp/src
mkdir -p obj_gcc
for f in evaluator7 hash hashtable hashtable7 dptables tables_bitwise; do
  gcc -O3 -march=native -DNDEBUG -c "$PHE/$f.c" -o "obj_gcc/$f.o"
done
g++ -O3 -march=native -std=c++17 -DNDEBUG -I"$EXT/OMPEval" -I"$EXT/PokerHandEvaluator/cpp/include" -I"$EXT/senzee" \
  bench.cpp "$EXT/OMPEval/omp/HandEvaluator.cpp" obj_gcc/*.o -o bench_gcc.exe -static
