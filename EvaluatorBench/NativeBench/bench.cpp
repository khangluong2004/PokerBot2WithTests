// Native side of the evaluator benchmark (see ..\README.md): OMPEval, Two Plus Two, PokerHandEvaluator
// (phevaluator) and Senzee's 7-card perfect index, on the same random 7-card hands as the .NET side
// (data\hands7.bin), each checked against PokerBot2's ranks (data\ranks7.bin).
// Card in the data files = rank * 4 + suit (rank 0 = deuce ... 12 = ace).
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <map>
#include <string>
#include <vector>

#include "omp/HandEvaluator.h"
#include "phevaluator/phevaluator.h"
#ifndef _MSC_VER
#define __int64 long long
#endif
#include "index52c7.h"

static std::vector<uint8_t> ReadFile(const std::string& path)
{
    std::vector<uint8_t> data;
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) { std::printf("Cannot open %s\n", path.c_str()); std::exit(1); }
    std::fseek(f, 0, SEEK_END);
    data.resize(std::ftell(f));
    std::fseek(f, 0, SEEK_SET);
    if (std::fread(data.data(), 1, data.size(), f) != data.size()) { std::printf("Short read %s\n", path.c_str()); std::exit(1); }
    std::fclose(f);
    return data;
}

// Same check as the .NET side: reference rank -> result must be a function (same ties) that is strictly
// increasing (higherIsBetter) or strictly decreasing
static std::string OrderCheck(const std::vector<int32_t>& reference, const std::vector<int64_t>& results, bool higherIsBetter)
{
    std::map<int32_t, int64_t> map;
    long conflicts = 0, misordered = 0;
    for (size_t h = 0; h < reference.size(); h++) {
        auto it = map.find(reference[h]);
        if (it != map.end() && it->second != results[h]) conflicts++;
        map[reference[h]] = results[h];
    }
    int64_t prev = 0;
    bool first = true;
    for (auto& kv : map) {
        if (!first && (higherIsBetter ? kv.second <= prev : kv.second >= prev)) misordered++;
        prev = kv.second;
        first = false;
    }
    char buf[128];
    if (conflicts == 0 && misordered == 0) std::snprintf(buf, sizeof buf, "ok (%zu classes)", map.size());
    else std::snprintf(buf, sizeof buf, "WRONG: %ld tie conflicts, %ld out of order", conflicts, misordered);
    return buf;
}

// ---------------------------------------------------------------------------------------------------------------
// Sequential order (bench_xxx.exe data seq), the same 2 cases as DotnetBench's seq mode (SeqBench.cs):
// - all hands: every 7-card hand in order (c0 < c1 < ... < c6)
// - boards: every hole-card pair (1,081) on each board of data\boards5.bin (written by DotnetBench seq), in order
// Each evaluator reuses the work for the cards that didn't change, its own natural way. A first pass counts the hand
// types (checked against the known totals, or against OMPEval on the boards), then the best of 2 timed passes.

static const long long kAllHandTypes[10] = {0, 23294460, 58627800, 31433400, 6461620, 6180020, 4047644, 3473184, 224848, 41584};

// Hand type 1 (high card) - 9 (straight flush) of a phevaluator rank (1 = best, Cactus Kev's 7,462 classes)
static int PhevalType(int rank)
{
    return rank > 6185 ? 1 : rank > 3325 ? 2 : rank > 2467 ? 3 : rank > 1609 ? 4 : rank > 1599 ? 5
         : rank > 322 ? 6 : rank > 166 ? 7 : rank > 10 ? 8 : 9;
}

// The cards not on the board, in order
static int Remaining(const uint8_t* board, int* rest)
{
    uint64_t used = 0;
    for (int i = 0; i < 5; i++) used |= 1ULL << board[i];
    int n = 0;
    for (int card = 0; card < 52; card++) if (!(used >> card & 1)) rest[n++] = card;
    return n;
}

