using PokerBot2;

namespace PokerBot2UnitTests
{
    // Checks EvalHand against an independent, textbook evaluator: the best of the 21 five-card subsets.
    [TestFixture]
    public class EvalHandReferenceTest
    {
        // Value = category << 20 | ranks of the groups ordered by (count desc, rank desc), 4 bits each.
        // Category uses the same numbers as WinHandType.
        private static long ReferenceRank5(int c0, int c1, int c2, int c3, int c4)
        {
            Span<int> cards = [c0, c1, c2, c3, c4];
            Span<int> count = stackalloc int[13];
            bool flush = true;
            int suit = Game.GetSuit(cards[0]), rankMask = 0;
            foreach (var card in cards)
            {
                count[Game.GetRank(card)]++;
                rankMask |= 1 << Game.GetRank(card);
                flush &= Game.GetSuit(card) == suit;
            }

            int straightTop = -1;
            for (int top = 12; top >= 4 && straightTop < 0; top--)
            {
                if (((rankMask >> (top - 4)) & 0x1F) == 0x1F)
                {
                    straightTop = top;
                }
            }
            // Wheel: A-2-3-4-5, the 5 is the top card
            if (straightTop < 0 && (rankMask & 0b1_0000_0000_1111) == 0b1_0000_0000_1111)
            {
                straightTop = 3;
            }

            long packed = 0;
            int numGroups = 0, pairs = 0, maxCount = 0;
            bool trip = false;
            for (int groupSize = 4; groupSize >= 1; groupSize--)
            {
                for (int rank = 12; rank >= 0; rank--)
                {
                    if (count[rank] == groupSize)
                    {
                        packed = (packed << 4) | (uint)rank;
                        numGroups++;
                        maxCount = Math.Max(maxCount, groupSize);
                        pairs += groupSize == 2 ? 1 : 0;
                        trip |= groupSize == 3;
                    }
                }
            }
            packed <<= 4 * (5 - numGroups);

            int category;
            if (straightTop >= 0 && flush) { category = 9; packed = straightTop; }
            else if (maxCount == 4) category = 8;
            else if (trip && pairs == 1) category = 7;
            else if (flush) category = 6;
            else if (straightTop >= 0) { category = 5; packed = straightTop; }
            else if (trip) category = 4;
            else if (pairs == 2) category = 3;
            else if (pairs == 1) category = 2;
            else category = 1;
            return ((long)category << 20) | packed;
        }

        private static long ReferenceBest(ReadOnlySpan<int> cards)
        {
            long best = -1;
            int n = cards.Length;
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            for (int c = b + 1; c < n; c++)
            for (int d = c + 1; d < n; d++)
            for (int e = d + 1; e < n; e++)
            {
                best = Math.Max(best, ReferenceRank5(cards[a], cards[b], cards[c], cards[d], cards[e]));
            }
            return best;
        }

        private static long Eval(int[] cards)
        {
            return Game.ToEvalValue(Game.EvalHand((int[]) cards.Clone(), 0));
        }

        private static int[] Cards(params string[] cardStrings)
        {
            return [.. cardStrings.Select(Game.StringToCard)];
        }

        private static int[] RandomHand(Random random, int numCard)
        {
            var deck = Enumerable.Range(0, 52).ToArray();
            for (int i = 0; i < numCard; i++)
            {
                int j = random.Next(i, 52);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }
            return deck[..numCard];
        }

        [Test]
        // Regression: 6 and 7 card flushes overflowed the int shift (rank << 30, rank << 36) and were ranked wrongly.
        public void EvalHand_ShouldRankSixAndSevenCardFlushesByTopFiveCards()
        {
            var sevenSpades = Eval(Cards("A♠", "K♠", "Q♠", "9♠", "7♠", "5♠", "3♠"));
            var sameTopFive = Eval(Cards("A♠", "K♠", "Q♠", "9♠", "7♠", "2♠", "3♦"));
            var sixHigherFlush = Eval(Cards("A♥", "K♥", "Q♥", "J♥", "3♥", "2♥", "5♣"));
            var sixLowerFlush = Eval(Cards("A♥", "K♥", "Q♥", "9♥", "3♥", "2♥", "5♣"));

            Assert.Multiple(() =>
            {
                Assert.That(sevenSpades >> 32, Is.EqualTo((long) WinHandType.FLUSH));
                Assert.That(sixHigherFlush >> 32, Is.EqualTo((long) WinHandType.FLUSH));
                Assert.That(sevenSpades, Is.EqualTo(sameTopFive));
                Assert.That(sixHigherFlush, Is.GreaterThan(sixLowerFlush));
                // Any flush beats any straight
                Assert.That(sixLowerFlush, Is.GreaterThan(Eval(Cards("5♣", "6♦", "7♥", "8♠", "9♣", "2♦", "K♠"))));
            });
        }

        [Test]
        // Hands are compared through ToEvalValue, so its order must match the reference order for every pair of hands.
        public void EvalHand_ShouldOrderRandomHandsLikeReference()
        {
            var random = new Random(1234);
            const int numHands = 20000;
            var evalValues = new long[numHands];
            var refValues = new long[numHands];
            for (int i = 0; i < numHands; i++)
            {
                var hand = RandomHand(random, 7);
                evalValues[i] = Eval(hand);
                refValues[i] = ReferenceBest(hand);
                Assert.That((int) (evalValues[i] >> 32), Is.EqualTo((int) (refValues[i] >> 20)),
                    $"Hand type of {string.Join(" ", hand.Select(Game.CardToString))}");
            }

            // Sorting by eval value must also sort by the reference value, and ties must match
            var order = Enumerable.Range(0, numHands).OrderBy(i => evalValues[i]).ToArray();
            for (int k = 1; k < numHands; k++)
            {
                int prev = order[k - 1], cur = order[k];
                Assert.That(Math.Sign(evalValues[cur].CompareTo(evalValues[prev])),
                    Is.EqualTo(Math.Sign(refValues[cur].CompareTo(refValues[prev]))),
                    $"Hands {prev} and {cur}");
            }
        }

