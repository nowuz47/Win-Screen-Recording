#include "recording_timeline.h"
#include "audio_resample.h"
#include "audio_packet_clock.h"
#include <iostream>
#include <stdexcept>
static void Require(bool value) { if (!value) throw std::runtime_error("Assertion failed"); }
int main() {
    RecordingTimeline clock;
    Require(clock.Intersect(1,100).empty());
    clock.Start(100);
    clock.Pause(true,180); clock.Pause(true,190); clock.Pause(false,300);
    clock.Pause(true,340); clock.Pause(false,400); clock.Stop(460); clock.Stop(500);
    auto s=clock.Intersect(50,500);
    Require(s.size()==3 && s[0].begin==100 && s[0].end==180 && s[0].project==0);
    Require(s[1].begin==300 && s[1].end==340 && s[1].project==80);
    Require(s[2].begin==400 && s[2].end==460 && s[2].project==120);
    Require(clock.Duration(1000)==180 && !clock.Active());
    Require(clock.Intersect(180,300).empty() && clock.Intersect(460,900).empty());
    auto boundary=clock.Intersect(170,320);
    Require(boundary.size()==2 && boundary[0].project==70 && boundary[0].end==180 && boundary[1].project==80 && boundary[1].end==320);
    RecordingTimeline paused; paused.Start(100); paused.Pause(true,100); paused.Stop(100);
    Require(paused.Intersect(1,1000).empty() && paused.Duration(1000)==0);
    RecordingTimeline ongoing; ongoing.Start(10); ongoing.Pause(true,20);
    Require(ongoing.Intersect(20,1000).empty()); ongoing.Pause(false,50);
    Require(ongoing.Intersect(55,65)[0].project==15);
    bool rejected=false; try { ongoing.Pause(true,49); } catch (const std::logic_error&) { rejected=true; }
    Require(rejected && ongoing.Active());
    RecordingTimeline audio; audio.Start(1000);
    std::vector<float> ramp(960);
    for(size_t i=0;i<480;i++)ramp[i*2]=ramp[i*2+1]=static_cast<float>(i);
    auto a=MapAudioPacket(audio,1000,99000,480,ramp,{480,480});
    auto b=MapAudioPacket(audio,99000,197000,480,ramp,{480,480});
    Require(a.size()==1 && b.size()==1 && a[0].first_frame==0);
    Require(a[0].samples.size()/2==b[0].first_frame && b[0].first_frame+b[0].samples.size()/2==941);
    Require(std::abs(a[0].samples[400*2]-400.0/48000*10'000'000*480/98000)<.001);
    audio.Pause(true,197000); audio.Pause(false,297000);
    auto cut=MapAudioPacket(audio,187000,307000,480,ramp,{480,480});
    Require(cut.size()==2 && cut[0].first_frame+cut[0].samples.size()/2==cut[1].first_frame);
    audio.Stop(317000); Require(MapAudioPacket(audio,317000,417000,480,ramp,{480,480}).empty());
    std::cout << "PASS packet start/pause/resume/stop intersections and project clock\n";
    std::cout << "PASS clock-drift resampling without per-packet gaps, including pause and stop boundaries\n";
    AudioPacketClock exact;
    for (uint64_t i=0;i<6000;++i) {
        auto point=exact.Observe(1'000'000+static_cast<int64_t>(i)*100'000,i*480,480,false);
        Require(point.begin==1'000'000+static_cast<int64_t>(i)*100'000 && point.contiguous==(i!=0));
    }
    Require(exact.maximum_error_ticks==0);
    AudioPacketClock slow;
    int64_t last=0, current=0;
    for (uint64_t i=0;i<6000;++i) {
        current=slow.Observe(1'000'000+static_cast<int64_t>(i)*100'010,i*480,480,false).begin;
        if (i) Require(current-last>=99'900 && current-last<=100'100);
        last=current;
    }
    Require(std::abs(current-(1'000'000+5999LL*100'010))<3000);
    AudioPacketClock jitter;
    last=0;
    for (uint64_t i=0;i<6000;++i) {
        current=jitter.Observe(1'000'000+static_cast<int64_t>(i)*100'000+(i%2 ? 5000 : 0),i*480,480,false).begin;
        if(i)Require(current-last>=99'900 && current-last<=100'100);
        last=current;
    }
    Require(jitter.maximum_error_ticks<10'000);
    AudioPacketClock broken;
    last=0;
    for (uint64_t i=0;i<400;++i) {
        current=broken.Observe(1'000'000+static_cast<int64_t>(i)*97'700,i*480,480,false).begin;
        if(i)Require(current-last>=99'900 && current-last<=100'100);
        last=current;
    }
    Require(broken.maximum_error_ticks>500'000 && broken.limited_packets>0);
    auto reset=broken.Observe(70'000'000,0,480,true);
    Require(!reset.contiguous && reset.begin==70'000'000 && broken.epochs==2);
    rejected=false;try{broken.Observe(69'000'000,480,480,false);}catch(const std::invalid_argument&){rejected=true;}
    Require(rejected);
    std::cout << "PASS stable sample clock, 100ppm drift convergence, timestamp jitter, explicit excessive residual and reset\n";
}