// Two Plus Two: one state per loop level, so the innermost loop does 1 lookup per hand, at consecutive entries
template <class F> static void AllTwoPlusTwo(const int* HR, F&& f)
{
    for (int a = 0; a < 46; a++) { int ua = HR[53 + a + 1];
    for (int b = a + 1; b < 47; b++) { int ub = HR[ua + b + 1];
    for (int c = b + 1; c < 48; c++) { int uc = HR[ub + c + 1];
    for (int d = c + 1; d < 49; d++) { int ud = HR[uc + d + 1];
    for (int e = d + 1; e < 50; e++) { int ue = HR[ud + e + 1];
    for (int k = e + 1; k < 51; k++) { int uk = HR[ue + k + 1];
    for (int g = k + 1; g < 52; g++) f(HR[uk + g + 1]); } } } } } }
}

// OMPEval: one Hand per loop level, each the previous one plus a card
template <class F> static void AllOmp(const omp::HandEvaluator& omp, const std::vector<omp::Hand>& cards, F&& f)
{
    for (int a = 0; a < 46; a++) { omp::Hand ha = omp::Hand::empty() + cards[a];
    for (int b = a + 1; b < 47; b++) { omp::Hand hb = ha + cards[b];
    for (int c = b + 1; c < 48; c++) { omp::Hand hc = hb + cards[c];
    for (int d = c + 1; d < 49; d++) { omp::Hand hd = hc + cards[d];
    for (int e = d + 1; e < 50; e++) { omp::Hand he = hd + cards[e];
    for (int k = e + 1; k < 51; k++) { omp::Hand hk = he + cards[k];
    for (int g = k + 1; g < 52; g++) f((int)omp.evaluate(hk + cards[g])); } } } } } }
}

// phevaluator has no incremental API: all 7 cards every time
template <class F> static void AllPheval(F&& f)
{
    for (int a = 0; a < 46; a++)
    for (int b = a + 1; b < 47; b++)
    for (int c = b + 1; c < 48; c++)
    for (int d = c + 1; d < 49; d++)
    for (int e = d + 1; e < 50; e++)
    for (int k = e + 1; k < 51; k++)
    for (int g = k + 1; g < 52; g++) f(evaluate_7cards(a, b, c, d, e, k, g));
}

// Senzee: the card mask ORed at each loop level, then index52c7 and 1 table lookup per hand
template <class F> static void AllSenzee(const uint16_t* table, F&& f)
{
    for (int a = 0; a < 46; a++) { uint64_t ma = 1ULL << a;
    for (int b = a + 1; b < 47; b++) { uint64_t mb = ma | 1ULL << b;
    for (int c = b + 1; c < 48; c++) { uint64_t mc = mb | 1ULL << c;
    for (int d = c + 1; d < 49; d++) { uint64_t md = mc | 1ULL << d;
    for (int e = d + 1; e < 50; e++) { uint64_t me = md | 1ULL << e;
    for (int k = e + 1; k < 51; k++) { uint64_t mk = me | 1ULL << k;
    for (int g = k + 1; g < 52; g++) f((int)table[index52c7(mk | 1ULL << g)]); } } } } } }
}

template <class F> static void BoardsTwoPlusTwo(const int* HR, const std::vector<uint8_t>& boards, F&& f)
{
    int rest[52];
    for (size_t at = 0; at + 5 <= boards.size(); at += 5) {
        const uint8_t* board = &boards[at];
        int n = Remaining(board, rest);
        int u = 53;
        for (int i = 0; i < 5; i++) u = HR[u + board[i] + 1];
        for (int i = 0; i < n - 1; i++) {
            int u6 = HR[u + rest[i] + 1];
            for (int j = i + 1; j < n; j++) f(HR[u6 + rest[j] + 1]);
        }
    }
}

template <class F> static void BoardsOmp(const omp::HandEvaluator& omp, const std::vector<omp::Hand>& cards, const std::vector<uint8_t>& boards, F&& f)
{
    int rest[52];
    for (size_t at = 0; at + 5 <= boards.size(); at += 5) {
        const uint8_t* board = &boards[at];
        int n = Remaining(board, rest);
        omp::Hand hb = omp::Hand::empty();
        for (int i = 0; i < 5; i++) hb += cards[board[i]];
        for (int i = 0; i < n - 1; i++) {
            omp::Hand h6 = hb + cards[rest[i]];
            for (int j = i + 1; j < n; j++) f((int)omp.evaluate(h6 + cards[rest[j]]));
        }
    }
}

