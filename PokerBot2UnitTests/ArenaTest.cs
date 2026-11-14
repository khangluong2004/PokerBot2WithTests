using PokerBot2;
using PokerBot2.Agents;
using PokerBot2.GameSpace;

namespace PokerBot2UnitTests
{
    [TestFixture]
    public class ArenaTest
    {
        // Agent whose Play decision is supplied by the test, with the stack handled like a real agent
        private class TestAgent : IAgent
        {
            public double Stack = Game.STACK;
            public int Id;
            public (int card1, int card2) Hole;
            public int NumCard;
            public int NumGetStack;
            public List<(int playerId, int card1, int card2)[]> Witnessed = [];
            public List<int[]> WitnessedBoards = [];
            public double StackAtWitness;
            public Func<TestAgent, double, double, double> Policy = (_, _, _) => Game.FOLD_VALUE;

            public TestAgent(int id)
            {
                Id = id;
            }

            public void Reset(int playerId, int position)
            {
                Id = playerId;
            }

            public void ReceiveDealCard(int card1, int card2)
            {
                Hole = (card1, card2);
            }

            public (int card1, int card2) GetDealCard()
            {
                return Hole;
            }

            public double Play(IList<int> history, Span<int> board, double toCall, double minRaise, double pot)
            {
                NumCard = board.Length;
                double bet = Policy(this, toCall, minRaise);
                if (bet != Game.FOLD_VALUE && bet != Game.ALL_IN_VALUE)
                {
                    Stack -= bet;
                }
                return bet;
            }

            public double AllIn()
            {
                var allIn = Stack;
                Stack = 0;
                return allIn;
            }

            public int GetId()
            {
                return Id;
            }

            public double GetStack()
            {
                NumGetStack++;
                return Stack;
            }

            public void ReceiveReturn(double money)
            {
                Stack += money;
            }

            public void WitnessShowdown(ReadOnlySpan<int> board, ReadOnlySpan<(int playerId, int card1, int card2)> shownHands)
            {
                WitnessedBoards.Add(board.ToArray());
                Witnessed.Add(shownHands.ToArray());
                StackAtWitness = Stack;
            }
        }

        private static List<TestAgent> Agents(int numPlayer)
        {
            return [.. Enumerable.Range(0, numPlayer).Select(i => new TestAgent(i))];
        }

