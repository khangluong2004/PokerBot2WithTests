using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PokerBot2.Agents
{
    public class HumanPlayer : IAgent
    {
        private int playerPosition;
        private int stack;
        private int lowCard;
        private int highCard;
        private int playerId;

        public HumanPlayer(int playerId)
        {
            this.playerId = playerId;
            stack = Game.STACK;
        }

        public void Reset(int playerId, int position)
        {
            // Reset internal state for given position (no-op placeholder)
            playerPosition = position;
            this.playerId = playerId;
        }

        public void ReceiveDealCard(int card1, int card2)
        {
            // Receive dealt cards (no-op placeholder)
            if (card1 > card2)
            {
                highCard = card1;
                lowCard = card2;
            } else
            {
                highCard = card2;
                lowCard = card1;
            }
        }

        public int Play(IList<int> history, Span<int> board)
        {
            // Decide and return the bet amount
            bool validResponse = false;
            int bet = -1;
            while (!validResponse)
            {
                Console.Write($"Player[{playerId}][{Game.CardToString(lowCard)}, {Game.CardToString(highCard)}]: ");
                validResponse = int.TryParse(Console.ReadLine(), out bet);
            }

            Debug.Assert(bet <= stack, "Can't bet more than stack");
            if (bet == -1)
            {
                bet = Game.FOLD_VALUE;
            } else
            {
                stack -= bet;
            }
            return bet;
        }

        public int GetId()
        {
            return playerId;
        }
    }
}
