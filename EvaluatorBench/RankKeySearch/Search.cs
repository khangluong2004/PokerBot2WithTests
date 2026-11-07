using System.Collections.Concurrent;
using System.Diagnostics;

namespace RankKeySearch
{
    // Objective: bytes of Ranks + RowOffsets that RankTables.Build would make with these keys
    static class Objective
    {
        public const int Invalid = int.MaxValue;
        static readonly ThreadLocal<(int[] sums, Layout layout)> scratch = new(() => (new int[Multisets.Count], new Layout()));

        public static int Bytes(int[] keys, int shift, bool relaxed) => Size(keys, shift, relaxed)?.Bytes ?? Invalid;

        public static Layout.Size? Size(int[] keys, int shift, bool relaxed)
        {
            var (sums, layout) = scratch.Value;
            if (!Multisets.Sums(keys, sums, out int maxSum) || !layout.IsValid(sums, relaxed))
            {
                return null;
            }
            return layout.Build(sums, maxSum, shift);
        }

        public static string Format(int[] keys) => string.Join(", ", keys);
    }

    // Beam search over the keys, rank by rank. Where the greedy takes the smallest valid key, each beam entry here
    // branches into the smallest few valid keys plus a few jumps (the first valid key some percent above the smallest).
    // Each child is scored by completing it greedily and building the real table; the best `width` children go on.
    static class BeamSearch
    {
        sealed record Entry(int[] Prefix, PartialSums State, int Score, int[] Rollout);

        public static (int[] keys, int bytes) Run(int shift, int width, int branch, int[] jumpPercents, bool exactCompletion, TextWriter log)
        {
            var sw = Stopwatch.StartNew();
            var beam = new List<Entry> { new([0], PartialSums.Start(0, exactCompletion), 0, null) };
            int[] bestKeys = null;
            int bestBytes = Objective.Invalid;
            for (int rank = 1; rank < Multisets.NumRanks; rank++)
            {
                var children = new ConcurrentBag<Entry>();
                Parallel.ForEach(beam.SelectMany(entry => Candidates(entry, branch, jumpPercents, exactCompletion)
                    .Select(key => (entry, key))).ToArray(), item =>
                {
                    var (entry, key) = item;
                    int[] prefix = [.. entry.Prefix, key];
                    var state = rank < Multisets.NumRanks - 1 ? entry.State.Add(key, exactCompletion) : null;
                    var rollout = state != null ? Greedy.Complete(state, prefix, exactCompletion) : prefix;
                    children.Add(new Entry(prefix, state, Objective.Bytes(rollout, shift, false), rollout));
                });
                beam = children.Where(c => c.Score != Objective.Invalid)
                    .GroupBy(c => Objective.Format(c.Prefix)).Select(g => g.First())
                    .OrderBy(c => c.Score).Take(width).ToList();
                if (beam[0].Score < bestBytes)
                {
                    bestBytes = beam[0].Score;
                    bestKeys = beam[0].Rollout;
                }
                log.WriteLine($"  rank {rank,2}: {children.Count,4} children, best rollout {beam[0].Score,7:N0} B ({sw.Elapsed.TotalSeconds,5:F0} s)  [{Objective.Format(beam[0].Rollout)}]");
            }
            return (bestKeys, bestBytes);
        }

        // The `branch` smallest valid keys, then for each jump the first valid key that much above the smallest
        static IEnumerable<int> Candidates(Entry entry, int branch, int[] jumpPercents, bool exactCompletion)
        {
            var keys = new List<int>();
            int key = entry.State.LastKey;
            for (int i = 0; i < branch; i++)
            {
                key = entry.State.NextValid(key + 1, exactCompletion);
                keys.Add(key);
            }
            foreach (int percent in jumpPercents)
            {
                int from = (int)Math.Min(int.MaxValue / 8, keys[0] + Math.Max(1, (long)keys[0] * percent / 100));
                if (from > keys[^1])
                {
                    keys.Add(entry.State.NextValid(from, exactCompletion));
                }
            }
            return keys.Distinct();
        }
    }