        [Test]
        // The new evaluator does not need to sort, so the caller's cards stay as they were.
        public void EvalHand_ShouldNotModifyInput()
        {
            var hand = Cards("K♠", "2♣", "A♦", "7♥", "7♠", "Q♣", "3♦");
            var copy = (int[]) hand.Clone();

            Game.EvalHand(hand, 0);

            Assert.That(hand, Is.EqualTo(copy));
        }

        [Test]
        // The mask API, the array API and the AVX-512 batch API agree, including the scalar tail and filler hands.
        public void EvalHandValues_ShouldMatchScalar_ForAnyBatchSize()
        {
            var random = new Random(99);
            for (int size = 0; size <= 37; size++)
            {
                var masks = new ulong[size];
                var expected = new long[size];
                for (int i = 0; i < size; i++)
                {
                    // Mix full hands with short hands (fillers) to cover the -1 kicker encoding
                    int numCard = 1 + random.Next(7);
                    var cards = RandomHand(random, numCard);
                    var padded = Enumerable.Repeat(-4, 7 - numCard).Concat(cards).ToArray();
                    masks[i] = Game.ToHandMask(cards);
                    expected[i] = Game.ToEvalValue(Game.EvalHand(padded, 7 - numCard));
                    Assert.That(Game.EvalHandValue(masks[i]), Is.EqualTo(expected[i]));
                }

                var values = new long[size];
                Game.EvalHandValues(masks, values);

                Assert.That(values, Is.EqualTo(expected), $"Batch size {size}");
            }
        }

        [Test, Explicit("Exhaustive over all 133,784,560 seven-card hands, takes about a minute")]
        public void EvalHand_ShouldOrderAllSevenCardHandsLikeReference()
        {
            // eval value -> reference value: must be a function (ties agree) and strictly increasing (order agrees)
            var maps = new System.Collections.Concurrent.ConcurrentBag<Dictionary<long, long>>();
            long typeErrors = 0;
            var firstCards = new List<(int, int)>();
            for (int a = 0; a < 52; a++) for (int b = a + 1; b < 52; b++) firstCards.Add((a, b));
            Parallel.ForEach(firstCards, () => new Dictionary<long, long>(), (first, _, map) =>
            {
                var hand = new int[7];
                (hand[0], hand[1]) = first;
                for (int c = hand[1] + 1; c < 52; c++)
                for (int d = c + 1; d < 52; d++)
                for (int e = d + 1; e < 52; e++)
                for (int f = e + 1; f < 52; f++)
                for (int g = f + 1; g < 52; g++)
                {
                    hand[2] = c; hand[3] = d; hand[4] = e; hand[5] = f; hand[6] = g;
                    long eval = Game.EvalHandValue(Game.ToHandMask(hand));
                    long reference = ReferenceBest(hand);
                    if ((eval >> 32) != (reference >> 20))
                    {
                        Interlocked.Increment(ref typeErrors);
                    }
                    if (map.TryGetValue(eval, out var prev) && prev != reference)
                    {
                        Interlocked.Increment(ref typeErrors);
                    }
                    map[eval] = reference;
                }
                return map;
            }, map => maps.Add(map));

            var merged = new Dictionary<long, long>();
            foreach (var map in maps)
            {
                foreach (var (eval, reference) in map)
                {
                    if (merged.TryGetValue(eval, out var prev) && prev != reference)
                    {
                        typeErrors++;
                    }
                    merged[eval] = reference;
                }
            }
            var refInEvalOrder = merged.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(typeErrors, Is.EqualTo(0));
                // Known number of distinct 7-card hand strengths
                Assert.That(merged, Has.Count.EqualTo(4824));
                Assert.That(refInEvalOrder, Is.Ordered.Ascending);
                Assert.That(refInEvalOrder.Distinct().Count(), Is.EqualTo(refInEvalOrder.Length));
            });
        }

        [Test, Explicit("Exhaustive over all 133,784,560 seven-card hands, takes about 20 seconds")]
        public void EvalHandValues_ShouldMatchScalar_ForAllSevenCardHands()
        {
            long mismatches = 0;
            Parallel.For(0, 52, a =>
            {
                var masks = new List<ulong>(1 << 16);
                for (int b = a + 1; b < 52; b++)
                for (int c = b + 1; c < 52; c++)
                for (int d = c + 1; d < 52; d++)
                for (int e = d + 1; e < 52; e++)
                for (int f = e + 1; f < 52; f++)
                for (int g = f + 1; g < 52; g++)
                {
                    masks.Add(Game.ToCardMask(a) | Game.ToCardMask(b) | Game.ToCardMask(c) | Game.ToCardMask(d)
                        | Game.ToCardMask(e) | Game.ToCardMask(f) | Game.ToCardMask(g));
                    if (masks.Count == masks.Capacity || (b == 50 && masks.Count > 0))
                    {
                        var values = new long[masks.Count];
                        Game.EvalHandValues(masks.ToArray(), values);
                        for (int i = 0; i < values.Length; i++)
                        {
                            if (values[i] != Game.EvalHandValue(masks[i]))
                            {
                                Interlocked.Increment(ref mismatches);
                            }
                        }
                        masks.Clear();
                    }
                }
            });

            Assert.That(mismatches, Is.EqualTo(0));
        }
    }
}
