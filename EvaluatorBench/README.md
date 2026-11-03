# Evaluator benchmark

Benchmarks PokerBot2's 7-card evaluators against published ones on the same random 7-card hands. The results, references and summaries are in [PokerBot2/README.md](../PokerBot2/README.md#comparison-with-other-evaluators).

| Folder | What |
|---|---|
| `DotnetBench/` | .NET side: PokerBot2 (`EvalHandRank`, `EvalHandValue`, `EvalHandValues`) and poker.net. Also writes the shared hands and the reference ranks, and runs the showdown benchmark |
| `NativeBench/` | C/C++ side: OMPEval, Two Plus Two, PokerHandEvaluator (phevaluator) and Senzee's 7-card index. Built with MSVC and with GCC |
| `external/` | The third-party code, cloned or downloaded (not committed, see `.gitignore`) |
| `data/` | Generated hands, reference ranks and the Two Plus Two table (not committed) |

Every evaluator gets the same 2,000,000 random hands (`data/hands7.bin`, 7 bytes per hand, card = `rank * 4 + suit`). Each one's own card format is prepared before timing (a relabeling of the 52 cards), and building the hand from its 7 cards is inside the timed loop. Before timing, each result is checked against PokerBot2's ranks (`data/ranks7.bin`): equal ranks must get equal results, and better ranks strictly better results.

## Get the third-party code

From this folder:

```bash
git clone --depth 1 https://github.com/JBelthoff/poker.net external/poker.net
git clone --depth 1 https://github.com/zekyll/OMPEval external/OMPEval
git clone --depth 1 https://github.com/tangentforks/TwoPlusTwoHandEvaluator external/TwoPlusTwoHandEvaluator
git clone --depth 1 https://github.com/HenryRLee/PokerHandEvaluator external/PokerHandEvaluator
mkdir -p external/senzee
curl -L -o external/senzee/index52c7.h "https://drive.google.com/uc?export=download&id=10H2sb1Uz559Rv7t6FuR2HGi2LGi8too4"
```

Measured with poker.net `c00f7f4`, OMPEval `4aec210`, TwoPlusTwoHandEvaluator `f324fd8` and PokerHandEvaluator `10be452`. `index52c7.h` is the file linked from [Senzee's blog post](https://senzee.blogspot.com/2007/01/7.html).

## Run

```bash
dotnet run -c Release --project DotnetBench -- gen        # data/hands7.bin and data/ranks7.bin
dotnet run -c Release --project DotnetBench -- bench      # .NET evaluators
dotnet run -c Release --project DotnetBench -- showdown   # PokerBot2 at a 2 / 6 / 9-player showdown, old vs new
dotnet run -c Release --project DotnetBench -- cold       # one showdown at a time, after evicting the caches
```

`cold` times single showdowns after evicting the caches: hot, after reading 4 MB (out of L2), after reading 64 MB (out of L3), or out of L3 and then the code warmed again. It takes about 40 s; see `DotnetBench/ColdBench.cs`.

Native side, Windows:

```bat
NativeBench\build_msvc.cmd
cd data && ..\NativeBench\generate_table.exe && cd ..
NativeBench\bench_msvc.exe data
```

`generate_table.exe` is Two Plus Two's own table generator. It writes `HandRanks.dat` (130 MB) into the current folder in about 4 s.

GCC (MSYS2 UCRT64, `pacman -S mingw-w64-ucrt-x86_64-gcc`):

```bash
PATH="/c/msys64/ucrt64/bin:$PATH" NativeBench/build_gcc.sh
NativeBench/bench_gcc.exe data
```

The Senzee table (267 MB) is filled by the native benchmark itself, from OMPEval, in under a second. The author's generator was lost.