        private static IList<int> History(Arena arena)
        {
            return (IList<int>) typeof(Arena)
                .GetField("history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(arena)!;
        }

        private static int[] Cards(params string[] cardStrings)
        {
            return [.. cardStrings.Select(Game.StringToCard)];
        }

        private static double CallPolicy(TestAgent agent, double toCall, double minRaise)
        {
            if (toCall >= agent.Stack)
            {
                return Game.ALL_IN_VALUE;
            }
            return toCall;
        }

        private static string CaptureConsole(Action action)
        {
            var original = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                action();
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Test]
        // When everyone folds pre-flop, the big blind takes both blinds.
        public void RoundPlay_ShouldGiveBlindsToBigBlind_WhenEveryoneFolds()
        {
            var agents = Agents(3);
            var arena = new Arena(3, [.. agents]);

            arena.RoundPlay();

            Assert.Multiple(() =>
            {
                Assert.That(agents[0].Stack, Is.EqualTo(Game.STACK));
                Assert.That(agents[1].Stack, Is.EqualTo(Game.STACK - Game.SMALL_BLIND));
                Assert.That(agents[2].Stack, Is.EqualTo(Game.STACK + Game.SMALL_BLIND));
            });
        }

        [Test]
        // Heads-up, the dealer posts the small blind, acts first pre-flop and last post-flop.
        public void RoundPlay_ShouldFollowHeadsUpActionOrder()
        {
            var agents = Agents(2);
            var order = new List<(int numCard, int player)>();
            foreach (var agent in agents)
            {
                agent.Policy = (a, toCall, minRaise) =>
                {
                    order.Add((a.NumCard, a.Id));
                    return CallPolicy(a, toCall, minRaise);
                };
            }
            var arena = new Arena(2, [.. agents]);

            arena.RoundPlay();

            Assert.Multiple(() =>
            {
                Assert.That(order, Is.EqualTo(new List<(int, int)> {
                    (0, 0), (0, 1), (3, 1), (3, 0), (4, 1), (4, 0), (5, 1), (5, 0)
                }));
                Assert.That(agents.Sum(a => a.Stack), Is.EqualTo(2 * Game.STACK));
            });
        }

        [Test]
        // A short all-in only wins the side pot they can cover; the rest goes to the next best hand.
        public void HandleShowdown_ShouldSplitSidePots_WhenShortStackWins()
        {
            var agents = Agents(3);
            agents.ForEach(a => a.Stack = 0);
            agents[0].Hole = (Game.StringToCard("K♠"), Game.StringToCard("K♦")); // Trip kings
            agents[1].Hole = (Game.StringToCard("Q♠"), Game.StringToCard("Q♦")); // Pair of queens
            agents[2].Hole = (Game.StringToCard("3♠"), Game.StringToCard("4♦")); // High card
            var arena = new Arena(3, [.. agents]);

            var board = Cards("2♣", "7♦", "9♥", "J♠", "K♣");
            // Bets of 100, 500, 500: entitled to 100 * 3 and 100 + 500 + 500
            arena.HandleShowdown(board, new bool[3], new double[] { 300, 1100, 1100 }, new double[] { 100, 500, 500 }, 1100);

            var history = History(arena);
            Assert.Multiple(() =>
            {
                Assert.That(agents[0].Stack, Is.EqualTo(300));
                Assert.That(agents[1].Stack, Is.EqualTo(800));
                Assert.That(agents[2].Stack, Is.EqualTo(0));

                // Showdown history: board, pot, then id + PnL per player
                Assert.That(history, Has.Count.EqualTo(5));
                for (int i = 0; i < 5; i++)
                {
                    Assert.That((history[0] >> (6 * i)) & 0b11_1111, Is.EqualTo(board[i]));
                }
                Assert.That(history[1], Is.EqualTo(1100));
                Assert.That(history.Skip(2).Select(h => (h & 0b1111, h >> 4)),
                    Is.EqualTo(new[] { (0, 200), (1, 300), (2, -500) }));
            });
        }

        [Test]
        // Tied hands with different bets split only the pots they are both entitled to.
        public void HandleShowdown_ShouldSplitTiesPerSidePot()
        {
            var agents = Agents(3);
            agents.ForEach(a => a.Stack = 0);
            agents[0].Hole = (Game.StringToCard("Q♠"), Game.StringToCard("Q♦"));
            agents[1].Hole = (Game.StringToCard("Q♥"), Game.StringToCard("Q♣"));
            agents[2].Hole = (Game.StringToCard("3♠"), Game.StringToCard("4♦"));
            var arena = new Arena(3, [.. agents]);

            // Bets of 100, 500, 500: entitled to 100 * 3 and 100 + 500 + 500
            arena.HandleShowdown(Cards("2♣", "7♦", "9♥", "J♠", "K♣"), new bool[3], new double[] { 300, 1100, 1100 }, new double[] { 100, 500, 500 }, 1100);

            Assert.Multiple(() =>
            {
                Assert.That(agents[0].Stack, Is.EqualTo(150));
                Assert.That(agents[1].Stack, Is.EqualTo(950));
                Assert.That(agents[2].Stack, Is.EqualTo(0));
            });
        }

        [Test]
        // A 3-way tie whose seats are in the reverse order of their entitled pots: each side pot slice is split
        // only among the tied players entitled to it, smallest stack first.
        public void HandleShowdown_ShouldSplitTiesPerSidePot_WhenSeatsAreOutOfOrder()
        {
            var agents = Agents(4);
            agents.ForEach(a => a.Stack = 0);
            agents[0].Hole = (Game.StringToCard("2♠"), Game.StringToCard("3♦"));
            agents[1].Hole = (Game.StringToCard("4♠"), Game.StringToCard("5♦"));
            agents[2].Hole = (Game.StringToCard("6♠"), Game.StringToCard("7♦"));
            agents[3].Hole = (Game.StringToCard("A♠"), Game.StringToCard("A♦"));
            var arena = new Arena(4, [.. agents]);

            // The board is a broadway straight, so seats 0-2 tie. Bets of 500, 300, 100, and 200 from the folded
            // seat 3: entitled to 500 + 300 + 100 + 200, 300 + 300 + 100 + 200, 100 * 4 (and 700 for seat 3)
            arena.HandleShowdown(Cards("10♣", "J♦", "Q♥", "K♠", "A♣"), [false, false, false, true],
                new double[] { 1100, 900, 400, 700 }, new double[] { 500, 300, 100, 200 }, 1100);

            // 400 split 3 ways, the next 500 split between seats 0 and 1, the last 200 to seat 0
            var history = History(arena);
            Assert.Multiple(() =>
            {
                Assert.That(agents[2].Stack, Is.EqualTo(400.0 / 3).Within(1e-9));
                Assert.That(agents[1].Stack, Is.EqualTo(400.0 / 3 + 250).Within(1e-9));
                Assert.That(agents[0].Stack, Is.EqualTo(400.0 / 3 + 250 + 200).Within(1e-9));
                Assert.That(agents[3].Stack, Is.EqualTo(0));
                Assert.That(history.Skip(2).Select(h => (h & 0b1111, h >> 4)),
                    Is.EqualTo(new[] { (0, 83), (1, 83), (2, 33), (3, -200) }));
            });
        }

        [Test]
        // Chips put in by a player who folded still go to the winners, and a folded best hand wins nothing.
        public void HandleShowdown_ShouldAwardFoldedChips()
        {
            var agents = Agents(3);
            agents.ForEach(a => a.Stack = 0);
            agents[0].Hole = (Game.StringToCard("K♠"), Game.StringToCard("K♦"));
            agents[1].Hole = (Game.StringToCard("Q♠"), Game.StringToCard("Q♦"));
            agents[2].Hole = (Game.StringToCard("A♠"), Game.StringToCard("A♦"));
            var arena = new Arena(3, [.. agents]);

            // Bets of 100, 200, and 150 from the folded player: entitled to 100 * 3 and 100 + 200 + 150
            arena.HandleShowdown(Cards("2♣", "7♦", "9♥", "J♠", "K♣"), [false, false, true], new double[] { 300, 450, 450 }, new double[] { 100, 200, 150 }, 450);

            Assert.Multiple(() =>
            {
                Assert.That(agents[0].Stack, Is.EqualTo(300));
                Assert.That(agents[1].Stack, Is.EqualTo(150));
                Assert.That(agents[2].Stack, Is.EqualTo(0));
            });
        }

        [Test]
        // Every agent sees the board and the hands still in, once and before the pot is paid. That includes the player
        // who folded, whose own hand stays hidden. Hands are identified by player id, in seat order.
        public void HandleShowdown_ShouldShowHandsStillIn_ToEveryAgent()
        {
            var agents = Agents(3);
            agents.ForEach(a => a.Stack = 0);
            agents[0].Id = 5;
            agents[1].Id = 3;
            agents[2].Id = 7;
            agents[0].Hole = (Game.StringToCard("K♠"), Game.StringToCard("K♦"));
            agents[1].Hole = (Game.StringToCard("Q♠"), Game.StringToCard("Q♦"));
            agents[2].Hole = (Game.StringToCard("A♠"), Game.StringToCard("A♦"));
            var arena = new Arena(3, [.. agents]);

            var board = Cards("2♣", "7♦", "9♥", "J♠", "K♣");
            arena.HandleShowdown(board, [false, false, true], new double[] { 300, 450, 450 }, new double[] { 100, 200, 150 }, 450);

            var shown = new[] { (5, agents[0].Hole.card1, agents[0].Hole.card2), (3, agents[1].Hole.card1, agents[1].Hole.card2) };
            Assert.Multiple(() =>
            {
                foreach (var agent in agents)
                {
                    Assert.That(agent.Witnessed, Has.Count.EqualTo(1));
                    Assert.That(agent.Witnessed[0], Is.EqualTo(shown));
                    Assert.That(agent.WitnessedBoards, Has.Count.EqualTo(1));
                    Assert.That(agent.WitnessedBoards[0], Is.EqualTo(board));
                    Assert.That(agent.StackAtWitness, Is.EqualTo(0), "Shown before the pot is paid");
                }
                Assert.That(agents[0].Stack, Is.EqualTo(300));
            });
        }

        [Test]
        // A full hand: every hand is shown, including that of a player who has been all-in since the blinds. That player
        // never sees the board through Play, so the showdown is where they get all 5 cards.
        public void RoundPlay_ShouldShowEveryHand_WhenNobodyFolds()
        {
            var agents = Agents(3);
            agents.ForEach(a => a.Policy = CallPolicy);
            agents[1].Stack = 5;
            var arena = new Arena(3, [.. agents]);

            arena.RoundPlay();

            var shown = agents.Select(a => (a.Id, a.Hole.card1, a.Hole.card2)).ToArray();
            // The showdown history starts with the board (6 bits per card), then the pot, then 1 entry per player
            var history = History(arena);
            int boardEntry = history[history.Count - agents.Count - 2];
            var board = Enumerable.Range(0, 5).Select(i => (boardEntry >> (6 * i)) & 0b11_1111).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(agents[1].NumCard, Is.LessThan(5), "The all-in player never saw the river through Play");
                foreach (var agent in agents)
                {
                    Assert.That(agent.Witnessed, Has.Count.EqualTo(1));
                    Assert.That(agent.Witnessed[0], Is.EqualTo(shown));
                    Assert.That(agent.WitnessedBoards, Has.Count.EqualTo(1));
                    Assert.That(agent.WitnessedBoards[0], Is.EqualTo(board));
                }
            });
        }

