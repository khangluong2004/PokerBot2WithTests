# PokerBot2

A fast No-Limit Texas Hold'em engine for training poker bots in house. It deals hands, runs the betting with agents that plug in through `IAgent`, settles side pots, and records a compact integer history for each hand.

- `Game.cs`: card encoding, deck, hand evaluation (scalar and AVX-512 batch).
- `Arena.cs`: table, betting rounds, side pots, showdown, history encoding.
- `Agents/IAgent.cs`: the agent contract. `Agents/HumanPlayer.cs` is a console agent.
- `../PokerBot2UnitTests`: NUnit tests (`dotnet test`).

## Game rules

| Setting | Value |
|---|---|
| Players | 2 to 9 (`Arena(numPlayer, agents)`) |
| Blinds | Small blind 10, big blind 20 (`Game.SMALL_BLIND`, `Game.BIG_BLIND`) |
| Starting stack | 2500 (`Game.STACK`); agents own their stack |
| Betting | No limit. Bets can be fractional (`double`) |

**Order of play** (`Arena.RoundPlay`, one hand per call):
1. The deck is reshuffled and the history cleared.
2. Every player is dealt 2 hole cards.
3. Blinds are posted: small blind left of the dealer, big blind next. **Heads-up**, the dealer posts the small blind.
4. **Pre-flop**: action starts left of the big blind and the big blind has the option.
5. **Flop** (3 cards), **turn** (1), **river** (1): action starts left of the dealer (heads-up: the non-dealer).
6. If only one player is left at any point, they win the pot without a showdown. Otherwise, the best hand wins at showdown, with side pots.
7. The dealer button moves one seat to the left.

**Betting rules:**
- The minimum raise is the size of the previous raise. Pre-flop that starts at the big blind, and after the flop the minimum bet is the big blind.
- A betting round ends when the action returns to the last player who raised (or to the first player, if nobody raised).
- **Short all-in**: an all-in that's less than a full raise still has to be matched by everyone, but it doesn't reopen the raising. Players who already acted can only call or fold. Several short all-ins that together add up to a full raise do reopen it, and the minimum raise stays the same.
- A player who is the only one not all-in only acts while they still owe chips to call.

**Side pots**: each player can win, from each opponent, at most what they put in themselves: `entitled[i] = Σ_j min(handBet[i], handBet[j])`, using bets over the whole hand. Hands are paid from best to worst, and each one only gets the part of its entitlement that better hands haven't already taken. Tied players share every slice they're all entitled to equally. Chips from players who folded go to the winners.

**Odd chips**: not handled on purpose. Pots are split as exact fractions, so stacks can become fractional.

## Agent contract (`IAgent`)

```csharp
double Play(IList<int> history, Span<int> board, double toCall, double minRaise);
```

| Return | Meaning |
|---|---|
| `Game.FOLD_VALUE` | Fold |
| `toCall` | Call (`0` = check) |
| `toCall + r`, with `r >= minRaise` | Raise by `r` |
| `Game.ALL_IN_VALUE` | All-in: the arena then calls `AllIn()`, which returns the whole stack and sets it to 0 |

- `toCall` is the exact number of chips needed to call, and `minRaise` is the minimum extra on top. `minRaise` is `+∞` when the player may not raise, i.e. a short all-in didn't reopen betting.
- **Agents manage their own stack.** `Play` must subtract whatever it returns (fold and all-in excepted). The arena charges blinds with `ReceiveReturn(-blind)` and pays winnings with `ReceiveReturn(amount)`.
- `GetDealCard()` returns the hole cards from `ReceiveDealCard`. `GetId()` must be unique per player and fit in 4 bits (`0..15`, and not `11`, see the history encoding).

### Assumptions (checked by `Debug.Assert` only)

The engine trusts the agents, so these checks cost nothing in Release builds:
- A bet is never more than the stack.
- Betting the whole stack is declared as `ALL_IN_VALUE`, never as a call or raise that leaves 0 chips.
- A call matches `toCall` exactly, and a raise is at least `minRaise`.
- An agent never raises when `minRaise` is `+∞`.
- A player's stack covers the blind they have to post. This one isn't checked at all: top up or remove short stacks before a hand.

Run training at least once in Debug, so a misbehaving agent fails an assert instead of silently corrupting the game.

## Card encoding

```
card = rank * 4 + suit        0..51
rank = card >> 2              0 = 2, 1 = 3, ..., 8 = 10, 9 = J, 10 = Q, 11 = K, 12 = A
suit = card & 3               0 = ♠, 1 = ♣, 2 = ♦, 3 = ♥
```

`Game.StringToCard("A♠")` parses a rank character followed by a suit symbol. A filler card (used to evaluate fewer than 7 cards) is `-4`, which has rank `-1`.

## Hand evaluation

### Hand mask: 16 bits per suit

Hands are evaluated from a `ulong` mask with **one 16-bit lane per suit**, and one bit per rank in each lane:

```
bit (suit * 16 + rank)        Game.ToCardMask(card) = 1UL << ((suit << 4) + rank)

 63      48 47      32 31      16 15       0
 [ hearts ] [diamonds] [ clubs  ] [ spades ]   low 13 bits of each lane are the ranks 2..A
```

`Game.ToHandMask(cards)` ORs the cards together and skips fillers. A hand is just `boardMask | hole1 | hole2`, so the board is encoded once per showdown. Everything below is then bitwise operations without loops:

- **Rank counts across the 4 suits** (`s, c, d, h` = each suit's 13-bit mask):
  ```
  ranks = s | c | d | h
  two   = (s&c) | (d&h) | ((s|c) & (d|h))       // pair or better
  three = (s&c & (d|h)) | (d&h & (s|c))         // trip or better
  four  = s & c & d & h                         // quad
  ```
- **Flush**: a suit with `popcount >= 5`. With 7 cards at most one suit can have that many.
- **Straight**: `m = ranks << 1 | (ace bit → bit 0)`, and `m & m>>1 & m>>2 & m>>3 & m>>4` keeps the bottom of every 5-card run. The ace counts low too, for the wheel A-2-3-4-5.
- **Top card / kickers**: `31 - lzcnt(mask)`. An empty mask gives `-1`, which is what makes missing kickers encode like filler cards.

### Result encoding

`EvalHand` returns `(orderingValue, WinHandType)`. `Game.ToEvalValue` packs them into one comparable `long`:

```
evalValue = ((long)WinHandType << 32) + orderingValue      // higher wins, equal = split pot
```

`WinHandType`: `HIGH_CARD = 1`, `PAIR`, `TWO_PAIR`, `THREE_OF_A_KIND`, `STRAIGHT`, `FLUSH`, `FULL_HOUSE`, `QUAD`, `STRAIGHT_FLUSH = 9` (royal flush is the highest straight flush).

`orderingValue` packs ranks into **6-bit fields, most important first**:

| Hand type | orderingValue |
|---|---|
| Straight flush, straight | Rank of the top card (the wheel A-2-3-4-5 is `3`, its 5) |
| Quad | `quad << 6 \| kicker` |
| Full house | `trip << 6 \| pair` (the pair may come from a second trip) |
| Flush | The top 5 suited ranks: `r1 << 24 \| r2 << 18 \| r3 << 12 \| r4 << 6 \| r5` |
| Three of a kind | `trip << 12 \| k1 << 6 \| k2` |
| Two pair | `highPair << 12 \| lowPair << 6 \| kicker` (the kicker can come from a third pair) |
| Pair | `pair << 18 \| k1 << 12 \| k2 << 6 \| k3` |
| High card | The top 5 ranks, like a flush |

With fillers (fewer than 5 real cards) a missing kicker is rank `-1`, so values stay consistent for hands with the same number of cards.

### APIs

| API | Use |
|---|---|
| `EvalHand(Span<int> cards, int numFiller)` | 7 cards (fillers allowed). Doesn't modify the input. Kept for compatibility |
| `EvalHand(ulong handMask)` | Same result from a mask |
| `EvalHandValue(ulong handMask)` | `ToEvalValue(EvalHand(mask))` without the tuple |
| `EvalHandValues(ReadOnlySpan<ulong>, Span<long>)` | Batch. Uses AVX-512 (8 hands per instruction, branch-free) when supported, scalar otherwise. Used by `Arena.HandleShowdown` |

Single thread on a Ryzen 9 9900X (Zen 5), random 7-card hands:

| Evaluator | ns/hand |
|---|---|
| Original sort-based `EvalHand` | 84.4 |
| `EvalHand(int[])` | 11.5 |
| `EvalHandValue(ulong)` | 8.3 |
| `EvalHandValues` (AVX-512) | 2.3 |

**Native AOT**: the project sets `PublishAot`. Ahead-of-time code is built for a baseline instruction set, so the AVX-512 path can fall back to scalar unless you raise it, e.g. `<IlcInstructionSet>native</IlcInstructionSet>`. `dotnet run` (JIT) detects the CPU by itself.

### Correctness

`EvalHandReferenceTest` checks the evaluator against an independent reference: the best of the 21 five-card subsets, scored the textbook way. Two exhaustive tests are marked `[Explicit]` because they take about a minute:
- every one of the 133,784,560 seven-card hands ranks like the reference, with exactly 4,824 distinct values;
- the AVX-512 batch equals the scalar result on every hand.

```bash
dotnet test PokerBot2UnitTests --filter "FullyQualifiedName~AllSevenCardHands"
```

## History encoding

`Arena` keeps the hand history as `IList<int>` and passes it to `Play`. It's cleared at the start of each hand.

| Entry | Encoding |
|---|---|
| Bet action (blind, call, raise, all-in, fold) | bits 0–3 `playerId`, bit 4 reserved (showdown flag, unused), bits 5–19 the player's **total bet in this betting round** (integer part), bit 20 fold, bit 21 all-in |
| New street dealt | `DEALER_HISTORY_STATE` (`11`) |
| Showdown, written at the end of `HandleShowdown` | 1 entry for the board: 5 cards × 6 bits, `board[0]` lowest. Then 1 entry for the pot (integer part). Then 1 entry per player in seat order: bits 0–3 `playerId`, upper bits the signed PnL (integer part, decode with `entry >> 4`) |

`Arena.GetRoundBet(history, playerId)` decodes a player's current bet in the round.

## Known limitations

- The bet field is 15 bits, so totals above 32,767 don't fit. The default table (at most 9 × 2,500 = 22,500 chips) is safe, but larger stacks are not.
- The history only keeps integer parts. Use `toCall` / `minRaise` for exact amounts.
- Hands that end because everyone else folded don't write showdown entries.
- `IAgent.Reset(playerId, position)` is never called by the arena.
- Shuffling uses `Random.Shared`, so games can't be reproduced from a seed.
- `Game.CardToString` prints ranks 2–10 wrongly (`'1' + rank` is an `int`), and `StringToCard` can't parse a ten. Both are display/parsing only and don't affect play.
