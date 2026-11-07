using System.Diagnostics;
using System.Reflection;
using PokerBot2.GameSpace;
using RankKeySearch;

// Searches for RANK_KEYS that make RankTables' Ranks + RowOffsets smaller (see ..\README.md). Everything else stays as
// RankTables.Build does it: rank sum = sum of count[r] * key[r] over the 7 cards, rows of 2^shift sums, rows with the
// most sums placed first, each at its first-fit offset. RankTables' keys came from this search (all --minutes 12, then
// local --shift 11 --minutes 8 --seed 11 from the best; the log is in search-2026-11-07.txt). The searches start from
// the greedy keys, so they redo the search from scratch; pass --from to start from other keys.
//   verify                      the fast layout gives the same tables as RankTables.Build's own loop, and as the real tables
//   greedy                      greedy keys, with RankTables' rule and with the exact completion rule, at shifts 8-12
//   beam   [--shift 10] [--width 16] [--branch 3] [--jumps 5,20,50,100] beam search, rank by rank, greedy rollouts
//   local  [--shift 10] [--minutes 10] [--relaxed] [--from k0,...,k12]  local beam search with annealing
//   measure [--shift 10] k0,...,k12 [...]   size, cache lines and lookup speed, against the greedy and RankTables' keys
//   sizes  k0,...,k12 [...]     bytes of each key set at shifts 8-12
//   all    [--minutes 10]       greedy, beam and local search at shifts 10, 9, 11, a second pass from every shift's best,
//                               then measure the best
// Output also goes to ..\data\rank-key-search.log.
var options = new Options(args);
string logPath = Path.Combine(FindBenchDir(), "data", "rank-key-search.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath));
using var logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };
var log = new Tee(Console.Out, logFile);
log.WriteLine($"\n=== {DateTime.Now:yyyy-MM-dd HH:mm} rank-key-search {string.Join(' ', args)}");

