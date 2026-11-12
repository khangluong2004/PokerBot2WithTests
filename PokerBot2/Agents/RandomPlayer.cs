using PokerBot2.GameSpace;

namespace PokerBot2.Agents
{
    internal class RandomPlayer(int id) : IAgent
    {
        private double stack = Game.STACK;
        private int highCard;
        private int lowCard;
        private int id = id;

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
            var action = Random.Shared.Next(3);
            switch (action) {
                case 0: return Game.FOLD_VALUE;
                case 1: return (toCall >= stack ? Game.ALL_IN_VALUE : toCall);
                case 2: var raise = Math.Max(pot, minRaise); return (raise >= stack ? Game.ALL_IN_VALUE : raise);
            }

            throw new ArithmeticException("Random picks invalid value");
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

        public void WitnessShowdown(ReadOnlySpan<(int playerId, int card1, int card2)> shownHands)
        {
            return;
        }
    }
}
