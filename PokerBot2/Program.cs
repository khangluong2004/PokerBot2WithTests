using PokerBot2;
using PokerBot2.Agents;
using PokerBot2.GameSpace;
using System.Text;

// Need to set console to utf8 so it's able to print out the card
Console.OutputEncoding = Encoding.UTF8;

// Call the eval hand once to build the table
Span<int> testHand = stackalloc int[7];
testHand[0] = 1;
testHand[1] = 2;
testHand[2] = 3;
testHand[3] = 4;
testHand[4] = 5;
testHand[5] = 6;
testHand[6] = 7;
Console.WriteLine($"Warm-up to build the rank tables: {Game.EvalHandRank(Game.HandKeyMask(testHand))}");

Logger.DebugOnlyConsoleLog("Test play: Human player vs human player");
Arena arena = new(2, [new HumanPlayer(1), new RandomPlayer(2)]);
arena.RoundPlay();