using PokerBot2;
using PokerBot2.Agents;
using PokerBot2.GameSpace;
using System.Text;

// Need to set console to utf8 so it's able to print out the card
Console.OutputEncoding = Encoding.UTF8;

// Call the eval hand once to build the table (if not built)
Span<int> testHand = [1, 2, 3, 4, 5, 6, 7];
Console.WriteLine($"Warm-up to build/load the rank tables: {Game.EvalHandRank(Game.HandKeyMask(testHand))}");

// Test the WinRateCalc
Span<int> testBoard = [1, 2, 3, 4, 5];
Console.WriteLine($"Test win-rate calc {WinLoseRateCalculator.GetWinLoseRateHeadup(testBoard, 0, 50)}");

Logger.DebugOnlyConsoleLog("Test play: Human player vs human player");
Arena arena = new(2, [new HumanPlayer(1), new RandomPlayer(2)]);
arena.RoundPlay();