var tablesType = typeof(Game).Assembly.GetType("PokerBot2.GameSpace.RankTables")!;
int[] currentKeys = (int[])tablesType.GetField("RANK_KEYS", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
int currentShift = (int)tablesType.GetField("ROW_SHIFT", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
// The greedy keys (what `greedy` computes) with rows of 1,024: RankTables' layout before the search, and where it starts
int[] greedyKeys = [0, 1, 5, 22, 98, 453, 2031, 8698, 22854, 83661, 262349, 636345, 1479181];
const int greedyShift = 10;
// What `measure` and `sizes` compare against: the greedy keys, and RankTables' keys if they're different
var baselines = new List<(int[] keys, int shift)> { (greedyKeys, greedyShift) };
if (!currentKeys.SequenceEqual(greedyKeys) || currentShift != greedyShift)
{
    baselines.Add((currentKeys, currentShift));
}
log.WriteLine($"{Multisets.Count:N0} rank multisets, {Multisets.NumValues:N0} values; RankTables' keys [{Objective.Format(currentKeys)}], shift {currentShift}");

switch (options.Mode)
{
    case "verify":
        Verify();
        break;
    case "greedy":
        GreedyKeys();
        break;
    case "beam":
        BeamSearch.Run(options.Shift, options.Width, options.Branch, options.Jumps, true, log);
        break;
    case "local":
        LocalSearch.Run(options.From ?? GreedyStarts(), options.Shift, options.Relaxed, TimeSpan.FromMinutes(options.Minutes),
            options.Width, options.Children, options.Seed, log);
        break;
    case "measure":
        Measure(options.KeySets, options.Shift);
        break;
    case "sizes":
        Sizes(options.KeySets);
        break;
    case "all":
        All();
        break;
    default:
        log.WriteLine($"Unknown mode {options.Mode}");
        break;
}

void Verify()
{
    var sums = new int[Multisets.Count];
    var layout = new Layout();
    var ranks = (ushort[])tablesType.GetField("Ranks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var rowOffsets = (int[])tablesType.GetField("RowOffsets", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    Multisets.Sums(currentKeys, sums, out int maxSum);
    // The real tables against the fast layout of RankTables' keys and shift: the same size and every row offset equal
    var fastOffsets = new int[(maxSum >> currentShift) + 1];
    var fastSize = layout.Build(sums, maxSum, currentShift, fastOffsets);
    bool same = fastSize.RanksEntries == ranks.Length && fastOffsets.AsSpan().SequenceEqual(rowOffsets);
    log.WriteLine($"RankTables: Ranks {ranks.Length:N0}, RowOffsets {rowOffsets.Length:N0}; strict-valid {layout.IsValid(sums, false)}; "
        + $"fast layout {fastSize.RanksEntries:N0} + {fastOffsets.Length:N0} rows, {(same ? "same tables" : "DIFFERENT")}");
    var keySets = new List<int[]> { currentKeys, Greedy.Complete([0], 1, true) };
    var random = new Random(1);
    while (keySets.Count < 6)
    {
        // Scaled greedy keys: other layouts, still all-distinct sums
        double factor = 1 + random.NextDouble();
        int[] k = [.. greedyKeys.Select(x => (int)(x * factor))];
        if (Multisets.Sums(k, sums, out _) && layout.IsValid(sums, false))
        {
            keySets.Add(k);
        }
    }
    foreach (var keys in keySets)
    {
        Multisets.Sums(keys, sums, out maxSum);
        foreach (int shift in new[] { 8, 10, 12, currentShift }.Distinct())
        {
            var sw = Stopwatch.StartNew();
            var fast = layout.Build(sums, maxSum, shift);
            double fastMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var reference = Layout.BuildReference(sums, maxSum, shift);
            log.WriteLine($"  shift {shift,2}: fast {fast.RanksEntries,7:N0} + {fast.NumRows,6:N0} rows ({fastMs,6:F1} ms), "
                + $"reference {reference.RanksEntries,7:N0} + {reference.NumRows,6:N0} rows ({sw.Elapsed.TotalMilliseconds,6:F0} ms) "
                + $"{(fast == reference ? "same" : "DIFFERENT")}  [{Objective.Format(keys)}]");
        }
    }
}

void GreedyKeys()
{
    foreach (bool exact in new[] { false, true })
    {
        var sw = Stopwatch.StartNew();
        var keys = Greedy.Complete([0], 1, exact);
        log.WriteLine($"greedy, {(exact ? "exact completion" : "RankTables' rule")}: [{Objective.Format(keys)}] ({sw.Elapsed.TotalSeconds:F1} s)");
        foreach (int shift in new[] { 8, 9, 10, 11, 12 })
        {
            var size = Objective.Size(keys, shift, false).Value;
            log.WriteLine($"  shift {shift,2}: Ranks {size.RanksEntries,7:N0} + RowOffsets {size.NumRows,6:N0} = {size.Bytes,9:N0} B");
        }
    }
}

List<int[]> GreedyStarts() => [Greedy.Complete([0], 1, true)];

// The baselines, then each key set at `shift`: ns per lookup, hot (throughput), as a dependent chain (latency), and
// with other data taking most of the L2
void Measure(List<int[]> keySets, int shift)
{
    log.WriteLine($"{"shift",5} {"keys",-92} {"bytes",9} {"lines",6} {"hot",7} {"chain",7} {"L2 full",8}");
    foreach (var (keys, s) in baselines.Concat(keySets.Select(k => (k, shift))))
    {
        var result = LookupBench.Run(keys, s);
        log.WriteLine($"{s,5} {Objective.Format(keys),-92} {result.Bytes,9:N0} {result.CacheLines,6:N0} {result.HotNs,6:F2}  {result.ChainNs,6:F2}  {result.PressureNs,7:F2}");
    }
}

// Bytes of each key set at shifts 8-12
void Sizes(List<int[]> keySets)
{
    foreach (var keys in baselines.Select(b => b.keys).Concat(keySets))
    {
        var cells = new[] { 8, 9, 10, 11, 12 }.Select(shift => $"{shift}: {Objective.Bytes(keys, shift, false),9:N0}");
        log.WriteLine($"[{Objective.Format(keys)}]  {string.Join("  ", cells)}");
    }
}

void All()
{
    GreedyKeys();
    var best = new List<(int shift, int[] keys, int bytes)>();
    foreach (int shift in new[] { 10, 9, 11 })
    {
        log.WriteLine($"\n--- shift {shift}: beam search (width {options.Width}, branch {options.Branch}, jumps {string.Join(",", options.Jumps)}%)");
        var (beamKeys, beamBytes) = BeamSearch.Run(shift, options.Width, options.Branch, options.Jumps, true, log);
        // Starts: the greedy keys, the beam's result, and the best keys found at the other shifts
        var starts = GreedyStarts().Append(beamKeys).Concat(best.Select(b => b.keys)).ToList();
        log.WriteLine($"--- shift {shift}: local search, strict ({options.Minutes} min)");
        var (localKeys, localBytes) = LocalSearch.Run(starts, shift, false, TimeSpan.FromMinutes(options.Minutes), options.Width,
            options.Children, options.Seed, log);
        log.WriteLine($"--- shift {shift}: local search, relaxed: multisets with the same value may share a sum ({options.Minutes / 4} min)");
        var (relaxedKeys, relaxedBytes) = LocalSearch.Run([localKeys], shift, true, TimeSpan.FromMinutes(options.Minutes / 4), options.Width,
            options.Children, options.Seed + 1, log);
        best.Add((shift, localKeys, localBytes));
        log.WriteLine($"--- shift {shift}: greedy keys {Objective.Bytes(greedyKeys, shift, false):N0} B, beam {beamBytes:N0} B, local {localBytes:N0} B, relaxed {relaxedBytes:N0} B");
        if (relaxedBytes < localBytes)
        {
            log.WriteLine($"    relaxed keys [{Objective.Format(relaxedKeys)}]");
        }
    }
    // Second pass: keys tuned for one shift can do better at another, so each shift starts again from every shift's best
    var firstPass = best.ToList();
    best.Clear();
    foreach (var (shift, _, _) in firstPass)
    {
        log.WriteLine($"\n--- shift {shift}: second pass, from the best keys of every shift ({options.Minutes / 2} min)");
        var (keys, bytes) = LocalSearch.Run(firstPass.Select(f => f.keys), shift, false, TimeSpan.FromMinutes(options.Minutes / 2),
            options.Width, options.Children, options.Seed + 2, log);
        best.Add((shift, keys, bytes));
    }
    log.WriteLine();
    foreach (var (shift, keys, bytes) in best)
    {
        var size = Objective.Size(keys, shift, false).Value;
        log.WriteLine($"best at shift {shift}: Ranks {size.RanksEntries:N0} + RowOffsets {size.NumRows:N0} = {bytes:N0} B [{Objective.Format(keys)}]");
    }
    foreach (var (shift, keys, _) in best)
    {
        Measure([keys], shift);
    }
}

static string FindBenchDir()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (dir.Name == "EvaluatorBench")
            {
                return dir.FullName;
            }
        }
    }
    throw new DirectoryNotFoundException("Run from inside EvaluatorBench");
}

sealed class Options
{
    public string Mode = "all";
    public int Shift = 10, Width = 16, Branch = 3, Children = 24, Seed = 1;
    public int[] Jumps = [5, 20, 50, 100];
    public double Minutes = 10;
    public bool Relaxed;
    public List<int[]> From;
    public List<int[]> KeySets = [];

    public Options(string[] args)
    {
        static int[] Keys(string text) => [.. text.Split(',').Select(int.Parse)];
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shift": Shift = int.Parse(args[++i]); break;
                case "--width": Width = int.Parse(args[++i]); break;
                case "--branch": Branch = int.Parse(args[++i]); break;
                case "--children": Children = int.Parse(args[++i]); break;
                case "--seed": Seed = int.Parse(args[++i]); break;
                case "--jumps": Jumps = Keys(args[++i]); break;
                case "--minutes": Minutes = double.Parse(args[++i]); break;
                case "--relaxed": Relaxed = true; break;
                case "--from": (From ??= []).Add(Keys(args[++i])); break;
                default:
                    if (i == 0)
                    {
                        Mode = args[i];
                    }
                    else
                    {
                        KeySets.Add(Keys(args[i]));
                    }
                    break;
            }
        }
    }
}

// Writes to the console and the log file
sealed class Tee(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string value) { a.WriteLine(value); b.WriteLine(value); }
}
