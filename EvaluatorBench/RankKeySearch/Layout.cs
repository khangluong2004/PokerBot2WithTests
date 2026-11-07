using System.Numerics;

namespace RankKeySearch
{
    // Size of the tables RankTables.Build makes for a set of rank sums, and whether the sums are usable.
    // One instance per thread: it keeps its buffers between calls.
    sealed class Layout
    {
        public readonly record struct Size(int RanksEntries, int NumRows)
        {
            // What the objective minimizes: Ranks is ushort, RowOffsets is int
            public int Bytes => 2 * RanksEntries + 4 * NumRows;
        }

        int[] rowCount = [];
        int[] rowStart = [];
        int[] columns = [];
        int[] order = [];
        ulong[] used = [];
        readonly int[] hashKeys = new int[1 << 17];
        readonly int[] hashValues = new int[1 << 17];
        readonly int[] hashStamps = new int[1 << 17];
        int stamp;

        // The sums can index a table if no two multisets with different values share a sum. Strict: all different,
        // as RankTables assumes. Relaxed: multisets with the same value may share a sum (Build writes the same rank
        // twice to the same slot, so it works unchanged)
        public bool IsValid(int[] sums, bool relaxed)
        {
            stamp++;
            int mask = hashKeys.Length - 1;
            var valueIds = Multisets.ValueIds;
            for (int m = 0; m < sums.Length; m++)
            {
                int sum = sums[m];
                int h = (int)((uint)(sum * -1640531535) >> 15) & mask;
                while (true)
                {
                    if (hashStamps[h] != stamp)
                    {
                        hashStamps[h] = stamp;
                        hashKeys[h] = sum;
                        hashValues[h] = valueIds[m];
                        break;
                    }
                    if (hashKeys[h] == sum)
                    {
                        if (!relaxed || hashValues[h] != valueIds[m])
                        {
                            return false;
                        }
                        break;
                    }
                    h = (h + 1) & mask;
                }
            }
            return true;
        }

        // The same first fit as RankTables.Build, 64 offsets at a time: rows by number of sums (most first, ties by row
        // index, like the stable OrderByDescending), each at the lowest offset from (first free slot - lowest column)
        // where all its slots are free. Bit b of the AND of (free bits from column + offset) says whether offset + b fits.
        // rowOffsets, if given, gets what RankTables stores: the row's offset minus the row's start
        public Size Build(int[] sums, int maxSum, int shift, int[] rowOffsets = null)
        {
            int numRows = (maxSum >> shift) + 1;
            int rowSize = 1 << shift;
            if (rowOffsets != null)
            {
                for (int row = 0; row < numRows; row++)
                {
                    rowOffsets[row] = -row * rowSize;
                }
            }
            if (rowCount.Length < numRows + 1)
            {
                rowCount = new int[numRows + 1];
                rowStart = new int[numRows + 1];
            }
            Array.Clear(rowCount, 0, numRows + 1);
            foreach (int sum in sums)
            {
                rowCount[sum >> shift]++;
            }
            rowStart[0] = 0;
            int maxCount = 0;
            for (int row = 0; row < numRows; row++)
            {
                rowStart[row + 1] = rowStart[row] + rowCount[row];
                maxCount = Math.Max(maxCount, rowCount[row]);
            }
            if (columns.Length < sums.Length)
            {
                columns = new int[sums.Length];
            }
            // Columns grouped by row (rowCount is reused as the fill pointer, then restored)
            for (int row = 0; row < numRows; row++)
            {
                rowCount[row] = rowStart[row];
            }
            foreach (int sum in sums)
            {
                columns[rowCount[sum >> shift]++] = sum & (rowSize - 1);
            }
            // Non-empty rows by count descending, then row index: counting sort over the counts
            var bucketStart = new int[maxCount + 2];
            for (int row = 0; row < numRows; row++)
            {
                int count = rowStart[row + 1] - rowStart[row];
                if (count > 0)
                {
                    bucketStart[maxCount - count + 1]++;
                }
            }
            for (int b = 0; b <= maxCount; b++)
            {
                bucketStart[b + 1] += bucketStart[b];
            }
            int numOrdered = bucketStart[maxCount + 1];
            if (order.Length < numOrdered)
            {
                order = new int[numOrdered];
            }
            for (int row = 0; row < numRows; row++)
            {
                int count = rowStart[row + 1] - rowStart[row];
                if (count > 0)
                {
                    order[bucketStart[maxCount - count]++] = row;
                }
            }

            int words = ((4 * sums.Length + 4 * rowSize) >> 6) + 4;
            if (used.Length < words)
            {
                used = new ulong[words];
            }
            Array.Clear(used, 0, words);
            int size = 0, firstFree = 0;
            for (int i = 0; i < numOrdered; i++)
            {
                int row = order[i];
                var rowColumns = columns.AsSpan(rowStart[row], rowStart[row + 1] - rowStart[row]);
                firstFree = FirstFree(firstFree);
                int minColumn = int.MaxValue, maxColumn = 0;
                foreach (int column in rowColumns)
                {
                    minColumn = Math.Min(minColumn, column);
                    maxColumn = Math.Max(maxColumn, column);
                }
                int offset = Math.Max(0, firstFree - minColumn);
                while (true)
                {
                    EnsureBits(offset + maxColumn + 130);
                    ulong fits = ~0UL;
                    foreach (int column in rowColumns)
                    {
                        fits &= ~Get64(column + offset);
                        if (fits == 0)
                        {
                            break;
                        }
                    }
                    if (fits != 0)
                    {
                        offset += BitOperations.TrailingZeroCount(fits);
                        break;
                    }
                    offset += 64;
                }
                foreach (int column in rowColumns)
                {
                    int slot = column + offset;
                    used[slot >> 6] |= 1UL << slot;
                }
                size = Math.Max(size, maxColumn + offset + 1);
                if (rowOffsets != null)
                {
                    rowOffsets[row] = offset - row * rowSize;
                }
            }
            return new Size(size, numRows);
        }