template <class F> static void BoardsPheval(const std::vector<uint8_t>& boards, F&& f)
{
    int rest[52];
    for (size_t at = 0; at + 5 <= boards.size(); at += 5) {
        const uint8_t* b = &boards[at];
        int n = Remaining(b, rest);
        for (int i = 0; i < n - 1; i++)
            for (int j = i + 1; j < n; j++) f(evaluate_7cards(b[0], b[1], b[2], b[3], b[4], rest[i], rest[j]));
    }
}

template <class F> static void BoardsSenzee(const uint16_t* table, const std::vector<uint8_t>& boards, F&& f)
{
    int rest[52];
    for (size_t at = 0; at + 5 <= boards.size(); at += 5) {
        const uint8_t* board = &boards[at];
        int n = Remaining(board, rest);
        uint64_t mb = 0;
        for (int i = 0; i < 5; i++) mb |= 1ULL << board[i];
        for (int i = 0; i < n - 1; i++) {
            uint64_t m6 = mb | 1ULL << rest[i];
            for (int j = i + 1; j < n; j++) f((int)table[index52c7(m6 | 1ULL << rest[j])]);
        }
    }
}

// pass(consumer) runs the evaluator over all its hands, calling consumer(value) for each
template <class Pass, class TypeOf>
static void MeasureSeq(const char* name, long long numHands, Pass&& pass, TypeOf&& typeOf, const long long* known,
                       std::vector<long long>& reference)
{
    std::vector<long long> counts(10);
    pass([&](int v) { counts[typeOf(v)]++; });
    std::string check;
    if (known) {
        check = std::equal(counts.begin(), counts.end(), known) ? "ok (known totals)" : "WRONG";
    } else if (reference.empty()) {
        reference = counts;
        check = "reference (";
        for (int t = 1; t < 10; t++) check += std::to_string(counts[t]) + (t < 9 ? " " : ")");
    } else {
        check = counts == reference ? "ok (= OMPEval)" : "WRONG";
    }
    double best = 1e30;
    long long checksum = 0;
    for (int r = 0; r < 2; r++) {
        long long sum = 0;
        auto start = std::chrono::steady_clock::now();
        pass([&](int v) { sum += v; });
        best = std::min(best, std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - start).count() / numHands);
        checksum += sum;
    }
    std::printf("  %-62s %7.2f ns  %7.0f M/s  hand types %s  (checksum %lld)\n", name, best, 1000 / best, check.c_str(),
                (long long)(checksum & 0xFFFF));
}

