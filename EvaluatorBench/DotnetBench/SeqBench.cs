using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PokerBot2.GameSpace;
using poker.net.Services;

// The evaluators on hands in sequential order, where each one can reuse the work done for the previous hand:
// - all hands: every 7-card hand in order (c0 < c1 < ... < c6), as an exhaustive enumeration does. The work for
//   the first 6 cards is done once per innermost loop
// - boards: every hole-card pair (1,081) on each of the boards in data\boards5.bin, in order, as an equity
//   calculation or a CFR showdown over all holdings does. The board's work is done once
// Each evaluator does this its own natural incremental way. A first pass counts the hand types and checks them (the
// known totals for all hands, EvalHandRank's counts for the boards). Then the best of 2 timed passes sums the results.
public static class SeqBench
{
    // Hand types (1 = high card ... 9 = straight flush) over all 133,784,560 seven-card hands
    static readonly long[] AllHandTypes = [0, 23294460, 58627800, 31433400, 6461620, 6180020, 4047644, 3473184, 224848, 41584];
    public const int NumBoards = 20000;

    interface IConsumer
    {
        void Add(long value);
    }

    struct Sum : IConsumer
    {
        public long Total;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long value) => Total += value;
    }

    // Counts hand types, for the check pass (not timed)
    sealed class TypeCount(Func<long, int> typeOf) : IConsumer
    {
        public readonly long[] Counts = new long[10];

        public void Add(long value) => Counts[typeOf(value)]++;
    }

    delegate void Pass<T>(ref T consumer) where T : IConsumer;

    sealed class Evaluator
    {
        public string Name;
        public Func<long, int> TypeOf;
        public Pass<Sum> Timed;
        public Pass<TypeCount> Check;
    }

    public static void Run(string boardsPath)
    {
        Game.HandKeyMask(0); // build EvalHandRank's tables
        var boards = LoadBoards(boardsPath);
        var cactusKev = new int[52];
        for (int card = 0; card < 52; card++)
        {
            cactusKev[card] = CactusKevCard(card);
        }
        Func<long, int> rankType = v => (int)(v >> 12);
        Func<long, int> valueType = v => (int)(v >> 32);
        Func<long, int> pokerNetType = v => 10 - PokerLib.HandRank((int)v);

        Console.WriteLine("All 133,784,560 hands in order, best of 2 passes, ns per hand");
        var all = new[]
        {
            new Evaluator { Name = "PokerBot2 EvalHandRank (vector added card by card)", TypeOf = rankType,
                Timed = AllRank, Check = AllRank },
            new Evaluator { Name = "PokerBot2 EvalHandValue (mask ORed card by card)", TypeOf = valueType,
                Timed = AllValue, Check = AllValue },
            new Evaluator { Name = "PokerBot2 EvalHandValues (each innermost loop as a batch)", TypeOf = valueType,
                Timed = AllBatch, Check = AllBatch },
            new Evaluator { Name = "poker.net (best of 21 x Eval5CardsFast)", TypeOf = pokerNetType,
                Timed = (ref Sum c) => AllPokerNet(cactusKev, ref c), Check = (ref TypeCount c) => AllPokerNet(cactusKev, ref c) },
        };
        foreach (var e in all)
        {
            Measure(e, 133_784_560, counts => counts.SequenceEqual(AllHandTypes) ? "ok (known totals)" : "WRONG");
        }

        Console.WriteLine($"\nEvery hole-card pair on {boards.Length / 5:N0} boards in order (1,081 per board), best of 2 passes, ns per hand");
        long numBoardHands = boards.Length / 5 * 1081L;
        long[] reference = null;
        var onBoards = new[]
        {
            new Evaluator { Name = "PokerBot2 EvalHandRank (board vector once, + 2 cards)", TypeOf = rankType,
                Timed = (ref Sum c) => BoardsRank(boards, ref c), Check = (ref TypeCount c) => BoardsRank(boards, ref c) },
            new Evaluator { Name = "PokerBot2 EvalHandValue (board mask once, | 2 cards)", TypeOf = valueType,
                Timed = (ref Sum c) => BoardsValue(boards, ref c), Check = (ref TypeCount c) => BoardsValue(boards, ref c) },
            new Evaluator { Name = "PokerBot2 EvalHandValues (each board's 1,081 hands as a batch)", TypeOf = valueType,
                Timed = (ref Sum c) => BoardsBatch(boards, ref c), Check = (ref TypeCount c) => BoardsBatch(boards, ref c) },
            new Evaluator { Name = "poker.net (best of 21 x Eval5CardsFast)", TypeOf = pokerNetType,
                Timed = (ref Sum c) => BoardsPokerNet(boards, cactusKev, ref c), Check = (ref TypeCount c) => BoardsPokerNet(boards, cactusKev, ref c) },
        };
        foreach (var e in onBoards)
        {
            Measure(e, numBoardHands, counts =>
            {
                if (reference == null)
                {
                    reference = counts;
                    return $"reference ({string.Join(" ", counts.Skip(1))})";
                }
                return counts.SequenceEqual(reference) ? "ok (= EvalHandRank)" : "WRONG";
            });
        }
    }

    static void Measure(Evaluator e, long numHands, Func<long[], string> check)
    {
        var types = new TypeCount(e.TypeOf);
        e.Check(ref types);
        string result = check(types.Counts);
        double best = double.MaxValue;
        long checksum = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var sum = new Sum();
            var sw = Stopwatch.StartNew();
            e.Timed(ref sum);
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / numHands);
            checksum += sum.Total;
        }
        Console.WriteLine($"  {e.Name,-66} {best,7:F2} ns  {1000 / best,7:F0} M/s  hand types {result}  (checksum {checksum & 0xFFFF})");
    }

    // Random boards, 5 distinct cards each (seeded, shared with the native side)
    public static byte[] LoadBoards(string path)
    {
        if (!File.Exists(path))
        {
            var random = new Random(20261107);
            var deck = Enumerable.Range(0, 52).ToArray();
            var data = new byte[NumBoards * 5];
            for (int b = 0; b < NumBoards; b++)
            {
                for (int i = 0; i < 5; i++)
                {
                    int j = random.Next(i, 52);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                    data[b * 5 + i] = (byte)deck[i];
                }
            }
            File.WriteAllBytes(path, data);
            Console.WriteLine($"Wrote {NumBoards:N0} boards to {path}");
        }
        return File.ReadAllBytes(path);
    }

    static int CactusKevCard(int card)
    {
        ReadOnlySpan<int> primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41];
        ReadOnlySpan<int> suits = [PokerLib.SPADE, PokerLib.CLUB, PokerLib.DIAMOND, PokerLib.HEART];
        int rank = Game.GetRank(card);
        return (1 << (16 + rank)) | suits[Game.GetSuit(card)] | (rank << 8) | primes[rank];
    }

    // The cards not on the board, in order
    static int Remaining(ReadOnlySpan<byte> board, Span<int> rest)
    {
        ulong used = 0;
        foreach (byte card in board)
        {
            used |= 1UL << card;
        }
        int n = 0;
        for (int card = 0; card < 52; card++)
        {
            if ((used >> card & 1) == 0)
            {
                rest[n++] = card;
            }
        }
        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void AllRank<T>(ref T consumer) where T : IConsumer
    {
        var empty = Game.HandKeyMask(ReadOnlySpan<int>.Empty);
        for (int a = 0; a < 46; a++)
        {
            var va = empty + Game.HandKeyMask(a);
            for (int b = a + 1; b < 47; b++)
            {
                var vb = va + Game.HandKeyMask(b);
                for (int c = b + 1; c < 48; c++)
                {
                    var vc = vb + Game.HandKeyMask(c);
                    for (int d = c + 1; d < 49; d++)
                    {
                        var vd = vc + Game.HandKeyMask(d);
                        for (int e = d + 1; e < 50; e++)
                        {
                            var ve = vd + Game.HandKeyMask(e);
                            for (int f = e + 1; f < 51; f++)
                            {
                                var vf = ve + Game.HandKeyMask(f);
                                for (int g = f + 1; g < 52; g++)
                                {
                                    consumer.Add(Game.EvalHandRank(vf + Game.HandKeyMask(g)));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void AllValue<T>(ref T consumer) where T : IConsumer
    {
        for (int a = 0; a < 46; a++)
        {
            ulong ma = Game.ToCardMask(a);
            for (int b = a + 1; b < 47; b++)
            {
                ulong mb = ma | Game.ToCardMask(b);
                for (int c = b + 1; c < 48; c++)
                {
                    ulong mc = mb | Game.ToCardMask(c);
                    for (int d = c + 1; d < 49; d++)
                    {
                        ulong md = mc | Game.ToCardMask(d);
                        for (int e = d + 1; e < 50; e++)
                        {
                            ulong me = md | Game.ToCardMask(e);
                            for (int f = e + 1; f < 51; f++)
                            {
                                ulong mf = me | Game.ToCardMask(f);
                                for (int g = f + 1; g < 52; g++)
                                {
                                    consumer.Add(Game.EvalHandValue(mf | Game.ToCardMask(g)));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void AllBatch<T>(ref T consumer) where T : IConsumer
    {
        Span<ulong> masks = stackalloc ulong[46];
        Span<long> values = stackalloc long[46];
        for (int a = 0; a < 46; a++)
        {
            ulong ma = Game.ToCardMask(a);
            for (int b = a + 1; b < 47; b++)
            {
                ulong mb = ma | Game.ToCardMask(b);
                for (int c = b + 1; c < 48; c++)
                {
                    ulong mc = mb | Game.ToCardMask(c);
                    for (int d = c + 1; d < 49; d++)
                    {
                        ulong md = mc | Game.ToCardMask(d);
                        for (int e = d + 1; e < 50; e++)
                        {
                            ulong me = md | Game.ToCardMask(e);
                            for (int f = e + 1; f < 51; f++)
                            {
                                ulong mf = me | Game.ToCardMask(f);
                                int n = 0;
                                for (int g = f + 1; g < 52; g++)
                                {
                                    masks[n++] = mf | Game.ToCardMask(g);
                                }
                                Game.EvalHandValues(masks[..n], values);
                                for (int i = 0; i < n; i++)
                                {
                                    consumer.Add(values[i]);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // The same best of 21 as poker.net's EvalEngine, with the 7 cards kept in an array updated card by card
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void AllPokerNet<T>(int[] cactusKev, ref T consumer) where T : IConsumer
    {
        Span<int> seven = stackalloc int[7];
        for (int a = 0; a < 46; a++)
        {
            seven[0] = cactusKev[a];
            for (int b = a + 1; b < 47; b++)
            {
                seven[1] = cactusKev[b];
                for (int c = b + 1; c < 48; c++)
                {
                    seven[2] = cactusKev[c];
                    for (int d = c + 1; d < 49; d++)
                    {
                        seven[3] = cactusKev[d];
                        for (int e = d + 1; e < 50; e++)
                        {
                            seven[4] = cactusKev[e];
                            for (int f = e + 1; f < 51; f++)
                            {
                                seven[5] = cactusKev[f];
                                for (int g = f + 1; g < 52; g++)
                                {
                                    seven[6] = cactusKev[g];
                                    consumer.Add(BestOf21(seven));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ushort BestOf21(ReadOnlySpan<int> seven)
    {
        var perm = PokerLib.Perm7Indices;
        ushort best = ushort.MaxValue;
        for (int row = 0; row < 21; row++)
        {
            int i = row * 5;
            ushort s = PokerLib.Eval5CardsFast(seven[perm[i]], seven[perm[i + 1]], seven[perm[i + 2]], seven[perm[i + 3]], seven[perm[i + 4]]);
            if (s < best)
            {
                best = s;
            }
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void BoardsRank<T>(byte[] boards, ref T consumer) where T : IConsumer
    {
        Span<int> rest = stackalloc int[52];
        Span<int> board = stackalloc int[5];
        for (int at = 0; at < boards.Length; at += 5)
        {
            for (int i = 0; i < 5; i++)
            {
                board[i] = boards[at + i];
            }
            int n = Remaining(boards.AsSpan(at, 5), rest);
            var vBoard = Game.HandKeyMask(board);
            for (int i = 0; i < n - 1; i++)
            {
                var v6 = vBoard + Game.HandKeyMask(rest[i]);
                for (int j = i + 1; j < n; j++)
                {
                    consumer.Add(Game.EvalHandRank(v6 + Game.HandKeyMask(rest[j])));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void BoardsValue<T>(byte[] boards, ref T consumer) where T : IConsumer
    {
        Span<int> rest = stackalloc int[52];
        for (int at = 0; at < boards.Length; at += 5)
        {
            int n = Remaining(boards.AsSpan(at, 5), rest);
            ulong boardMask = 0;
            for (int i = 0; i < 5; i++)
            {
                boardMask |= Game.ToCardMask(boards[at + i]);
            }
            for (int i = 0; i < n - 1; i++)
            {
                ulong m6 = boardMask | Game.ToCardMask(rest[i]);
                for (int j = i + 1; j < n; j++)
                {
                    consumer.Add(Game.EvalHandValue(m6 | Game.ToCardMask(rest[j])));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void BoardsBatch<T>(byte[] boards, ref T consumer) where T : IConsumer
    {
        Span<int> rest = stackalloc int[52];
        var masks = new ulong[1081];
        var values = new long[1081];
        for (int at = 0; at < boards.Length; at += 5)
        {
            int n = Remaining(boards.AsSpan(at, 5), rest);
            ulong boardMask = 0;
            for (int i = 0; i < 5; i++)
            {
                boardMask |= Game.ToCardMask(boards[at + i]);
            }
            int k = 0;
            for (int i = 0; i < n - 1; i++)
            {
                ulong m6 = boardMask | Game.ToCardMask(rest[i]);
                for (int j = i + 1; j < n; j++)
                {
                    masks[k++] = m6 | Game.ToCardMask(rest[j]);
                }
            }
            Game.EvalHandValues(masks, values);
            for (int i = 0; i < k; i++)
            {
                consumer.Add(values[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void BoardsPokerNet<T>(byte[] boards, int[] cactusKev, ref T consumer) where T : IConsumer
    {
        Span<int> rest = stackalloc int[52];
        Span<int> seven = stackalloc int[7];
        for (int at = 0; at < boards.Length; at += 5)
        {
            int n = Remaining(boards.AsSpan(at, 5), rest);
            for (int i = 0; i < 5; i++)
            {
                seven[i] = cactusKev[boards[at + i]];
            }
            for (int i = 0; i < n - 1; i++)
            {
                seven[5] = cactusKev[rest[i]];
                for (int j = i + 1; j < n; j++)
                {
                    seven[6] = cactusKev[rest[j]];
                    consumer.Add(BestOf21(seven));
                }
            }
        }
    }
}
