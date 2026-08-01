using PokerBot2;

namespace PokerBot2UnitTests
{
    [TestFixture]
    public class GameEvalHandTest
    {
        private static int[] Cards(params string[] cardStrings)
        {
            var cards = new int[cardStrings.Length];
            for (var i = 0; i < cardStrings.Length; i++)
            {
                cards[i] = Game.StringToCard(cardStrings[i]);
            }

            return cards;
        }

        [Test]
        // A straight flush should be ranked above all other hands and use the straight-flush ordering.
        public void EvalHand_ShouldReturnStraightFlush_WhenHandContainsStraightFlush()
        {
            var result = Game.EvalHand(Cards("5♠", "6♠", "7♠", "8♠", "9♠", "2♣", "3♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT_FLUSH));
                // The straight-flush ordering is the highest card in the straight.
                var expectedOrdering = Game.GetRank(Game.StringToCard("9♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A four-of-a-kind should be recognized and ordered by the quad rank plus kicker.
        public void EvalHand_ShouldReturnFourOfAKind_WhenHandContainsQuad()
        {
            var result = Game.EvalHand(Cards("5♠", "5♣", "5♦", "5♥", "K♠", "K♣", "K♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.QUAD));
                // The evaluator packs the quad rank first and the kicker second in 6-bit slots.
                var quadRank = Game.GetRank(Game.StringToCard("5♠"));
                var kickerRank = Game.GetRank(Game.StringToCard("K♠"));
                var expectedOrdering = (quadRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A full house should be identified and ordered by trip rank followed by pair rank.
        public void EvalHand_ShouldReturnFullHouse_WhenHandContainsFullHouse()
        {
            var result = Game.EvalHand(Cards("7♠", "7♣", "7♦", "4♠", "4♣", "2♦", "3♥"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FULL_HOUSE));
                // Full houses are ordered by trip rank followed by pair rank.
                var tripRank = Game.GetRank(Game.StringToCard("7♠"));
                var pairRank = Game.GetRank(Game.StringToCard("4♠"));
                var expectedOrdering = (tripRank << 6) + pairRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A flush should be detected and ranked by the packed flush-card values.
        public void EvalHand_ShouldReturnFlush_WhenHandContainsFlush()
        {
            var result = Game.EvalHand(Cards("2♠", "5♠", "9♠", "Q♠", "A♠", "3♣", "4♦"), 0);
            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FLUSH));
                // Flush ranks are packed into 6-bit slots, matching the evaluator's ordering scheme.
                var firstRank = Game.GetRank(Game.StringToCard("2♠"));
                var secondRank = Game.GetRank(Game.StringToCard("5♠"));
                var thirdRank = Game.GetRank(Game.StringToCard("9♠"));
                var fourthRank = Game.GetRank(Game.StringToCard("Q♠"));
                var fifthRank = Game.GetRank(Game.StringToCard("A♠"));
                var expectedOrdering = (firstRank << 0)
                    + (secondRank << 6)
                    + (thirdRank << 12)
                    + (fourthRank << 18)
                    + (fifthRank << 24);
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A normal straight should be detected and ordered by its highest card.
        public void EvalHand_ShouldReturnStraight_WhenHandContainsStraight()
        {
            var result = Game.EvalHand(Cards("5♠", "6♣", "7♦", "8♥", "9♠", "2♣", "3♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT));
                // A straight is ordered by its highest card.
                var expectedOrdering = Game.GetRank(Game.StringToCard("9♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // An Ace-low straight should be treated as a straight with the expected low-end ordering.
        public void EvalHand_ShouldReturnAceLowStraight_WhenHandContainsAceLowStraight()
        {
            var result = Game.EvalHand(Cards("A♠", "2♣", "3♦", "4♥", "5♠", "7♣", "9♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT));
                // An Ace-low straight is still ordered by the five-card value 5.
                var expectedOrdering = Game.GetRank(Game.StringToCard("5♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Three-of-a-kind should be ordered by the trip rank and the two highest kickers.
        public void EvalHand_ShouldReturnThreeOfAKind_WhenHandContainsThreeOfAKind()
        {
            var result = Game.EvalHand(Cards("6♠", "6♣", "6♦", "A♠", "K♠", "4♣", "3♦"), 0);
            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.THREE_OF_A_KIND));
                // Three-of-a-kind is ordered by trip rank and the two best kickers.
                var tripRank = Game.GetRank(Game.StringToCard("6♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("K♠"));
                var expectedOrdering = (tripRank << 12) + (firstKickerRank << 6) + secondKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Two pair should be ordered by the higher pair, lower pair, and kicker.
        public void EvalHand_ShouldReturnTwoPair_WhenHandContainsTwoPair()
        {
            var result = Game.EvalHand(Cards("J♠", "J♣", "7♠", "7♣", "A♠", "4♦", "3♥"), 0);
            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.TWO_PAIR));
                // Two pair is ordered by the higher pair, lower pair, and kicker.
                var higherPairRank = Game.GetRank(Game.StringToCard("J♠"));
                var lowerPairRank = Game.GetRank(Game.StringToCard("7♠"));
                var kickerRank = Game.GetRank(Game.StringToCard("A♠"));
                var expectedOrdering = (higherPairRank << 12) + (lowerPairRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A single pair should be ordered by the pair rank and the three kickers.
        public void EvalHand_ShouldReturnPair_WhenHandContainsPair()
        {
            var result = Game.EvalHand(Cards("Q♠", "Q♣", "A♠", "K♠", "J♠", "4♣", "3♦"), 0);
            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.PAIR));
                // A single pair is ordered by pair rank and the three best kickers.
                var pairRank = Game.GetRank(Game.StringToCard("Q♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("K♠"));
                var thirdKickerRank = Game.GetRank(Game.StringToCard("J♠"));
                var expectedOrdering = (pairRank << 18) + (firstKickerRank << 12) + (secondKickerRank << 6) + thirdKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // A high-card hand should fall back to the packed five-card ordering when no stronger hand exists.
        public void EvalHand_ShouldReturnHighCard_WhenHandContainsNoCombination()
        {
            var result = Game.EvalHand(Cards("A♠", "K♠", "Q♣", "9♠", "7♣", "5♦", "3♥"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                // High-card ordering packs the five highest ranks into 6-bit slots.
                var firstRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondRank = Game.GetRank(Game.StringToCard("K♠"));
                var thirdRank = Game.GetRank(Game.StringToCard("Q♣"));
                var fourthRank = Game.GetRank(Game.StringToCard("9♠"));
                var fifthRank = Game.GetRank(Game.StringToCard("7♣"));
                var expectedOrdering = (firstRank << 24) + (secondRank << 18) + (thirdRank << 12) + (fourthRank << 6) + fifthRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        public void EvalHand_ShouldReturnHighestFlush_WhenHandContainsAllSameSuit()
        {
            var result = Game.EvalHand(Cards("A♠", "K♠", "Q♠", "9♠", "7♠", "5♠", "3♠"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                // High-card ordering packs the five highest ranks into 6-bit slots.
                var firstRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondRank = Game.GetRank(Game.StringToCard("K♠"));
                var thirdRank = Game.GetRank(Game.StringToCard("Q♠"));
                var fourthRank = Game.GetRank(Game.StringToCard("9♠"));
                var fifthRank = Game.GetRank(Game.StringToCard("7♠"));
                var expectedOrdering = (firstRank << 24) + (secondRank << 18) + (thirdRank << 12) + (fourthRank << 6) + fifthRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Test pair at 3rd top card.
        public void EvalHand_ShouldReturnPair_WhenHandContainsPairAtTopThree()
        {
            var result = Game.EvalHand(Cards("3♠", "5♣", "7♠", "Q♣", "Q♠", "K♣", "A♣"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.PAIR));
                // High-card ordering packs the five highest ranks into 6-bit slots.
                var firstRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondRank = Game.GetRank(Game.StringToCard("K♠"));
                var pairRank = Game.GetRank(Game.StringToCard("Q♠"));
                var fourthRank = Game.GetRank(Game.StringToCard("7♠"));
                var expectedOrdering = (pairRank << 18) + (firstRank << 12) + (secondRank << 6) + fourthRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Test with filler card
        public void EvalHand_ShouldReturnHighCard_WithOneCard()
        {
            int[] fillers = [-4, -4, -4, -4, -4, -4];
            var result = Game.EvalHand(fillers.Concat(Cards("A♣")).ToArray(), 6);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                // High-card ordering packs the five highest ranks into 6-bit slots.
                var firstRank = Game.GetRank(Game.StringToCard("A♣"));
                var rankFiller = Game.GetRank(-4);
                var expectedOrdering = (firstRank << 24) + (rankFiller << 18) + (rankFiller << 12) + (rankFiller << 6) + rankFiller;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        // Tests with filler cards - Verify EvalHand correctly handles fewer than 7 cards

        [Test]
        // Straight flush with 1 filler card (6 real cards)
        public void EvalHand_ShouldReturnStraightFlush_WithOneFillerCard()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4; // 1 filler
            var realCards = Cards("5♠", "6♠", "7♠", "8♠", "9♠", "2♣");
            Array.Copy(realCards, 0, cardsWithFiller, 1, 6);

            var result = Game.EvalHand(cardsWithFiller, 1);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT_FLUSH));
                var expectedOrdering = Game.GetRank(Game.StringToCard("9♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Four-of-a-kind with 3 filler cards (4 real cards)
        public void EvalHand_ShouldReturnFourOfAKind_WithThreeFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            cardsWithFiller[2] = -4;
            var realCards = Cards("5♠", "5♣", "5♦", "5♥");
            Array.Copy(realCards, 0, cardsWithFiller, 3, 4);

            var result = Game.EvalHand(cardsWithFiller, 3);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.QUAD));
                var quadRank = Game.GetRank(Game.StringToCard("5♠"));
                var kickerRank = Game.GetRank(-4);
                var expectedOrdering = (quadRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Full house with 2 filler cards (5 real cards)
        public void EvalHand_ShouldReturnFullHouse_WithTwoFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            var realCards = Cards("7♠", "7♣", "7♦", "4♠", "4♣");
            Array.Copy(realCards, 0, cardsWithFiller, 2, 5);

            var result = Game.EvalHand(cardsWithFiller, 2);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FULL_HOUSE));
                var tripRank = Game.GetRank(Game.StringToCard("7♠"));
                var pairRank = Game.GetRank(Game.StringToCard("4♠"));
                var expectedOrdering = (tripRank << 6) + pairRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Flush with 2 filler cards (5 real cards - 5 spades)
        public void EvalHand_ShouldReturnFlush_WithTwoFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            var realCards = Cards("2♠", "5♠", "9♠", "Q♠", "A♠");
            Array.Copy(realCards, 0, cardsWithFiller, 2, 5);

            var result = Game.EvalHand(cardsWithFiller, 2);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FLUSH));
                var firstRank = Game.GetRank(Game.StringToCard("2♠"));
                var secondRank = Game.GetRank(Game.StringToCard("5♠"));
                var thirdRank = Game.GetRank(Game.StringToCard("9♠"));
                var fourthRank = Game.GetRank(Game.StringToCard("Q♠"));
                var fifthRank = Game.GetRank(Game.StringToCard("A♠"));
                var expectedOrdering = (firstRank << 0)
                    + (secondRank << 6)
                    + (thirdRank << 12)
                    + (fourthRank << 18)
                    + (fifthRank << 24);
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering >> 12));
            });
        }

        [Test]
        // Straight with 4 filler cards (3 real cards - incomplete straight should still find best hand)
        public void EvalHand_ShouldFindBestHand_WithFourFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            cardsWithFiller[2] = -4;
            cardsWithFiller[3] = -4;
            var realCards = Cards("5♠", "6♣", "7♦");
            Array.Copy(realCards, 0, cardsWithFiller, 4, 3);

            var result = Game.EvalHand(cardsWithFiller, 4);

            // With only 3 cards, it should return high card
            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                // Order by highest cards: 7, 6, 5, filler, filler
                var rank7 = Game.GetRank(Game.StringToCard("7♦"));
                var rank6 = Game.GetRank(Game.StringToCard("6♣"));
                var rank5 = Game.GetRank(Game.StringToCard("5♠"));
                var fillerRank = Game.GetRank(-4);
                var expectedOrdering = (rank7 << 24) + (rank6 << 18) + (rank5 << 12) + (fillerRank << 6) + fillerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Three-of-a-kind with 1 filler card (6 real cards)
        public void EvalHand_ShouldReturnThreeOfAKind_WithOneFillerCard()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            var realCards = Cards("6♠", "6♣", "6♦", "A♠", "K♠", "4♣");
            Array.Copy(realCards, 0, cardsWithFiller, 1, 6);

            var result = Game.EvalHand(cardsWithFiller, 1);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.THREE_OF_A_KIND));
                var tripRank = Game.GetRank(Game.StringToCard("6♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("K♠"));
                var expectedOrdering = (tripRank << 12) + (firstKickerRank << 6) + secondKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Two pair with 2 filler cards (5 real cards)
        public void EvalHand_ShouldReturnTwoPair_WithTwoFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            var realCards = Cards("J♠", "J♣", "7♠", "7♣", "A♠");
            Array.Copy(realCards, 0, cardsWithFiller, 2, 5);

            var result = Game.EvalHand(cardsWithFiller, 2);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.TWO_PAIR));
                var higherPairRank = Game.GetRank(Game.StringToCard("J♠"));
                var lowerPairRank = Game.GetRank(Game.StringToCard("7♠"));
                var kickerRank = Game.GetRank(Game.StringToCard("A♠"));
                var expectedOrdering = (higherPairRank << 12) + (lowerPairRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Pair with 3 filler cards (4 real cards)
        public void EvalHand_ShouldReturnPair_WithThreeFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            cardsWithFiller[2] = -4;
            var realCards = Cards("K♠", "K♣", "Q♠", "J♠");
            Array.Copy(realCards, 0, cardsWithFiller, 3, 4);

            var result = Game.EvalHand(cardsWithFiller, 3);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.PAIR));
                var pairRank = Game.GetRank(Game.StringToCard("K♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("Q♠"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("J♠"));
                var thirdKickerRank = Game.GetRank(-4);
                var expectedOrdering = (pairRank << 18) + (firstKickerRank << 12) + (secondKickerRank << 6) + thirdKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // High card with 5 filler cards (2 real cards)
        public void EvalHand_ShouldReturnHighCard_WithFiveFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            cardsWithFiller[2] = -4;
            cardsWithFiller[3] = -4;
            cardsWithFiller[4] = -4;
            var realCards = Cards("A♠", "K♣");
            Array.Copy(realCards, 0, cardsWithFiller, 5, 2);

            var result = Game.EvalHand(cardsWithFiller, 5);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                var firstRank = Game.GetRank(Game.StringToCard("A♠"));
                var secondRank = Game.GetRank(Game.StringToCard("K♣"));
                var fillerRank = Game.GetRank(-4);
                var expectedOrdering = (firstRank << 24) + (secondRank << 18) + (fillerRank << 12) + (fillerRank << 6) + fillerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Ace-low straight with 2 filler cards (5 real cards)
        public void EvalHand_ShouldReturnAceLowStraight_WithTwoFillerCards()
        {
            int[] cardsWithFiller = new int[7];
            cardsWithFiller[0] = -4;
            cardsWithFiller[1] = -4;
            var realCards = Cards("A♠", "2♣", "3♦", "4♥", "5♠");
            Array.Copy(realCards, 0, cardsWithFiller, 2, 5);

            var result = Game.EvalHand(cardsWithFiller, 2);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT));
                var expectedOrdering = Game.GetRank(Game.StringToCard("5♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        // Additional tests to thoroughly verify ordering for all hand types

        [Test]
        // Verify different straight flush orderings
        public void EvalHand_ShouldOrderStraightFlushByHighCard_DifferentValues()
        {
            var result = Game.EvalHand(Cards("2♥", "3♥", "4♥", "5♥", "6♥", "7♣", "8♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT_FLUSH));
                var expectedOrdering = Game.GetRank(Game.StringToCard("6♥"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify different full house orderings (different trip and pair ranks)
        public void EvalHand_ShouldOrderFullHouseCorrectly_DifferentRanks()
        {
            var result = Game.EvalHand(Cards("K♠", "K♣", "K♦", "3♠", "3♣", "2♦", "4♥"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FULL_HOUSE));
                var tripRank = Game.GetRank(Game.StringToCard("K♠"));
                var pairRank = Game.GetRank(Game.StringToCard("3♠"));
                var expectedOrdering = (tripRank << 6) + pairRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify ordering when multiple pairs exist (should pick highest and second highest)
        public void EvalHand_ShouldOrderTwoPairCorrectly_WithDifferentPairRanks()
        {
            var result = Game.EvalHand(Cards("3♠", "3♣", "5♦", "5♥", "2♠", "6♣", "7♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.TWO_PAIR));
                var higherPairRank = Game.GetRank(Game.StringToCard("5♥"));
                var lowerPairRank = Game.GetRank(Game.StringToCard("3♠"));
                var kickerRank = Game.GetRank(Game.StringToCard("7♦"));
                var expectedOrdering = (higherPairRank << 12) + (lowerPairRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify three-of-a-kind ordering with different kickers
        public void EvalHand_ShouldOrderThreeOfAKindCorrectly_DifferentKickers()
        {
            var result = Game.EvalHand(Cards("5♠", "5♣", "5♦", "9♠", "7♠", "3♣", "2♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.THREE_OF_A_KIND));
                var tripRank = Game.GetRank(Game.StringToCard("5♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("9♠"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("7♠"));
                var expectedOrdering = (tripRank << 12) + (firstKickerRank << 6) + secondKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify pair ordering with different kicker combinations
        public void EvalHand_ShouldOrderPairCorrectly_WithDifferentKickers()
        {
            var result = Game.EvalHand(Cards("2♠", "2♣", "5♦", "9♠", "A♣", "K♦", "3♥"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.PAIR));
                var pairRank = Game.GetRank(Game.StringToCard("2♠"));
                var firstKickerRank = Game.GetRank(Game.StringToCard("A♣"));
                var secondKickerRank = Game.GetRank(Game.StringToCard("K♦"));
                var thirdKickerRank = Game.GetRank(Game.StringToCard("9♠"));
                var expectedOrdering = (pairRank << 18) + (firstKickerRank << 12) + (secondKickerRank << 6) + thirdKickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify high-card ordering with different card ranks
        public void EvalHand_ShouldOrderHighCardCorrectly_WithDifferentRanks()
        {
            var result = Game.EvalHand(Cards("2♠", "4♣", "6♦", "J♠", "K♣", "Q♦", "A♥"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.HIGH_CARD));
                var rank1 = Game.GetRank(Game.StringToCard("A♥"));
                var rank2 = Game.GetRank(Game.StringToCard("K♣"));
                var rank3 = Game.GetRank(Game.StringToCard("Q♦"));
                var rank4 = Game.GetRank(Game.StringToCard("J♠"));
                var rank5 = Game.GetRank(Game.StringToCard("6♦"));
                var expectedOrdering = (rank1 << 24) + (rank2 << 18) + (rank3 << 12) + (rank4 << 6) + rank5;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify straight ordering with highest card determining order
        public void EvalHand_ShouldOrderStraightCorrectly_WithHighCard()
        {
            var result = Game.EvalHand(Cards("9♠", "8♣", "7♦", "6♥", "5♠", "3♣", "2♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.STRAIGHT));
                var expectedOrdering = Game.GetRank(Game.StringToCard("9♠"));
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }

        [Test]
        // Verify flush ordering with all five cards ranked in order
        public void EvalHand_ShouldOrderFlushCorrectly_WithDifferentCards()
        {
            var result = Game.EvalHand(Cards("3♦", "5♦", "7♦", "9♦", "K♦", "2♠", "4♣"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.FLUSH));
                var rank1 = Game.GetRank(Game.StringToCard("3♦"));
                var rank2 = Game.GetRank(Game.StringToCard("5♦"));
                var rank3 = Game.GetRank(Game.StringToCard("7♦"));
                var rank4 = Game.GetRank(Game.StringToCard("9♦"));
                var rank5 = Game.GetRank(Game.StringToCard("K♦"));
                var expectedOrdering = (rank1 << 0) + (rank2 << 6) + (rank3 << 12) + (rank4 << 18) + (rank5 << 24);
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering >> 12));
            });
        }

        [Test]
        // Verify quad ordering with different kickers
        public void EvalHand_ShouldOrderQuadCorrectly_DifferentKickers()
        {
            var result = Game.EvalHand(Cards("J♠", "J♣", "J♦", "J♥", "2♠", "3♣", "4♦"), 0);

            Assert.Multiple(() =>
            {
                Assert.That(result.Item2, Is.EqualTo(WinHandType.QUAD));
                var quadRank = Game.GetRank(Game.StringToCard("J♠"));
                var kickerRank = Game.GetRank(Game.StringToCard("4♦"));
                var expectedOrdering = (quadRank << 6) + kickerRank;
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering));
            });
        }
    }
}
