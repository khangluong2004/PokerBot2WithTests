using PokerBot2;
using System.Runtime.Intrinsics;

namespace PokerBot2UnitTests
{
    // Game.EvalHandRank (table-based, exactly 7 cards) must order hands exactly like Game.EvalHandValue.
    [TestFixture]
    public class GameEvalHandRankTest
    {
        private static int[] RandomHand(Random random, int[] deck)
        {
            for (int i = 0; i < 7; i++)
            {
                int j = random.Next(i, 52);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }
            return deck[..7];
        }

        // Collects (EvalHandValue, EvalHandRank) pairs. The value -> rank map must be a function (ties agree),
        // strictly increasing (order agrees), and the rank must carry the value's hand type
        private sealed class OrderCheck
        {
            public readonly Dictionary<long, int> Map = new();
            public long Errors;

            public void Add(long value, int rank)
            {
                if ((int)Game.HandRankType(rank) != (int)(value >> 32) || (Map.TryGetValue(value, out int prev) && prev != rank))
                {
                    Errors++;
                }
                Map[value] = rank;
            }

            public void Merge(OrderCheck other)
            {
                Errors += other.Errors;
                foreach (var (value, rank) in other.Map)
                {
                    Add(value, rank);
                }
            }

            public void AssertSameOrder()
            {
                var ranksInValueOrder = Map.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(Errors, Is.EqualTo(0));
                    Assert.That(ranksInValueOrder, Is.Ordered.Ascending);
                    Assert.That(ranksInValueOrder.Distinct().Count(), Is.EqualTo(ranksInValueOrder.Length));
                });
            }
        }

        [Test]
        // Random hands rank like EvalHandValue, and the vector carries the hand's mask.
        public void EvalHandRank_ShouldOrderRandomHandsLikeEvalHandValue()
        {
            var random = new Random(4321);
            var deck = Enumerable.Range(0, 52).ToArray();
            var check = new OrderCheck();
            long maskMismatches = 0;
            for (int i = 0; i < 1_000_000; i++)
            {
                var hand = RandomHand(random, deck);
                ulong mask = Game.ToHandMask(hand);
                var keyMask = Game.HandKeyMask(hand);
                if (keyMask.GetElement(1) != mask)
                {
                    maskMismatches++;
                }
                check.Add(Game.EvalHandValue(mask), Game.EvalHandRank(keyMask));
            }

            Assert.That(maskMismatches, Is.EqualTo(0));
            check.AssertSameOrder();
        }

        [Test]
        // Every 5, 6 and 7-card flush (and straight flush) of every suit, completed with cards of other suits.
        public void EvalHandRank_ShouldRankEveryFlushLikeEvalHandValue()
        {
            var random = new Random(77);
            var check = new OrderCheck();
            for (int suit = 0; suit < 4; suit++)
            {
                for (int ranks = 0; ranks < 1 << 13; ranks++)
                {
                    int numSuited = System.Numerics.BitOperations.PopCount((uint)ranks);
                    if (numSuited < 5 || numSuited > 7)
                    {
                        continue;
                    }
                    var hand = new List<int>();
                    for (int rank = 0; rank < 13; rank++)
                    {
                        if ((ranks >> rank & 1) != 0)
                        {
                            hand.Add(rank * 4 + suit);
                        }
                    }
                    while (hand.Count < 7)
                    {
                        int card = random.Next(52);
                        if (Game.GetSuit(card) != suit && !hand.Contains(card))
                        {
                            hand.Add(card);
                        }
                    }
                    check.Add(Game.EvalHandValue(Game.ToHandMask(hand.ToArray())), Game.EvalHandRank(Game.HandKeyMask(hand.ToArray())));
                }
            }

            check.AssertSameOrder();
            Assert.That(check.Map.Keys.Select(v => (WinHandType)(v >> 32)).Distinct(),
                Is.EquivalentTo(new[] { WinHandType.FLUSH, WinHandType.STRAIGHT_FLUSH }));
        }

        [Test]
        // How the Arena uses it: the board's vector is computed once, and each player adds their 2 hole cards.
        public void HandKeyMask_ShouldAddUp_FromBoardAndHoleCards()
        {
            int[] board = [.. new[] { "2♣", "7♦", "9♥", "J♠", "K♣" }.Select(Game.StringToCard)];
            int[] hole = [Game.StringToCard("K♠"), Game.StringToCard("K♦")];
            var handKeyMask = Game.HandKeyMask(board) + Game.HandKeyMask(hole[0]) + Game.HandKeyMask(hole[1]);

            Assert.Multiple(() =>
            {
                Assert.That(handKeyMask, Is.EqualTo(Game.HandKeyMask([.. board, .. hole])));
                // Element 1 is the hand mask: adding the masks of different cards is the same as OR-ing them
                Assert.That(handKeyMask.GetElement(1), Is.EqualTo(Game.ToHandMask([.. board, .. hole])));
                Assert.That(Game.HandRankType(Game.EvalHandRank(handKeyMask)), Is.EqualTo(WinHandType.THREE_OF_A_KIND));
            });
        }

        [Test]
        public void HandRankType_ShouldMatchEvalHand_ForEachHandType()
        {
            var hands = new Dictionary<WinHandType, string[]>
            {
                [WinHandType.STRAIGHT_FLUSH] = ["A♥", "2♥", "3♥", "4♥", "5♥", "K♣", "K♦"],
                [WinHandType.QUAD] = ["9♠", "9♣", "9♦", "9♥", "2♠", "3♦", "A♣"],
                [WinHandType.FULL_HOUSE] = ["Q♠", "Q♣", "Q♦", "7♥", "7♠", "7♦", "2♣"],
                [WinHandType.FLUSH] = ["2♦", "6♦", "9♦", "J♦", "K♦", "A♦", "A♠"],
                [WinHandType.STRAIGHT] = ["A♠", "2♣", "3♦", "4♥", "5♠", "9♣", "J♦"],
                [WinHandType.THREE_OF_A_KIND] = ["8♠", "8♣", "8♦", "2♥", "5♠", "10♣", "K♦"],
                [WinHandType.TWO_PAIR] = ["8♠", "8♣", "4♦", "4♥", "J♠", "J♣", "2♦"],
                [WinHandType.PAIR] = ["8♠", "8♣", "2♦", "5♥", "10♠", "Q♣", "A♦"],
                [WinHandType.HIGH_CARD] = ["2♠", "4♣", "6♦", "8♥", "10♠", "Q♣", "A♦"],
            };
            Assert.Multiple(() =>
            {
                foreach (var (type, cards) in hands)
                {
                    int[] hand = [.. cards.Select(Game.StringToCard)];
                    Assert.That(Game.EvalHand(hand, 0).winHand, Is.EqualTo(type));
                    Assert.That(Game.HandRankType(Game.EvalHandRank(Game.HandKeyMask(hand))), Is.EqualTo(type), type.ToString());
                }
            });
        }

        [Test, Explicit("Exhaustive over all 133,784,560 seven-card hands, takes about 10 seconds")]
        public void EvalHandRank_ShouldOrderAllSevenCardHandsLikeEvalHandValue()
        {
            var checks = new System.Collections.Concurrent.ConcurrentBag<OrderCheck>();
            long maskMismatches = 0;
            var empty = Game.HandKeyMask(ReadOnlySpan<int>.Empty);
            Parallel.For(0, 52, () => new OrderCheck(), (a, _, check) =>
            {
                for (int b = a + 1; b < 52; b++)
                for (int c = b + 1; c < 52; c++)
                for (int d = c + 1; d < 52; d++)
                for (int e = d + 1; e < 52; e++)
                for (int f = e + 1; f < 52; f++)
                for (int g = f + 1; g < 52; g++)
                {
                    ulong mask = Game.ToCardMask(a) | Game.ToCardMask(b) | Game.ToCardMask(c) | Game.ToCardMask(d)
                        | Game.ToCardMask(e) | Game.ToCardMask(f) | Game.ToCardMask(g);
                    var keyMask = empty + Game.HandKeyMask(a) + Game.HandKeyMask(b) + Game.HandKeyMask(c)
                        + Game.HandKeyMask(d) + Game.HandKeyMask(e) + Game.HandKeyMask(f) + Game.HandKeyMask(g);
                    if (keyMask.GetElement(1) != mask)
                    {
                        Interlocked.Increment(ref maskMismatches);
                    }
                    check.Add(Game.EvalHandValue(mask), Game.EvalHandRank(keyMask));
                }
                return check;
            }, checks.Add);

            var merged = new OrderCheck();
            foreach (var check in checks)
            {
                merged.Merge(check);
            }
            Assert.That(maskMismatches, Is.EqualTo(0));
            // Known number of distinct 7-card hand strengths
            Assert.That(merged.Map, Has.Count.EqualTo(4824));
            merged.AssertSameOrder();
        }
    }
}
