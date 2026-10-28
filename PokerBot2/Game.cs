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
            // Turn each lane's 1 into 0xFFFF: (1 << 16) - 1 per lane. Lanes are 0 or 1, so no borrow crosses lanes.
            // Then keep only the flush suit's lane of the hand
            var flushBits = hand & ((isFlushSuit << 16) - isFlushSuit);
            // Fold the 4 lanes into the low 13 bits: only the flush lane is non-zero, so this is its rank mask
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
    }
}
