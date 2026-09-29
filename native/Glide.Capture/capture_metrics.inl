// Bounded timing histograms contain no pixels, titles or input coordinates.
struct CaptureTiming {
    static constexpr int64_t limits[]{100,250,500,1000,2000,4000,8000,16000,33334,50000,100000,250000,500000,1000000,5000000};
    uint64_t buckets[16]{}, count = 0;
    int64_t total = 0, maximum = 0;
    void Add(int64_t ticks) {
        const auto us = std::max<int64_t>(0, ticks / 10);
        ++count; total += us; maximum = std::max(maximum, us);
        size_t index = 0; while (index < 15 && us > limits[index]) ++index;
        ++buckets[index];
    }
    int64_t Percentile(unsigned percent) const {
        if (!count) return 0;
        uint64_t seen = 0, target = (count * percent + 99) / 100;
        for (size_t i = 0; i < 16; ++i) {
            seen += buckets[i];
            if (seen >= target) return i == 15 ? maximum : std::min(maximum, limits[i]);
        }
        return maximum;
    }
    void Write(std::ostream& out) const {
        out << "{\"count\":" << count << ",\"meanUs\":" << (count ? total / static_cast<int64_t>(count) : 0)
            << ",\"p95UpperUs\":" << Percentile(95) << ",\"p99UpperUs\":" << Percentile(99)
            << ",\"maxUs\":" << maximum << "}";
    }
};
