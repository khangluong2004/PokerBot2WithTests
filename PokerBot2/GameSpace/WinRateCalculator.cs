using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PokerBot2.GameSpace
{
    internal class WinRateCalculator
    {

        static WinRateCalculator()
        {

        }

        // Find the win-rate of (card1, card2) against 1 opponent, on a 5-card board
        static double GetWinRateHeadup(Span<int> board, int card1, int card2)
        {
            Debug.Assert(board.Length == 5, "Compute the win rate for 5-card board only");
            return 0;
        }
    }
}