        [Test]
        // No showdown when everyone else folds: the winner doesn't have to show.
        public void RoundPlay_ShouldShowNoHands_WhenEveryoneFolds()
        {
            var agents = Agents(3);
            var arena = new Arena(3, [.. agents]);

            arena.RoundPlay();

            Assert.That(agents.Sum(a => a.Witnessed.Count), Is.EqualTo(0));
        }

#if DEBUG
        [Test]
        // The big blind wins a pot of 30, but only gains the small blind: the pot includes their own big blind.
        public void RoundPlay_ShouldLogPotWinningAndStackChange_WhenEveryoneFolds()
        {
            var agents = Agents(3);
            var arena = new Arena(3, [.. agents]);

            string log = CaptureConsole(arena.RoundPlay);

            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain($"Player 0: Pot winning 0: Stack {Game.STACK} -> {Game.STACK}: PnL 0"));
                Assert.That(log, Does.Contain($"Player 1: Pot winning 0: Stack {Game.STACK} -> {Game.STACK - 10}: PnL -10"));
                Assert.That(log, Does.Contain($"Player 2: Pot winning 30: Stack {Game.STACK} -> {Game.STACK + 10}: PnL 10"));
            });
        }

        [Test]
        // Heads-up, both call the big blind and check down: each put in 20, and what they got from the pot depends on
        // the cards.
        public void RoundPlay_ShouldLogPotWinningAndStackChange_AtShowdown([Range(1, 5)] int seed)
        {
            var agents = Agents(2);
            agents.ForEach(a => a.Policy = CallPolicy);
            var arena = new Arena(2, [.. agents], seed);

            string log = CaptureConsole(arena.RoundPlay);

            Assert.Multiple(() =>
            {
                foreach (var agent in agents)
                {
                    double pnl = agent.Stack - Game.STACK;
                    Assert.That(log, Does.Contain($"Player {agent.Id}: Pot winning {pnl + Game.BIG_BLIND}: Stack {Game.STACK} -> {agent.Stack}: PnL {pnl}"));
                }
            });
        }
