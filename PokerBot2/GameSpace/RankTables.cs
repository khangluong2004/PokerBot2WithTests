using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PokerBot2.GameSpace
{
    // Lookup tables for Game.EvalHandRank, built once (on first use, about 75 ms) from Game.EvalHandValue, so the two
    // evaluators always order hands the same way. About 270 KB (Ranks 222 KB, RowOffsets 30 KB, Flush 16 KB), small
    // enough to stay in the L2 cache while nothing else pushes it out.
    internal static class RankTables
    {
        // Greedy search: each key is the smallest one above the previous key that keeps the sums of all multisets
        // of up to 7 ranks unique for each number of cards. Rank 0 can be 0: with exactly 7 cards, its count is
        // whatever the other ranks leave
        internal static readonly int[] RANK_KEYS = [0, 1, 5, 22, 98, 453, 2031, 8698, 22854, 83661, 262349, 636345, 1479181];
        internal const int MAX_RANK_KEY = 4 * 1479181 + 3 * 636345;
        // Perfect hash by displacement: the rank sum's high bits pick a row, and each row has its own offset into
        // Ranks, chosen when the table is built so that no two hands share a slot
        const int ROW_SHIFT = 10;

        // Each card (0-51): its key (rank key in bits 0-31, 1 in its suit's counter at bits 32 + 4 * suit) and its
        // Game.ToCardMask
        internal static readonly Vector128<ulong>[] CardKeyMasks = new Vector128<ulong>[52];
        // Rank of a flush (or straight flush) by the 13-bit rank mask of the flush suit (5-7 bits set)
        internal static readonly ushort[] Flush = new ushort[1 << 13];
        // RowOffsets[key >> ROW_SHIFT] + key is the slot in Ranks (the row's offset minus the row's start)
        internal static readonly int[] RowOffsets;
        internal static readonly ushort[] Ranks;

        // Rank of a non-flush 7-card hand from its rank sum: 2 dependent loads (RowOffsets, then Ranks)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Lookup(uint rankKey)
        {
            return Ranks[(int)rankKey + RowOffsets[rankKey >> ROW_SHIFT]];
        }

        static RankTables()
        {
            (RowOffsets, Ranks) = Build();
        }

        // Fills the card, suit and flush tables and returns the perfect hash. Compiled optimized from the start: a static
        // constructor runs only once, so the runtime would run it as unoptimized code (about 0.5 s instead of ms)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static (int[] rowOffsets, ushort[] ranks) Build()
        {
            for (int card = 0; card < 52; card++)
            {
                ulong key = (uint)RANK_KEYS[Game.GetRank(card)] | (1UL << (32 + 4 * Game.GetSuit(card)));
                CardKeyMasks[card] = Vector128.Create(key, Game.ToCardMask(card));
            }

            // Every multiset of 7 ranks: its rank sum, and the EvalHandValue of a hand with those ranks and no flush
            var keys = new List<int>(49205);
            var values = new List<long>(49205);
            Span<int> counts = stackalloc int[13];
            AddRankMultisets(counts, 0, 7, keys, values);

            // Flushes: every suit mask with 5-7 ranks, as an all-spade hand
            var flushValues = new long[Flush.Length];
            for (int mask = 0; mask < Flush.Length; mask++)
            {
                int numCards = BitOperations.PopCount((uint)mask);
                flushValues[mask] = numCards >= 5 && numCards <= 7 ? Game.EvalHandValue((ulong)mask) : -1;
            }

            // Replace each EvalHandValue by (type << 12) | its position among the values of that type
            // type = Game.WinHandType (eg: flush, straight, ...)
            // << 12 to use the first 12 bits to store the offset (largest offset is 1,470 (for 1 pair))
            var distinct = values.Concat(flushValues.Where(v => v >= 0)).Distinct().Order().ToArray();
            var toRank = new Dictionary<long, ushort>(distinct.Length);
            for (int i = 0, typeStart = 0; i < distinct.Length; i++)
            {
                long type = distinct[i] >> 32;
                if (i > 0 && type != distinct[i - 1] >> 32)
                {
                    typeStart = i;
                }
                Debug.Assert(i - typeStart < 1 << 12);
                toRank[distinct[i]] = (ushort)((type << 12) | (long)(i - typeStart));
            }
            for (int mask = 0; mask < Flush.Length; mask++)
            {
                Flush[mask] = flushValues[mask] >= 0 ? toRank[flushValues[mask]] : (ushort)0;
            }

            // Displacement: place the rows with the most keys first, each at the lowest offset where all its slots
            // are still free. A row starts its search a little before the previous row with as many keys, since the
            // offsets before that mostly failed for rows like it: 10x faster to build, for a 3% bigger table
            int numRows = (MAX_RANK_KEY >> ROW_SHIFT) + 1;
            int rowSize = 1 << ROW_SHIFT;
            var rowStart = new int[numRows + 1];
            foreach (int key in keys)
            {
                rowStart[(key >> ROW_SHIFT) + 1]++;
            }
            for (int row = 0; row < numRows; row++)
            {
                rowStart[row + 1] += rowStart[row];
            }
            // Keys grouped by row, as their column inside the row
            var columns = new int[keys.Count];
            var filled = (int[])rowStart.Clone();
            foreach (int key in keys)
            {
                columns[filled[key >> ROW_SHIFT]++] = key & (rowSize - 1);
            }
            var rowOrder = Enumerable.Range(0, numRows).OrderByDescending(row => rowStart[row + 1] - rowStart[row]).ToArray();
            var used = new bool[4 * keys.Count + 2 * rowSize];
            var searchStart = new int[rowSize + 1];
            var rowOffsets = new int[numRows];
            int size = 0, firstFree = 0;
            foreach (int row in rowOrder)
            {
                var rowColumns = columns.AsSpan(rowStart[row], rowStart[row + 1] - rowStart[row]);
                int offset = 0;
                if (rowColumns.Length > 0)
                {
                    while (used[firstFree])
                    {
                        firstFree++;
                    }
                    int minColumn = int.MaxValue;
                    foreach (int column in rowColumns)
                    {
                        minColumn = Math.Min(minColumn, column);
                    }
                    for (offset = Math.Max(Math.Max(0, firstFree - minColumn), searchStart[rowColumns.Length] - 256); ; offset++)
                    {
                        bool fits = true;
                        foreach (int column in rowColumns)
                        {
                            if (used[column + offset])
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (fits)
                        {
                            break;
                        }
                    }
                    searchStart[rowColumns.Length] = offset;
                    foreach (int column in rowColumns)
                    {
                        used[column + offset] = true;
                        size = Math.Max(size, column + offset + 1);
                    }
                }
                rowOffsets[row] = offset - row * rowSize;
            }
            // Room for a full row, so even an invalid key in an empty row stays in bounds
            var ranks = new ushort[Math.Max(size, rowSize)];
            for (int i = 0; i < keys.Count; i++)
            {
                ranks[keys[i] + rowOffsets[keys[i] >> ROW_SHIFT]] = toRank[values[i]];
            }
            return (rowOffsets, ranks);
        }

        // Enumerates the counts of ranks rank..12 that add up to cardsLeft (at most 4 each)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AddRankMultisets(Span<int> counts, int rank, int cardsLeft, List<int> keys, List<long> values)
        {
            if (rank == 13)
            {
                if (cardsLeft > 0)
                {
                    return;
                }
                // Deal the suits in turn, card after card: the copies of a rank get different suits, and no suit
                // gets more than 2 of the 7 cards, so there is no flush.

                // We don't care about the suits for these hands, as long as there is no flush 
                // since this table deals with only non-flush hand.
                int key = 0, numCard = 0;
                ulong mask = 0;
                for (int r = 0; r < 13; r++)
                {
                    key += counts[r] * RANK_KEYS[r];
                    for (int c = 0; c < counts[r]; c++, numCard++)
                    {
                        mask |= Game.ToCardMask(r * 4 + numCard % 4);
                    }
                }
                keys.Add(key);
                values.Add(Game.EvalHandValue(mask));
                return;
            }
            for (int count = 0; count <= Math.Min(4, cardsLeft); count++)
            {
                counts[rank] = count;
                AddRankMultisets(counts, rank + 1, cardsLeft - count, keys, values);
            }
            // Backtrack
            counts[rank] = 0;
        }
    }
}
