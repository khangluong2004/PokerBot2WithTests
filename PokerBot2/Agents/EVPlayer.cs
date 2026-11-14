using PokerBot2.GameSpace;

namespace PokerBot2.Agents
{
    internal class EVPlayer : IAgent
    {
        private int id;
        private double stack;
        int highCard, lowCard;

        public EVPlayer(int id)
        {
            this.id = id;
            stack = Game.STACK;
        }

        public double AllIn()
        {
            var allIn = stack;
            stack = 0;
            return allIn;
        }

        public (int card1, int card2) GetDealCard()
        {
            return (highCard, lowCard);
        }

        public int GetId()
        {
            return id;
        }

        public double GetStack()
        {
            return stack;
        }

        public double Play(IList<int> history, Span<int> board, double toCall, double minRaise, double pot)
        {
            throw new NotImplementedException();
        }

        public void ReceiveDealCard(int card1, int card2)
        {
            if (card1 > card2)
            {
                highCard = card1; lowCard = card2;
            } else
            {
                highCard = card2; lowCard = card1;
            }
        }

        public void ReceiveReturn(double money)
        {
            stack += money;
        }

        public void Reset(int playerId, int position)
        {
            id = playerId;
        }

        public void WitnessShowdown(ReadOnlySpan<int> board, ReadOnlySpan<(int playerId, int card1, int card2)> shownHands)
        {
            return;
        }
    }
}
