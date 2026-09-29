#include "bounded_audio_queue.h"
#include <future>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <thread>
static void Require(bool value) { if(!value)throw std::runtime_error("Queue assertion failed"); }
int main() {
    BoundedAudioQueue<std::unique_ptr<int>,4,10> queue;
    Require(queue.TryPush(std::make_unique<int>(1),6));
    Require(!queue.TryPush(std::make_unique<int>(9),5));
    Require(queue.TryPush(std::make_unique<int>(2),4));
    queue.Close(); Require(!queue.TryPush(std::make_unique<int>(9),0));
    std::unique_ptr<int> item;
    Require(queue.WaitPop(item) && *item==1);Require(queue.WaitPop(item) && *item==2);Require(!queue.WaitPop(item));
    auto stats=queue.Stats();Require(stats.peak_items==2 && stats.peak_weight==10 && stats.accepted==2 && stats.consumed==2 && stats.rejected==2 && stats.discarded==0);
    BoundedAudioQueue<int,2,10> commands;
    Require(commands.TryPush(1,0) && commands.TryPush(2,0) && !commands.TryPush(3,0));commands.Abort();
    int number=0;Require(!commands.WaitPop(number) && commands.Stats().discarded==2);
    BoundedAudioQueue<int,32,128> concurrent;
    auto reader=std::async(std::launch::async,[&] {
        int expected=0,value=0;
        while(concurrent.WaitPop(value))Require(value==expected++);
        return expected;
    });
    for(int i=0;i<10000;++i)while(!concurrent.TryPush(i,4))std::this_thread::yield();
    concurrent.Close();Require(reader.get()==10000);
    auto parallel_stats=concurrent.Stats();Require(parallel_stats.accepted==10000 && parallel_stats.consumed==10000 && parallel_stats.peak_items<=32 && parallel_stats.peak_weight<=128);
    BoundedAudioQueue<int,1,1> empty;
    auto waiter=std::async(std::launch::async,[&]{int value;return empty.WaitPop(value);});
    empty.Abort();Require(!waiter.get());
    std::cout<<"PASS bounded PCM weight, zero-weight command capacity, ownership, FIFO close-drain, abort and 10000 concurrent transfers\n";
}
