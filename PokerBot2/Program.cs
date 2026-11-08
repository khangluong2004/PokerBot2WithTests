using PokerBot2;
using PokerBot2.Agents;
using System.Text;

// Need to set console to utf8 so it's able to print out the card
Console.OutputEncoding = Encoding.UTF8;

Logger.DebugOnlyConsoleLog("Test play: Human player vs human player");
Arena arena = new(2, [new HumanPlayer(1), new HumanPlayer(2)], 2605);
arena.RoundPlay();