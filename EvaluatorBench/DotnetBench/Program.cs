using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PokerBot2.GameSpace;
using poker.net.Services;

// .NET side of the evaluator benchmark (see ..\README.md).
//   gen [hands]  write data\hands7.bin (random 7-card hands) and data\ranks7.bin (PokerBot2 ranks, the reference)
//   bench        7-card evaluators on data\hands7.bin: PokerBot2 (EvalHandRank and EvalHandValue(s)) and poker.net
//   showdown     PokerBot2 at a showdown: one board, 2 / 6 / 9 players, before (AVX-512 batch) vs now (EvalHandRank)
//   cold         one showdown at a time, with the tables hot, evicted from L2, or evicted to DRAM (see ColdBench.cs)
string mode = args.Length > 0 ? args[0] : "bench";
string dataDir = Path.Combine(FindBenchDir(), "data");
Directory.CreateDirectory(dataDir);
string handsPath = Path.Combine(dataDir, "hands7.bin");
string ranksPath = Path.Combine(dataDir, "ranks7.bin");
Console.WriteLine($".NET {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, Vector512 accelerated: {System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}");

switch (mode)
{
    case "gen":
        Generate(args.Length > 1 ? int.Parse(args[1]) : 2_000_000);
        break;
    case "bench":
        Bench();
        break;
    case "cold":
        ColdBench.Run();
        break;
    case "showdown":
        foreach (int numPlayer in new[] { 2, 6, 9 })
        {
            Showdown(numPlayer);
        }
        break;
    default:
        Console.WriteLine($"Unknown mode {mode}");
        break;
}

// The EvaluatorBench folder above the executable (dotnet run) or above the current folder (a published build)
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

// Random hands: 7 distinct cards each, card = rank * 4 + suit (rank 0 = deuce ... 12 = ace, suit 0-3 = ♠♣♦♥)
void Generate(int numHands)
{
    var random = new Random(20261030);
    var deck = Enumerable.Range(0, 52).ToArray();
    var hands = new byte[numHands * 7];
    var ranks = new int[numHands];
    for (int h = 0; h < numHands; h++)
    {
        for (int i = 0; i < 7; i++)
        {
            int j = random.Next(i, 52);
            (deck[i], deck[j]) = (deck[j], deck[i]);
            hands[h * 7 + i] = (byte)deck[i];
        }
        ranks[h] = Game.EvalHandRank(Game.HandKeyMask(deck.AsSpan(0, 7)));
    }
    File.WriteAllBytes(handsPath, hands);
    var rankBytes = new byte[numHands * 4];
    Buffer.BlockCopy(ranks, 0, rankBytes, 0, rankBytes.Length);
    File.WriteAllBytes(ranksPath, rankBytes);
    Console.WriteLine($"Wrote {numHands:N0} hands to {handsPath} and their ranks to {ranksPath}");
}

void Bench()
{
    // First use builds PokerBot2's tables (static constructor)
    var build = Stopwatch.StartNew();
    Game.HandKeyMask(0);
    Console.WriteLine($"PokerBot2 EvalHandRank tables built in {build.Elapsed.TotalMilliseconds:F0} ms");
    byte[] hands = File.ReadAllBytes(handsPath);
    int numHands = hands.Length / 7;
    var reference = new int[numHands];
    Buffer.BlockCopy(File.ReadAllBytes(ranksPath), 0, reference, 0, numHands * 4);
    Console.WriteLine($"{numHands:N0} random 7-card hands, best of 7 passes, ns per hand (lower is better)\n");

    // Inputs each evaluator takes natively, prepared outside the timed loops
    var masks = new ulong[numHands];
    var keyMasks = new Vector128<ulong>[numHands];
    var cactusKev = new int[numHands * 7];
    for (int h = 0; h < numHands; h++)
    {
        var cards = hands.AsSpan(h * 7, 7);
        keyMasks[h] = Game.HandKeyMask(ReadOnlySpan<int>.Empty);
        for (int i = 0; i < 7; i++)
        {
            masks[h] |= Game.ToCardMask(cards[i]);
            keyMasks[h] += Game.HandKeyMask(cards[i]);
            cactusKev[h * 7 + i] = CactusKevCard(cards[i]);
        }
    }
    var values = new long[numHands];
    var results = new long[numHands];

    void Run(string name, Func<long> pass, Func<int, long> resultOf, bool higherIsBetter)
    {
        // Correctness first: the same order as the reference ranks on every hand
        pass();
        for (int h = 0; h < numHands; h++)
        {
            results[h] = resultOf(h);
        }
        string check = OrderCheck.Check(reference, results, higherIsBetter);
        pass();
        double best = double.MaxValue;
        long checksum = 0;
        for (int r = 0; r < 7; r++)
        {
            var sw = Stopwatch.StartNew();
            checksum += pass();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / numHands);
        }
        Console.WriteLine($"{name,-62} {best,7:F2} ns  {1000 / best,7:F0} M/s  order {check}  (checksum {checksum & 0xFFFF})");
    }

    // PokerBot2 table evaluator from the 7 cards: the vector built in the loop, like the other evaluators' card input
    Run("PokerBot2 EvalHandRank, from 7 cards", () => PassRankFromCards(hands, results),
        h => results[h], true);
    Run("PokerBot2 EvalHandRank, HandKeyMask vector precomputed", () => PassRankFromKeyMask(keyMasks, results),
        h => results[h], true);
    Run("PokerBot2 EvalHandValue(mask), scalar, mask precomputed", () => PassValueFromMask(masks, values),
        h => values[h], true);
    Run("PokerBot2 EvalHandValues(masks), AVX-512 batch", () => PassValuesBatch(masks, values),
        h => values[h], true);
    Run("poker.net, best of 21 x Eval5CardsFast, from 7 cards", () => PassPokerNet(cactusKev, results),
        h => results[h], false);
}

