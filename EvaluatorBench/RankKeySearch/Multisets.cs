using PokerBot2.GameSpace;

namespace RankKeySearch
{
    // The 49,205 multisets of 7 ranks (at most 4 of each), in the order RankTables enumerates them, with the value of
    // a non-flush hand with those ranks: equal ValueIds = equal EvalHandValue
    static class Multisets
    {
        public const int NumRanks = 13;
        // Largest rank sum allowed: RankTables keeps the sum in an int, and row tables past this get absurd anyway
        public const int MaxSumLimit = 1 << 28;

        public static readonly int Count;
        public static readonly byte[] Counts;
        public static readonly int[] ValueIds;

        static Multisets()
        {
            var counts = new List<byte>();
            var values = new List<long>();
            var current = new int[NumRanks];
            void Add(int rank, int cardsLeft)
            {
                if (rank == NumRanks)
                {
                    if (cardsLeft > 0)
                    {
                        return;
                    }
                    // Same representative hand as RankTables: suits dealt in turn, so there's no flush
                    ulong mask = 0;
                    for (int r = 0, numCard = 0; r < NumRanks; r++)
                    {
                        counts.Add((byte)current[r]);
                        for (int c = 0; c < current[r]; c++, numCard++)
                        {
                            mask |= Game.ToCardMask(r * 4 + numCard % 4);
                        }
                    }
                    values.Add(Game.EvalHandValue(mask));
                    return;
                }
                for (int count = 0; count <= Math.Min(4, cardsLeft); count++)
                {
                    current[rank] = count;
                    Add(rank + 1, cardsLeft - count);
                }
                current[rank] = 0;
            }
            Add(0, 7);
            Count = values.Count;
            Counts = counts.ToArray();
            var ids = values.Distinct().Order().Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            ValueIds = values.Select(v => ids[v]).ToArray();
        }

        public static int NumValues => ValueIds.Max() + 1;

        // Every multiset's rank sum for these keys. False if a key is negative or a sum is over MaxSumLimit
        public static bool Sums(ReadOnlySpan<int> keys, int[] sums, out int maxSum)
        {
            maxSum = 0;
            for (int r = 0; r < NumRanks; r++)
            {
                if (keys[r] < 0 || keys[r] > MaxSumLimit / 4)
                {
                    return false;
                }
            }
            var counts = Counts.AsSpan();
            for (int m = 0, i = 0; m < Count; m++, i += NumRanks)
            {
                long sum = 0;
                for (int r = 0; r < NumRanks; r++)
                {
                    sum += counts[i + r] * keys[r];
                }
                if (sum > MaxSumLimit)
                {
                    return false;
                }
                sums[m] = (int)sum;
                maxSum = Math.Max(maxSum, (int)sum);
            }
            return true;
        }
    }
}
