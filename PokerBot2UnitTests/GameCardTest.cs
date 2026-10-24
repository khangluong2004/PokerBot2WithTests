using PokerBot2;

namespace PokerBot2UnitTests
{
    [TestFixture]
    public class GameCardTest
    {
        [Test]
        // Every card prints as rank + suit and parses back to itself, including the ten ("10").
        public void CardToString_ShouldRoundTripThroughStringToCard()
        {
            for (int card = 0; card < 52; card++)
            {
                Assert.That(Game.StringToCard(Game.CardToString(card)), Is.EqualTo(card), Game.CardToString(card));
            }
        }

        [Test]
        public void CardToString_ShouldUseStandardRankNames()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Game.CardToString(Game.StringToCard("2♠")), Is.EqualTo("2♠"));
                Assert.That(Game.CardToString(0), Is.EqualTo("2♠"));
                Assert.That(Game.GetRank(Game.StringToCard("9♦")), Is.EqualTo(7));
                Assert.That(Game.GetRank(Game.StringToCard("10♥")), Is.EqualTo(8));
                Assert.That(Game.CardToString(8 * 4 + 3), Is.EqualTo("10♥"));
                Assert.That(Game.GetRank(Game.StringToCard("J♣")), Is.EqualTo(9));
                Assert.That(Game.CardToString(51), Is.EqualTo("A♥"));
            });
        }

        [Test]
        // A hand never deals the same card twice, across many hands on the same deck.
        public void DealCard_ShouldNotRepeatCardsWithinAHand()
        {
            var game = new Game(9, 7);
            var seen = new HashSet<int>();
            for (int hand = 0; hand < 10000; hand++)
            {
                game.Reset();
                seen.Clear();
                for (int i = 0; i < 52; i++)
                {
                    int card = game.DealCard();
                    Assert.That(card, Is.InRange(0, 51));
                    Assert.That(seen.Add(card), Is.True, $"Card {card} dealt twice in hand {hand}");
                }
            }
        }

        [Test]
        // Each card is equally likely at every position (chi-square test on the first and the 23rd card).
        public void DealCard_ShouldDealUniformly()
        {
            var game = new Game(9, 11);
            const int hands = 520000;
            var firstCounts = new int[52];
            var lastCounts = new int[52];
            for (int hand = 0; hand < hands; hand++)
            {
                game.Reset();
                firstCounts[game.DealCard()]++;
                // 9 players: 18 hole cards + 5 board cards
                for (int i = 1; i < 22; i++)
                {
                    game.DealCard();
                }
                lastCounts[game.DealCard()]++;
            }

            static double ChiSquare(int[] counts, double expected) => counts.Sum(c => (c - expected) * (c - expected) / expected);
            // 51 degrees of freedom: p = 0.001 critical value is about 87
            Assert.Multiple(() =>
            {
                Assert.That(ChiSquare(firstCounts, hands / 52.0), Is.LessThan(87));
                Assert.That(ChiSquare(lastCounts, hands / 52.0), Is.LessThan(87));
            });
        }
    }
}