static int RunSequential(const std::string& dataDir)
{
    auto boards = ReadFile(dataDir + "\\boards5.bin");
    auto hrBytes = ReadFile(dataDir + "\\HandRanks.dat");
    const int* HR = reinterpret_cast<const int*>(hrBytes.data());
    omp::HandEvaluator omp;
    std::vector<omp::Hand> ompCards;
    for (unsigned card = 0; card < 52; card++) ompCards.push_back(omp::Hand(card));
    // Senzee's table, filled from OMPEval as in the random benchmark
    std::vector<uint16_t> senzee(133784560);
    for (int a = 0; a < 46; a++) for (int b = a + 1; b < 47; b++) for (int c = b + 1; c < 48; c++)
    for (int d = c + 1; d < 49; d++) for (int e = d + 1; e < 50; e++) for (int k = e + 1; k < 51; k++)
    for (int g = k + 1; g < 52; g++) {
        uint64_t mask = (1ULL << a) | (1ULL << b) | (1ULL << c) | (1ULL << d) | (1ULL << e) | (1ULL << k) | (1ULL << g);
        senzee[index52c7(mask)] = omp.evaluate(omp::Hand::empty() + ompCards[a] + ompCards[b] + ompCards[c]
            + ompCards[d] + ompCards[e] + ompCards[k] + ompCards[g]);
    }
#if defined(_MSC_VER)
    std::printf("MSVC %d\n", _MSC_VER);
#else
    std::printf("GCC %d.%d\n", __GNUC__, __GNUC_MINOR__);
#endif
    auto byShift = [](int v) { return v >> 12; };
    std::vector<long long> unused, reference;

    std::printf("All 133,784,560 hands in order, best of 2 passes, ns per hand\n");
    const long long numAll = 133784560;
    MeasureSeq("Two Plus Two (one state per loop level)", numAll, [&](auto&& f) { AllTwoPlusTwo(HR, f); }, byShift, kAllHandTypes, unused);
    MeasureSeq("OMPEval (one Hand per loop level)", numAll, [&](auto&& f) { AllOmp(omp, ompCards, f); }, byShift, kAllHandTypes, unused);
    MeasureSeq("PokerHandEvaluator (phevaluator), 7 cards each time", numAll, [&](auto&& f) { AllPheval(f); }, PhevalType, kAllHandTypes, unused);
    MeasureSeq("Senzee index52c7 + 267 MB table (mask per loop level)", numAll, [&](auto&& f) { AllSenzee(senzee.data(), f); }, byShift, kAllHandTypes, unused);

    long long numBoardHands = (long long)(boards.size() / 5) * 1081;
    std::printf("\nEvery hole-card pair on %zu boards in order (1,081 per board), best of 2 passes, ns per hand\n", boards.size() / 5);
    MeasureSeq("OMPEval (board Hand once, + 2 cards)", numBoardHands, [&](auto&& f) { BoardsOmp(omp, ompCards, boards, f); }, byShift, nullptr, reference);
    MeasureSeq("Two Plus Two (board state once, + 2 lookups)", numBoardHands, [&](auto&& f) { BoardsTwoPlusTwo(HR, boards, f); }, byShift, nullptr, reference);
    MeasureSeq("PokerHandEvaluator (phevaluator), 7 cards each time", numBoardHands, [&](auto&& f) { BoardsPheval(boards, f); }, PhevalType, nullptr, reference);
    MeasureSeq("Senzee index52c7 + 267 MB table (board mask once)", numBoardHands, [&](auto&& f) { BoardsSenzee(senzee.data(), boards, f); }, byShift, nullptr, reference);
    return 0;
}

