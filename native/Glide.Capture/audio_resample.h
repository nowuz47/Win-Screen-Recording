#pragma once
#include "recording_timeline.h"
#include <array>
#include <cmath>

struct AudioBlock { uint64_t first_frame; std::vector<float> samples; };
inline std::vector<AudioBlock> MapAudioPacket(const RecordingTimeline& clock, int64_t begin, int64_t end,
    uint32_t count, const std::vector<float>& samples, const std::array<float,2>& following) {
    constexpr int64_t rate = 48000, ticks = 10'000'000, limit = rate*1800;
    if (!count || count > rate || end <= begin || (!samples.empty() && samples.size() != static_cast<size_t>(count)*2)
        || end-begin > static_cast<int64_t>(count)*ticks/rate*2)
        throw std::invalid_argument("Invalid audio packet interval");
    std::vector<AudioBlock> result;
    for (const auto& span : clock.Intersect(begin,end)) {
        int64_t first = std::clamp<int64_t>((span.project*rate+ticks-1)/ticks,0,limit);
        int64_t last = std::clamp<int64_t>(((span.project+span.end-span.begin)*rate+ticks-1)/ticks,0,limit);
        if (last <= first) continue;
        AudioBlock block{static_cast<uint64_t>(first),std::vector<float>(static_cast<size_t>(last-first)*2)};
        for (int64_t output = first; output < last; ++output) {
            const double relative = static_cast<double>(span.begin-begin) + static_cast<double>(output)*ticks/rate-span.project;
            const double input = std::clamp(relative*count/static_cast<double>(end-begin),0.0,static_cast<double>(count));
            const auto index = std::min<uint32_t>(count-1,static_cast<uint32_t>(input));
            const float fraction = static_cast<float>(input-index);
            for (size_t channel = 0; channel < 2; ++channel) {
                const float a = samples.empty() ? 0 : samples[index*2+channel];
                const float b = index+1 < count ? (samples.empty() ? 0 : samples[(index+1)*2+channel]) : following[channel];
                block.samples[static_cast<size_t>(output-first)*2+channel] = a+(b-a)*fraction;
            }
        }
        result.push_back(std::move(block));
    }
    return result;
}
