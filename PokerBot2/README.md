   # PokerBot2

A fast No-Limit Texas Hold'em engine for training poker bots in house. It deals hands, runs the betting with agents that plug in through `IAgent`, settles side pots, and records a compact integer history for each hand.

- `Game.cs`: card encoding, dealing, hand evaluation (table-based `EvalHandRank`, plus the bit-operation evaluator in scalar and AVX-512 batch form).
- `Arena.cs`: table, betting rounds, side pots, showdown, history encoding.
- `Agents/IAgent.cs`: the agent contract. `Agents/HumanPlayer.cs` is a console agent.
- `PokerBot2.csproj`: build settings for speed (see [Build settings and performance](#build-settings-and-performance)).
- `../PokerBot2UnitTests`: NUnit tests (`dotnet test`).
- `../EvaluatorBench`: benchmark against other hand evaluators (see [Comparison with other evaluators](#comparison-with-other-evaluators)).

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

There are two evaluators, and they rank hands in exactly the same order:
- **`EvalHandRank`**, table-based, for exactly 7 cards. It returns a single `int`. It's the fastest while its tables are in cache, and it's what `Arena` uses at showdown. See [Table-based evaluator](#table-based-evaluator-evalhandrank).
- **`EvalHand` / `EvalHandValue`**, computed with bit operations and no tables, for 1 to 7 cards (fillers allowed). `EvalHandValues` evaluates many hands at once with AVX-512. These read no memory, so they win once something else has pushed `EvalHandRank`'s tables out of the cache (see [When the tables aren't in cache](#when-the-tables-arent-in-cache)). `EvalHandValue` is also the source of truth that `EvalHandRank`'s tables are built from. The rest of this section describes it.

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
| `EvalHandValue(ulong handMask)` | `ToEvalValue(EvalHand(mask))` without the tuple. Reads no memory: the fastest for 1 or 2 hands when `EvalHandRank`'s tables aren't in cache |
| `EvalHandValues(ReadOnlySpan<ulong>, Span<long>)` | Batch. Uses AVX-512 (8 hands per instruction, branch-free) when supported, scalar otherwise. Reads no memory: the fastest for many hands at once when the tables have been evicted to DRAM |
| `HandKeyMask(int card)`, `HandKeyMask(ReadOnlySpan<int> cards)` | A card's (or the cards') key and mask in one `Vector128<ulong>`, for `EvalHandRank`. Add them up: `HandKeyMask(board) + HandKeyMask(c1) + HandKeyMask(c2)`. `HandKeyMask([])` is the empty hand |
| `EvalHandRank(Vector128<ulong> keyMask)` | **Fastest** while its tables are in cache, exactly 7 cards. Single `int` rank, higher wins, equal = split pot. Used by `Arena.HandleShowdown` |
| `HandRankType(int handRank)` | The `WinHandType` of a rank (`rank >> 12`) |

Single thread on a Ryzen 9 9900X (Zen 5), random 7-card hands (the [comparison](#comparison-with-other-evaluators) has the method):

| Evaluator | ns/hand |
|---|---|
| Original sort-based `EvalHand` | 84.4 |
| `EvalHand(int[])` | 11.5 |
| `EvalHandValue(ulong)` | 8.4 |
| `EvalHandValues` (AVX-512 batch) | 2.3 |
| `EvalHandRank(HandKeyMask(...))`, from the 7 cards | 2.3 |
| `EvalHandRank(keyMask)`, vector given | 1.3 |

### Table-based evaluator (`EvalHandRank`)

**Result.** A single `int`: `(WinHandType << 12) | position among the hands of that type`. Higher wins and equal is a split pot, exactly like `EvalHandValue` (the tables are built from it). `HandRankType(rank)` gives the type back. The ranks aren't consecutive across types, so compare them but don't use them as array indices.

**Key.** Every card has a 64-bit key, and a hand's key is the sum of its cards' keys:

```
bits 0-31   sum of RANK_KEYS[rank]      RANK_KEYS = 0, 7, 35, 161, 1364, 5838, 19938, 48521, 247320, 365745, 804426, 1392250, 2262584
bits 32-47  4-bit counter per suit     EMPTY_HAND_KEY starts each counter at 3
```

**Why suit counts are enough.** Apart from a flush or a straight flush, a hand's value depends only on its ranks; which suit each card is in doesn't matter. So the key keeps all the ranks (as the rank sum), but for the suits only how many cards each one has. The counts tell whether there's a flush, and in which suit. Only then does the evaluator need to know which ranks are in that suit, and it reads them from the hand mask.

- **Rank sum.** The rank keys make the sum different for every multiset of 7 ranks (at most 4 of each), so the sum identifies the hand unless it's a flush. Of all the key sets tried, these give the smallest perfect hash below (see [How `RANK_KEYS` were found](#how-rank_keys-were-found)). The largest sum is 13,227,086.
- **Suit counters.** A suit with 5 or more cards takes its counter from 3 to 8–10, which sets the counter's bit 3. So `key & 0x8888 << 32` is the flush check, and `tzcnt` of it gives the flush suit. A counter never goes above 3 + 7 = 10, so it doesn't carry into the next one.

**Lookup.**
- **Flush**: `Flush[13-bit rank mask of the flush suit]`, 8,192 entries.
- **Anything else**: `Ranks[rankSum + RowOffsets[rankSum >> 11]]`. This is a perfect hash by displacement: the sum's high bits pick a row of 2,048 sums, and each row has its own offset, chosen when the table is built so that the 49,205 rank multisets of 7 cards all get different slots. That's 2 dependent loads. Two smaller layouts were measured and not kept, because they're slower; see [Shrinking the rank table](#shrinking-the-rank-table).

**Tables**: `Ranks` 70,834 × 2 B (138 KB, 69% used), `RowOffsets` 6,459 × 4 B (25 KB), `Flush` 16 KB, and the 52 card vectors: about 180 KB. They're built on first use, in about 0.6 s, by evaluating one hand of every rank multiset and every flush with `EvalHandValue`. They fit in the L2 cache (1 MB per core here). An agent with a big working set of its own can push them out; see [When the tables aren't in cache](#when-the-tables-arent-in-cache).

**Key and mask in one vector.** `HandKeyMask(card)` holds the key and the card mask in one `Vector128<ulong>`. The cards of a hand are all different, so adding their masks is the same as OR-ing them, and one `vpaddq` per card builds both. The mask is only read for a flush.

**Showdown.** The board's key and mask are computed once, and each player only adds their 2 hole cards: `EvalHandRank(board + HandKeyMask(c1) + HandKeyMask(c2))`.

**Credits.** The technique is not new. The rank keys come from a search that started at [SKPokerEval](https://github.com/kennethshackleton/SKPokerEval)'s greedy keys (see [How `RANK_KEYS` were found](#how-rank_keys-were-found)). The suit counters that start at 3, the 128-bit key + mask add, and the row-offset perfect hash are [OMPEval](https://github.com/zekyll/OMPEval)'s. The code and the tables here are PokerBot2's own.

**Limits.** Exactly 7 cards (checked by `Debug.Assert` only). For 5 or 6 cards, use `EvalHand`/`EvalHandValue`. Cards must be 0–51: the card table is bounds-checked, so a bad card throws instead of reading memory past the table. Removing that check measured about 5% faster, and wasn't kept.

#### How `RANK_KEYS` were found

**What the keys must do.** The rank sum `Σ count[r] × RANK_KEYS[r]` must be different for each of the 49,205 multisets of 7 ranks with at most 4 of each rank. Only then does the sum alone identify a non-flush hand.

**Two steps**, both run once, offline. `RankTables.RANK_KEYS` holds the result as constants.
1. [**A greedy search**](#the-greedy-keys) finds the smallest valid keys, `0, 1, 5, 22, ..., 1479181`. They're the ones [SKPokerEval](https://github.com/kennethshackleton/SKPokerEval) uses, and `EvalHandRank` used them until 2026-11-07.
2. [**A search for the smallest tables**](#searching-for-better-rank_keys) starts from them. It changes the keys and the row size until the perfect hash (`Ranks` + `RowOffsets`) is as small as it can find. That took the tables from 250,888 B to 167,504 B (−33%), with the keys used now. The tool is [`EvaluatorBench/RankKeySearch`](../EvaluatorBench/RankKeySearch).

With any keys, the exhaustive test would catch a clash: two clashing hands would share a table slot, so one of them would get the wrong rank.

##### The greedy keys

**The search** goes through the ranks from the deuce up to the ace. For each rank, it picks the smallest key above the previous one that keeps the sums apart.

To test a candidate key `k`, it keeps `partial[t]`: the sums of every partial hand of `t` cards, over the ranks done so far. Adding `c` cards of the new rank turns a sum `s` from `partial[t − c]` into `s + c·k`. Two partial hands with the same card count `t` clash when `s1 + c1·k == s2 + c2·k`, that is, when `s1 − s2 == (c2 − c1)·k`. The first `k` without a clash is kept.

```csharp
// partial[t]: the sums of all partial hands of t cards over the ranks done so far (unique within each t).
// Rank 0 (the deuce) has key 0: 0-4 deuces all sum to 0, one partial hand per card count
var partial = new List<int>[8];
for (int t = 0; t <= 7; t++) partial[t] = t <= 4 ? [0] : [];
int[] keys = new int[13];
for (int rank = 1; rank < 13; rank++)
{
    var lookup = partial.Select(sums => sums.ToHashSet()).ToArray();
    for (int k = keys[rank - 1] + 1; ; k++)
    {
        // Clash: two t-card hands with c1 < c2 cards of this rank and the same sum, s1 + c1*k == s2 + c2*k
        bool clash = false;
        for (int t = 0; t <= 7 && !clash; t++)
            for (int c1 = 0; c1 <= Math.Min(4, t) && !clash; c1++)
                for (int c2 = c1 + 1; c2 <= Math.Min(4, t) && !clash; c2++)
                    foreach (int s2 in partial[t - c2])
                        if (lookup[t - c1].Contains(s2 + (c2 - c1) * k)) { clash = true; break; }
        if (clash) continue;

        // Keep k, and extend every partial hand with 0-4 cards of this rank
        keys[rank] = k;
        var next = new List<int>[8];
        for (int t = 0; t <= 7; t++)
        {
            next[t] = [];
            for (int c = 0; c <= Math.Min(4, t); c++)
                foreach (int s in partial[t - c]) next[t].Add(s + c * k);
        }
        partial = next;
        break;
    }
}
// keys = 0, 1, 5, 22, 98, 453, 2031, 8698, 22854, 83661, 262349, 636345, 1479181 (about 30 s)
// partial[7]: 49,205 sums, all different, the largest 7,825,759
```

Why it's built this way:
- **Only hands with the same number of cards must differ.** Two 7-card hands that agree on the higher ranks have the same number of cards left for the lower ranks. So partial sums only have to be unique within each card count. Sums for different counts may be equal, since a lookup always has exactly 7 cards. This is what keeps the keys small.
- **The deuce gets key 0.** With exactly 7 cards, its count is 7 minus the other ranks' counts, so it adds nothing to identify the hand.
- **Small keys keep the sums small, but not the table.** The largest sum, 4 × 1,479,181 + 3 × 636,345 = 7,825,759, fits in 23 bits. With rows of 1,024 that's only 7,643 row offsets (30 KB). But `Ranks` is only 45% full (110,158 entries for 49,205 sums): the rows placed last don't fit in the gaps the first ones left. The next step fixes that.

##### Searching for better `RANK_KEYS`

`EvaluatorBench/RankKeySearch` searches for the keys that make `Ranks` + `RowOffsets` smallest. Only the 13 keys and the row size change. Everything else stays exactly as `RankTables.Build` does it: the same rank sums, rows of 2^`ROW_SHIFT` sums, the rows with the most sums placed first, each at its first-fit offset.

**Objective**: bytes of `Ranks` (2 per entry) plus `RowOffsets` (4 per row), for the tables `RankTables.Build` would make with those keys.

**A fast, exact copy of the build.** Scoring a key set means building its tables, and the search scores millions. The tool's copy of `RankTables.Build`'s first fit keeps one bit per slot and tests 64 offsets at once:

```csharp
// used: 1 bit per Ranks slot. Get64(p): the 64 used bits from slot p.
// Bit b of `fits` says whether every column of the row is free at offset + b
int offset = Math.Max(0, firstFree - minColumn);
while (true)
{
    ulong fits = ~0UL;
    foreach (int column in rowColumns)
    {
        fits &= ~Get64(column + offset);
        if (fits == 0) break;
    }
    if (fits != 0) { offset += BitOperations.TrailingZeroCount(fits); break; }
    offset += 64;
}
```

It gives exactly the same tables as `RankTables.Build`'s loop on every key set and row size tested, and the same row offsets as the real tables (`verify` mode). It takes about 20 ms, where `RankTables.Build`'s loop takes 0.3–0.6 s.

**The search**, in 3 stages:
1. **Greedy**, as above. A variant that only keeps apart partial hands that can still become 7-card hands gives the same keys: it's the full 7-card hands that hold back the two largest keys.
2. **Beam search, rank by rank.** Each of 16 partial key sets branches into its 3 smallest valid next keys, plus the first valid key 5, 20, 50 and 100% above the smallest. Each child is scored by completing it greedily and building the real table, and the best 16 go on.
3. **Local search with annealing** over complete key sets, from the beam's results and the other row sizes' best.
   - **The move.** Change one key, then repair the keys above it, so every key set stays valid. Random changes almost never stay valid, because the keys are tight: in a first try, only 29 of 4,472 did.

     ```csharp
     // state: the partial sums over the ranks below r. NextValid(k): the first key >= k that keeps them apart.
     // target: key r moved by 0.001-10% (log-uniform), up or down. r is mostly one of the upper ranks
     k[r] = state.NextValid(target);
     state = state.Add(k[r]);
     double ratio = (double)k[r] / keys[r];
     for (int j = r + 1; j < 13; j++)
     {
         // Each key above keeps its old value (or, half the time, the old value scaled by the same ratio)
         // if that's still valid, and otherwise takes the next valid key
         int want = scaleAbove ? (int)Math.Round(keys[j] * ratio) : keys[j];
         k[j] = state.NextValid(Math.Max(k[j - 1] + 1, want));
         state = state.Add(k[j]);
     }
     ```
   - **The beam.** Each of the 16 key sets makes 24 moved copies, scored in parallel. The next beam is the best 12 of parents and copies, plus 4 others drawn with weight exp(−(bytes − best) / T). T falls from 1% to 0.01% of the best over the run, so the search can climb out of a local minimum early on, and settles later.
   - **A second pass** restarts each row size from every row size's best keys: keys tuned for one row size can do better at another.

**Letting hands with the same value share a sum** (the build handles that unchanged) never found anything better. If two hands have equal sums, then so does every pair of hands that differ by the same rank counts, and some such pair always has different values.

**How the keys used now were found.** About 70 minutes on 24 threads (Ryzen 9 9900X). The log is in [`RankKeySearch/search-2026-11-07.txt`](../EvaluatorBench/RankKeySearch/search-2026-11-07.txt).

| Step | Row size | Bytes | Time |
|---|---|---|---|
| Greedy keys | 1,024 | 250,888 | 2 s |
| Beam search | 1,024 | 201,276 | 4 min |
| Local search from the beam's keys | 1,024 | 186,540 | 12 min |
| Local search from those keys, at rows of 2,048 (207,142 B at the start) | 2,048 | 167,524 | 12 min |
| Local search again from those, another seed: **the keys used now** | 2,048 | **167,504** | 8 min |

At rows of 2,048 the keys grew (the largest by 18%) and spread the sums further apart. The commands were `all --minutes 12` (which ran the steps above at rows of 512, 1,024 and 2,048), then `local --shift 11 --minutes 8 --seed 11 --from <the best keys>`. `all` now also runs that second pass itself. The runs stop after a set time, so a rerun on another machine won't retrace them exactly.

Best results:

| Row size (`ROW_SHIFT`) | Greedy keys | Beam search | Local search | `Ranks` entries (used) | `RowOffsets` rows |
|---|---|---|---|---|---|
| 512 (9) | 239,040 B | 198,838 B | 198,838 B | 72,173 (68%) | 13,623 |
| 1,024 (10) | 250,888 B | 201,276 B | 172,748 B (−31%) | 67,580 (73%) | 9,397 |
| **2,048 (11), used** | 283,484 B | 224,288 B | **167,504 B** (−33% against the greedy keys at 1,024) | 70,834 (69%) | 6,459 |

The best keys:
- Rows of 2,048, **used**: `0, 7, 35, 161, 1364, 5838, 19938, 48521, 247320, 365745, 804426, 1392250, 2262584` (largest sum 13,227,086)
- Rows of 1,024: `0, 6, 30, 138, 1169, 5019, 17142, 39720, 202125, 298921, 600015, 930592, 1707494` (largest sum 9,621,752)

Lookups of random 7-card hands' rank sums, ns per lookup (`measure` mode, 2 runs):

| Keys | Bytes | Cache lines | Hot | Dependent chain | Most of L2 holding other data |
|---|---|---|---|---|---|
| Greedy, rows of 1,024 (before) | 250,888 | 3,920 | 0.37 | 4.76 | 0.28–0.32 |
| Best for rows of 1,024 | 172,748 | 2,700 | 0.39 | 4.84 | 0.27 |
| **Best for rows of 2,048 (used)** | 167,504 | 2,618 | 0.38 | 4.74 | 0.24–0.28 |

What it means:
- **A third smaller**, mostly by spreading the sums out with larger keys. The rows get sparser and pack better (`Ranks` goes from 45% to 69–73% full), at the cost of a few more row offsets.
- **Keys are tuned to their row size.** The best keys for 2,048 give 180,602 B at 1,024, and the best for 1,024 give 185,212 B at 2,048.
- **Lookups aren't measurably faster in cache.** Both table sets fit in L2, and with most of L2 holding other data the gain is within the noise (0–15%). What's gained is space: about 80 KB more of L2 for the agents.
- **The whole evaluator, before and after** (`EvaluatorBench`, the same build with the old and the new keys, 3 processes each, alternating). Ranks and checksums are identical.

  | | Greedy keys, rows of 1,024 | Keys used now, rows of 2,048 |
  |---|---|---|
  | Tables built on first use | 430–474 ms | 584–592 ms |
  | Random 7-card hands, from the cards (best) | 2.33 ns | 2.32 ns |
  | The same, vector precomputed (best) | 1.29 ns | 1.25 ns |
  | Showdown, hot, 2 / 6 / 9 players (best) | 12.4 / 22.0 / 28.2 ns | 12.4 / 21.9 / 28.0 ns |
  | Heads-up showdown, L2 evicted (mean) | 59 ns | 59 ns |
  | Heads-up showdown, tables in DRAM, code warm (mean) | 302 ns (290–317) | **274 ns** (272–278) |
  | 9-player showdown, tables in DRAM, code warm (mean) | 295 ns | 286 ns |

  In cache, it's the same speed. With the tables in DRAM, heads-up is about 9% faster, probably because the tables span fewer pages (about 41 of 4 KB instead of 61). The build is slower: the first fit takes longer on rows of 2,048.
- **Rows of 2,048 were adopted** over the best keys for 1,024: their tables are smaller, and the lookup is the same.
- **The local search was still improving slowly when it stopped**, so longer runs may find a little more.

**To adopt other keys**, set `RANK_KEYS`, `MAX_RANK_KEY` (4 × the largest key + 3 × the second largest) and `ROW_SHIFT`. Then run the exhaustive tests, and `RankKeySearch verify` to check that the real tables are the ones the search scored.

```bash
cd EvaluatorBench
dotnet run -c Release --project RankKeySearch -- all --minutes 12       # the whole search from the greedy keys (about 75 min)
dotnet run -c Release --project RankKeySearch -- local --shift 11 --minutes 8 --seed 11 --from 0,7,35,161,1364,5838,19938,48601,247320,365745,804450,1392262,2252411
dotnet run -c Release --project RankKeySearch -- measure --shift 11 0,7,35,161,1364,5838,19938,48521,247320,365745,804426,1392250,2262584
```

### Shrinking the rank table

The 49,205 non-flush rank multisets of 7 cards have only 3,537 different values: hands with the same 5 best ranks are worth the same, whatever the other 2 cards. So the rank table doesn't strictly need a slot per multiset. Two ways to use that were measured. Neither was kept: lookup speed matters more than the memory.

Both were measured before the key search, with the greedy keys and rows of 1,024 (`Ranks` 215 KB + row offsets 30 KB). The keys used now shrink the perfect hash itself to 163 KB (`Ranks` 138 KB + row offsets 25 KB) with the same lookup; see [Searching for better `RANK_KEYS`](#searching-for-better-rank_keys).

**Sharing slots in the perfect hash.** Letting a rank sum reuse a slot that already holds the same rank saves almost nothing. A row keeps its sums at fixed distances from each other, so two rows can only overlap where the ranks happen to match at every shared slot. Smaller rows share more, but then the table of row offsets grows:

| Sums per row | `Ranks` entries, no sharing | `Ranks` entries, sharing | Row offsets | Total, no sharing → sharing |
|---|---|---|---|---|
| 1,024 (used then) | 110,158 | 109,660 | 30 KB | 245 → 244 KB |
| 256 | 72,131 | 70,618 | 119 KB | 260 → 257 KB |
| 64 | 52,327 | 45,413 | 478 KB | 580 → 566 KB |
| 16 | 49,205 | 29,211 | 1.9 MB | 2.0 → 1.9 MB |

Both columns use the exact first-fit search, which takes 0.2–0.5 s to build. At 1,024 sums per row (used then), sharing saves only 498 entries (1 KB). The key search found nothing better by letting hands of equal value share a sum either.

**An XOR table.** Each rank sum hashes to 3 slots, one in each third of the table, and its rank is the XOR of those 3 entries. No slot belongs to a single hand, and there are no row offsets. The table needs about 1.23 slots per rank multiset: 60,546 slots, 118 KB.

Building it: think of the slots as vertices and each rank sum as an edge joining its 3 slots. Repeatedly take a slot used by only one remaining rank sum, and set that rank sum aside. When all of them have been set aside, go through them in reverse order. Each rank sum's own slot is set last among its 3, to whatever makes its XOR come out right. This took 30 ms with the first seed; with 1.20 or 1.15 slots per multiset, no seed worked.

The hash is one multiply. Three 21-bit slices of the product are each scaled to the size of a third.

**Why it wasn't kept.** It halves the memory, but each lookup costs a multiply and a third load. Its 3 loads don't depend on each other, unlike the perfect hash's 2, so it's faster once the tables are out of cache. It's slower while they're in cache, which is when evaluation is a real share of the hand (JIT; cold numbers are the average of 2–3 runs):

| | Perfect hash (greedy keys, used then) | XOR table (not kept) |
|---|---|---|
| Rank table | 245 KB (`Ranks` 215 KB + row offsets 30 KB) | **118 KB** |
| All `EvalHandRank` tables | 260 KB | **135 KB** |
| Build, on first use | 0.4 s | **30 ms** |
| Random 7-card hands, from the cards | **2.3 ns** | 3.5 ns |
| Heads-up showdown, hot | **12.5 ns** | 13.9 ns |
| 9-player showdown, hot | **28.5 ns** | 36.7 ns |
| Heads-up showdown, tables in L3 | 57 ns | **45 ns** |
| Heads-up showdown, tables in DRAM, code warm | 269 ns | **170 ns** |
| Heads-up showdown, everything in DRAM | 427 ns | **283 ns** |
| Whole hand, heads-up, always call / mixed | **129 / 126 ns** | 133 / 129 ns |
| Whole hand, 9 players, always call / mixed | **315 / 612 ns** | 329 / 640 ns |

### When the tables aren't in cache

`EvalHandRank` reads 2–3 tables in a chain: card vector, then row offset, then rank. If an agent's own work between hands has pushed those tables out of the cache, each read waits on L3 or DRAM. `EvalHandValue` and `EvalHandValues` read no memory, only their code. `EvaluatorBench`'s `cold` mode measures one showdown at a time, evaluated like `Arena.HandleShowdown`, after one of 4 cache states:
- **hot**: nothing evicted;
- **L2 evicted**: a 4 MB buffer read first, which pushes everything out of the 1 MB L2 into the L3;
- **all evicted**: a 64 MB buffer read first, twice this core's 32 MB L3, so tables and code come from DRAM;
- **tables cold, code warm**: the same, then one evaluation of another showdown to bring the code back.

ns per showdown, after subtracting the cost of an empty call:

| Evaluator | Hot | L2 evicted | All evicted | Tables cold, code warm |
|---|---|---|---|---|
| **Heads-up** `EvalHandRank` | **12** | 58 | 447 | 227 |
| Heads-up `EvalHandValue` per player | 26 | **38** | **175** | **64** |
| Heads-up `EvalHandValues` (AVX-512 batch) | 30 | 45 | 261 | 73 |
| **9 players** `EvalHandRank` | **29** | **82** | 385 | 245 |
| 9 players `EvalHandValue` per player | 90 | 121 | **375** | 136 |
| 9 players `EvalHandValues` (AVX-512 batch) | 45 | 100 | 699 | **98** |

Measured with the greedy keys, before the key search. With the keys used now, a paired rerun put `EvalHandRank` about 9% faster heads-up with the tables cold and the code warm, and the same elsewhere within the noise; see [Searching for better `RANK_KEYS`](#searching-for-better-rank_keys). Repeated runs vary by about ±15–20% in the evicted columns. What it means:
- **Hot, `EvalHandRank` wins**: about 2x heads-up, 1.6x at 9 players.
- **Once the tables have dropped to L3**, heads-up `EvalHandValue` is already faster. At 9 players `EvalHandRank` still wins.
- **Once they're in DRAM**, `EvalHandValue` (2 hands) or `EvalHandValues` (many hands) is 2.5–3.5x faster while their code is still cached: the table lookups become 2–3 dependent DRAM misses.
- **When everything is cold, code included**, `EvalHandValue` still wins heads-up (2.6x) but only ties at 9 players. The AVX-512 batch loses there, because its own code is bigger.

**Why the Arena stays on `EvalHandRank`.** The tables only go cold when the agents' own work between showdowns touches megabytes of memory. That work costs microseconds to milliseconds per hand, against at most about 0.2–0.4 µs lost on evaluation. When the agents are light and evaluation is a real share of the hand, the tables stay hot, and `EvalHandRank` is the faster one.

**When to use the others.** Inside an agent's own memory-heavy code, for example between neural network evaluations, `EvalHandValue` is the better choice for 1 or 2 hands, and `EvalHandValues` for a batch. Both are also the only option for 5 or 6 cards.

**Removed, since nothing needed them**: `EvalHandRank(ulong handKey, ulong handMask)`, `HandKey(...)`, and the mask-only `EvalHandRank(ulong handMask)`.
- The vector API does the same job faster: 2.3 ns from the 7 cards, against 3.9 ns with separate key and mask.
- The mask-only overload needed its own 32 KB table, and was never faster for card input.
- Code that only has a mask can use `EvalHandValue`, which needs no tables.

### Correctness

`EvalHandReferenceTest` checks `EvalHand` against an independent reference: the best of the 21 five-card subsets, scored the textbook way. `GameEvalHandRankTest` checks that `EvalHandRank` orders hands exactly like `EvalHandValue`: equal values get equal ranks, higher values strictly higher ranks, with the same hand type. It covers 1M random hands (and checks that the vector carries the hand's mask), every 5, 6 and 7-card flush of every suit, and how the Arena adds up the board and hole-card vectors.

Three exhaustive tests are marked `[Explicit]` because they take about 10 s together:
- every one of the 133,784,560 seven-card hands ranks like the reference, with exactly 4,824 distinct values;
- the AVX-512 batch equals the scalar result on every hand;
- `EvalHandRank` orders every hand like `EvalHandValue`.

```bash
dotnet test PokerBot2UnitTests --filter "FullyQualifiedName~AllSevenCardHands"
```

## Comparison with other evaluators

PokerBot2's evaluators against 5 published ones on the same 2,000,000 random 7-card hands, one thread, Ryzen 9 9900X (Zen 5), Windows 11. The code and how to rerun it are in [EvaluatorBench](../EvaluatorBench/README.md).

**Method.**
- Each evaluator gets its own card format, prepared before timing (a relabeling of the 52 cards). Building the hand from its 7 cards is inside the timed loop, so every row from cards does the same job.
- The C# evaluators run on .NET 10.0.13 (JIT). The C/C++ ones are built with both MSVC 19.51 (`/O2 /GL /arch:AVX512`) and GCC 14.2 (`-O3 -march=native`). Each is timed as the best of 7 passes after 2 warm-up passes.
- **Correctness.** Before timing, every evaluator's result on all 2M hands is checked against `EvalHandRank`. Equal ranks must get equal results, and better ranks strictly better results (4,809 distinct strengths appear in the sample). All of them pass.

| Evaluator | Language | Method | Memory | ns/hand | M hands/s |
|---|---|---|---|---|---|
| **PokerBot2 `EvalHandRank`** | C# | Rank-key sum + perfect hash, flush table | 180 KB | **2.28** | 439 |
| [OMPEval](https://github.com/zekyll/OMPEval) | C++ | Rank-key sum + perfect hash, flush table | 220 KB | **1.60** GCC, 2.33 MSVC | 626 |
| PokerBot2 `EvalHandValues` (AVX-512 batch, masks given) | C# | Bit operations, 8 hands per instruction | none | 2.32 | 430 |
| PokerBot2 `EvalHandValue` (mask given) | C# | Bit operations | none | 8.4 | 119 |
| [PokerHandEvaluator](https://github.com/HenryRLee/PokerHandEvaluator) (phevaluator) | C | Suit hash + base-5 rank hash | 125 KB | 12.4 MSVC, 14.7 GCC | 81 |
| [Two Plus Two](https://github.com/tangentforks/TwoPlusTwoHandEvaluator) | C | 7-step state machine | 130 MB | 21.5 GCC, 29.9 MSVC | 47 |
| [Senzee 7-card index](https://senzee.blogspot.com/2007/01/7.html) | C++ | Perfect index of the 52-bit mask + full table | 267 MB | 28.8 MSVC, 29.3 GCC | 35 |
| [poker.net](https://github.com/JBelthoff/poker.net) | C# | Cactus Kev 5-card, best of 21 | 50 KB | 91.0 | 11 |

M hands/s is for the fastest build.

What the numbers say:
- **The two small-table evaluators win by a wide margin.** OMPEval and `EvalHandRank` use the same technique (see [Credits](#table-based-evaluator-evalhandrank)). OMPEval built with GCC takes 30% less time than `EvalHandRank` under the .NET JIT (1.60 vs 2.28 ns); built with MSVC, the two are even (2.33 ns). The JIT's loop does two things the C++ one doesn't:
  - it reloads each table's address from its static field, since an array on the GC heap can move;
  - it bounds-checks the card table. Removing that check alone gained about 5%.

  With the hand's vector already built, `EvalHandRank`'s lookup alone is 1.3 ns.
- **The big tables lose on random hands.** Two Plus Two and Senzee read 130–267 MB tables, so a random hand is a cache miss, or several for Two Plus Two's 7 dependent steps. Two Plus Two is famous for enumerating all hands in order, where consecutive hands share their first 6 lookups and the table stays in cache. That's not how hands come at a showdown, but it is how an equity calculation can walk them (see [Hands in sequential order](#hands-in-sequential-order)).
- **The old bit-operation evaluator** (`EvalHandValue`) needs no memory at all. Its AVX-512 batch matches `EvalHandRank` when there are many hands to evaluate together, but not for the 2 hands of a heads-up showdown.
- **Not included**: [palmistry3](https://github.com/felixubl/palmistry3), a fast table-free evaluator with the same 4 × 16-bit suit layout as `EvalHand`. Its flush test needs arm64 NEON, so it doesn't build on x86 without rewriting it.

The evaluators:

- **OMPEval**: Timo A. (zekyll), C++, ISC license, measured at commit `4aec210` (2016).
  - A hand is a 128-bit value: a rank-key sum with suit counters, and the card mask. It's built with SSE adds.
  - Flushes are found by the suit counters (starting at 3) and looked up in an 8,192-entry table. Other hands go through a row-offset perfect hash into an 86,547-entry `uint16` table (173 KB) with 32 KB of offsets.
  - It evaluates 0 to 7 cards, and it also includes an equity calculator.
- **PokerHandEvaluator (phevaluator)**: Henry Lee, C/C++ with Python bindings, Apache-2.0, commit `10be452` (2026).
  - A suit hash finds flushes, which are looked up by the flush suit's 13-bit mask.
  - Other hands are hashed from their rank counts in base 5 (`hash_quinary`, a loop over the 13 ranks with a DP table) into a 49,205-entry table.
  - Rank 1 is the best hand (7,462 classes, Cactus Kev's numbering). Omaha is also supported.
- **Two Plus Two**: Ray Wotton and the Two Plus Two forum (2006), C, via the tangentforks fork of XPokerEval, commit `f324fd8`. The license is unclear: the code says "My code is GPL, use it as you like".
  - A 32,487,834-entry `int` table (130 MB) is a finite-state machine: each card is one lookup, and the 7th lookup returns the hand rank.
  - The table comes from its own generator (4 s), which builds it with Cactus Kev's evaluator.
- **Senzee 7-card index**: Paul Senzee (2007), C++ header `index52c7.h` from his blog, "(c) 2007", no license.
  - `index52c7` maps any 52-bit mask with 7 bits set to a unique index 0–133,784,559, from the bit counts of its 4 × 16-bit chunks (512 KB of tables). One lookup in a 133,784,560-entry `uint16` table (267 MB) then gives the rank.
  - The blog post says the table generator was lost, so the benchmark fills the table from OMPEval (0.6 s).
- **poker.net**: John Belthoff, C# (.NET 10), MIT, commit `c00f7f4` (2025).
  - It's a port of Cactus Kev's 5-card evaluator. Each card carries a prime per rank, and a non-flush 5-card hand is identified by the product of its primes, through Senzee's perfect hash. Flushes and straights are table lookups.
  - A 7-card hand takes the best of its 21 five-card subsets, as in its `EvalEngine`.

### Hands in sequential order

The table above is for random hands. When the code that asks for evaluations can choose their order, each evaluator can reuse the work for the cards that didn't change. Two orders were measured (`seq` mode of both benchmarks, one thread, best of 2 passes):
- **All hands in order**: all 133,784,560 hands, with c0 < c1 < … < c6, as an exhaustive enumeration does. The work for the first 6 cards is done once per innermost loop.
- **Fixed boards**: every hole-card pair (1,081) on each of 20,000 random boards, in order. This is what an equity calculation, or a CFR showdown over all holdings, does. The board's work is done once.

Each evaluator does this its own way:
- Two Plus Two keeps one state per loop level.
- OMPEval keeps one `Hand` per level.
- `EvalHandRank` keeps one `HandKeyMask` vector per level.
- `EvalHandValue` and Senzee keep one card mask per level.
- `EvalHandValues` evaluates each innermost loop, or each board, as one batch.
- phevaluator and poker.net have no incremental form, so they take the 7 cards every time.

Over all hands, every evaluator's hand-type counts match the known totals. On the boards, they all match each other.

| Evaluator, ns per hand | All hands in order | Fixed boards, all hole pairs | Random hands (table above) |
|---|---|---|---|
| Two Plus Two | **0.29** GCC, 0.38 MSVC | **0.78** GCC, 0.81 MSVC | 21.5 |
| OMPEval | 0.59 GCC, 0.67 MSVC | 0.90 GCC, 0.94 MSVC | 1.60 |
| PokerBot2 `EvalHandRank` | 0.74 | 1.18 | 2.28 |
| PokerBot2 `EvalHandValues` (AVX-512 batch) | 5.37 (batches of up to 46) | 3.14 (batches of 1,081) | 2.32 |
| PokerBot2 `EvalHandValue` | 7.83 | 7.91 | 8.4 |
| PokerHandEvaluator (phevaluator) | 7.63 GCC, 10.29 MSVC | 9.06 GCC, 10.15 MSVC | 12.4 |
| Senzee 7-card index | 19.8 GCC, 20.0 MSVC | 12.2 MSVC, 14.2 GCC | 28.8 |
| poker.net | 43.5 | 47.6 | 91.0 |

**Why Two Plus Two wins in order.** Its table is a state machine: `HR[state + card]` is the next state, and the 7th lookup gives the rank.
- With one state kept per loop level, the innermost loop does one lookup per hand, `HR[u6 + c6]`.
- Consecutive hands read consecutive entries of the same 53-entry block. That's the same few cache lines, so the size of the table (130 MB) doesn't matter.
- OMPEval and `EvalHandRank` still do their whole lookup for every hand: 2 dependent loads, at places in the table that jump by each card's rank key.

On random hands it's the other way round. Two Plus Two then needs all 7 lookups, one after the other, each a cache miss in 130 MB.

On fixed boards the gap is small. Each hand costs Two Plus Two 2 lookups instead of 1, and every new board starts with a walk through cold parts of its table. Senzee's index jumps around its table even for hands in order, so order barely helps it.

**For later: equity and CFR training.** Both can choose the order of the hands they evaluate: an equity calculation by enumeration, or CFR that evaluates every holding at a showdown. There, a sequential-order evaluator may be the better choice.
- Two Plus Two enumerates everything at 0.29 ns per hand, 2.5x less than `EvalHandRank`, and takes 0.78 ns per hand against 1.18 on fixed boards.
- Its costs are 130 MB of memory (its own generator builds the table in about 4 s), a license that's unclear (see above), and slow lookups when the hands come in random order. So it suits batch computations that walk hands in order, not the Arena's showdowns.
- `EvalHandRank` called in order is a middle ground: 0.74 and 1.18 ns, with 180 KB of tables.

## History encoding

`Arena` keeps the hand history as a `List<int>` and passes it to `Play` as `IList<int>`. It's cleared at the start of each hand.

| Entry | Encoding |
|---|---|
| Bet action (blind, call, raise, all-in, fold) | bits 0–3 `playerId`, bit 4 reserved (showdown flag, unused), bits 5–19 the player's **total bet in this betting round** (integer part), bit 20 fold, bit 21 all-in |
| New street dealt | `Arena.DEALER_HISTORY_STATE` (`11`) |
| Showdown, written at the end of `HandleShowdown` | 1 entry for the board: 5 cards × 6 bits, `board[0]` lowest. Then 1 entry for the pot (integer part). Then 1 entry per player in seat order: bits 0–3 `playerId`, upper bits the signed PnL (integer part, decode with `entry >> 4`) |

`Arena.GetRoundBet(history, playerId)` decodes a player's current bet in the round.

## Arena implementation notes

**Zero allocations per hand**, under both the JIT and Native AOT. Everything per hand lives in `stackalloc` spans (one entry per seat, with the chip buffers padded to 16 for AVX-512), and the history list keeps its capacity across hands.

What the betting loop avoids, and why:
- **No integer division**: the next seat is `seat + 1 == numPlayer ? 0 : seat + 1` instead of `(seat + 1) % numPlayer`. A division is 10+ cycles, and the loop moves to the next seat on every action.
- **No interface calls for bookkeeping**: agents are kept in an `IAgent[]` and the history in a `List<int>` (not `IList<>`), and player ids are cached in an `int[]`. Only the real agent calls (`Play`, `AllIn`, `ReceiveDealCard`, `GetDealCard`, `ReceiveReturn`) go through the interface.
- **Spans passed by value** to `RoundBet` (not `ref Span`), so the JIT can keep their pointers and lengths in registers.
- **Side-pot entitlement computed once**, just before the showdown. It used to be recomputed after every betting round, which is a 9×9 loop once anyone is all-in, even for hands that end early.
- **No sort at showdown**: the payout takes the best hand still in (a scan over a bit-per-seat mask), pays it, and only looks further down for the side pots that hand isn't entitled to. That's usually a single pass. The insertion sort of all hands it replaces cost about 80 ns per 9-player showdown, mostly mispredicted branches. Ties go to `PaySplit`, which orders only the tied seats by entitled pot. (`Span.Sort` was never an option: its default comparer allocated 48 B per showdown under Native AOT.)
- **Showdown hands are evaluated with `EvalHandRank`**: the board's key and mask (`HandKeyMask(board)`, one vector) are computed once, and each player adds 2 hole cards and does 1–2 table lookups. Heads-up this takes 12.5 ns instead of 24 ns, because the old AVX-512 batch only pays off for 8 hands at a time. See the [showdown numbers](#build-settings-and-performance), and [When the tables aren't in cache](#when-the-tables-arent-in-cache) for when it doesn't win.
- **`canRaise` is a bitmask** (bit per seat): re-opening the betting sets every bit in one instruction instead of filling a `bool` span, and that span's `stackalloc` is gone.
- `DEALER_HISTORY_STATE` is a `const`, so it folds into the code.

### SIMD

Most of a hand can't be vectorized. The betting loop is sequential: each action depends on the previous agent's decision. Dealing and the agent calls are sequential too. The parts that are data-parallel over the seats (at most 9) use AVX-512 when `Vector512.IsHardwareAccelerated`:

| Where | What | Time, scalar → vector (9 players) |
|---|---|---|
| `Game.EvalHandValues` | Batch evaluation, 8 hands per instruction. The Arena no longer uses it: `EvalHandRank` is faster at 2 to 9 players while its tables are in cache | See [Hand evaluation](#hand-evaluation) |
| `ComputeEntitledPot` | Side-pot entitlement Σⱼ min(bet[i], bet[j]) for every seat at once: per seat j, 2 × `vminpd` + 2 × `vaddpd` | 20 → 2 ns per showdown with an all-in |
| End of `RoundBet` | `handBet += totalBet` for every seat: 2 vector adds | 4 → 0.7 ns per street |

Tried and not kept:
- **Vector best-hand search at showdown** (`vpmaxsq` reduction, `vpcmpeqq` for the ties): no faster than the scalar bitmask scan in whole-hand benchmarks, and slower heads-up. With at most 9 seats, the shuffles of the horizontal max cost about as much as the scalar loop. It only won (50 → 34 ns) in a contrived test where every player had a different all-in amount.
- **Writing the showdown history with one `CollectionsMarshal.SetCount`** instead of a `List.Add` per entry: within noise.

The vector code does the same additions in the same order as the scalar code, so the results are identical. 1.28M seeded hands replay bit for bit against the previous version, with and without AVX-512. Without AVX-512, or on CPUs where .NET leaves 512-bit vectors off by default, the scalar loops run. To test that path on an AVX-512 machine, set `DOTNET_PreferredVectorBitWidth=256`, or `DOTNET_EnableAVX512=0` to turn AVX-512 off completely. (`DOTNET_EnableAVX512F=0` does nothing in .NET 10.)

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

Measured on a Ryzen 9 9900X, Release, single thread, 1M hands per run, best run out of 3 processes × 5 runs (single process for "original"). The numbers include the agents' own work. "Always call" sends every hand to showdown; "mixed" folds 25%, raises 15% and goes all-in 3% of the time. Each dated column is the code of that day. The 2026-10-25 columns were re-measured next to the current code.

| Agents | Players | Original (ns/hand) | 2026-10-24, JIT | 2026-10-25, JIT | 2026-10-25, AOT | Now, JIT | Now, Native AOT |
|---|---|---|---|---|---|---|---|
| Always call | 2 | 315 | 162 | 142 | 159 | **129** | 148 |
| Always call | 6 | 583 | 373 | 286 | 336 | **231** | 290 |
| Always call | 9 | 758 | 498 | 331 | 411 | **315** | 398 |
| Mixed | 2 | 294 | 148 | 133 | 134 | 126 | **124** |
| Mixed | 6 | 785 | 563 | 471 | 466 | 426 | **419** |
| Mixed | 9 | 1,175 | 788 | 623 | 609 | 612 | **599** |

| Hand evaluator, random 7-card hands (ns/hand) | JIT | Native AOT, default CPU | Native AOT, `IlcInstructionSet=native` |
|---|---|---|---|
| `EvalHandRank(HandKeyMask(...))`, from the 7 cards | 2.3 | 3.1 | 3.1 |
| `EvalHandValue` (scalar, mask given) | 8.4 | 9.1 | 7.9 |
| `EvalHandValues` (batch, masks given) | 2.3 | 9.2 (no AVX-512) | 2.1 |

| Showdown evaluation, ns per showdown (JIT) | 2 players | 6 players | 9 players |
|---|---|---|---|
| Before: hand masks + `EvalHandValues` | 24.3 | 66.3 | 44.8 |
| Now: `HandKeyMask(board)` once + `EvalHandRank` per player | **12.5** (1.9x) | **22.0** (3.0x) | **28.5** (1.6x) |

The 6-player case gained the most because the old batch only vectorizes groups of 8 hands, so 6 players ran 6 scalar evaluations.

What the measurements say:
- **The JIT is the safer choice for training**: 15–26% ahead of Native AOT with always-call agents (AOT can't devirtualize the agent calls, or the call into `System.Random`), and within 2% with mixed agents, where AOT is slightly ahead. Prefer `dotnet run -c Release` for training. Native AOT starts instantly.
- **Train unseeded** when throughput matters (`new Arena(numPlayer, agents)`): a seeded `Random` uses the slower legacy algorithm and costs about 5–8% per hand with these benchmark agents.
- **Native AOT without `IlcInstructionSet=native` loses AVX-512 and LZCNT**, and the batch evaluator gets 4x slower. That's why the setting is on. `EvalHandRank` doesn't need either, so it runs at the same speed with or without the setting. It is about 35% slower under Native AOT than under the JIT.
- With a single agent type, the JIT knobs are within noise. 9 players, always call / mixed (ns/hand, 2026-10-24 code): default 459 / 777, `DOTNET_TieredPGO=0` 459 / 781, `DOTNET_TieredCompilation=0` 469 / 763, Server GC 460 / 773.
- **Not enabled on purpose**: `SkipLocalsInit` showed no measurable gain (the `stackalloc` buffers are at most 9 entries), and the code relies on them starting at zero. Disabling tiered compilation gave no gain and turns off dynamic PGO.

## Known limitations

- The bet field is 15 bits, so totals above 32,767 don't fit. The default table (at most 9 × 2,500 = 22,500 chips) is safe, but larger stacks are not.
- `EvalHandRank` takes exactly 7 cards (5 board + 2 hole). For the flop or the turn, use `EvalHand` / `EvalHandValue`.
- The history only keeps integer parts. Use `toCall` / `minRaise` for exact amounts.
- Hands that end because everyone else folded don't write showdown entries.
- `IAgent.Reset(playerId, position)` is never called by the arena.

## Changelog

### 2026-11-07

- **Simpler table build**: `RankTables` places each row with an exact first-fit search. The search shortcut and its tuning constant (256) are gone. `Ranks` is 3% smaller (110,158 entries, 215 KB), and the tables now take about 0.4 s to build on first use instead of 75 ms. Lookups are unchanged: busy and rare rank patterns sit side by side in each row of 1,024, so the table is read from L2 either way. Placing the busiest rows first didn't change lookup speed either.
- `EvaluatorBench/DotnetBench` builds again after `Game` moved to the `PokerBot2.GameSpace` namespace.
- **Smaller `EvalHandRank` tables**: new `RANK_KEYS` and rows of 2,048 sums (`ROW_SHIFT = 11`), with the build unchanged. The perfect hash is a third smaller (`Ranks` + `RowOffsets` 250,888 → 167,504 B; all the tables 260 → 180 KB), which leaves about 80 KB more of the L2 for the agents. Ranks are the same as before, so results don't change. Lookups are as fast in cache, and heads-up showdowns about 9% faster with the tables in DRAM (302 → 274 ns). The tables take about 0.6 s to build on first use instead of 0.45 s. See [How `RANK_KEYS` were found](#how-rank_keys-were-found).
- `EvaluatorBench/RankKeySearch`: the search that found those keys, starting from the greedy ones: beam search, then local search with annealing, scored by an exact 64-offsets-at-a-time copy of the table build. Its log is `RankKeySearch/search-2026-11-07.txt`. See [Searching for better `RANK_KEYS`](#searching-for-better-rank_keys).
- **Benchmarks in sequential order** (`seq` mode of `DotnetBench` and `NativeBench`): all hands in order, and all hole-card pairs on fixed boards. Two Plus Two is the fastest there (0.29 and 0.78 ns per hand), which makes it a candidate for equity calculations and CFR training later. See [Hands in sequential order](#hands-in-sequential-order).

### 2026-11-01

API changes:
- `Arena.HandleShowdown` no longer takes `isAllIn`: it was never read, since the side pots come from the entitled pots.

Benchmarks:
- [When the tables aren't in cache](#when-the-tables-arent-in-cache): which evaluator wins once an agent's own work has evicted `EvalHandRank`'s tables (`EvaluatorBench`, `cold` mode). `EvalHandValue` and `EvalHandValues` stay for that case.

Docs:
- [How `RANK_KEYS` were found](#how-rank_keys-were-found), with the search code, and why the key only needs suit counts.
- [Shrinking the rank table](#shrinking-the-rank-table): sharing slots and an XOR table were measured. The XOR table halves the memory but is slower while the tables are in cache, so the perfect hash stays.

### 2026-10-30

Features:
- **`Game.EvalHandRank`**: a table-based 7-card evaluator that returns a single `int` rank, ordered exactly like `EvalHandValue`. It comes with `HandKeyMask` and `HandRankType`. See [Table-based evaluator](#table-based-evaluator-evalhandrank).

Performance:
- `Arena.HandleShowdown` uses `EvalHandRank`, with the board's key and mask computed once. Evaluating a showdown is 1.9x faster heads-up, 3.0x at 6 players and 1.6x at 9 players while the tables are in cache. Whole hands under the JIT are 5–9% faster heads-up, 10–19% at 6 players and 2–5% at 9 players (see the [tables](#build-settings-and-performance)). Results are unchanged, hand for hand.
- On random 7-card hands, `EvalHandRank` takes 2.3 ns from the 7 cards, against 8.4 ns for `EvalHandValue`.

Benchmarks:
- [Comparison with other evaluators](#comparison-with-other-evaluators): OMPEval, PokerHandEvaluator, Two Plus Two, Senzee's 7-card index and poker.net, on the same hands. The code is in `EvaluatorBench/`.

Tests (57 run by default, plus 3 explicit exhaustive ones):
- `GameEvalHandRankTest`: `EvalHandRank` orders 1M random hands and every flush exactly like `EvalHandValue`, and the Arena-style board + hole-card vectors add up. An `[Explicit]` test checks all 133,784,560 hands.

### 2026-10-25

Performance: 13–32% less time per hand at 6–9 players under the JIT (11–18% under Native AOT), and 5–12% heads-up (see the tables above). Results are unchanged, hand for hand.
- Showdown pays out by scanning for the best hand still in, instead of sorting every hand first. This is the biggest gain (see [Arena implementation notes](#arena-implementation-notes)).
- AVX-512 for the side-pot entitlement and for adding each round's bets to the hand totals (see [SIMD](#simd)). Scalar fallbacks run without AVX-512.
- `canRaise` in `RoundBet` is a bit-per-seat mask instead of a `bool` span.
- The per-seat chip buffers in `RoundPlay` are padded to 16 entries for the vector code.

Tests (53 run by default):
- `HandleShowdown_ShouldSplitTiesPerSidePot_WhenSeatsAreOutOfOrder`: a 3-way tie whose seats are in the reverse order of their entitled pots, so the tied seats must be reordered before the split.

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