int main(int argc, char** argv)
{
    std::string dataDir = argc > 1 ? argv[1] : "..\\data";
    if (argc > 2 && std::string(argv[2]) == "seq") {
        return RunSequential(dataDir);
    }
    auto hands = ReadFile(dataDir + "\\hands7.bin");
    auto rankBytes = ReadFile(dataDir + "\\ranks7.bin");
    size_t numHands = hands.size() / 7;
    std::vector<int32_t> reference(numHands);
    std::memcpy(reference.data(), rankBytes.data(), numHands * 4);
#if defined(_MSC_VER)
    std::printf("MSVC %d, ", _MSC_VER);
#else
    std::printf("GCC %d.%d, ", __GNUC__, __GNUC_MINOR__);
#endif
    std::printf("%zu random 7-card hands, best of 7 passes, ns per hand (lower is better)\n\n", numHands);

    std::vector<int64_t> results(numHands);
    auto run = [&](const char* name, const std::function<int64_t()>& pass, bool higherIsBetter) {
        pass();
        std::string check = OrderCheck(reference, results, higherIsBetter);
        pass();
        double best = 1e30;
        int64_t checksum = 0;
        for (int r = 0; r < 7; r++) {
            auto start = std::chrono::steady_clock::now();
            checksum += pass();
            double ns = std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - start).count() / numHands;
            best = std::min(best, ns);
        }
        std::printf("%-62s %7.2f ns  %7.0f M/s  order %s  (checksum %lld)\n", name, best, 1000 / best, check.c_str(),
                    (long long)(checksum & 0xFFFF));
    };
    const uint8_t* c = hands.data();

    // OMPEval: a Hand is built by adding the 7 cards (key + mask in one 128-bit add), then 1-2 table lookups.
    // Its card index is 4 * rank + suit, the same as ours
    omp::HandEvaluator omp;
    std::vector<omp::Hand> ompCards;
    for (unsigned card = 0; card < 52; card++) ompCards.push_back(omp::Hand(card));
    run("OMPEval, from 7 cards", [&] {
        int64_t sum = 0;
        for (size_t h = 0; h < numHands; h++) {
            const uint8_t* x = c + h * 7;
            omp::Hand hand = omp::Hand::empty() + ompCards[x[0]] + ompCards[x[1]] + ompCards[x[2]] + ompCards[x[3]]
                + ompCards[x[4]] + ompCards[x[5]] + ompCards[x[6]];
            int64_t v = omp.evaluate(hand);
            results[h] = v;
            sum += v;
        }
        return sum;
    }, true);

    // Two Plus Two: 7 dependent lookups in a 130 MB state table (HandRanks.dat from generate_table).
    // Its cards are 1-52 in rank-major order, so card + 1
    {
        auto table = ReadFile(dataDir + "\\HandRanks.dat");
        const int* HR = reinterpret_cast<const int*>(table.data());
        run("Two Plus Two (HandRanks.dat, 130 MB), from 7 cards", [&] {
            int64_t sum = 0;
            for (size_t h = 0; h < numHands; h++) {
                const uint8_t* x = c + h * 7;
                int p = HR[53 + x[0] + 1];
                p = HR[p + x[1] + 1];
                p = HR[p + x[2] + 1];
                p = HR[p + x[3] + 1];
                p = HR[p + x[4] + 1];
                p = HR[p + x[5] + 1];
                int v = HR[p + x[6] + 1];
                results[h] = v;
                sum += v;
            }
            return sum;
        }, true);
    }

    // PokerHandEvaluator (phevaluator): suit hash -> flush table, otherwise a base-5 rank count hashed with a DP table.
    // Card id = rank * 4 + suit, rank 1 is the best hand
    run("PokerHandEvaluator (phevaluator) evaluate_7cards, from 7 cards", [&] {
        int64_t sum = 0;
        for (size_t h = 0; h < numHands; h++) {
            const uint8_t* x = c + h * 7;
            int v = evaluate_7cards(x[0], x[1], x[2], x[3], x[4], x[5], x[6]);
            results[h] = v;
            sum += v;
        }
        return sum;
    }, false);

    // Senzee: index52c7 maps the 52-bit card mask to a unique index 0 - 133,784,559, then one lookup in a 267 MB table.
    // The table generator was lost (see the blog post), so the table is filled here from OMPEval for every 7-card hand
    {
        std::vector<uint16_t> table(133784560);
        auto start = std::chrono::steady_clock::now();
        for (int a = 0; a < 52; a++)
        for (int b = a + 1; b < 52; b++)
        for (int d = b + 1; d < 52; d++)
        for (int e = d + 1; e < 52; e++)
        for (int f = e + 1; f < 52; f++)
        for (int g = f + 1; g < 52; g++)
        for (int k = g + 1; k < 52; k++) {
            uint64_t mask = (1ULL << a) | (1ULL << b) | (1ULL << d) | (1ULL << e) | (1ULL << f) | (1ULL << g) | (1ULL << k);
            table[index52c7(mask)] = omp.evaluate(omp::Hand::empty() + ompCards[a] + ompCards[b] + ompCards[d]
                + ompCards[e] + ompCards[f] + ompCards[g] + ompCards[k]);
        }
        std::printf("(Senzee table filled in %.1f s)\n",
                    std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count());
        run("Senzee index52c7 + 267 MB table, from 7 cards", [&] {
            int64_t sum = 0;
            for (size_t h = 0; h < numHands; h++) {
                const uint8_t* x = c + h * 7;
                uint64_t mask = (1ULL << x[0]) | (1ULL << x[1]) | (1ULL << x[2]) | (1ULL << x[3]) | (1ULL << x[4])
                    | (1ULL << x[5]) | (1ULL << x[6]);
                int v = table[index52c7(mask)];
                results[h] = v;
                sum += v;
            }
            return sum;
        }, true);
    }
    return 0;
}
