using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PokerBot2.Agents
{
    public class HumanPlayer : IAgent
    {
        private int playerPosition;
        private double stack;
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

        public double Play(IList<int> history, Span<int> board, double toCall, double minRaise)
        {
            // Decide and return the bet amount
            bool validResponse = false;
            double bet = -1;
            while (!validResponse)
            {
                Console.Write($"Player[{playerId}][{Game.CardToString(lowCard)}, {Game.CardToString(highCard)}] to call {toCall}, min raise {minRaise}: ");
                validResponse = double.TryParse(Console.ReadLine(), out bet);
            }

            // -1 = fold, -2 = all-in, otherwise the chips put in
            if (bet == -1)
            {
                bet = Game.FOLD_VALUE;
            } else if (bet == -2 || bet == stack)
            {
                // Betting the whole stack must be declared as all-in
                bet = Game.ALL_IN_VALUE;
            } else
            {
                Debug.Assert(bet <= stack, "Can't bet more than stack");
                stack -= bet;
            }
            return bet;
        }

        public (int card1, int card2) GetDealCard()
        {
            return (lowCard, highCard);
        }

        public double AllIn()
        {
            var allIn = stack;
            stack = 0;
            return allIn;
        }

        public int GetId()
        {
            return playerId;
        }

        public double GetStack()
        {
            return stack;
        }

        public void ReceiveReturn(double money)
        {
            stack += money;
        }
    }
}