    // Local beam search with annealing over complete key sets: each member of the beam makes `children` mutants, and
    // the next beam is the best of parents and children, plus a few worse ones drawn with probability
    // exp(-(bytes - best) / T), T falling from 1% to 0.01% of the best over the run, to get out of local minima
    static class LocalSearch
    {
        public static (int[] keys, int bytes) Run(IEnumerable<int[]> starts, int shift, bool relaxed, TimeSpan duration,
            int width, int children, int seed, TextWriter log)
        {
            var random = new Random(seed);
            var sw = Stopwatch.StartNew();
            var pool = starts.Select(k => (keys: k, bytes: Objective.Bytes(k, shift, relaxed)))
                .Where(e => e.bytes != Objective.Invalid).ToList();
            var beam = pool.OrderBy(e => e.bytes).Take(width).ToList();
            var (bestKeys, bestBytes) = beam[0];
            log.WriteLine($"  start {bestBytes:N0} B  [{Objective.Format(bestKeys)}]");
            var seen = new HashSet<string>(beam.Select(e => Objective.Format(e.keys)));
            long evaluated = 0, valid = 0;
            int generation = 0;
            while (sw.Elapsed < duration)
            {
                double progress = sw.Elapsed / duration;
                double temperature = bestBytes * 0.01 * Math.Pow(0.01, progress);
                var mutants = new List<int[]>();
                foreach (var (keys, _) in beam)
                {
                    for (int c = 0; c < children; c++)
                    {
                        var mutant = Mutate(keys, random, shift, relaxed);
                        if (mutant != null && seen.Add(Objective.Format(mutant)))
                        {
                            mutants.Add(mutant);
                        }
                    }
                }
                var scores = new int[mutants.Count];
                Parallel.For(0, mutants.Count, i => scores[i] = Objective.Bytes(mutants[i], shift, relaxed));
                evaluated += mutants.Count;
                var next = new List<(int[] keys, int bytes)>(beam);
                for (int i = 0; i < mutants.Count; i++)
                {
                    if (scores[i] != Objective.Invalid)
                    {
                        valid++;
                        next.Add((mutants[i], scores[i]));
                    }
                }
                next.Sort((a, b) => a.bytes.CompareTo(b.bytes));
                if (next[0].bytes < bestBytes)
                {
                    (bestKeys, bestBytes) = next[0];
                    log.WriteLine($"  {sw.Elapsed.TotalSeconds,6:F0} s  generation {generation,5}: {bestBytes:N0} B  [{Objective.Format(bestKeys)}]");
                }
                // Elites, then explorers drawn from the rest by Boltzmann weight
                int elites = Math.Max(1, width * 3 / 4);
                beam = next.Take(elites).ToList();
                var rest = next.Skip(elites).ToList();
                while (beam.Count < width && rest.Count > 0)
                {
                    double total = rest.Sum(e => Math.Exp(-(e.bytes - bestBytes) / temperature));
                    double pick = random.NextDouble() * total;
                    int j = 0;
                    for (; j < rest.Count - 1; j++)
                    {
                        pick -= Math.Exp(-(rest[j].bytes - bestBytes) / temperature);
                        if (pick <= 0)
                        {
                            break;
                        }
                    }
                    beam.Add(rest[j]);
                    rest.RemoveAt(j);
                }
                generation++;
            }
            log.WriteLine($"  {evaluated:N0} key sets tried in {sw.Elapsed.TotalMinutes:F1} min, {valid:N0} valid");
            return (bestKeys, bestBytes);
        }

        static int[] Mutate(int[] keys, Random random, int shift, bool relaxed)
        {
            return relaxed ? RandomChange(keys, random, relaxed) : PerturbAndRepair(keys, random);
        }

