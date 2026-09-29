#pragma once
#include <array>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <optional>
#include <utility>

// A single producer and consumer use short locks to transfer ownership. No
// callback, sample conversion or disk access runs under this lock. Capacity is
// fixed for both command count and PCM weight; an overflowing producer fails.
template<class T, size_t Capacity, size_t MaximumWeight>
class BoundedAudioQueue {
    struct Entry { T value; size_t weight; };
    std::array<std::optional<Entry>,Capacity> slots;
    mutable std::mutex mutex;
    std::condition_variable available;
    size_t head=0, tail=0, count=0, weight=0;
    bool closed=false;
public:
    struct Statistics { size_t peak_items=0, peak_weight=0; uint64_t accepted=0, consumed=0, rejected=0, discarded=0; };
private:
    Statistics statistics;
public:
    bool TryPush(T value,size_t cost) {
        {
            std::lock_guard lock(mutex);
            if (closed || count==Capacity || cost>MaximumWeight-weight) { ++statistics.rejected; return false; }
            slots[tail].emplace(Entry{std::move(value),cost}); tail=(tail+1)%Capacity; ++count; weight+=cost;
            ++statistics.accepted;
            if(count>statistics.peak_items)statistics.peak_items=count;
            if(weight>statistics.peak_weight)statistics.peak_weight=weight;
        }
        available.notify_one(); return true;
    }
    bool WaitPop(T& value) {
        std::unique_lock lock(mutex);
        available.wait(lock,[&]{return count!=0 || closed;});
        if(!count)return false;
        auto& entry=*slots[head]; value=std::move(entry.value); weight-=entry.weight;
        slots[head].reset(); head=(head+1)%Capacity; --count; ++statistics.consumed; return true;
    }
    void Close() { { std::lock_guard lock(mutex); closed=true; } available.notify_all(); }
    void Abort() {
        { std::lock_guard lock(mutex); closed=true; statistics.discarded+=count;
          while(count) { slots[head].reset(); head=(head+1)%Capacity; --count; } weight=0; }
        available.notify_all();
    }
    Statistics Stats() const { std::lock_guard lock(mutex); return statistics; }
};
