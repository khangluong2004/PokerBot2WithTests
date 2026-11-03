using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using PokerBot2;

// Showdown evaluation when the lookup tables are not in cache, as when an agent's own work between hands evicts them.
// Every showdown is timed on its own, after one of 4 cache states:
// - hot: nothing evicted
// - L2 evicted: a 4 MB buffer is read first, so the tables and the code drop out of the 1 MB L2 into the L3
// - all evicted: a 64 MB buffer is read first, twice the 32 MB L3 of this core's CCD (.NET has no clflush), so the
//   tables and the evaluator's code come from DRAM
// - tables cold, code warm: the same, then one evaluation of another showdown brings the code (and a few table
//   lines) back, so mostly the timed showdown's own table lines are still in DRAM
// Stopwatch ticks are 100 ns, longer than one showdown: the eviction before each sample makes its start random
// against the tick, so the average over many samples is exact. A call that does nothing is measured the same way
// and subtracted.
public static unsafe class ColdBench
{
    static readonly string[] Modes = ["hot", "L2 evicted", "all evicted", "tables cold, code warm"];

    public static void Run()
    {
        Game.HandKeyMask(0); // build EvalHandRank's tables
        var l2Buffer = GC.AllocateArray<byte>(4 << 20, pinned: true);
        var l3Buffer = GC.AllocateArray<byte>(64 << 20, pinned: true);
        l3Buffer.AsSpan().Fill(1); // commit the pages
        Console.WriteLine("ns per showdown, after subtracting an empty call\n");

        foreach (int numPlayer in new[] { 2, 9 })
        {
            Console.WriteLine($"{numPlayer} players{"",-26}{string.Join("", Modes.Select(m => $"{m,24}"))}");
            var random = new Random(numPlayer);
            const int numScenarios = 4096;
            int cardsPer = 5 + 2 * numPlayer;
            var deck = Enumerable.Range(0, 52).ToArray();
            var scenarios = new int[numScenarios * cardsPer];
            for (int s = 0; s < numScenarios; s++)
            {
                for (int i = 0; i < cardsPer; i++)
                {
                    int j = random.Next(i, 52);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                    scenarios[s * cardsPer + i] = deck[i];
                }
            }
            var evaluators = new (string name, nint fn)[]
            {
                ("EvalHandRank (tables)", (nint)(delegate*<int*, int, long>)&Table),
                ("EvalHandValue per player", (nint)(delegate*<int*, int, long>)&Scalar),
                ("EvalHandValues (AVX-512 batch)", (nint)(delegate*<int*, int, long>)&Batch),
            };
            // Warm up: the evaluators' callees (EvalHand, EvalHandValues) start as unoptimized tier-0 code and are
            // recompiled in the background after enough calls, so call everything, then give the JIT time
            for (int round = 0; round < 3; round++)
            {
                foreach (var (_, fn) in evaluators)
                {
                    Measure(fn, 0, scenarios, numPlayer, l2Buffer, l3Buffer);
                }
                Thread.Sleep(300);
            }
            var empty = new double[Modes.Length];
            for (int mode = 0; mode < Modes.Length; mode++)
            {
                empty[mode] = Measure((nint)(delegate*<int*, int, long>)&Empty, mode, scenarios, numPlayer, l2Buffer, l3Buffer);
            }
            foreach (var (name, fn) in evaluators)
            {
                var cells = new List<string>();
                for (int mode = 0; mode < Modes.Length; mode++)
                {
                    cells.Add($"{Measure(fn, mode, scenarios, numPlayer, l2Buffer, l3Buffer) - empty[mode],24:F1}");
                }
                Console.WriteLine($"  {name,-31}{string.Concat(cells)}");
            }
            Console.WriteLine($"  {"(empty call, subtracted)",-31}{string.Concat(empty.Select(e => $"{e,24:F1}"))}");
            Console.WriteLine();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static double Measure(nint fn, int mode, int[] scenarios, int numPlayer, byte[] l2Buffer, byte[] l3Buffer)
    {
        var eval = (delegate*<int*, int, long>)fn;
        int cardsPer = 5 + 2 * numPlayer;
        int numScenarios = scenarios.Length / cardsPer;
        int samples = mode == 0 ? 400_000 : mode == 1 ? 40_000 : 4_000;
        int* cards = stackalloc int[23];
        int* other = stackalloc int[23];
        long ticks = 0, sink = 0;
        fixed (byte* l2 = l2Buffer, l3 = l3Buffer)
        {
            for (int s = -samples / 10; s < samples; s++)
            {
                int scenario = (s & int.MaxValue) % numScenarios;
                for (int i = 0; i < cardsPer; i++)
                {
                    cards[i] = scenarios[scenario * cardsPer + i];
                }
                if (mode == 1)
                {
                    for (int i = 0; i < l2Buffer.Length; i += 64)
                    {
                        sink += l2[i];
                    }
                }
                else if (mode >= 2)
                {
                    for (int i = 0; i < l3Buffer.Length; i += 64)
                    {
                        sink += l3[i];
                    }
                }
                if (mode == 3)
                {
                    int otherScenario = (scenario + numScenarios / 2) % numScenarios;
                    for (int i = 0; i < cardsPer; i++)
                    {
                        other[i] = scenarios[otherScenario * cardsPer + i];
                    }
                    sink += eval(other, numPlayer);
                }
                // The cards themselves are hot, as they are in the Arena
                for (int i = 0; i < cardsPer; i++)
                {
                    sink += cards[i];
                }
                Sse2.MemoryFence();
                long t0 = Stopwatch.GetTimestamp();
                Sse2.LoadFence();
                sink += eval(cards, numPlayer);
                Sse2.LoadFence();
                long t1 = Stopwatch.GetTimestamp();
                if (s >= 0)
                {
                    ticks += t1 - t0;
                }
            }
        }
        GC.KeepAlive(sink);
        return (double)ticks * 1e9 / Stopwatch.Frequency / samples;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long Empty(int* cards, int numPlayer) => 0;

    // As Arena.HandleShowdown does it now: the board's key and mask once, then 2 cards per player
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long Table(int* cards, int numPlayer)
    {
        var board = Game.HandKeyMask(new ReadOnlySpan<int>(cards, 5));
        int best = 0;
        for (int p = 0, c = 5; p < numPlayer; p++, c += 2)
        {
            best = Math.Max(best, Game.EvalHandRank(board + Game.HandKeyMask(cards[c]) + Game.HandKeyMask(cards[c + 1])));
        }
        return best;
    }

    // Bit operations, no tables: one EvalHandValue per player
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long Scalar(int* cards, int numPlayer)
    {
        ulong boardMask = Game.ToHandMask(new ReadOnlySpan<int>(cards, 5));
        long best = 0;
        for (int p = 0, c = 5; p < numPlayer; p++, c += 2)
        {
            best = Math.Max(best, Game.EvalHandValue(boardMask | Game.ToCardMask(cards[c]) | Game.ToCardMask(cards[c + 1])));
        }
        return best;
    }

    // As Arena.HandleShowdown did it before EvalHandRank: masks, then the AVX-512 batch (8 hands per vector)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static long Batch(int* cards, int numPlayer)
    {
        ulong boardMask = Game.ToHandMask(new ReadOnlySpan<int>(cards, 5));
        Span<ulong> masks = stackalloc ulong[numPlayer];
        Span<long> values = stackalloc long[numPlayer];
        for (int p = 0, c = 5; p < numPlayer; p++, c += 2)
        {
            masks[p] = boardMask | Game.ToCardMask(cards[c]) | Game.ToCardMask(cards[c + 1]);
        }
        Game.EvalHandValues(masks, values);
        long best = 0;
        for (int p = 0; p < numPlayer; p++)
        {
            best = Math.Max(best, values[p]);
        }
        return best;
    }
}
