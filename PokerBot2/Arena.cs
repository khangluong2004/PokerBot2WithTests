using PokerBot2.Agents;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PokerBot2
{
    public class Arena
    {
        public const int DEALER_HISTORY_STATE = 11;
        // Tolerance for comparing chip amounts, which can be fractional after split pots
        const double CHIP_EPSILON = 1e-9;

        // Game structure: 10 small blind, 20 big blind, 2500 stack per player, up to 9 players
        readonly int numPlayer;
        int dealer = 0;
        // Copied to an array, so seat lookups are not interface calls
        readonly IAgent[] agents;
        // Player ids, refreshed once per hand instead of calling GetId() for every history entry
        readonly int[] ids;
        // History action will be encode as: first 4 bits for playerId (if set to 11, then it's dealer),
        // For player: next 1 bit = whether showdown or not,
        // If not showdown: next 15 bits for bet (the player's bet in the current betting round, integer part for all-in),
        // next 1 bit for fold or not, next 1 bit for all-in or not
        // If showdown (at the end of the hand): 1 number with 30 bits for 5 card on board (6 bits each, board[0] first),
        // 1 number for the pot size (integer part), followed by numPlayers numbers for each player:
        // first 4 bits for playerId, the rest for their PnL (integer part, signed, decode with >> 4)
        // Concrete List (not IList) so Add is not an interface call; capacity is kept across hands
        readonly List<int> history = new(256);
        readonly Game game;

        // seed: fixed seed for reproducible games, null = random seed
        public Arena(int numPlayer, IList<IAgent> agents, int? seed = null)
        {
            Debug.Assert(numPlayer <= 9);
            Debug.Assert(agents.Count == numPlayer, "One agent per seat");
            this.numPlayer = numPlayer;
            game = new Game(numPlayer, seed);
            this.agents = [.. agents];
            ids = new int[numPlayer];
            for (int i = 0; i < numPlayer; i++)
            {
                ids[i] = this.agents[i].GetId();
            }
        }

        // Reset for new round
        public void Reset()
        {
            history.Clear();
            game.Reset();
        }

        // Next seat clockwise, without the integer division of (seat + 1) % numPlayer
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int NextSeat(int seat)
        {
            seat++;
            return seat == numPlayer ? 0 : seat;
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

        // Spans are passed by value (not ref Span), so the JIT can keep their pointer and length in registers
        public (double pot, int activePlayers, int numAllIn, int isFinished) RoundBet(Span<bool> isFold,
            Span<double> isAllIn,
            Span<double> totalBet,
            Span<double> handBet,
            Span<int> board,
            int numCard,
            double pot, int activePlayers,
            int numAllIn,
            double startBet, int startPlayer, double startRaise)
        {
            int curPlayer = startPlayer;
            int actionPlayer = curPlayer;

            var prevRaise = startRaise;
            var maxBet = startBet;
            double bet, toCall;

            int winner = -1;
            var preAllInMaxBet = maxBet;
            Span<int> dealtBoard = board[..numCard];

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
                    IAgent agent = agents[curPlayer];
                    // Exact chips needed to call, and the min raise on top of it (infinite if betting not re-opened)
                    toCall = maxBet - totalBet[curPlayer];
                    bet = agent.Play(history, dealtBoard, toCall,
                        canRaise[curPlayer] ? prevRaise : double.PositiveInfinity);
                    Debug.Assert(bet == Game.FOLD_VALUE || bet == Game.ALL_IN_VALUE || agent.GetStack() >= 0,
                        "Can't bet more than stack");
                    if (bet == Game.FOLD_VALUE)
                    {
                        isFold[curPlayer] = true;
                        activePlayers--;
                        history.Add(EncodeHistoryBetState(ids[curPlayer], 0, 1, 0));
                    }
                    else if (bet == Game.ALL_IN_VALUE)
                    {
                        isAllIn[curPlayer] = agent.AllIn();
                        numAllIn++;
                        pot += isAllIn[curPlayer];

                        totalBet[curPlayer] += isAllIn[curPlayer];

                        history.Add(EncodeHistoryBetState(ids[curPlayer], (int) totalBet[curPlayer], 0, 1));

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
                    // Compare with the exact toCall the agent got: recomputing totalBet + bet > maxBet can round
                    // a call 1 ulp above maxBet once amounts are fractional, and turn it into a raise
                    else if (bet > toCall)
                    {
                        Debug.Assert(canRaise[curPlayer], "Betting not re-opened, can only call or fold");
                        // toCall + minRaise can round 1 ulp below the min raise with fractional amounts
                        Debug.Assert(bet - toCall >= prevRaise - CHIP_EPSILON, "Need to raise minimum prev raise");
                        pot += bet;
                        totalBet[curPlayer] += bet;
                        prevRaise = totalBet[curPlayer] - maxBet;
                        maxBet = totalBet[curPlayer];
                        preAllInMaxBet = maxBet;
                        actionPlayer = curPlayer;
                        canRaise.Fill(true);
                        history.Add(EncodeHistoryBetState(ids[curPlayer], totalBet[curPlayer], 0, 0));
                    }
                    else
                    {
                        // Call (check if nothing to call)
                        Debug.Assert(bet == toCall, "Must fold if bet less");
                        pot += bet;
                        // Set exactly to avoid floating point drift from maxBet
                        totalBet[curPlayer] = maxBet;
                        history.Add(EncodeHistoryBetState(ids[curPlayer], totalBet[curPlayer], 0, 0));
                    }

                    // Agents must declare ALL_IN_VALUE when betting their whole stack (no runtime check, for speed)
                    Debug.Assert(bet == Game.FOLD_VALUE || bet == Game.ALL_IN_VALUE || agent.GetStack() > 0,
                        "Betting the whole stack must be declared as all-in");
                    canRaise[curPlayer] = false;
                }

                curPlayer = NextSeat(curPlayer);
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

            return (pot, activePlayers, numAllIn, winner);
        }

        // Splitting pot: each player is entitled to at most what they bet, from every player.
        // Only needed at showdown, so it's computed once per hand instead of after every betting round
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeEntitledPot(Span<double> entitledPot, Span<double> handBet, double pot, int numAllIn)
        {
            if (numAllIn == 0)
            {
                // If no all in, then everyone still in has the same pot
                entitledPot.Fill(pot);
                return;
            }

            // Use the bets over the whole hand: summing per round is wrong once a player calls a fractional
            // all-in with its integer part, then bets more than the all-in player on a later round
            for (int i = 0; i < numPlayer; i++)
            {
                double entitled = 0;
                for (int j = 0; j < numPlayer; j++)
                {
                    entitled += Math.Min(handBet[i], handBet[j]);
                }
                entitledPot[i] = entitled;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAfter((long evalValue, double negEntitledPot, int playerIndex) a,
            (long evalValue, double negEntitledPot, int playerIndex) b)
        {
            if (a.evalValue != b.evalValue) return a.evalValue > b.evalValue;
            if (a.negEntitledPot != b.negEntitledPot) return a.negEntitledPot > b.negEntitledPot;
            return a.playerIndex > b.playerIndex;
        }

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

            // Insertion sort, same order as the tuple's default comparison (evalValue, then negEntitledPot, then
            // playerIndex). At most 9 items, and unlike Span.Sort it needs no comparer (which allocates under AOT)
            for (int i = 1; i < numPlayer; i++)
            {
                var item = evalArr[i];
                int j = i - 1;
                while (j >= 0 && IsAfter(evalArr[j], item))
                {
                    evalArr[j + 1] = evalArr[j];
                    j--;
                }
                evalArr[j + 1] = item;
            }
            int counter = numPlayer - 1;
            long curMaxEval;
            int numMaxEval;
            double entitledShare;
            double entitled;
            // Entitled pot already paid out to better hands, and the level paid out within the current tie
            double paidLevel = 0;
            double tieLevel;
            // pot > CHIP_EPSILON to ignore any rounding errors, folded players (evalValue = -1) never win
            while (counter >= 0 && pot > CHIP_EPSILON && evalArr[counter].evalValue >= 0)
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
                history.Add(EncodeHistoryShowdownPnL(ids[i], pnl[i]));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void HandleEarlyFinish(int winner, double pot)
        {
            agents[winner].ReceiveReturn(pot);
            dealer = NextSeat(dealer);
        }

        public void RoundPlay()
        {
            Reset();

            // Heads-up: the dealer posts the small blind, acts first pre-flop and last post-flop
            int smallBlind = numPlayer == 2 ? dealer : NextSeat(dealer);
            int bigBlind = NextSeat(smallBlind);

            // Deal card
            for (int i = 0, seat = (dealer + 3) % numPlayer; i < numPlayer; i++, seat = NextSeat(seat)) {
                agents[seat].ReceiveDealCard(game.DealCard(), game.DealCard());
            }

            for (int i = 0; i < numPlayer; i++)
            {
                ids[i] = agents[i].GetId();
                Debug.Assert(agents[i].GetStack() > 0, "Players with no chips can't be dealt in");
            }

            Span<bool> isFold = stackalloc bool[numPlayer];
            Span<double> isAllIn = stackalloc double[numPlayer]; // Stored the all-in amount
            Span<double> entitledPot = stackalloc double[numPlayer]; // Stored the pot entitled by each player
            Span<double> totalBet = stackalloc double[numPlayer]; // Stored the bet of each player in the current round
            Span<double> handBet = stackalloc double[numPlayer]; // Stored the bet of each player over the whole hand
            int activePlayers = numPlayer;
            int numAllIn = 0;

            // A player who can't cover the blind posts the whole stack and is all-in
            double smallBlindStack = agents[smallBlind].GetStack();
            double bigBlindStack = agents[bigBlind].GetStack();
            double smallBlindAmount = Math.Min(smallBlindStack, Game.SMALL_BLIND);
            double bigBlindAmount = Math.Min(bigBlindStack, Game.BIG_BLIND);
            int smallBlindAllIn = smallBlindAmount == smallBlindStack ? 1 : 0;
            int bigBlindAllIn = bigBlindAmount == bigBlindStack ? 1 : 0;

            Span<int> board = stackalloc int[5];
            double pot = smallBlindAmount + bigBlindAmount;
            int isFinished = -1;

            // PRE-FLOP
            // Blinds: agents manage their own stack, so the blinds are charged as negative return
            agents[smallBlind].ReceiveReturn(-smallBlindAmount);
            agents[bigBlind].ReceiveReturn(-bigBlindAmount);
            totalBet[smallBlind] = smallBlindAmount;
            totalBet[bigBlind] = bigBlindAmount;
            if (smallBlindAllIn == 1)
            {
                isAllIn[smallBlind] = smallBlindAmount;
                numAllIn++;
            }
            if (bigBlindAllIn == 1)
            {
                isAllIn[bigBlind] = bigBlindAmount;
                numAllIn++;
            }
            history.Add(EncodeHistoryBetState(ids[smallBlind], smallBlindAmount, 0, smallBlindAllIn));
            history.Add(EncodeHistoryBetState(ids[bigBlind], bigBlindAmount, 0, bigBlindAllIn));

            // Bet
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(isFold, isAllIn, totalBet, handBet, board,
                0, pot, activePlayers, numAllIn, Game.BIG_BLIND, NextSeat(bigBlind), Game.BIG_BLIND);

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
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(isFold, isAllIn, totalBet, handBet, board,
                3, pot, activePlayers, numAllIn, 0, NextSeat(dealer), Game.BIG_BLIND);

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
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(isFold, isAllIn, totalBet, handBet, board,
                4, pot, activePlayers, numAllIn, 0, NextSeat(dealer), Game.BIG_BLIND);

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
            (pot, activePlayers, numAllIn, isFinished) = RoundBet(isFold, isAllIn, totalBet, handBet, board,
                5, pot, activePlayers, numAllIn, 0, NextSeat(dealer), Game.BIG_BLIND);

            if (isFinished != -1)
            {
                HandleEarlyFinish(isFinished, pot);
                return;
            }

            ComputeEntitledPot(entitledPot, handBet, pot, numAllIn);
            HandleShowdown(board, isFold, isAllIn, entitledPot, handBet, pot);
            dealer = NextSeat(dealer);
        }
    }
}