// Cactus Kev card: bit (16 + rank), suit bit (spade 0x1000, heart 0x2000, diamond 0x4000, club 0x8000), rank << 8, prime
static int CactusKevCard(int card)
{
    ReadOnlySpan<int> primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41];
    ReadOnlySpan<int> suits = [PokerLib.SPADE, PokerLib.CLUB, PokerLib.DIAMOND, PokerLib.HEART];
    int rank = Game.GetRank(card);
    return (1 << (16 + rank)) | suits[Game.GetSuit(card)] | (rank << 8) | primes[rank];
}

[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long PassRankFromCards(byte[] hands, long[] results)
{
    long sum = 0;
    var empty = Game.HandKeyMask(ReadOnlySpan<int>.Empty);
    for (int h = 0, i = 0; h < results.Length; h++, i += 7)
    {
        // One bounds check for the 7 cards (the slice), like the C++ loops which have none
        ReadOnlySpan<byte> x = hands.AsSpan(i, 7);
        var keyMask = empty + Game.HandKeyMask(x[0]) + Game.HandKeyMask(x[1]) + Game.HandKeyMask(x[2])
            + Game.HandKeyMask(x[3]) + Game.HandKeyMask(x[4]) + Game.HandKeyMask(x[5]) + Game.HandKeyMask(x[6]);
        int rank = Game.EvalHandRank(keyMask);
        results[h] = rank;
        sum += rank;
    }
    return sum;
}

[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long PassRankFromKeyMask(Vector128<ulong>[] keyMasks, long[] results)
{
    long sum = 0;
    for (int h = 0; h < keyMasks.Length; h++)
    {
        int rank = Game.EvalHandRank(keyMasks[h]);
        results[h] = rank;
        sum += rank;
    }
    return sum;
}

[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long PassValueFromMask(ulong[] masks, long[] values)
{
    long sum = 0;
    for (int h = 0; h < masks.Length; h++)
    {
        long value = Game.EvalHandValue(masks[h]);
        values[h] = value;
        sum += value;
    }
    return sum;
}

[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long PassValuesBatch(ulong[] masks, long[] values)
{
    Game.EvalHandValues(masks, values);
    long sum = 0;
    for (int h = 0; h < values.Length; h++)
    {
        sum += values[h];
    }
    return sum;
}

// Same loop as poker.net's EvalEngine.EvaluateRiverNinePlayersArrays, for one player: the best (lowest) of the
// 21 five-card subsets
[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long PassPokerNet(int[] cards, long[] results)
{
    long sum = 0;
    var perm = PokerLib.Perm7Indices;
    for (int h = 0; h < results.Length; h++)
    {
        var seven = cards.AsSpan(h * 7, 7);
        ushort bestScore = ushort.MaxValue;
        for (int row = 0; row < 21; row++)
        {
            int i = row * 5;
            ushort s = PokerLib.Eval5CardsFast(seven[perm[i]], seven[perm[i + 1]], seven[perm[i + 2]], seven[perm[i + 3]], seven[perm[i + 4]]);
            if (s < bestScore)
            {
                bestScore = s;
            }
        }
        results[h] = bestScore;
        sum += bestScore;
    }
    return sum;
}

// A showdown as the Arena runs it: one board, numPlayer hole-card pairs
void Showdown(int numPlayer)
{
    const int numShowdowns = 1_000_000;
    var random = new Random(numPlayer);
    var deck = Enumerable.Range(0, 52).ToArray();
    int cardsPer = 5 + 2 * numPlayer;
    var cards = new int[numShowdowns * cardsPer];
    for (int s = 0; s < numShowdowns; s++)
    {
        for (int i = 0; i < cardsPer; i++)
        {
            int j = random.Next(i, 52);
            (deck[i], deck[j]) = (deck[j], deck[i]);
            cards[s * cardsPer + i] = deck[i];
        }
    }
    long oldSum = ShowdownOld(cards, numPlayer), newSum = ShowdownNew(cards, numPlayer);
    double oldBest = double.MaxValue, newBest = double.MaxValue;
    for (int r = 0; r < 7; r++)
    {
        var sw = Stopwatch.StartNew();
        oldSum += ShowdownOld(cards, numPlayer);
        oldBest = Math.Min(oldBest, sw.Elapsed.TotalNanoseconds / numShowdowns);
        sw.Restart();
        newSum += ShowdownNew(cards, numPlayer);
        newBest = Math.Min(newBest, sw.Elapsed.TotalNanoseconds / numShowdowns);
    }
    Console.WriteLine($"{numPlayer} players: before (EvalHandValues) {oldBest,6:F1} ns, now (EvalHandRank) {newBest,6:F1} ns per showdown ({oldBest / newBest:F2}x)  (checksums {oldSum & 0xFF} {newSum & 0xFF})");
}

// Before: hand masks from the board mask, then EvalHandValues (AVX-512 for 8 hands at a time, scalar for the rest)
[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long ShowdownOld(int[] cards, int numPlayer)
{
    long sum = 0;
    int cardsPer = 5 + 2 * numPlayer;
    Span<ulong> masks = stackalloc ulong[numPlayer];
    Span<long> values = stackalloc long[numPlayer];
    for (int s = 0; s < cards.Length; s += cardsPer)
    {
        ulong boardMask = Game.ToHandMask(cards.AsSpan(s, 5));
        for (int p = 0, c = s + 5; p < numPlayer; p++, c += 2)
        {
            masks[p] = boardMask | Game.ToCardMask(cards[c]) | Game.ToCardMask(cards[c + 1]);
        }
        Game.EvalHandValues(masks, values);
        long best = 0;
        for (int p = 0; p < numPlayer; p++)
        {
            best = Math.Max(best, values[p]);
        }
        sum += best;
    }
    return sum;
}

// Now: the board's key and mask once, each player adds 2 hole cards and looks up its rank
[MethodImpl(MethodImplOptions.AggressiveOptimization)]
static long ShowdownNew(int[] cards, int numPlayer)
{
    long sum = 0;
    int cardsPer = 5 + 2 * numPlayer;
    Span<int> ranks = stackalloc int[numPlayer];
    for (int s = 0; s < cards.Length; s += cardsPer)
    {
        var board = Game.HandKeyMask(cards.AsSpan(s, 5));
        for (int p = 0, c = s + 5; p < numPlayer; p++, c += 2)
        {
            ranks[p] = Game.EvalHandRank(board + Game.HandKeyMask(cards[c]) + Game.HandKeyMask(cards[c + 1]));
        }
        int best = 0;
        for (int p = 0; p < numPlayer; p++)
        {
            best = Math.Max(best, ranks[p]);
        }
        sum += best;
    }
    return sum;
}

static class OrderCheck
{
    // The evaluator orders hands like the reference when reference rank -> its result is a function (same ties) that is
    // strictly increasing (higherIsBetter) or strictly decreasing
    public static string Check(int[] reference, long[] results, bool higherIsBetter)
    {
        var map = new Dictionary<int, long>();
        long conflicts = 0;
        for (int h = 0; h < reference.Length; h++)
        {
            if (map.TryGetValue(reference[h], out long prev) && prev != results[h])
            {
                conflicts++;
            }
            map[reference[h]] = results[h];
        }
        var inOrder = map.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
        long misordered = 0;
        for (int i = 1; i < inOrder.Length; i++)
        {
            if (higherIsBetter ? inOrder[i] <= inOrder[i - 1] : inOrder[i] >= inOrder[i - 1])
            {
                misordered++;
            }
        }
        return conflicts == 0 && misordered == 0 ? $"ok ({map.Count} classes)" : $"WRONG: {conflicts} tie conflicts, {misordered} out of order";
    }
}
