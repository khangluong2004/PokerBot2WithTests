using System.Numerics;

namespace RankKeySearch
{
    // Partial hands over ranks 0..Rank: for each number of cards t, the sums of all partial hands with t cards. Keys
    // are chosen rank by rank, and a key is valid for the next rank when it keeps the partial sums of each t apart.
    //
    // exactCompletion = false is RankTables' rule (the README's search): every t, at every rank.
    // exactCompletion = true only keeps the partial hands that can still become 7-card hands: after rank i, a partial
    // hand with t cards needs 7 - t more from the 12 - i ranks left, at most 4 each. Two partial hands that can't both
    // be completed can share a sum. That only changes the last 2 ranks, which have the largest keys
    sealed class PartialSums
    {
        public readonly int Rank;
        public readonly int LastKey;
        readonly int[][] lists;
        ulong[][] bits;

        PartialSums(int rank, int lastKey, int[][] lists)
        {
            Rank = rank;
            LastKey = lastKey;
            this.lists = lists;
        }

        static bool Needed(int t, int rank, bool exactCompletion)
        {
            return !exactCompletion || 7 - t <= 4 * (Multisets.NumRanks - 1 - rank);
        }

        public static PartialSums Start(int key0, bool exactCompletion)
        {
            var lists = new int[8][];
            for (int t = 0; t <= 7; t++)
            {
                lists[t] = t <= 4 && Needed(t, 0, exactCompletion) ? [t * key0] : null;
            }
            return new PartialSums(0, key0, lists);
        }

        // Can key be the next rank's key: for every t still needed after that rank, and every 2 counts c1 < c2 of
        // the new rank, (sums with t - c1 cards) + c1 * key and (sums with t - c2 cards) + c2 * key must not meet
        public bool IsValid(int key, bool exactCompletion)
        {
            int rank = Rank + 1;
            bits ??= BuildBits();
            for (int t = 7; t >= 0; t--)
            {
                if (!Needed(t, rank, exactCompletion))
                {
                    continue;
                }
                for (int c2 = Math.Min(4, t); c2 >= 1; c2--)
                {
                    var lower = lists[t - c2];
                    if (lower == null)
                    {
                        continue;
                    }
                    for (int c1 = c2 - 1; c1 >= 0; c1--)
                    {
                        var higher = bits[t - c1];
                        if (higher == null)
                        {
                            continue;
                        }
                        long d = (long)(c2 - c1) * key;
                        long limit = (long)higher.Length << 6;
                        foreach (int s2 in lower)
                        {
                            long s = s2 + d;
                            if (s >= limit)
                            {
                                break;
                            }
                            if ((higher[s >> 6] >> (int)(s & 63) & 1) != 0)
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            return true;
        }

        public int NextValid(int from, bool exactCompletion)
        {
            for (int key = from; ; key++)
            {
                if (IsValid(key, exactCompletion))
                {
                    return key;
                }
            }
        }

        public PartialSums Add(int key, bool exactCompletion)
        {
            int rank = Rank + 1;
            var next = new int[8][];
            for (int t = 0; t <= 7; t++)
            {
                if (!Needed(t, rank, exactCompletion))
                {
                    continue;
                }
                var sums = new List<int>();
                for (int c = 0; c <= Math.Min(4, t); c++)
                {
                    if (lists[t - c] != null)
                    {
                        foreach (int s in lists[t - c])
                        {
                            sums.Add(s + c * key);
                        }
                    }
                }
                sums.Sort();
                next[t] = sums.ToArray();
            }
            return new PartialSums(rank, key, next);
        }

        ulong[][] BuildBits()
        {
            var result = new ulong[8][];
            for (int t = 0; t <= 7; t++)
            {
                if (lists[t] == null || lists[t].Length == 0)
                {
                    continue;
                }
                var b = new ulong[(lists[t][^1] >> 6) + 1];
                foreach (int s in lists[t])
                {
                    b[s >> 6] |= 1UL << s;
                }
                result[t] = b;
            }
            return result;
        }
    }

    static class Greedy
    {
        // Completes keys[0..fromRank-1] (already valid) with the smallest valid key for each remaining rank
        public static int[] Complete(int[] prefix, int prefixLength, bool exactCompletion)
        {
            var state = PartialSums.Start(prefix[0], exactCompletion);
            for (int r = 1; r < prefixLength; r++)
            {
                state = state.Add(prefix[r], exactCompletion);
            }
            return Complete(state, prefix.AsSpan(0, prefixLength), exactCompletion);
        }

        public static int[] Complete(PartialSums state, ReadOnlySpan<int> prefix, bool exactCompletion)
        {
            var keys = new int[Multisets.NumRanks];
            prefix.CopyTo(keys);
            for (int r = prefix.Length; r < Multisets.NumRanks; r++)
            {
                keys[r] = state.NextValid(state.LastKey + 1, exactCompletion);
                if (r < Multisets.NumRanks - 1)
                {
                    state = state.Add(keys[r], exactCompletion);
                }
            }
            return keys;
        }
    }
}
