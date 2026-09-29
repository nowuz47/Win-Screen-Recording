#pragma once
#include <algorithm>
#include <cstdint>
#include <mutex>
#include <stdexcept>
#include <vector>

// All times are QPC converted to 100ns, including WASAPI GetBuffer timestamps.
// A packet may straddle start, pause, resume, or stop. Return only the active
// intersections, each mapped onto the same pause-free project clock as video.
struct RecordingSpan { int64_t begin, end, project; };
class RecordingTimeline {
    mutable std::mutex mutex;
    std::vector<RecordingSpan> spans;
    bool active = false, ended = false;
    int64_t last_change = 0;
public:
    void Start(int64_t now) {
        std::lock_guard lock(mutex);
        if (!spans.empty() || now <= 0) throw std::logic_error("Invalid recording clock start");
        spans.push_back({now,0,0}); last_change = now; active = true;
    }
    void Pause(bool value, int64_t now) {
        std::lock_guard lock(mutex);
        if (spans.empty() || ended || now < last_change) throw std::logic_error("Invalid recording clock transition");
        if (value == !active) return;
        if (value) spans.back().end = now;
        else {
            if (spans.size() >= 4096) throw std::length_error("Too many recording clock transitions");
            const auto& previous = spans.back();
            spans.push_back({now,0,previous.project+previous.end-previous.begin});
        }
        active = !value; last_change = now;
    }
    void Stop(int64_t now) {
        std::lock_guard lock(mutex);
        if (ended || spans.empty()) return;
        if (now < last_change) throw std::logic_error("Recording clock moved backwards");
        if (active) spans.back().end = now;
        active = false; ended = true; last_change = now;
    }
    bool Active() const { std::lock_guard lock(mutex); return active; }
    int64_t Duration(int64_t now) const {
        std::lock_guard lock(mutex);
        if (spans.empty()) return 0;
        const auto& s = spans.back();
        return s.project + std::max<int64_t>(0,(active ? now : s.end)-s.begin);
    }
    std::vector<RecordingSpan> Intersect(int64_t begin, int64_t end) const {
        std::lock_guard lock(mutex);
        std::vector<RecordingSpan> result;
        if (end <= begin || spans.empty()) return result;
        auto it = std::upper_bound(spans.begin(),spans.end(),begin,[](int64_t t,const RecordingSpan& s) { return t < s.begin; });
        if (it != spans.begin()) --it;
        for (; it != spans.end() && it->begin < end; ++it) {
            auto left = std::max(begin,it->begin), right = std::min(end,it->end ? it->end : end);
            if (right > left) result.push_back({left,right,it->project+left-it->begin});
        }
        return result;
    }
};
