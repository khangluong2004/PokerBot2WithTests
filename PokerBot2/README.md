# PokerBot2

A fast No-Limit Texas Hold'em engine for training poker bots in house. It deals hands, runs the betting with agents that plug in through `IAgent`, settles side pots, and records a compact integer history for each hand.

- `Game.cs`: card encoding, dealing, hand evaluation (scalar and AVX-512 batch).
- `Arena.cs`: table, betting rounds, side pots, showdown, history encoding.
- `Agents/IAgent.cs`: the agent contract. `Agents/HumanPlayer.cs` is a console agent.
- `PokerBot2.csproj`: build settings for speed (see [Build settings and performance](#build-settings-and-performance)).
- `../PokerBot2UnitTests`: NUnit tests (`dotnet test`).

## Game rules

| Setting | Value |
|---|---|
| Players | 2 to 9 (`new Arena(numPlayer, agents, seed)`, `seed` optional) |
| Blinds | Small blind 10, big blind 20 (`Game.SMALL_BLIND`, `Game.BIG_BLIND`) |
| Starting stack | 2500 (`Game.STACK`); agents own their stack |
| Betting | No limit. Bets can be fractional (`double`) |

**Order of play** (`Arena.RoundPlay`, one hand per call):
1. A new hand starts: the history is cleared and cards are drawn from the full deck again.
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
- **Short-stacked blind**: a player who can't cover their blind posts their whole stack and is all-in for it (the history entry has the all-in flag). They're never asked to act. Everyone else still has to call the full big blind.
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

- `toCall` is the exact number of chips needed to call, and `minRaise` is the minimum extra on top. `minRaise` is `+∞` when the player may not raise, i.e. a short all-in didn't reopen betting. To call, return `toCall` itself (not a recomputed amount): the arena compares the return value against it exactly.
- **Agents manage their own stack.** `Play` must subtract whatever it returns (fold and all-in excepted). The arena charges blinds with `ReceiveReturn(-blind)` and pays winnings with `ReceiveReturn(amount)`.
- `GetDealCard()` returns the hole cards from `ReceiveDealCard`. `GetId()` must be unique per player and fit in 4 bits (`0..15`, and not `11`, see the history encoding). The arena reads the ids when it's created and again at the start of every hand.
- The arena copies the agent list when it's created. To change who sits at the table, create a new `Arena`.

### Assumptions (checked by `Debug.Assert` only)

The engine trusts the agents, so these checks cost nothing in Release builds:
- A bet is never more than the stack.
- Betting the whole stack is declared as `ALL_IN_VALUE`, never as a call or raise that leaves 0 chips.
- A call matches `toCall` exactly, and a raise is at least `minRaise` (within `1e-9` chips, see [floating point](#floating-point)).
- An agent never raises when `minRaise` is `+∞`.
- Every seated player has more than 0 chips when a hand starts. Remove or top up busted players between hands.

Run training at least once in Debug, so a misbehaving agent fails an assert instead of silently corrupting the game.

## Card encoding

```
card = rank * 4 + suit        0..51
rank = card >> 2              0 = 2, 1 = 3, ..., 8 = 10, 9 = J, 10 = Q, 11 = K, 12 = A
suit = card & 3               0 = ♠, 1 = ♣, 2 = ♦, 3 = ♥
```

`Game.StringToCard` parses the rank (`2`–`10`, `J`, `Q`, `K`, `A`) followed by the suit symbol, e.g. `"10♠"` or `"A♥"`, and `Game.CardToString` prints the same format, so the two round-trip. A filler card (used to evaluate fewer than 7 cards) is `-4`, which has rank `-1`.

## Dealing and seeds

`Game.DealCard` does a **lazy Fisher–Yates** shuffle: each call swaps a random card from the rest of the deck into the next position (`Rng.Next(CurDealtCard, 52)`). A hand therefore pays for the 2N + 5 cards it uses (23 for 9 players) instead of shuffling all 52, and `Game.Reset` is just "start from the top again". Any order of the deck works as a starting point, so it's never reset to sorted.

- **Generator**: a plain `System.Random` per `Game`. Unseeded, it uses **xoshiro256\*\*** and its bounded `Next(min, max)` uses rejection sampling, so every card is exactly equally likely.
- **Seeds**: `new Arena(numPlayer, agents, seed)` (or `new Game(numPlayer, seed)`, `seed` is an `int`) deals the same cards every time for the same seed. With deterministic agents, the whole game replays exactly. Leave `seed` out for a random one. A seeded `Random` falls back to the **legacy .NET Framework algorithm** (Knuth's subtractive generator) for compatibility. It's slower, and its `Next(min, max)` scales a floating-point sample: 52 cards over about 2³¹ sample values, a bias of a few parts in 10⁸ that doesn't matter here.
- **Which to use**: for training where throughput matters, **leave the seed out**. Unseeded games are statistically just as good and use the faster generator. Pass a seed only to reproduce a game: debugging an agent, replaying a failure, or tests.

Dealing a 9-player hand (23 cards), measured on its own:

| Generator | ns |
|---|---|
| `new Random()` (unseeded, used) | 41 |
| `new Random(seed)` (seeded, used) | 98 |
| `Random.Shared` | 45 |
| Hand-written xoshiro256** with Lemire's bounded draw (tried, not used) | 36 |
| Old `Random.Shared.Shuffle` of all 52 cards | 125 |

The hand-written generator made whole hands 2–6% faster unseeded and 6–8% faster seeded with the trivial benchmark agents. It was dropped for the library, which removes about 55 lines of RNG code: with real agents (a network forward pass or a strategy lookup per decision), dealing is a negligible part of a hand.

`GameCardTest` checks that no card repeats within a hand and that the first and the last dealt card are uniform (chi-square over 520,000 hands).

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

### Correctness

`EvalHandReferenceTest` checks the evaluator against an independent reference: the best of the 21 five-card subsets, scored the textbook way. Two exhaustive tests are marked `[Explicit]` because they take about a minute:
- every one of the 133,784,560 seven-card hands ranks like the reference, with exactly 4,824 distinct values;
- the AVX-512 batch equals the scalar result on every hand.

```bash
dotnet test PokerBot2UnitTests --filter "FullyQualifiedName~AllSevenCardHands"
```

## History encoding

`Arena` keeps the hand history as a `List<int>` and passes it to `Play` as `IList<int>`. It's cleared at the start of each hand.

| Entry | Encoding |
|---|---|
| Bet action (blind, call, raise, all-in, fold) | bits 0–3 `playerId`, bit 4 reserved (showdown flag, unused), bits 5–19 the player's **total bet in this betting round** (integer part), bit 20 fold, bit 21 all-in |
| New street dealt | `Arena.DEALER_HISTORY_STATE` (`11`) |
| Showdown, written at the end of `HandleShowdown` | 1 entry for the board: 5 cards × 6 bits, `board[0]` lowest. Then 1 entry for the pot (integer part). Then 1 entry per player in seat order: bits 0–3 `playerId`, upper bits the signed PnL (integer part, decode with `entry >> 4`) |

`Arena.GetRoundBet(history, playerId)` decodes a player's current bet in the round.

## Arena implementation notes

**Zero allocations per hand**, under both the JIT and Native AOT. Everything per hand lives in `stackalloc` spans (at most 9 entries), and the history list keeps its capacity across hands.

What the betting loop avoids, and why:
- **No integer division**: the next seat is `seat + 1 == numPlayer ? 0 : seat + 1` instead of `(seat + 1) % numPlayer`. A division is 10+ cycles, and the loop moves to the next seat on every action.
- **No interface calls for bookkeeping**: agents are kept in an `IAgent[]` and the history in a `List<int>` (not `IList<>`), and player ids are cached in an `int[]`. Only the real agent calls (`Play`, `AllIn`, `ReceiveDealCard`, `GetDealCard`, `ReceiveReturn`) go through the interface.
- **Spans passed by value** to `RoundBet` (not `ref Span`), so the JIT can keep their pointers and lengths in registers.
- **Side-pot entitlement computed once**, just before the showdown. It used to be recomputed after every betting round, which is a 9×9 loop once anyone is all-in, even for hands that end early.
- **Insertion sort at showdown**: at most 9 hands to rank, and `Span.Sort` on tuples allocated 48 B per showdown under Native AOT (its default comparer).
- `DEALER_HISTORY_STATE` is a `const`, so it folds into the code.

### Floating point

Amounts become fractional after split pots, so the arena never recomputes a number the agent already used:
- **Call or raise** is decided by comparing the return value with the exact `toCall` it passed (`bet > toCall`). Recomputing `totalBet + bet > maxBet` could round a call 1 ulp above `maxBet` and turn it into a raise that reopens the betting.
- **Minimum raise** is checked with a tolerance of `CHIP_EPSILON = 1e-9` chips: an exact `toCall + minRaise` can round 1 ulp below the minimum. The same tolerance stops the showdown payout loop.

`RoundPlay_ShouldAcceptExactMinRaise_WithFractionalAmounts` replays the two seeds that hit this before the fix.

## Build settings and performance

Build and run with **`-c Release`**. Debug keeps the `Debug.Assert` checks (use it to validate agents); Release removes them.

```bash
dotnet run -c Release --project PokerBot2
dotnet publish PokerBot2 -c Release -r win-x64      # Native AOT
```

`PokerBot2.csproj` settings:

| Setting | Applies to | Why |
|---|---|---|
| `TieredPGO=true` | JIT | Dynamic PGO can devirtualize the agent calls. It's the default since .NET 8, set explicitly so it isn't turned off by accident |
| `ServerGarbageCollection=true`, `ConcurrentGarbageCollection=false` | JIT and AOT | Throughput GC. The engine allocates nothing per hand, so this only matters when agents allocate |
| `IlcInstructionSet=native` | Native AOT | Compile for this machine's CPU (AVX-512, LZCNT, BMI) instead of the x86-64-v2 baseline. The published binary only runs on CPUs with the same instruction sets |
| `OptimizationPreference=Speed` | Native AOT | Optimize for speed over size |
| `PublishAot=true`, `InvariantGlobalization=true` | Already set | |

Runtime settings (`TieredPGO`, GC) belong to the application. If training runs from a separate project that references PokerBot2, put them in that project's `.csproj`. Native AOT needs the Visual Studio "Desktop development with C++" workload. If the link step fails with `'vswhere.exe' is not recognized`, add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` or publish from a Developer PowerShell.

Measured on a Ryzen 9 9900X, Release, single thread, 1M hands per run, best run out of 3 processes × 5 runs (single process for "before"). The numbers include the agents' own work. "Always call" sends every hand to showdown; "mixed" folds 25%, raises 15% and goes all-in 3% of the time.

| Agents | Players | Before (ns/hand) | JIT now | Native AOT now |
|---|---|---|---|---|
| Always call | 2 | 315 | 149 | 170 |
| Always call | 6 | 583 | 336 | 374 |
| Always call | 9 | 758 | 468 | 503 |
| Mixed | 2 | 294 | 134 | 145 |
| Mixed | 6 | 785 | 543 | 548 |
| Mixed | 9 | 1,175 | 757 | 780 |

| Hand evaluator (ns/hand) | JIT | Native AOT, default CPU | Native AOT, `IlcInstructionSet=native` |
|---|---|---|---|
| `EvalHandValue` (scalar) | 8.5 | 9.7 | 8.4 |
| `EvalHandValues` (batch) | 2.4 | 9.8 (no AVX-512) | 2.2 |

What the measurements say:
- **The JIT is the fastest way to run the Arena**, about 1–14% ahead of Native AOT (AOT can't devirtualize the agent calls, or the call into `System.Random`). Prefer `dotnet run -c Release` for training. Native AOT is a close second and starts instantly.
- **Train unseeded** when throughput matters (`new Arena(numPlayer, agents)`): a seeded `Random` uses the slower legacy algorithm and costs about 5–8% per hand with these benchmark agents.
- **Native AOT without `IlcInstructionSet=native` loses AVX-512 and LZCNT**, and the batch evaluator gets 4x slower. That's why the setting is on.
- With a single agent type, the JIT knobs are within noise. 9 players, always call / mixed (ns/hand): default 459 / 777, `DOTNET_TieredPGO=0` 459 / 781, `DOTNET_TieredCompilation=0` 469 / 763, Server GC 460 / 773.
- **Not enabled on purpose**: `SkipLocalsInit` showed no measurable gain (the `stackalloc` buffers are at most 9 entries), and the code relies on them starting at zero. Disabling tiered compilation gave no gain and turns off dynamic PGO.

## Known limitations

- The bet field is 15 bits, so totals above 32,767 don't fit. The default table (at most 9 × 2,500 = 22,500 chips) is safe, but larger stacks are not.
- The history only keeps integer parts. Use `toCall` / `minRaise` for exact amounts.
- Hands that end because everyone else folded don't write showdown entries.
- `IAgent.Reset(playerId, position)` is never called by the arena.

## Changelog

### 2026-10-24

Fixes:
- `CardToString` printed every rank from 2 to 10 one too low (a 2 as `"1"`), and `StringToCard("10♠")` returned a queen. All 52 cards now round-trip.
- A short-stacked blind is now marked all-in. Before, it was asked to act with 0 chips, its "all-in for 0" was counted without marking it, and the other players were then skipped on the flop, turn and river.
- With fractional amounts, an exact minimum raise could fail the min-raise check, and a call could be taken for a raise (see [Floating point](#floating-point)).

Features:
- Seeded, reproducible games: `new Arena(numPlayer, agents, seed)`, `new Game(numPlayer, seed)` (`int` seed).

Performance (see the tables above):
- Lazy dealing with `System.Random.Next` instead of shuffling all 52 cards with `Random.Shared`: 125 ns → 41 ns to deal a 9-player hand (98 ns seeded).
- Arena betting loop and showdown: see [Arena implementation notes](#arena-implementation-notes).
- Build settings in `PokerBot2.csproj`.

API changes:
- `Arena.DEALER_HISTORY_STATE` is a `const` (it was a mutable `static`).
- `Arena.RoundBet` takes its spans by value and no longer has the `entitledPot` parameter.
- `Arena` copies the agent list when it's created.

Docs:
- Comments on every non-trivial bitwise step of the hand evaluator in `Game.cs` (scalar and AVX-512): the suit-lane layout, the rank-count formulas, the straight shift-and-AND, `lzcnt` top-rank extraction, and the SWAR popcount for flushes.

Tests (52 run by default, plus 2 explicit exhaustive ones):
- `GameCardTest`: card strings round-trip; dealing never repeats a card in a hand and is uniform.
- `ArenaTest`: short-stacked blind is all-in, the same seed replays the same hands, and the floating-point regression seeds. The random chip-conservation test is now seeded, so a failure can be replayed.
