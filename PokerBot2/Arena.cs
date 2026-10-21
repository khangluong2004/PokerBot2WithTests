using PokerBot2.Agents;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PokerBot2
{
    public class Arena
    {
        public static int DEALER_HISTORY_STATE = 11;

        // Game structure: 10 small blind, 20 big blind, 2500 stack per player, up to 9 players
        int numPlayer;
        int dealer = 0;
        IList<IAgent> agents = [];
        // History action will be encode as: first 4 bits for playerId (if set to 11, then it's dealer),
        // For player: next 1 bit = whether showdown or not,
        // If not showdown: next 15 bits for bet (the player's bet in the current betting round, integer part for all-in),
        // next 1 bit for fold or not, next 1 bit for all-in or not
        // If showdown (at the end of the hand): 1 number with 30 bits for 5 card on board (6 bits each, board[0] first),
        // 1 number for the pot size (integer part), followed by numPlayers numbers for each player:
        // first 4 bits for playerId, the rest for their PnL (integer part, signed, decode with >> 4)
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
        public static int EncodeHistoryShowdownBoard(Span<int> board)
        {
            return board[0] + (board[1] << 6) + (board[2] << 12) + (board[3] << 18) + (board[4] << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EncodeHistoryShowdownPnL(int playerId, double pnl)
        {
            return playerId + (((int) pnl) << 4);
        }

        // Bet the player already has in the current betting round, so agents know what a call/raise costs
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetRoundBet(IList<int> history, int playerId)
        {
            for (int i = history.Count - 1; i >= 0 && history[i] != DEALER_HISTORY_STATE; i--)
            {
                if ((history[i] & 0b1111) == playerId)
                {
                    return (history[i] >> 5) & 0b111_1111_1111_1111;
                }
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (double pot, int activePlayers, int numAllIn, int isFinished) RoundBet(ref Span<bool> isFold,
            ref Span<double> isAllIn,
            ref Span<double> entitledPot,
            ref Span<double> totalBet,
            ref Span<double> handBet,
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

            int winner = -1;
            var preAllInMaxBet = maxBet;

            // A player who already acted can only raise again once betting is re-opened by a full raise
            Span<bool> canRaise = stackalloc bool[numPlayer];
            canRaise.Fill(true);
            do
            {
                if (activePlayers == 1)
                {
                    break;
                }

                // The last player not all-in only acts if they still have a bet to call
                if (!isFold[curPlayer] && isAllIn[curPlayer] == 0
                    && (activePlayers - numAllIn > 1 || totalBet[curPlayer] < maxBet))
                {
                    // Exact chips needed to call, and the min raise on top of it (infinite if betting not re-opened)
                    bet = agents[curPlayer].Play(history, board[..numCard], maxBet - totalBet[curPlayer],
                        canRaise[curPlayer] ? prevRaise : double.PositiveInfinity);
                    Debug.Assert(bet == Game.FOLD_VALUE || bet == Game.ALL_IN_VALUE || agents[curPlayer].GetStack() >= 0,
                        "Can't bet more than stack");
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

                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), (int) totalBet[curPlayer], 0, 1));

                        if (totalBet[curPlayer] > maxBet)
                        {
                            var curRaise = totalBet[curPlayer] - maxBet;
                            var curRaiseBeforeAllIn = totalBet[curPlayer] - preAllInMaxBet;
                            // Everyone has to respond to the extra amount, even for a short all-in
                            actionPlayer = curPlayer;
                            if (curRaise >= prevRaise)
                            {
                                prevRaise = curRaise;
                                preAllInMaxBet = totalBet[curPlayer];
                                canRaise.Fill(true);
                            } else if (curRaiseBeforeAllIn >= prevRaise)
                            {
                                // Re-open betting if multiple short all-in reaches above full raise
                                // Min raise stays the same
                                preAllInMaxBet = totalBet[curPlayer];
                                canRaise.Fill(true);
                            }

                            maxBet = totalBet[curPlayer];
                        }
                    }
                    else if (totalBet[curPlayer] + bet > maxBet)
                    {
                        Debug.Assert(canRaise[curPlayer], "Betting not re-opened, can only call or fold");
                        Debug.Assert((totalBet[curPlayer] + bet - maxBet) >= prevRaise, "Need to raise minimum prev raise");
                        pot += bet;
                        totalBet[curPlayer] += bet;
                        prevRaise = totalBet[curPlayer] - maxBet;
                        maxBet = totalBet[curPlayer];
                        preAllInMaxBet = maxBet;
                        actionPlayer = curPlayer;
                        canRaise.Fill(true);
                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), totalBet[curPlayer], 0, 0));
                    }
                    else
                    {
                        // Call (check if nothing to call)
                        Debug.Assert(bet == maxBet - totalBet[curPlayer], "Must fold if bet less");
                        pot += bet;
                        // Set exactly to avoid floating point drift from maxBet
                        totalBet[curPlayer] = maxBet;
                        history.Add(EncodeHistoryBetState(agents[curPlayer].GetId(), totalBet[curPlayer], 0, 0));
                    }

                    // Putting the whole stack in with a call/raise is also all-in
                    if (bet != Game.FOLD_VALUE && bet != Game.ALL_IN_VALUE && agents[curPlayer].GetStack() <= 0)
                    {
                        isAllIn[curPlayer] = totalBet[curPlayer];
                        numAllIn++;
                    }
                    canRaise[curPlayer] = false;
                }

                curPlayer = (curPlayer + 1) % numPlayer;
            } while (curPlayer != actionPlayer);

            if (activePlayers == 1)
            {
                for (int i = 0; i < numPlayer; i++)
                {
                    if (!isFold[i])
                    {
                        winner = i;
                        break;
                    }
                }
            }

            for (int i = 0; i < numPlayer; i++)
            {
                handBet[i] += totalBet[i];
            }

            // Splitting pot: Each player is entitled to the max of their betting
            if (numAllIn == 0)
            {
                // If no all in so far, then everyone has the same pot
                entitledPot.Fill(pot);
            } else
            {

                // Use the bets over the whole hand: summing per round is wrong once a player calls a fractional
                // all-in with its integer part, then bets more than the all-in player on a later round
                for (int i = 0; i < numPlayer; i++)
                {
                    entitledPot[i] = 0;
                    for (int j = 0; j < numPlayer; j++)
                    {
                        entitledPot[i] += Math.Min(handBet[i], handBet[j]);
                    }
                }
            }

            return (pot, activePlayers, numAllIn, winner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleShowdown(Span<int> board, 
            Span<bool> isFold,
            Span<double> isAllIn,
            Span<double> entitledPot,
            Span<double> handBet,
            double pot)
        {
            int finalPot = (int) pot;
            // PnL of each player: what they won minus what they bet over the hand
            Span<double> pnl = stackalloc double[numPlayer];
            for (int i = 0; i < numPlayer; i++)
            {
                pnl[i] = -handBet[i];
            }

            // Evaluate all players at once (AVX-512 batch when available) from the shared board mask
            ulong boardMask = Game.ToHandMask(board);
            Span<ulong> handMasks = stackalloc ulong[numPlayer];
            Span<long> evalValues = stackalloc long[numPlayer];
            int card1, card2;
            for (int i=0; i < numPlayer; i++)
            {
                (card1, card2) = agents[i].GetDealCard();
                handMasks[i] = boardMask | Game.ToCardMask(card1) | Game.ToCardMask(card2);
            }
            Game.EvalHandValues(handMasks, evalValues);

            Span<(long evalValue, double negEntitledPot, int playerIndex)> evalArr = 
                stackalloc (long evalValue, double negEntitledPot, int playerIndex)[numPlayer];

            for (int i=0; i < numPlayer; i++)
            {
                if (isFold[i])
                {
                    evalArr[i] = (-1, 0, i);
                    continue;
                }

                evalArr[i] = (evalValues[i], -entitledPot[i],  i);
            }

            evalArr.Sort();
            int counter = numPlayer - 1;
            long curMaxEval;
            int numMaxEval;
            double entitledShare;
            double entitled;
            // Entitled pot already paid out to better hands, and the level paid out within the current tie
            double paidLevel = 0;
            double tieLevel;
            // pot > 1e-9 to ignore any rounding errors, folded players (evalValue = -1) never win
            while (counter >= 0 && pot > 1e-9 && evalArr[counter].evalValue >= 0)
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

                // Resolve the ties: going down the tie, entitled pot goes up (sorted by negEntitledPot).
                // Each side pot slice above what was already paid is split among the tied players entitled to it.
                counter += numMaxEval;
                entitledShare = 0;
                tieLevel = paidLevel;
                for (int i = 0; i < numMaxEval; i++)
                {
                    // IMPORTANT: This engine doesn't give a fuck about odd chips, since too complicated for no
                    // reason. So we'll allow fractional betting and stack size.
                    entitled = -evalArr[counter].negEntitledPot;
                    if (entitled > tieLevel)
                    {
                        entitledShare += (entitled - tieLevel) / (numMaxEval - i);
                        tieLevel = entitled;
                    }
                    if (entitledShare > 0)
                    {
                        agents[evalArr[counter].playerIndex].ReceiveReturn(
                            entitledShare
                        );
                        pnl[evalArr[counter].playerIndex] += entitledShare;
                    }
                    counter--;
                }
                pot -= tieLevel - paidLevel;
                paidLevel = tieLevel;
            }

            history.Add(EncodeHistoryShowdownBoard(board));
            history.Add(finalPot);
            for (int i = 0; i < numPlayer; i++)
            {
                history.Add(EncodeHistoryShowdownPnL(agents[i].GetId(), pnl[i]));
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
            Reset();

            // Heads-up: the dealer posts the small blind, acts first pre-flop and last post-flop
            int smallBlind = numPlayer == 2 ? dealer : (dealer + 1) % numPlayer;
            int bigBlind = (smallBlind + 1) % numPlayer;

            // Deal card
            for (int i=0; i < numPlayer; i++) {
                agents[(dealer + 3 + i) % agents.Count].ReceiveDealCard(game.DealCard(), game.DealCard());
            }

            Span<bool> isFold = stackalloc bool[numPlayer];
            Span<double> isAllIn = stackalloc double[numPlayer]; // Stored the all-in amount
            Span<double> entitledPot = stackalloc double[numPlayer]; // Stored the pot entitled by each player
            Span<double> totalBet = stackalloc double[numPlayer]; // Stored the bet of each player in the current round
            Span<double> handBet = stackalloc double[numPlayer]; // Stored the bet of each player over the whole hand
            int activePlayers = numPlayer;
            int numAllIn = 0;


            Span<int> board = stackalloc int[5];
            double pot = Game.SMALL_BLIND + Game.BIG_BLIND;
            int isFinished = -1;

            // PRE-FLOP
            // Blinds: agents manage their own stack, so the blinds are charged as negative return
            agents[smallBlind].ReceiveReturn(-Game.SMALL_BLIND);
            agents[bigBlind].ReceiveReturn(-Game.BIG_BLIND);
            totalBet[smallBlind] = Game.SMALL_BLIND;
            totalBet[bigBlind] = Game.BIG_BLIND;
            history.Add(EncodeHistoryBetState(agents[smallBlind].GetId(), Game.SMALL_BLIND, 0, 0));
            history.Add(EncodeHistoryBetState(agents[bigBlind].GetId(), Game.BIG_BLIND, 0, 0));

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref totalBet, ref handBet, ref board, 
                0, pot, activePlayers, numAllIn, Game.BIG_BLIND, (bigBlind + 1) % numPlayer, Game.BIG_BLIND);

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
            totalBet.Clear();
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref totalBet, ref handBet, ref board,
                3, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, Game.BIG_BLIND);

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
            totalBet.Clear();
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref totalBet, ref handBet, ref board,
                4, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, Game.BIG_BLIND);

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
            totalBet.Clear();
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(ref isFold, ref isAllIn, ref entitledPot, ref totalBet, ref handBet, ref board,
                5, pot, activePlayers, numAllIn, 0, (dealer + 1) % numPlayer, Game.BIG_BLIND);

            if (isFinished != -1)
            {
                HandleEarlyFinish(isFinished, pot);
                return;
            }

            HandleShowdown(board, isFold, isAllIn, entitledPot, handBet, pot);
            dealer = (dealer + 1) % numPlayer;
        }
    }
}
