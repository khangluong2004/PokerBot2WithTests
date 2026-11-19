using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;


namespace PokerBot2.GameSpace
{
    internal static class WinLoseRateCalculator
    {
        internal static Dictionary<int, List<(double, double)>> winLoseRateByBoardByHand;
        static WinLoseRateCalculator()
        {
            winLoseRateByBoardByHand = new(133_784_560); // 52C5 = 133_784_560
            BuildWinLoseRateByBoardByHand(0, -1, 0, 0, Vector128.Create(0UL, 0UL), winLoseRateByBoardByHand);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ToBoardMask(Span<int> board)
        {
            Debug.Assert(board.Length == 5, "Board must have exactly 5 cards");
            board.Sort();
            return (board[0] + (board[1] << 6) + (board[2] << 12) + (board[3] << 18) + (board[4] << 24));
        }

        public static (double, double) GetWinLoseRateHeadup(Span<int> board, int lowCard, int highCard)
        {
            Debug.Assert(board.Length == 5, "Board must have exactly 5 cards");
            return GetWinLoseRateHeadup(ToBoardMask(board), lowCard, highCard);
        }

        // Find the win-rate of (card1, card2) against 1 opponent, on a 5-card board
        internal static (double, double) GetWinLoseRateHeadup(int boardMask, int lowCard, int highCard)
        {

            return winLoseRateByBoardByHand[boardMask][PerBoardHandKey(lowCard, highCard)];
        }

        // Backtrack through all possible boards and fill the dicitonary at the leaves.
        // boardKeyMask is the key used to retrieve the ranks.
        internal static void BuildWinLoseRateByBoardByHand(int numCard, int lastCard,
            int boardMask,
            ulong boardIndexMask, Vector128<ulong> boardKeyMask,
            Dictionary<int, List<(double, double)>> winLoseRateByBoardByHand)
        {
            if (numCard == 5)
            {
                winLoseRateByBoardByHand[boardMask] = BuildWinLoseRateHeadupPerBoard(boardIndexMask, boardKeyMask);
                return;
            }

            for (int nextCard=lastCard+1; nextCard < 52; nextCard++)
            {
                var nextCardKey = Game.HandKeyMask(nextCard);

                // Move down
                boardMask |= (nextCard << (6 * numCard));
                boardIndexMask |= 1UL << nextCard;
                boardKeyMask += nextCardKey;
                BuildWinLoseRateByBoardByHand(numCard + 1, nextCard, boardMask, boardIndexMask, boardKeyMask, winLoseRateByBoardByHand);

                // Backtrack
                boardMask &= ~(nextCard << (6 * numCard));
                boardIndexMask &= ~(1UL << nextCard);
                boardKeyMask -= nextCardKey;
            }
        }

        // Create a board index 
        const int NUM_HANDS_PER_BOARD = 1081; // 47C2 = 1081
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PerBoardHandKey(int lowCard, int highCard) => 51 * lowCard + highCard;
        private static string BoardIndexMaskToString(ulong boardIndexMask)
        {
            string result = "";
            for (int i = 0; i < 52; i++)
            {
                if ((boardIndexMask & (1UL << i)) != 0)
                {
                    result += $"{Game.CardToString(i)}, ";
                }
            }
            return result;
        }
        internal static List<(double, double)> BuildWinLoseRateHeadupPerBoard(ulong boardIndexMask, Vector128<ulong> boardKeyMask)
        {
            Debug.Assert(BitOperations.PopCount(boardIndexMask) == 5, "Board needs to have exact 5 card");
            List<int> allHandValues = new List<int>(NUM_HANDS_PER_BOARD); 
            Dictionary<int, (int, int)> handValueToIndex = new (NUM_HANDS_PER_BOARD);
            Vector128<ulong> handKeyMask;
            
            for (int lowCard=0; lowCard < 51; lowCard++)
            {
                if ((boardIndexMask & (1UL << lowCard)) != 0)
                {
                    continue;
                }

                handKeyMask = boardKeyMask + Game.HandKeyMask(lowCard);

                for (int highCard=lowCard+1; highCard < 52; highCard++)
                {
                    if ((boardIndexMask & (1UL << highCard)) != 0)
                    {
                        continue;
                    }
                    allHandValues.Add(Game.EvalHandRank(handKeyMask + Game.HandKeyMask(highCard)));
                }
            }

            allHandValues.Sort();
            int start, index;
            index = 0;
            while (index < allHandValues.Count)
            {
                start = index;
                do
                {
                    index++;
                } while (index < allHandValues.Count && allHandValues[index] == allHandValues[index - 1]);

                handValueToIndex[allHandValues[start]] = (start, index);
            }

            List<(double, double)> winLoseRatePerHand = [.. new (double, double)[2602]]; // 51 * 51 + 1 = 2602, due to ordered card
            for (int lowCard = 0; lowCard < 51; lowCard++)
            {
                if ((boardIndexMask & (1UL << lowCard)) != 0)
                {
                    continue;
                }

                handKeyMask = boardKeyMask + Game.HandKeyMask(lowCard);

                for (int highCard = lowCard + 1; highCard < 52; highCard++)
                {
                    if ((boardIndexMask & (1UL << highCard)) != 0)
                    {
                        continue;
                    }

                    var handRank = Game.EvalHandRank(handKeyMask + Game.HandKeyMask(highCard));
                    var (startCount, endCount) = handValueToIndex[handRank];
                    Logger.DebugOnlyConsoleLog($"Board: {BoardIndexMaskToString(boardIndexMask)} Hand: {Game.CardToString(lowCard)}, {Game.CardToString(highCard)}, WinRate: {startCount * 1.0 / allHandValues.Count}, LoseRate: {(allHandValues.Count - endCount) * 1.0 / allHandValues.Count}");
                    winLoseRatePerHand[PerBoardHandKey(lowCard, highCard)] = (startCount * 1.0/allHandValues.Count, (allHandValues.Count - endCount) * 1.0/allHandValues.Count);
                }
            }

            return winLoseRatePerHand;
        }
    }
}
