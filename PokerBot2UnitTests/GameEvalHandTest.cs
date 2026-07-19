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
                Assert.That(result.Item1, Is.EqualTo(expectedOrdering >> 12));
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
                var thirdKickerRank = Game.GetRank(Game.StringToCard("Q♣"));
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
    }
}
