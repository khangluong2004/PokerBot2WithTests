# Evaluator benchmark

Benchmarks PokerBot2's 7-card evaluators against published ones on the same random 7-card hands. The results, references and summaries are in [PokerBot2/README.md](../PokerBot2/README.md#comparison-with-other-evaluators).

| Folder | What |
|---|---|
| `DotnetBench/` | .NET side: PokerBot2 (`EvalHandRank`, `EvalHandValue`, `EvalHandValues`) and poker.net. Also writes the shared hands and the reference ranks, and runs the showdown benchmark |
| `NativeBench/` | C/C++ side: OMPEval, Two Plus Two, PokerHandEvaluator (phevaluator) and Senzee's 7-card index. Built with MSVC and with GCC |
| `RankKeySearch/` | The search that found `RankTables.RANK_KEYS`: keys that make `EvalHandRank`'s `Ranks` + `RowOffsets` smallest, with `RankTables`' build unchanged. Its log is `RankKeySearch/search-2026-11-07.txt`, and the method and results are in [PokerBot2/README.md](../PokerBot2/README.md#searching-for-better-rank_keys) |
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

## Rank key search

`RankTables.RANK_KEYS` and `ROW_SHIFT` come from this search: `Ranks` + `RowOffsets` went from 250,888 B (the greedy keys, rows of 1,024) to 167,504 B (rows of 2,048). How it works and what it found are in [PokerBot2/README.md](../PokerBot2/README.md#searching-for-better-rank_keys).

```bash
dotnet run -c Release --project RankKeySearch -- verify                # the fast layout = RankTables.Build's loop, and the real tables
dotnet run -c Release --project RankKeySearch -- greedy                # the greedy keys at shifts 8-12
dotnet run -c Release --project RankKeySearch -- beam --shift 10       # beam search, rank by rank
dotnet run -c Release --project RankKeySearch -- local --shift 10 --minutes 10 --from 0,1,5,...  # local search with annealing
dotnet run -c Release --project RankKeySearch -- measure --shift 11 0,7,35,...  # size and lookup speed against the greedy and RankTables' keys
dotnet run -c Release --project RankKeySearch -- all --minutes 12      # all of it at shifts 10, 9 and 11, plus a second pass (about 75 min)
```

The keys used now came from `all --minutes 12`, then `local --shift 11 --minutes 8 --seed 11 --from <its best keys at shift 11>`, on 24 threads. `RankKeySearch/search-2026-11-07.txt` is the log of those runs. The searches start from the greedy keys, not from `RankTables`' keys, so `all` redoes the search from scratch. The runs stop after a set time, so a rerun won't retrace them exactly.

The output also goes to `data/rank-key-search.log`. The objective is the bytes of `Ranks` (2 per entry) plus `RowOffsets` (4 per row), as `RankTables.Build` would make them: the same rank sums, rows and first fit. Only the keys (and the row size, `--shift`) change. After changing `RANK_KEYS`, `MAX_RANK_KEY` or `ROW_SHIFT`, run `verify` and the exhaustive tests.
