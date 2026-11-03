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

int main(int argc, char** argv)
{
    std::string dataDir = argc > 1 ? argv[1] : "..\\data";
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
