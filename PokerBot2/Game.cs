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

        // Card = 1 - 52, following the rank-suit order [2♠, 2♣, 2♦, 2♥, 3♠, ...]
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
            if (rank <= 8)
            {
                display = "" + ('1' + rank);
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

        // Card needs to be 2 char, rank followed by suit, eg: '2♠' or 'A♠'
        public static int StringToCard(string card)
        {
            int rank;
            switch (card[0])
            {
                case 'J': rank = 9; break;
                case 'Q': rank = 10; break;
                case 'K': rank = 11; break;
                case 'A': rank = 12; break;
                default: rank = card[0] - '2'; break;
            }

            var suit = card[1] switch
            {
                '♠' => 0,
                '♣' => 1,
                '♦' => 2,
                '♥' => 3,
                _ => throw new Exception("Invalid suit"),
            };
            return (rank * 4 + suit);
        }

        private int[] Deck = [.. Enumerable.Range(0, 52)];
        private int CurDealtCard = 0;
        private readonly int NumPlayer;

        public Game(int numPlayer)
        {
            Random.Shared.Shuffle(Deck);
            NumPlayer = numPlayer;
        }

        public void Reset()
        {
            Random.Shared.Shuffle(Deck);
            CurDealtCard = 0;
        }

        // The first 2 * Numplayers are the dealt card for player
        // Should call this 2N times for hole card,
        // then 3 times for flop, 1 for turn, 1 for river
        public int DealCard()
        {
            return Deck[CurDealtCard++];
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

        // Hand mask: 16 bits per suit (suit * 16 + rank), so the 4 suits are 4 lanes of one ulong
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

        // Rank of the highest set bit, -1 if empty (lzcnt(0) = 32)
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
                packed = (packed << 6) + top;
                // No-op when empty: 0 & anything = 0
                ranks &= ~(1u << top);
            }
            return packed;
        }

        // Highest rank of a 5-card straight in the rank mask, -1 if none. Ace also counts as the lowest card.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int StraightTop(uint ranks)
        {
            // Bit 0 = ace low, bit (rank + 1) = rank
            uint m = (ranks << 1) | ((ranks >> 12) & 1);
            uint s = m & (m >> 1) & (m >> 2) & (m >> 3) & (m >> 4);
            // Bit j of s = straight from shifted bit j to j + 4, top rank = j + 4 - 1
            return s == 0 ? -1 : TopRank(s) + 3;
        }

        public static (int orderingValue, WinHandType winHand) EvalHand(ulong handMask)
        {
            uint spade = (uint)handMask & 0x1FFF;
            uint club = (uint)(handMask >> 16) & 0x1FFF;
            uint diamond = (uint)(handMask >> 32) & 0x1FFF;
            uint heart = (uint)(handMask >> 48) & 0x1FFF;

            // Rank counts from the 4 suit masks: a rank is in "two" if at least 2 suits have it, and so on
            uint sc = spade & club, dh = diamond & heart;
            uint spadeOrClub = spade | club, diamondOrHeart = diamond | heart;
            uint ranks = spadeOrClub | diamondOrHeart;
            uint two = sc | dh | (spadeOrClub & diamondOrHeart);
            uint three = (sc & diamondOrHeart) | (dh & spadeOrClub);
            uint four = sc & dh;

            // Flush (at most 1 suit can have 5+ out of 7 cards)
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
                return ((quad << 6) + TopRank(ranks & ~(1u << quad)), WinHandType.QUAD);
            }

            if (three != 0)
            {
                int trip = TopRank(three);
                // The pair can also be a second trip
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
                    // The kicker can come from a third pair
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

        // Vector version of TopRank: 63 - lzcnt per lane, -1 if empty
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> TopRank512(Vector512<ulong> ranks)
        {
            return Vector512.Create(63L) - Avx512CD.LeadingZeroCount(ranks).AsInt64();
        }

        // Clear the bit of rank per lane. Rank -1 (empty) is a shift count >= 64, so nothing is cleared
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ulong> ClearRank512(Vector512<ulong> ranks, Vector512<long> rank)
        {
            return ranks & ~Avx512F.ShiftLeftLogicalVariable(Vector512<ulong>.One, rank.AsUInt64());
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> StraightTop512(Vector512<ulong> ranks)
        {
            var m = (ranks << 1) | ((ranks >> 12) & Vector512<ulong>.One);
            var s = m & (m >> 1) & (m >> 2) & (m >> 3) & (m >> 4);
            return Vector512.ConditionalSelect(Vector512.Equals(s, Vector512<ulong>.Zero).AsInt64(),
                Vector512.Create(-1L), TopRank512(s) + Vector512.Create(3L));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> NonZero512(Vector512<ulong> v)
        {
            return ~Vector512.Equals(v, Vector512<ulong>.Zero).AsInt64();
        }

        // Same as EvalHandValue for 8 hands. All hand types are computed, then the best one is selected per lane.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<long> EvalHandValues512(Vector512<ulong> hand)
        {
            var rankBits = Vector512.Create(0x1FFFUL);
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

            // Flush: popcount of the 4 suit lanes (16 bits each) at once, then keep the suit lane with 5+ cards
            var count = hand - ((hand >> 1) & Vector512.Create(0x5555555555555555UL));
            count = (count & Vector512.Create(0x3333333333333333UL)) + ((count >> 2) & Vector512.Create(0x3333333333333333UL));
            count = (count + (count >> 4)) & Vector512.Create(0x0F0F0F0F0F0F0F0FUL);
            count = (count + (count >> 8)) & Vector512.Create(0x00FF00FF00FF00FFUL);
            // count <= 13 per lane, so count + 11 has bit 4 set exactly when count >= 5
            var isFlushSuit = ((count + Vector512.Create(0x000B000B000B000BUL)) >> 4) & Vector512.Create(0x0001000100010001UL);
            var flushBits = hand & ((isFlushSuit << 16) - isFlushSuit);
            var flush = (flushBits | (flushBits >> 16) | (flushBits >> 32) | (flushBits >> 48)) & rankBits;

            var quad = TopRank512(four);
            var trip = TopRank512(three);
            var fullHousePairs = ClearRank512(two, trip);
            var highPair = TopRank512(two);
            var otherPairs = ClearRank512(two, highPair);
            var lowPair = TopRank512(otherPairs);
            var straight = StraightTop512(ranks);
            var straightFlush = StraightTop512(flush);

            // From the lowest to the highest hand type, so a better hand overrides
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
        // to a single long value for faster comparison
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToEvalValue((int, WinHandType) evalHandReturn)
        {
            return (((long)evalHandReturn.Item2) << 32) + evalHandReturn.Item1;
        }
    }
}
