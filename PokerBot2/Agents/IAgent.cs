using System;
using System.Collections.Generic;
using System.Text;

namespace PokerBot2.Agents
{
    public interface IAgent
    {
        // 0 = dealer, 1 = small blind, 2 = big blind, and so on
        public void Reset(int playerId, int position);
        public void ReceiveDealCard(int card1, int card2);
        public (int card1, int card2) GetDealCard();
        // Return value: Game.FOLD_VALUE = fold, same value as previous = call, raise = higher value than previous bet
        // All-in: Return Game.ALL_IN_VALUE to signal all-in. The arena will call AllIn() to retrieve the bet amount.
        // Raised amount >= previous raised amount
        // Bets are integer only (simpler history encoding); only all-in and returns can be fractional.
        public int Play(IList<int> history, Span<int> board);
        // Return the full stack (may be fractional), and reset player stack to 0
        public double AllIn();
        public int GetId();
        public double GetStack();

        public void ReceiveReturn(double money);
    }
}