        int FirstFree(int from)
        {
            int w = from >> 6;
            EnsureBits((w + 2) << 6);
            ulong free = ~used[w] & (~0UL << (from & 63));
            while (free == 0)
            {
                w++;
                EnsureBits((w + 2) << 6);
                free = ~used[w];
            }
            return (w << 6) + BitOperations.TrailingZeroCount(free);
        }

        // The 64 used bits from bit p
        ulong Get64(int p)
        {
            int w = p >> 6, s = p & 63;
            return s == 0 ? used[w] : (used[w] >> s) | (used[w + 1] << (64 - s));
        }

        void EnsureBits(int bits)
        {
            int words = (bits >> 6) + 2;
            if (words > used.Length)
            {
                Array.Resize(ref used, Math.Max(words, used.Length * 2));
            }
        }

        // RankTables.Build's own loop, as it is in the repo (with the largest sum in place of MAX_RANK_KEY), to check
        // Build against. Slow: one offset at a time
        public static Size BuildReference(int[] keys, int maxSum, int shift)
        {
            int numRows = (maxSum >> shift) + 1;
            int rowSize = 1 << shift;
            var rowStart = new int[numRows + 1];
            foreach (int key in keys)
            {
                rowStart[(key >> shift) + 1]++;
            }
            for (int row = 0; row < numRows; row++)
            {
                rowStart[row + 1] += rowStart[row];
            }
            var columns = new int[keys.Length];
            var filled = (int[])rowStart.Clone();
            foreach (int key in keys)
            {
                columns[filled[key >> shift]++] = key & (rowSize - 1);
            }
            var rowOrder = Enumerable.Range(0, numRows).OrderByDescending(row => rowStart[row + 1] - rowStart[row]).ToArray();
            var used = new bool[4 * keys.Length + 2 * rowSize];
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
                    for (offset = Math.Max(0, firstFree - minColumn); ; offset++)
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
                    foreach (int column in rowColumns)
                    {
                        used[column + offset] = true;
                        size = Math.Max(size, column + offset + 1);
                    }
                }
            }
            return new Size(size, numRows);
        }
    }
}
