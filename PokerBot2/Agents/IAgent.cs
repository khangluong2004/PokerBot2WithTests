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
        // toCall: exact chips needed to call. minRaise: min chips to raise on top of toCall
        // (infinity if betting is not re-opened, so only call or fold allowed).
        // Return value: chips put in by this action, and deduct them from the stack.
        // Game.FOLD_VALUE = fold, toCall = call (0 = check), toCall + r with r >= minRaise = raise
        // All-in: Return Game.ALL_IN_VALUE to signal all-in. The arena will call AllIn() to retrieve the bet amount.
        // Bets can be fractional, the history only encodes the integer part.
        public double Play(IList<int> history, Span<int> board, double toCall, double minRaise, double pot);
        // Return the full stack (may be fractional), and reset player stack to 0
        public double AllIn();
        public int GetId();
        public double GetStack();

        public void ReceiveReturn(double money);

        // Called on every agent at a showdown, folded or not, before the pot is paid: the full board (5 cards), and the
        // hole cards of each player still in the hand, in seat order. Agents that folded or went all-in early haven't
        // seen the whole board through Play, so this is where they get it. Not called when the hand ends because
        // everyone else folded. The spans are only valid during the call: copy what you want to keep.
        public void WitnessShowdown(ReadOnlySpan<int> board, ReadOnlySpan<(int playerId, int card1, int card2)> shownHands);
    }
}
