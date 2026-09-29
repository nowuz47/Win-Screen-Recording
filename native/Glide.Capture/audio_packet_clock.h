#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <stdexcept>

// WASAPI sample positions preserve the waveform; individual QPC timestamps may
// jitter. Slew the continuous sample clock toward QPC over two seconds, bounded
// to 1000ppm. Larger residuals are measured explicitly, never silently declared
// synchronized. This is not a pitch-preserving time-stretch for broken clocks.
class AudioPacketClock {
    bool initialized = false;
    uint64_t position = 0;
    uint32_t frames = 0;
    int64_t observed = 0, mapped = 0;
public:
    struct Boundary { int64_t begin; bool contiguous; };
    int64_t maximum_error_ticks = 0;
    uint64_t limited_packets = 0, epochs = 0;
    Boundary Observe(int64_t qpc, uint64_t device_position, uint32_t count, bool discontinuity) {
        if (qpc <= 0 || !count || count > 48000 || device_position > UINT64_MAX-count)
            throw std::invalid_argument("Invalid audio clock observation");
        const bool contiguous = initialized && !discontinuity && device_position == position+frames;
        if (initialized && qpc <= observed) throw std::invalid_argument("Audio QPC moved backwards");
        int64_t next = qpc;
        if (contiguous) {
            const double nominal = static_cast<double>(frames)*10'000'000/48000;
            const double difference = static_cast<double>(qpc-mapped)-nominal;
            const double requested = difference/20'000'000;
            if (std::abs(requested) > .001) ++limited_packets;
            next = mapped+static_cast<int64_t>(std::llround(nominal*(1+std::clamp(requested,-.001,.001))));
            maximum_error_ticks = std::max(maximum_error_ticks,std::abs(qpc-next));
        } else ++epochs;
        initialized = true; observed = qpc; mapped = next; position = device_position; frames = count;
        return {next,contiguous};
    }
};