#else
        [Test]
        // Release: nothing is logged, the only stack reads are the 2 for the blinds, and the stored stacks don't exist.
        public void RoundPlay_ShouldNotRunDebugOnlyCode_InRelease([Values] bool goesToShowdown)
        {
            var agents = Agents(3);
            if (goesToShowdown)
            {
                agents.ForEach(a => a.Policy = CallPolicy);
            }
            var arena = new Arena(3, [.. agents]);

            string log = CaptureConsole(arena.RoundPlay);

            Assert.Multiple(() =>
            {
                Assert.That(log, Is.Empty);
                Assert.That(agents.Sum(a => a.NumGetStack), Is.EqualTo(2));
                Assert.That(typeof(Arena).GetField("debugStartStacks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance), Is.Null);
            });
        }
#endif

        // Plays seeded random hands (seeded agents and deck), asserting chips are conserved after every hand.
        // Debug.Assert failures inside the arena also fail the test.
        private static void PlayRandomHands(int seed, int numPlayer, int hands)
        {
            var random = new Random(seed);
            var agents = Agents(numPlayer);
            foreach (var agent in agents)
            {
                agent.Policy = (a, toCall, minRaise) =>
                {
                    double raiseBet = toCall + minRaise + (random.Next(2) == 0 ? 0 : Game.BIG_BLIND);
                    double roll = random.NextDouble();
                    if (roll < 0.2)
                    {
                        return Game.FOLD_VALUE;
                    }
                    if (roll < 0.3)
                    {
                        return Game.ALL_IN_VALUE;
                    }
                    // minRaise is infinite when betting is not re-opened
                    if (roll < 0.5 && raiseBet < a.Stack)
                    {
                        return raiseBet;
                    }
                    return CallPolicy(a, toCall, minRaise);
                };
            }
            var arena = new Arena(numPlayer, [.. agents], seed);

            for (int hand = 0; hand < hands; hand++)
            {
                // Reset the whole table once someone busts, so stacks stay within the 15-bit history encoding
                if (agents.Any(a => a.Stack < Game.BIG_BLIND))
                {
                    agents.ForEach(a => a.Stack = Game.STACK);
                }
                double total = agents.Sum(a => a.Stack);

                arena.RoundPlay();

                Assert.That(agents.Sum(a => a.Stack), Is.EqualTo(total).Within(1e-6), $"Hand {hand}");
                Assert.That(agents.All(a => a.Stack >= 0), $"Hand {hand}");
            }
        }

        [Test]
        // Random legal play never creates or destroys chips, for any table size.
        public void RoundPlay_ShouldConserveChips_WithRandomPlay([Range(2, 9)] int numPlayer)
        {
            PlayRandomHands(numPlayer, numPlayer, 2000);
        }

        // Regression: with fractional amounts (after split pots), an exact min raise toCall + minRaise could
        // round 1 ulp below the min raise, and recomputing totalBet + toCall could make a call look like a raise.
        // These seeds hit it before the fix (min raise assert failed).
        [TestCase(5882, 4)]
        [TestCase(14203, 5)]
        public void RoundPlay_ShouldAcceptExactMinRaise_WithFractionalAmounts(int seed, int numPlayer)
        {
            PlayRandomHands(seed, numPlayer, 2000);
        }

        [Test]
        // A player who can't cover the blind posts the whole stack and is all-in: never asked to act,
        // and the others still bet on every street.
        public void RoundPlay_ShouldPutShortBlindAllIn()
        {
            var agents = Agents(3);
            var streetsActedOn = new HashSet<(int player, int numCard)>();
            foreach (var agent in agents)
            {
                agent.Policy = (a, toCall, minRaise) =>
                {
                    streetsActedOn.Add((a.Id, a.NumCard));
                    return CallPolicy(a, toCall, minRaise);
                };
            }
            // Dealer is seat 0, so seat 1 posts the small blind with only 5 chips
            agents[1].Stack = 5;
            double total = agents.Sum(a => a.Stack);
            var arena = new Arena(3, [.. agents]);

            arena.RoundPlay();

            var history = History(arena);
            Assert.Multiple(() =>
            {
                Assert.That(streetsActedOn.Any(s => s.player == 1), Is.False, "All-in small blind was asked to act");
                foreach (int numCard in new[] { 0, 3, 4, 5 })
                {
                    Assert.That(streetsActedOn, Does.Contain((0, numCard)));
                    Assert.That(streetsActedOn, Does.Contain((2, numCard)));
                }
                // Small blind entry: id 1, bet 5, all-in flag
                Assert.That(history[0], Is.EqualTo(Arena.EncodeHistoryBetState(1, 5, 0, 1)));
                Assert.That(agents.Sum(a => a.Stack), Is.EqualTo(total));
            });
        }

        [Test]
        // The same seed deals the same cards, so the same agents play the same hands.
        public void Arena_ShouldReplayHands_WithSameSeed()
        {
            List<int> Play(int seed)
            {
                var agents = Agents(4);
                agents.ForEach(a => a.Policy = CallPolicy);
                var arena = new Arena(4, [.. agents], seed);
                var played = new List<int>();
                for (int hand = 0; hand < 50; hand++)
                {
                    arena.RoundPlay();
                    played.AddRange(History(arena));
                }
                return played;
            }

            Assert.Multiple(() =>
            {
                Assert.That(Play(42), Is.EqualTo(Play(42)));
                Assert.That(Play(42), Is.Not.EqualTo(Play(43)));
            });
        }
    }
}
