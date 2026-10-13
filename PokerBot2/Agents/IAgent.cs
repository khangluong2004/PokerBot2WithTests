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
        // Return value: 0b1111_1111_1111_1111 = fold, same value as previous = call, raise = higher value than previous bet
        // All-in: Raise to full stacks
        // Raised amount >= previous raised amount
        public int Play(IList<int> history, Span<int> board);
        public int GetId();
        public int GetStack();

        public int ReceiveReturn(int money);
    }
}
