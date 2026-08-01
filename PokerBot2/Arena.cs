using PokerBot2.Agents;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PokerBot2
{
    public class Arena
    {
        public static int DEALER_HISTORY_STATE = 11;

        // Game structure: 10-20 big blind, 2500 stack per player, up to 9 players
        int numPlayer;
        int dealer = 0;
        IList<IAgent> agents = [];
        // History action will be encode as: first 4 bits for playerId (if set to 11, then it's dealer),
        // For player: next 1 bit = whether showdown or not,
        // If not showdown: next 15 bits for bet (all bits set to 1 = fold), next 1 bit for fold or not, next 1 bit for all-in or not
        // If showdown: next 12 bits show the 2 displayed card, and next 1 bit shows the winner
        IList<int> history = [];
        Game game;

        public Arena(int numPlayer, IList<IAgent> agents)
        {
            Debug.Assert(numPlayer <= 9);
            this.numPlayer = numPlayer;
            game = new Game(numPlayer);
            this.agents = agents;
        }

        // Reset for new round
        public void Reset()
        {
            history.Clear();
            game.Reset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EncodeHistoryBetState(int playerId, int bet, int isFold, int isAllIn)
        {
            return (playerId) + (bet << 5) + (isFold << 20) + (isAllIn << 21);
        }

        public void RoundPlay()
        {
            // Deal card
            for (int i=0; i < numPlayer; i++) {
                agents[(dealer + 3 + i) % agents.Count].ReceiveDealCard(game.DealCard(), game.DealCard());
            }

            Span<bool> isFold = stackalloc bool[numPlayer];
            for (int i=0; i < numPlayer; i++)
            {
                isFold[i] = true;
            }
            Span<int> board = stackalloc int[5];

            // Pre-flop action
            // Blinds
            history.Add(EncodeHistoryBetState(agents[(dealer + 1) % agents.Count].GetId(), Game.SMALL_BLIND, 0, 0));
            history.Add(EncodeHistoryBetState(agents[(dealer + 2) % agents.Count].GetId(), Game.BIG_BLIND, 0, 0));

            // Bet
            int curPlayer = (dealer + 3) % agents.Count;
            int actionPlayer = curPlayer;
            int bet = Game.BIG_BLIND;
            int prevRaise = 0;
            int maxBet = Game.BIG_BLIND;
            do
            {
                if (!isFold[curPlayer])
                {
                    bet = agents[curPlayer].Play(history, board[..0]);
                    Debug.Assert(bet <= agents[curPlayer].GetStack(), "Can't bet more than stack");
                    if (bet == Game.FOLD_VALUE)
                    {
                        isFold[curPlayer] = true;
                        history.Add(EncodeHistoryBetState(agents[(dealer + 1) % agents.Count].GetId(), 0, 1, 0));
                    } else if (bet == Game.ALL_IN_VALUE)
                    {
                        history.Add(EncodeHistoryBetState(agents[(dealer + 1) % agents.Count].GetId(), 0, 0, 1));
                    }
                    else if (bet > maxBet)
                    {
                        maxBet = bet;
                        actionPlayer = curPlayer;
                        Debug.Assert((bet - maxBet) >= prevRaise, "Need to raise minimum prev raise");
                        prevRaise = bet - maxBet;
                        history.Add(EncodeHistoryBetState(agents[(dealer + 1) % agents.Count].GetId(), 0, 1, 0));
                    } else
                    {
                        Debug.Assert(bet >= maxBet, "Must fold if bet less");

                    }
                }

                curPlayer = (curPlayer + 1) % agents.Count;
            } while (curPlayer != actionPlayer);
        }
    }
}
