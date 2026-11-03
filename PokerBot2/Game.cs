using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.CompilerServices;

namespace PokerBot2
{
    public enum WinHandType
    {
        STRAIGHT_FLUSH = 9, // Royal flush is just the highest straight flush
        QUAD = 8,
        FULL_HOUSE = 7,
        FLUSH = 6,
        STRAIGHT = 5,
        THREE_OF_A_KIND = 4,
        TWO_PAIR = 3,
        PAIR = 2,
        HIGH_CARD = 1
    }

    public class Game
    {
        // Special value for fold and all-in is set above the stack x 9, so impossible for normal bet
        public const int FOLD_VALUE = 0b111_1111_1111_1111;
        public const int ALL_IN_VALUE = 0b111_1111_1111_1110;

        public const int SMALL_BLIND = 10;
        public const int BIG_BLIND = 20;
        public const int STACK = 2500;

        // Card = 0 - 51, following the rank-suit order [2♠, 2♣, 2♦, 2♥, 3♠, ...]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetRank(int card)
        {
            return card >> 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetSuit(int card)
        {
            return (card & 0b11);
        }

        public static string CardToString(int card)
        {
            int rank = GetRank(card);
            int suit = GetSuit(card);
            string display = "";
            if (rank <= 7)
            {
                // Rank 0 is the 2
                display = "" + (char) ('2' + rank);
            }
            else if (rank == 8)
            {
                display = "10";
            }
            else
            {
                switch (rank)
                {
                    case 9: display = "J"; break;
                    case 10: display = "Q"; break;
                    case 11: display = "K"; break;
                    case 12: display = "A"; break;
                }
            }

            switch (suit)
            {
                case 0: display += '♠'; break;
                case 1: display += '♣'; break;
                case 2: display += '♦'; break;
                case 3: display += '♥'; break;
            }
            return display;
        }

        // Card is the rank followed by the suit, eg: '2♠', '10♠' or 'A♠'
        public static int StringToCard(string card)
        {
            int rank;
            int index = 0;
            switch (card[index])
            {
                case 'J': rank = 9; break;
                case 'Q': rank = 10; break;
                case 'K': rank = 11; break;
                case 'A': rank = 12; break;
                case '1': rank = 8; index++; break; // "10"
                default: rank = card[index] - '2'; break;
            }

            index++;

            var suit = card[index] switch
            {
                '♠' => 0,
                '♣' => 1,
                '♦' => 2,
                '♥' => 3,
                _ => throw new Exception("Invalid suit"),
            };
            return (rank * 4 + suit);
        }

        private readonly int[] Deck = [.. Enumerable.Range(0, 52)];
        private int CurDealtCard = 0;
        private readonly int NumPlayer;
        private readonly Random Rng;

        // seed: fixed seed for reproducible games, null = random seed.
        // Note: an unseeded Random uses xoshiro256**; a seeded one falls back to the slower legacy
        // (.NET Framework compatible) algorithm, see README "Dealing and seeds"
        public Game(int numPlayer, int? seed = null)
        {
            NumPlayer = numPlayer;
            Rng = seed is int fixedSeed ? new Random(fixedSeed) : new Random();
        }

        // New hand. No shuffle needed: DealCard draws uniformly from the cards not dealt yet
        public void Reset()
        {
            CurDealtCard = 0;
        }

        // Lazy Fisher-Yates: swap a random card from the rest of the deck into the next position,
        // so a hand only pays for the 2N + 5 cards it uses instead of shuffling all 52.
        // Should call this 2N times for hole card, then 3 times for flop, 1 for turn, 1 for river
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DealCard()
        {
            int pick = Rng.Next(CurDealtCard, 52);
            int card = Deck[pick];
            Deck[pick] = Deck[CurDealtCard];
            Deck[CurDealtCard++] = card;
            return card;
        }

        // input = up to 7 cards (if not enough, add negative filler cards, all -4,
        // avoid confusion with 2). MUST have at least 1 non-filler.
        // Return (ordering value, win hand type), where the ordering value
        // is used to sort the win hand of the same type (higher = better, equal = same hand)
        // Missing kickers (fewer than 5 real cards) count as rank -1, same as a filler card.
        // Note: input is NOT modified (no sorting needed)
        public static (int orderingValue, WinHandType winHand) EvalHand(Span<int> cards, int numFiller)
        {
            Debug.Assert(cards.Length == 7);
            return EvalHand(ToHandMask(cards));
        }

        // Hand mask: 16 bits per suit (suit * 16 + rank), so the 4 suits are 4 lanes of one ulong.
        // (card & 0b11) << 4 = suit * 16 picks the lane, card >> 2 = rank picks the bit inside the lane.
        // Eg: K♦ = rank 11, suit 2 -> bit 2 * 16 + 11 = 43. Bits 13-15 of each lane stay 0.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ToCardMask(int card)
        {
            return 1UL << (((card & 0b11) << 4) + (card >> 2));
        }

        // Filler cards (negative) are skipped
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ToHandMask(ReadOnlySpan<int> cards)
        {
            ulong mask = 0;
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] >= 0)
                {
                    mask |= ToCardMask(cards[i]);
                }
            }
            return mask;
        }

        // Same as ToEvalValue(EvalHand(handMask)), without the tuple
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long EvalHandValue(ulong handMask)
        {
            return ToEvalValue(EvalHand(handMask));
        }

        // Rank of the highest set bit, -1 if empty.
        // lzcnt counts the 0 bits above the highest 1 bit, so 31 - lzcnt is that bit's index.
        // Eg: ranks = 0b1_0000_1000_0000 (A, 9) -> lzcnt = 19 -> 31 - 19 = 12 (A). lzcnt(0) = 32 -> -1
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TopRank(uint ranks)
        {
            return 31 - BitOperations.LeadingZeroCount(ranks);
        }

        // Pack the top n ranks into 6-bit fields, highest first. Missing ranks are -1 (like fillers).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TopRanks(uint ranks, int n)
        {
            int packed = 0;
            for (int i = 0; i < n; i++)
            {
                int top = TopRank(ranks);
                // Append a 6-bit field: same as summing top_i << (6 * (n - 1 - i)), also when top = -1
                packed = (packed << 6) + top;
                // Clear the rank just taken, so the next TopRank finds the next highest.
                // When empty (top = -1), C# masks the shift count to 31, but ranks is 0 so it stays 0
                ranks &= ~(1u << top);
            }
            return packed;
        }

        // Highest rank of a 5-card straight in the rank mask, -1 if none. Ace also counts as the lowest card.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int StraightTop(uint ranks)
        {
            // Shift every rank up by 1 and copy the ace (bit 12) into bit 0, so the ace is also below the 2:
            // bit 0 = ace low, bit (rank + 1) = rank
            uint m = (ranks << 1) | ((ranks >> 12) & 1);
            // Bit j of s is set only when bits j, j+1, j+2, j+3, j+4 of m are all set: 5 ranks in a row
            uint s = m & (m >> 1) & (m >> 2) & (m >> 3) & (m >> 4);
            // The highest j is the best straight. Its top card is at shifted bit j + 4, so rank j + 3.
            // Eg: 5-6-7-8-9 = ranks 3..7 = m bits 4..8 -> j = 4 -> rank 7 (the 9). Wheel A-5: j = 0 -> rank 3 (the 5)
            return s == 0 ? -1 : TopRank(s) + 3;
        }

        public static (int orderingValue, WinHandType winHand) EvalHand(ulong handMask)
        {
            // One 13-bit rank mask per suit: shift the suit's 16-bit lane down and keep its 13 rank bits
            uint spade = (uint)handMask & 0x1FFF;
            uint club = (uint)(handMask >> 16) & 0x1FFF;
            uint diamond = (uint)(handMask >> 32) & 0x1FFF;
            uint heart = (uint)(handMask >> 48) & 0x1FFF;

            // Rank counts from the 4 suit masks, for all 13 ranks at once. A rank has k cards when k suit masks
            // have its bit, so each mask below is "the ranks held in at least k suits":
            // - ranks (1+): in any suit
            // - two (2+): in some pair of suits. There are 6 pairs: (s,c), (d,h), and the 4 pairs with one suit from
            //   {s, c} and one from {d, h}, which is exactly (s|c) & (d|h)
            // - three (3+): in some 3 of the 4 suits: both of s,c and one of d,h, or both of d,h and one of s,c
            // - four: in all 4 suits
            // A quad rank is also in two and three, and a trip rank is also in two.
            uint sc = spade & club, dh = diamond & heart;
            uint spadeOrClub = spade | club, diamondOrHeart = diamond | heart;
            uint ranks = spadeOrClub | diamondOrHeart;
            uint two = sc | dh | (spadeOrClub & diamondOrHeart);
            uint three = (sc & diamondOrHeart) | (dh & spadeOrClub);
            uint four = sc & dh;

            // Flush: popcount of a suit mask = number of cards of that suit (at most 1 suit can have 5+ out of 7)
            uint flush = 0;
            if (BitOperations.PopCount(spade) >= 5) flush = spade;
            else if (BitOperations.PopCount(club) >= 5) flush = club;
            else if (BitOperations.PopCount(diamond) >= 5) flush = diamond;
            else if (BitOperations.PopCount(heart) >= 5) flush = heart;

            if (flush != 0)
            {
                int straightFlush = StraightTop(flush);
                if (straightFlush >= 0)
                {
                    return (straightFlush, WinHandType.STRAIGHT_FLUSH);
                }
            }

            if (four != 0)
            {
                int quad = TopRank(four);
                // Kicker: the highest of the other ranks (clear the quad's bit)
                return ((quad << 6) + TopRank(ranks & ~(1u << quad)), WinHandType.QUAD);
            }

            if (three != 0)
            {
                int trip = TopRank(three);
                // Other ranks with 2+ cards (clear the trip's bit). The pair can also be a second trip
                uint pairs = two & ~(1u << trip);
                if (pairs != 0)
                {
                    return ((trip << 6) + TopRank(pairs), WinHandType.FULL_HOUSE);
                }
            }

            if (flush != 0)
            {
                return (TopRanks(flush, 5), WinHandType.FLUSH);
            }

            int straight = StraightTop(ranks);
            if (straight >= 0)
            {
                return (straight, WinHandType.STRAIGHT);
            }

            if (three != 0)
            {
                int trip = TopRank(three);
                return ((trip << 12) + TopRanks(ranks & ~(1u << trip), 2), WinHandType.THREE_OF_A_KIND);
            }

            if (two != 0)
            {
                int highPair = TopRank(two);
                uint otherPairs = two & ~(1u << highPair);
                if (otherPairs != 0)
                {
                    int lowPair = TopRank(otherPairs);
                    // Kicker: the highest rank after clearing both pairs' bits. It can come from a third pair
                    return ((highPair << 12) + (lowPair << 6) + TopRank(ranks & ~((1u << highPair) | (1u << lowPair))),
                        WinHandType.TWO_PAIR);
                }
                return ((highPair << 18) + TopRanks(ranks & ~(1u << highPair), 3), WinHandType.PAIR);
            }

            return (TopRanks(ranks, 5), WinHandType.HIGH_CARD);
        }

        // Evaluate many hands at once: values[i] = EvalHandValue(handMasks[i]).
        // Uses AVX-512 (8 hands per instruction, branch-free) when available, scalar otherwise.
        public static void EvalHandValues(ReadOnlySpan<ulong> handMasks, Span<long> values)
        {
            Debug.Assert(values.Length >= handMasks.Length);
            int i = 0;
            if (Vector512.IsHardwareAccelerated && Avx512CD.IsSupported)
            {
                ref ulong src = ref MemoryMarshal.GetReference(handMasks);
                ref long dst = ref MemoryMarshal.GetReference(values);
                for (; i + Vector512<ulong>.Count <= handMasks.Length; i += Vector512<ulong>.Count)
                {
                    EvalHandValues512(Vector512.LoadUnsafe(ref src, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
                }
            }
            for (; i < handMasks.Length; i++)
            {
                values[i] = EvalHandValue(handMasks[i]);
            }
        }

        // Vector version of TopRank for 8 hands: vplzcntq (AVX512-CD) counts leading zeros in each 64-bit lane,
        // so 63 - lzcnt is the highest set bit. lzcnt(0) = 64 -> -1 if empty
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> TopRank512(Vector512<ulong> ranks)
        {
            return Vector512.Create(63L) - Avx512CD.LeadingZeroCount(ranks).AsInt64();
        }

        // Clear the bit of rank in each lane: ranks & ~(1 << rank), with vpsllvq (a shift by a different count per lane).
        // Unlike a scalar C# shift, the count is not masked: a count >= 64 gives 0. Rank -1 reads as a huge count,
        // so 1 << -1 = 0 and nothing is cleared
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ulong> ClearRank512(Vector512<ulong> ranks, Vector512<long> rank)
        {
            return ranks & ~Avx512F.ShiftLeftLogicalVariable(Vector512<ulong>.One, rank.AsUInt64());
        }

        // Vector version of TopRanks: pack the top n ranks of each lane into 6-bit fields, highest first
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> TopRanks512(Vector512<ulong> ranks, int n)
        {
            var packed = Vector512<long>.Zero;
            for (int i = 0; i < n; i++)
            {
                var top = TopRank512(ranks);
                packed = (packed << 6) + top;
                ranks = ClearRank512(ranks, top);
            }
            return packed;
        }

        // Vector version of StraightTop (same shift-and-AND per lane), -1 in the lanes without a straight
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> StraightTop512(Vector512<ulong> ranks)
        {
            var m = (ranks << 1) | ((ranks >> 12) & Vector512<ulong>.One);
            var s = m & (m >> 1) & (m >> 2) & (m >> 3) & (m >> 4);
            return Vector512.ConditionalSelect(Vector512.Equals(s, Vector512<ulong>.Zero).AsInt64(),
                Vector512.Create(-1L), TopRank512(s) + Vector512.Create(3L));
        }

        // Select mask: all 64 bits set in the lanes where v != 0. Equals gives all ones where v == 0, ~ flips it
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> NonZero512(Vector512<ulong> v)
        {
            return ~Vector512.Equals(v, Vector512<ulong>.Zero).AsInt64();
        }

        // Same as EvalHandValue for 8 hands. All hand types are computed, then the best one is selected per lane.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> EvalHandValues512(Vector512<ulong> hand)
        {
            var rankBits = Vector512.Create(0x1FFFUL); // Each suit uses 13 bits in the 16-bit lane to store rank
            var spade = hand & rankBits;
            var club = (hand >> 16) & rankBits;
            var diamond = (hand >> 32) & rankBits;
            var heart = (hand >> 48) & rankBits;

            var sc = spade & club;
            var dh = diamond & heart;
            var spadeOrClub = spade | club;
            var diamondOrHeart = diamond | heart;
            var ranks = spadeOrClub | diamondOrHeart;
            var two = sc | dh | (spadeOrClub & diamondOrHeart);
            var three = (sc & diamondOrHeart) | (dh & spadeOrClub);
            var four = sc & dh;

            // Flush: popcount of the 4 suit lanes (16 bits each) at once, with plain shifts and adds (SWAR), since
            // .NET has no API for the per-lane vpopcnt. Each step adds neighbouring fields into fields twice as wide:
            // 1. Every 2-bit field becomes the number of 1 bits in it (0-2): x - ((x >> 1) & 0b0101...)
            var count = hand - ((hand >> 1) & Vector512.Create(0x5555555555555555UL));
            // 2. Add neighbouring 2-bit counts into 4-bit fields (0-4)
            count = (count & Vector512.Create(0x3333333333333333UL)) + ((count >> 2) & Vector512.Create(0x3333333333333333UL));
            // 3. Add neighbouring 4-bit counts into bytes (0-8)
            count = (count + (count >> 4)) & Vector512.Create(0x0F0F0F0F0F0F0F0FUL);
            // 4. Add the 2 bytes of each 16-bit suit lane into its low byte, and drop the high byte (which now holds a
            //    partial sum with the next lane). Each lane now holds the number of cards of that suit (0-13)
            count = (count + (count >> 8)) & Vector512.Create(0x00FF00FF00FF00FFUL);
            // count <= 13 per lane, so count + 11 is in 11-24, and has bit 4 (16) set exactly when count >= 5.
            // Shift bit 4 down to bit 0: 1 in the lane of the flush suit, 0 in the others
            var isFlushSuit = ((count + Vector512.Create(0x000B000B000B000BUL)) >> 4) & Vector512.Create(0x0001000100010001UL);
            // Turn the flush lane's 1 into 0xFFFF: the shift moves the 1 up into the next lane, and subtracting the
            // original 1 borrows it back, leaving 0xFFFF in the flush lane and 0 above. For hearts (the top lane) the
            // shifted 1 falls off bit 63 and the borrow wraps around mod 2^64, which gives the same 0xFFFF << 48.
            // Then keep only the flush suit's lane of the hand
            var flushBits = hand & ((isFlushSuit << 16) - isFlushSuit);
            // Fold the 4 lanes into the low 13 bits: only the flush lane is non-zero, so this is its rank mask.
            // The shorter shifts leave copies in the lanes between (TopRanks512 would read them as ranks 16 and up),
            // so keep only the low lane
            var flush = (flushBits | (flushBits >> 16) | (flushBits >> 32) | (flushBits >> 48)) & rankBits;

            var quad = TopRank512(four);
            var trip = TopRank512(three);
            var fullHousePairs = ClearRank512(two, trip);
            var highPair = TopRank512(two);
            var otherPairs = ClearRank512(two, highPair);
            var lowPair = TopRank512(otherPairs);
            var straight = StraightTop512(ranks);
            var straightFlush = StraightTop512(flush);

            // Compute every hand type for every lane, then pick per lane with ConditionalSelect(mask, a, b), which
            // takes a in the lanes where mask is all ones and b elsewhere. Going from the lowest to the highest hand
            // type, a better hand overrides. The hand type goes in bits 32+, like ToEvalValue
            var result = TopRanks512(ranks, 5) + Vector512.Create((long)WinHandType.HIGH_CARD << 32);
            result = Vector512.ConditionalSelect(NonZero512(two),
                (highPair << 18) + TopRanks512(ClearRank512(ranks, highPair), 3)
                    + Vector512.Create((long)WinHandType.PAIR << 32), result);
            result = Vector512.ConditionalSelect(NonZero512(otherPairs),
                (highPair << 12) + (lowPair << 6) + TopRank512(ClearRank512(ClearRank512(ranks, highPair), lowPair))
                    + Vector512.Create((long)WinHandType.TWO_PAIR << 32), result);
            result = Vector512.ConditionalSelect(NonZero512(three),
                (trip << 12) + TopRanks512(ClearRank512(ranks, trip), 2)
                    + Vector512.Create((long)WinHandType.THREE_OF_A_KIND << 32), result);
            result = Vector512.ConditionalSelect(Vector512.GreaterThanOrEqual(straight, Vector512<long>.Zero),
                straight + Vector512.Create((long)WinHandType.STRAIGHT << 32), result);
            result = Vector512.ConditionalSelect(NonZero512(flush),
                TopRanks512(flush, 5) + Vector512.Create((long)WinHandType.FLUSH << 32), result);
            result = Vector512.ConditionalSelect(NonZero512(three) & NonZero512(fullHousePairs),
                (trip << 6) + TopRank512(fullHousePairs) + Vector512.Create((long)WinHandType.FULL_HOUSE << 32), result);
            result = Vector512.ConditionalSelect(NonZero512(four),
                (quad << 6) + TopRank512(ClearRank512(ranks, quad)) + Vector512.Create((long)WinHandType.QUAD << 32), result);
            result = Vector512.ConditionalSelect(Vector512.GreaterThanOrEqual(straightFlush, Vector512<long>.Zero),
                straightFlush + Vector512.Create((long)WinHandType.STRAIGHT_FLUSH << 32), result);
            return result;
        }

        // Combine the ordering value and WinHandType from EvalHand
        // to a single long value for faster comparison: the hand type in bits 32+ is compared first.
        // The ordering value is below 2^31 (and above -2^31 with fillers), so it never spills into the type
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToEvalValue((int, WinHandType) evalHandReturn)
        {
            return (((long)evalHandReturn.Item2) << 32) + evalHandReturn.Item1;
        }

        // Table-based evaluator for exactly 7 cards (EvalHandRank). It returns a single int hand rank: higher is
        // better, equal is a tie. The rank is (WinHandType << 12) | order within the type, and it orders hands
        // exactly like EvalHandValue (the tables are built from it). It's the fastest while its tables are in cache.
        // EvalHandValue and EvalHandValues need no memory, and win once the tables have been evicted to DRAM
        // (see README "When the tables aren't in cache").
        //
        // Every card has a 128-bit vector (HandKeyMask): an additive key (element 0) and its card mask (element 1).
        // A hand's vector is the sum of its cards' vectors: the cards are all different, so adding their masks is
        // the same as OR-ing them, and one vector add (paddq) per card builds both. The key:
        // - Bits 0-31: the sum of RankTables.RANK_KEYS[rank]. The keys make the sum different for every multiset
        //   of 7 ranks (at most 4 of each). The largest sum is 4 * 1479181 + 3 * 636345 = 7,825,759 < 2^23.
        // - Bits 32-47: one 4-bit counter per suit. EMPTY_HAND_KEY starts every counter at 3, so a suit with 5+
        //   cards reaches 8-10 and sets bit 3 of its counter. 3 + 7 = 10 never carries into the next counter.
        // Suit counts are all the key needs about suits: apart from a flush or a straight flush, a hand's value
        // depends only on its ranks. The counts tell whether there's a flush and in which suit, and only then are
        // that suit's ranks read from the hand mask.
        // RANK_KEYS come from a greedy search, see README "How RANK_KEYS were found".
        // A flush is looked up by the flush suit's 13-bit rank mask, any other hand by its rank sum through a
        // perfect hash. Because keys add up, the board's vector is computed once at a showdown, and each player
        // only adds their 2 hole cards.
        const ulong EMPTY_HAND_KEY = 0x3333UL << 32;
        // Bit 3 of each suit counter
        const ulong FLUSH_CHECK = 0x8888UL << 32;

        // Key and card mask of one card
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<ulong> HandKeyMask(int card)
        {
            return RankTables.CardKeyMasks[card];
        }

        // Key and mask of the cards, starting from the empty hand. With no cards, the empty hand to add cards to
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<ulong> HandKeyMask(ReadOnlySpan<int> cards)
        {
            var keyMask = Vector128.Create(EMPTY_HAND_KEY, 0UL);
            for (int i = 0; i < cards.Length; i++)
            {
                keyMask += HandKeyMask(cards[i]);
            }
            return keyMask;
        }

        // keyMask = HandKeyMask of exactly 7 cards
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EvalHandRank(Vector128<ulong> keyMask)
        {
            Debug.Assert(BitOperations.PopCount(keyMask.GetElement(1)) == 7 && (keyMask.ToScalar() & 0xFFFF_FFFFUL) <= RankTables.MAX_RANK_KEY);
            ulong handKey = keyMask.ToScalar();
            ulong flushSuit = handKey & FLUSH_CHECK;
            if (flushSuit != 0)
            {
                // At most 1 suit has 5+ of 7 cards. Its flag is bit 32 + 4 * suit + 3, so the shift to its 16-bit lane
                // in the mask is 16 * suit = (tzcnt - 35) * 4. Only a flush needs the mask, so it's only moved out of
                // the vector here
                return RankTables.Flush[(keyMask.GetElement(1) >> ((BitOperations.TrailingZeroCount(flushSuit) - 35) << 2)) & 0x1FFF];
            }
            return RankTables.Lookup((uint)handKey);
        }

        // Hand type of an EvalHandRank result
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WinHandType HandRankType(int handRank)
        {
            return (WinHandType)(handRank >> 12);
        }
    }

    // Lookup tables for Game.EvalHandRank, built once (on first use, about 75 ms) from Game.EvalHandValue, so the two
    // evaluators always order hands the same way. About 270 KB (Ranks 222 KB, RowOffsets 30 KB, Flush 16 KB), small
    // enough to stay in the L2 cache while nothing else pushes it out.
    internal static class RankTables
    {
        // Greedy search: each key is the smallest one above the previous key that keeps the sums of all multisets
        // of up to 7 ranks unique for each number of cards. Rank 0 can be 0: with exactly 7 cards, its count is
        // whatever the other ranks leave
        internal static readonly int[] RANK_KEYS = [0, 1, 5, 22, 98, 453, 2031, 8698, 22854, 83661, 262349, 636345, 1479181];
        internal const int MAX_RANK_KEY = 4 * 1479181 + 3 * 636345;
        // Perfect hash by displacement: the rank sum's high bits pick a row, and each row has its own offset into
        // Ranks, chosen when the table is built so that no two hands share a slot
        const int ROW_SHIFT = 10;

        // Each card (0-51): its key (rank key in bits 0-31, 1 in its suit's counter at bits 32 + 4 * suit) and its
        // Game.ToCardMask
        internal static readonly Vector128<ulong>[] CardKeyMasks = new Vector128<ulong>[52];
        // Rank of a flush (or straight flush) by the 13-bit rank mask of the flush suit (5-7 bits set)
        internal static readonly ushort[] Flush = new ushort[1 << 13];
        // RowOffsets[key >> ROW_SHIFT] + key is the slot in Ranks (the row's offset minus the row's start)
        internal static readonly int[] RowOffsets;
        internal static readonly ushort[] Ranks;

        // Rank of a non-flush 7-card hand from its rank sum: 2 dependent loads (RowOffsets, then Ranks)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Lookup(uint rankKey)
        {
            return Ranks[(int)rankKey + RowOffsets[rankKey >> ROW_SHIFT]];
        }

        static RankTables()
        {
            (RowOffsets, Ranks) = Build();
        }

        // Fills the card, suit and flush tables and returns the perfect hash. Compiled optimized from the start: a static
        // constructor runs only once, so the runtime would run it as unoptimized code (about 0.5 s instead of ms)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static (int[] rowOffsets, ushort[] ranks) Build()
        {
            for (int card = 0; card < 52; card++)
            {
                ulong key = (uint)RANK_KEYS[Game.GetRank(card)] | (1UL << (32 + 4 * Game.GetSuit(card)));
                CardKeyMasks[card] = Vector128.Create(key, Game.ToCardMask(card));
            }

            // Every multiset of 7 ranks: its rank sum, and the EvalHandValue of a hand with those ranks and no flush
            var keys = new List<int>(49205);
            var values = new List<long>(49205);
            Span<int> counts = stackalloc int[13];
            AddRankMultisets(counts, 0, 7, keys, values);

            // Flushes: every suit mask with 5-7 ranks, as an all-spade hand
            var flushValues = new long[Flush.Length];
            for (int mask = 0; mask < Flush.Length; mask++)
            {
                int numCards = BitOperations.PopCount((uint)mask);
                flushValues[mask] = numCards >= 5 && numCards <= 7 ? Game.EvalHandValue((ulong)mask) : -1;
            }

            // Replace each EvalHandValue by (type << 12) | its position among the values of that type
            var distinct = values.Concat(flushValues.Where(v => v >= 0)).Distinct().Order().ToArray();
            var toRank = new Dictionary<long, ushort>(distinct.Length);
            for (int i = 0, typeStart = 0; i < distinct.Length; i++)
            {
                long type = distinct[i] >> 32;
                if (i > 0 && type != distinct[i - 1] >> 32)
                {
                    typeStart = i;
                }
                Debug.Assert(i - typeStart < 1 << 12);
                toRank[distinct[i]] = (ushort)((type << 12) | (long)(i - typeStart));
            }
            for (int mask = 0; mask < Flush.Length; mask++)
            {
                Flush[mask] = flushValues[mask] >= 0 ? toRank[flushValues[mask]] : (ushort)0;
            }

            // Displacement: place the rows with the most keys first, each at the lowest offset where all its slots
            // are still free. A row starts its search a little before the previous row with as many keys, since the
            // offsets before that mostly failed for rows like it: 10x faster to build, for a 3% bigger table
            int numRows = (MAX_RANK_KEY >> ROW_SHIFT) + 1;
            int rowSize = 1 << ROW_SHIFT;
            var rowStart = new int[numRows + 1];
            foreach (int key in keys)
            {
                rowStart[(key >> ROW_SHIFT) + 1]++;
            }
            for (int row = 0; row < numRows; row++)
            {
                rowStart[row + 1] += rowStart[row];
            }
            // Keys grouped by row, as their column inside the row
            var columns = new int[keys.Count];
            var filled = (int[])rowStart.Clone();
            foreach (int key in keys)
            {
                columns[filled[key >> ROW_SHIFT]++] = key & (rowSize - 1);
            }
            var rowOrder = Enumerable.Range(0, numRows).OrderByDescending(row => rowStart[row + 1] - rowStart[row]).ToArray();
            var used = new bool[4 * keys.Count + 2 * rowSize];
            var searchStart = new int[rowSize + 1];
            var rowOffsets = new int[numRows];
            int size = 0, firstFree = 0;
            foreach (int row in rowOrder)
            {
                var rowColumns = columns.AsSpan(rowStart[row], rowStart[row + 1] - rowStart[row]);
                int offset = 0;
                if (rowColumns.Length > 0)
                {
                    while (used[firstFree])
                    {
                        firstFree++;
                    }
                    int minColumn = int.MaxValue;
                    foreach (int column in rowColumns)
                    {
                        minColumn = Math.Min(minColumn, column);
                    }
                    for (offset = Math.Max(Math.Max(0, firstFree - minColumn), searchStart[rowColumns.Length] - 256); ; offset++)
                    {
                        bool fits = true;
                        foreach (int column in rowColumns)
                        {
                            if (used[column + offset])
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (fits)
                        {
                            break;
                        }
                    }
                    searchStart[rowColumns.Length] = offset;
                    foreach (int column in rowColumns)
                    {
                        used[column + offset] = true;
                        size = Math.Max(size, column + offset + 1);
                    }
                }
                rowOffsets[row] = offset - row * rowSize;
            }
            // Room for a full row, so even an invalid key in an empty row stays in bounds
            var ranks = new ushort[Math.Max(size, rowSize)];
            for (int i = 0; i < keys.Count; i++)
            {
                ranks[keys[i] + rowOffsets[keys[i] >> ROW_SHIFT]] = toRank[values[i]];
            }
            return (rowOffsets, ranks);
        }

        // Enumerates the counts of ranks rank..12 that add up to cardsLeft (at most 4 each)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AddRankMultisets(Span<int> counts, int rank, int cardsLeft, List<int> keys, List<long> values)
        {
            if (rank == 13)
            {
                if (cardsLeft > 0)
                {
                    return;
                }
                // Deal the suits in turn, card after card: the copies of a rank get different suits, and no suit
                // gets more than 2 of the 7 cards, so there is no flush
                int key = 0, numCard = 0;
                ulong mask = 0;
                for (int r = 0; r < 13; r++)
                {
                    key += counts[r] * RANK_KEYS[r];
                    for (int c = 0; c < counts[r]; c++, numCard++)
                    {
                        mask |= Game.ToCardMask(r * 4 + numCard % 4);
                    }
                }
                keys.Add(key);
                values.Add(Game.EvalHandValue(mask));
                return;
            }
            for (int count = 0; count <= Math.Min(4, cardsLeft); count++)
            {
                counts[rank] = count;
                AddRankMultisets(counts, rank + 1, cardsLeft - count, keys, values);
            }
            counts[rank] = 0;
        }
    }
}
