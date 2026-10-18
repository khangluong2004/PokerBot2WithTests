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
        public static int EncodeHistoryBetState(int playerId, double bet, int isFold, int isAllIn)
        {
            return (playerId) + (((int) bet) << 5) + (isFold << 20) + (isAllIn << 21);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EncodeHistoryDealState()
        {
            return DEALER_HISTORY_STATE;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (double pot, int activePlayers, int numAllIn, int isFinished) RoundBet(ref Span<bool> isFold,
            ref Span<double> isAllIn,
            ref Span<double> entitledPot,
            ref Span<int> board, 
            int numCard, 
            double pot, int activePlayers,
            int numAllIn,
            double startBet, int startPlayer, double startRaise)
        {
            int curPlayer = startPlayer;
            int actionPlayer = curPlayer;

            var prevRaise = startRaise;
            var maxBet = startBet;
            double bet;

            int winner = 0;
            var preAllInMaxBet = maxBet;

            Span<double> totalBet = stackalloc double[numPlayer];
            do
            {
                if (!isFold[curPlayer] && isAllIn[curPlayer] == 0)
                {
                    bet = agents[curPlayer].Play(history, board[..numCard]);
                    Debug.Assert(bet <= agents[curPlayer].GetStack(), "Can't bet more than stack");
                    if (bet == Game.FOLD_VALUE)
                    {
                        isFold[curPlayer] = true;
                        activePlayers--;
                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), 0, 1, 0));
                    }
                    else if (bet == Game.ALL_IN_VALUE)
                    {
                        isAllIn[curPlayer] = agents[curPlayer].AllIn();
                        numAllIn++;
                        pot += isAllIn[curPlayer];

                        totalBet[curPlayer] += isAllIn[curPlayer];

                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), 0, 0, 1));
                        winner = curPlayer;

                        if (isAllIn[curPlayer] > maxBet)
                        {
                            var curRaise = isAllIn[curPlayer] - maxBet;
                            var curRaiseBeforeAllIn = isAllIn[curPlayer] - preAllInMaxBet;
                            if (curRaise >= prevRaise)
                            {
                                prevRaise = curRaise;
                                actionPlayer = curPlayer;
                            } else if (curRaiseBeforeAllIn >= prevRaise)
                            {
                                // Re-open betting if multiple short all-in reaches above full raise
                                // Min raise stays the same
                                actionPlayer = curPlayer;
                            }

                            maxBet = isAllIn[curPlayer];
                        }
                    }
                    else if (bet > maxBet)
                    {
                        totalBet[curPlayer] += bet;
                        pot += bet;
                        maxBet = bet;
                        preAllInMaxBet = maxBet;
                        actionPlayer = curPlayer;
                        Debug.Assert((bet - maxBet) >= prevRaise, "Need to raise minimum prev raise");
                        prevRaise = bet - maxBet;
                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), bet, 0, 0));
                        winner = curPlayer;
                    }
                    else
                    {
                        // Call
                        Debug.Assert(bet == maxBet, "Must fold if bet less");
                        totalBet[curPlayer] += bet;
                    }
                }

                curPlayer = (curPlayer + 1) % numPlayer;
            } while (curPlayer != actionPlayer);

            // Splitting pot: Each player is entitled to the max of their betting
            if (numAllIn == 0)
            {
                // If no all in so far, then everyone has the same pot
                entitledPot.Fill(pot);
            } else
            {

                for (int i = 0; i < numPlayer; i++)
                {
                    for (int j = 0; j < numPlayer; j++)
                    {
                        entitledPot[i] += Math.Min(totalBet[i], totalBet[j]);
                    }
                }
            }

            return (pot, activePlayers, numAllIn, (activePlayers == 1 ? winner : -1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleShowdown(Span<int> board, 
            Span<bool> isFold,
            Span<double> isAllIn,
            Span<double> entitledPot,
            double pot)
        {
            // The 7 cards used for eval
            Span<int> evalHand = stackalloc int[7];
            evalHand[2] = board[0];
            evalHand[3] = board[1];
            evalHand[4] = board[2];
            evalHand[5] = board[3];
            evalHand[6] = board[4];

            Span<(long evalValue, double negEntitledPot, int playerIndex)> evalArr = 
                stackalloc (long evalValue, double negEntitledPot, int playerIndex)[numPlayer];

            for (int i=0; i < numPlayer; i++)
            {
                if (isFold[i])
                {
                    evalArr[i] = (-1, 0, i);
                    continue;
                }

                (evalHand[0], evalHand[1]) = agents[i].GetDealCard();
                evalArr[i] = (Game.ToEvalValue(Game.EvalHand(evalHand, 0)), -entitledPot[i],  i);
            }

            evalArr.Sort();
            int counter = numPlayer;
            long curMaxEval;
            int numMaxEval;
            double entitledShare;
            // pot >= 1 to ignore any rounding errors
            while (counter >= 0 && pot >= 1)
            {
                curMaxEval = evalArr[counter].evalValue;
                numMaxEval = 1;

                // Find out how many ties
                counter--;
                while (counter >= 0 && evalArr[counter].evalValue == curMaxEval)
                {
                    numMaxEval++;
                    counter--;
                }

                // Resolve the ties
                counter += numMaxEval;
                for (int i = 0; i < numMaxEval; i++)
                {
                    // IMPORTANT: This engine doesn't give a fuck about odd chips, since too complicated for no
                    // reason. So we'll allow fractional betting and stack size.
                    entitledShare = -evalArr[counter].negEntitledPot / numMaxEval;
                    agents[evalArr[counter].playerIndex].ReceiveReturn(
                        entitledShare
                    );
                    pot -= entitledShare;
                    counter--;
                }
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleEarlyFinish(int winner, double pot)
        {
            agents[winner].ReceiveReturn(pot);
            dealer = (dealer + 1) % numPlayer;
        }

        public void RoundPlay()
        {
            // Deal card
            for (int i=0; i < numPlayer; i++) {
                agents[(dealer + 3 + i) % agents.Count].ReceiveDealCard(game.DealCard(), game.DealCard());
            }

            Span<bool> isFold = stackalloc bool[numPlayer];
            Span<double> isAllIn = stackalloc double[numPlayer]; // Stored the all-in amount
            Span<double> entitledPot = stackalloc double[numPlayer]; // Stored the pot entitled by each player
            int activePlayers = numPlayer;
            int numAllIn = 0;


            for (int i=0; i < numPlayer; i++)
            {
                isFold[i] = true;
            }
            Span<int> board = stackalloc int[5];
            double pot = 0; 
            int isFinished = -1;

            // PRE-FLOP
            // Blinds
            history.Add(EncodeHistoryBetState(agents[(dealer + 1) % agents.Count].GetId(), Game.SMALL_BLIND, 0, 0));
            history.Add(EncodeHistoryBetState(agents[(dealer + 2) % agents.Count].GetId(), Game.BIG_BLIND, 0, 0));

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref board, 
                0, pot, activePlayers, numAllIn, Game.BIG_BLIND, (dealer + 3) % numPlayer, Game.BIG_BLIND);

            if (isFinished != -1) {
                HandleEarlyFinish(isFinished, pot);
                return;
            }

            // FLOP
            // Deal
            board[0] = game.DealCard();
            board[1] = game.DealCard();
            board[2] = game.DealCard();
            history.Add(EncodeHistoryDealState());

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref board,
                3, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, 0);

            if (isFinished != -1)
            {
                HandleEarlyFinish(isFinished, pot);
                return;
            }

            // TURN
            // Deal
            board[3] = game.DealCard();
            history.Add(EncodeHistoryDealState());

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref board,
                4, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, 0);

            if (isFinished != -1)
            {
                HandleEarlyFinish(isFinished, pot);
                return;
            }

            // RIVER
            // Deal
            board[4] = game.DealCard();
            history.Add(EncodeHistoryDealState());

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref board,
                5, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, 0);

            HandleShowdown(board, isFold, isAllIn, entitledPot, pot);
        }
    }
}
