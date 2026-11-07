using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RankKeySearch
{
    // Builds the real Ranks and RowOffsets for a key set and times lookups of random 7-card hands' rank sums:
    // hot, as a dependent chain (latency), and while other data takes most of the 1 MB L2
    static class LookupBench
    {
        public sealed record Result(int Bytes, int CacheLines, double HotNs, double ChainNs, double PressureNs);

        public static Result Run(int[] keys, int shift)
        {
            var sums = new int[Multisets.Count];
            Multisets.Sums(keys, sums, out int maxSum);
            int numRows = (maxSum >> shift) + 1;
            var rowOffsets = new int[numRows];
            var size = new Layout().Build(sums, maxSum, shift, rowOffsets);
            var ranks = new ushort[size.RanksEntries];
            for (int m = 0; m < sums.Length; m++)
            {
                ranks[sums[m] + rowOffsets[sums[m] >> shift]] = (ushort)(Multisets.ValueIds[m] + 1);
            }
            // Cache lines of Ranks a lookup can touch, plus RowOffsets'
            int lines = 0;
            for (int l = 0; l < ranks.Length; l += 32)
            {
                bool any = false;
                for (int i = l; i < Math.Min(ranks.Length, l + 32); i++)
                {
                    any |= ranks[i] != 0;
                }
                lines += any ? 1 : 0;
            }
            lines += (numRows + 15) / 16;

            // Rank sums of random 7-card hands (deal 7 of 52), checked against the table
            var random = new Random(3);
            var deck = Enumerable.Range(0, 52).ToArray();
            var sample = new int[1 << 21];
            for (int h = 0; h < sample.Length; h++)
            {
                int sum = 0;
                for (int i = 0; i < 7; i++)
                {
                    int j = random.Next(i, 52);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                    sum += keys[deck[i] >> 2];
                }
                sample[h] = sum;
                if (ranks[sum + rowOffsets[sum >> shift]] == 0)
                {
                    throw new InvalidOperationException("A rank sum looked up an empty slot");
                }
            }
            var other = new byte[896 << 10]; // leaves about 128 KB of the L2
            return new Result(size.Bytes, lines,
                Best(() => Hot(sample, rowOffsets, ranks, shift)) / sample.Length,
                Best(() => Chain(sample, rowOffsets, ranks, shift)) / sample.Length,
                (Best(() => Pressure(sample, rowOffsets, ranks, shift, other, true)) - Best(() => Pressure(sample, rowOffsets, ranks, shift, other, false))) / sample.Length);
        }

        static double Best(Func<long> run)
        {
            run();
            double best = double.MaxValue;
            for (int r = 0; r < 7; r++)
            {
                var sw = Stopwatch.StartNew();
                run();
                best = Math.Min(best, sw.Elapsed.TotalNanoseconds);
            }
            return best;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static long Hot(int[] sample, int[] rowOffsets, ushort[] ranks, int shift)
        {
            long total = 0;
            foreach (int sum in sample)
            {
                total += ranks[sum + rowOffsets[sum >> shift]];
            }
            return total;
        }

        // Each lookup picks the next hand from the previous result, so lookups can't overlap
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static long Chain(int[] sample, int[] rowOffsets, ushort[] ranks, int shift)
        {
            long total = 0;
            int rank = 0, mask = sample.Length - 1;
            for (int i = 0; i < sample.Length; i++)
            {
                int sum = sample[(i + (rank & 1)) & mask];
                rank = ranks[sum + rowOffsets[sum >> shift]];
                total += rank;
            }
            return total;
        }

        // A random cache line of `other` per lookup, so most of the L2 holds other data. Without lookup = the baseline
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static long Pressure(int[] sample, int[] rowOffsets, ushort[] ranks, int shift, byte[] other, bool lookup)
        {
            long total = 0;
            uint x = 12345;
            int lines = other.Length / 64;
            for (int i = 0; i < sample.Length; i++)
            {
                x = x * 1664525 + 1013904223;
                total += other[(int)((ulong)x * (uint)lines >> 32) * 64];
                if (lookup)
                {
                    int sum = sample[i];
                    total += ranks[sum + rowOffsets[sum >> shift]];
                }
            }
            return total;
        }
    }
}