        // Strict keys: random changes almost never keep all the sums apart (the keys are tight), so change one key to
        // the next valid key near a random target, then walk up the ranks above it: each keeps its old key if that's
        // still valid (or the old key scaled by the same ratio as the changed one), else takes the next valid key.
        // Always valid, and a small change of the key set
        static int[] PerturbAndRepair(int[] keys, Random random)
        {
            var k = (int[])keys.Clone();
            // The upper ranks have the biggest keys and are the cheapest to repair; rank 0 moves every sum by 7 * k0
            int r = random.Next(20) switch { 0 => 0, < 6 => random.Next(1, Multisets.NumRanks), _ => random.Next(6, Multisets.NumRanks) };
            bool scaleAbove = random.Next(2) == 0;
            int old = Math.Max(1, k[r]);
            int target = r == 0 ? Math.Max(0, k[0] + Step(Math.Max(64, k[0]), random)) : Math.Max(k[r - 1] + 1, k[r] + Step(k[r], random));
            PartialSums state;
            if (r == 0)
            {
                k[0] = target;
                state = PartialSums.Start(k[0], true);
            }
            else
            {
                state = PartialSums.Start(k[0], true);
                for (int j = 1; j < r; j++)
                {
                    state = state.Add(k[j], true);
                }
                k[r] = state.NextValid(target, true);
                state = state.Add(k[r], true);
            }
            double ratio = (double)Math.Max(1, k[r]) / old;
            for (int j = r + 1; j < Multisets.NumRanks; j++)
            {
                int want = scaleAbove ? (int)Math.Min(Multisets.MaxSumLimit / 8, Math.Round(keys[j] * ratio)) : keys[j];
                k[j] = state.NextValid(Math.Max(k[j - 1] + 1, want), true);
                if (j < Multisets.NumRanks - 1)
                {
                    state = state.Add(k[j], true);
                }
            }
            return k;
        }

        // One random change, checked afterwards. Relaxed keys keep their rank order (a relaxed collision depends on
        // which rank has which key)
        static int[] RandomChange(int[] keys, Random random, bool relaxed)
        {
            var k = (int[])keys.Clone();
            int r = random.Next(Multisets.NumRanks);
            switch (random.Next(10))
            {
                case < 4:
                    k[r] += Step(k[r], random);
                    break;
                case < 6:
                    k[r] += Step(k[r], random);
                    int r2 = random.Next(Multisets.NumRanks);
                    k[r2] += Step(k[r2], random);
                    break;
                case < 8:
                    // Scale the keys from r up, which keeps the lower ranks' layout
                    double factor = 1 + (random.Next(2) == 0 ? 1 : -1) * Math.Pow(10, -4 + 2.5 * random.NextDouble());
                    for (int j = Math.Max(1, r); j < Multisets.NumRanks; j++)
                    {
                        k[j] = (int)Math.Round(k[j] * factor);
                    }
                    break;
                case 8:
                    k[r] += random.Next(2) == 0 ? random.Next(1, 17) : -random.Next(1, 17);
                    break;
                default:
                    // Change one of the upper keys and re-derive the ones above it greedily
                    r = random.Next(6, Multisets.NumRanks - 1);
                    k[r] += Step(k[r], random);
                    if (k[r] <= k[r - 1])
                    {
                        return null;
                    }
                    var state = PartialSums.Start(k[0], true);
                    for (int j = 1; j <= r; j++)
                    {
                        if (!state.IsValid(k[j], true))
                        {
                            return null;
                        }
                        state = state.Add(k[j], true);
                    }
                    return Greedy.Complete(state, k.AsSpan(0, r + 1), true);
            }
            if (k[0] < 0)
            {
                return null;
            }
            if (!relaxed)
            {
                Array.Sort(k);
            }
            for (int j = 1; j < Multisets.NumRanks; j++)
            {
                if (k[j] == k[j - 1])
                {
                    return null;
                }
            }
            return k;
        }

        // Up to 10% of the key, log-uniform from 0.001%, at least 1, either way
        static int Step(int key, Random random)
        {
            int size = (int)Math.Max(1, key * Math.Pow(10, -5 + 4 * random.NextDouble()));
            return random.Next(2) == 0 ? size : -size;
        }
    }
}
