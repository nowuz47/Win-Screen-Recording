struct AudioSelection {
    bool microphone = false, system = false;
    std::wstring microphone_id, system_id;
    bool Enabled() const { return microphone || system; }
    static AudioSelection Read(const GlideAudioOptions* value) {
        AudioSelection result;
        if (!value) return result;
        if (value->size != sizeof(GlideAudioOptions) || value->reserved != 0) throw hresult_error(E_INVALIDARG);
        auto copy = [](const wchar_t* id) {
            if (!id) return std::wstring{};
            const auto length = wcsnlen_s(id,32768);
            if (length == 32768) throw hresult_error(E_INVALIDARG);
            return std::wstring(id,length);
        };
        result.microphone = value->microphone != nullptr; result.system = value->system != nullptr;
        result.microphone_id = copy(value->microphone); result.system_id = copy(value->system);
        return result;
    }
};

// The acquisition worker owns WASAPI and promptly releases every device packet.
// A bounded ownership queue feeds a separate conversion/storage worker. Only
// selected endpoints are captured; overflow is an explicit recording failure.
class AudioCapture {
    static constexpr uint32_t rate = 48000, channels = 2, frame_bytes = 4;
    static constexpr uint64_t segment_frames = rate * 5, maximum_frames = rate * 1800ULL;
    class Track {
        fs::path directory;
        std::ofstream& journal;
        std::string role, filename;
        HANDLE file = INVALID_HANDLE_VALUE;
        uint64_t written = 0, segment_begin = 0;
        uint32_t sequence = 0;
        void Write(const void* bytes, DWORD length) {
            DWORD actual = 0;
            if (!WriteFile(file,bytes,length,&actual,nullptr)) throw_last_error();
            if (actual != length) throw hresult_error(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT));
        }
        void Open() {
            char name[80]; sprintf_s(name,"%s-%06u.wav",role.c_str(),sequence++); filename = name;
            file = CreateFileW((directory/filename).c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
            if (file == INVALID_HANDLE_VALUE) throw_last_error();
            segment_begin = written;
            std::array<uint8_t,44> empty{}; Write(empty.data(),static_cast<DWORD>(empty.size()));
        }
        void Commit() {
            if (file == INVALID_HANDLE_VALUE) return;
            const auto count = static_cast<uint32_t>(written-segment_begin);
            if (!count) { CloseHandle(file); file = INVALID_HANDLE_VALUE; return; }
            const uint32_t bytes = count*frame_bytes;
            std::array<uint8_t,44> header{};
            memcpy(header.data(),"RIFF",4); memcpy(header.data()+8,"WAVEfmt ",8); memcpy(header.data()+36,"data",4);
            auto u32 = [&](size_t at,uint32_t v) { memcpy(header.data()+at,&v,4); };
            auto u16 = [&](size_t at,uint16_t v) { memcpy(header.data()+at,&v,2); };
            u32(4,bytes+36); u32(16,16); u16(20,1); u16(22,channels); u32(24,rate);
            u32(28,rate*frame_bytes); u16(32,frame_bytes); u16(34,16); u32(40,bytes);
            LARGE_INTEGER offset{};
            if (!SetFilePointerEx(file,offset,nullptr,FILE_BEGIN)) throw_last_error();
            Write(header.data(),static_cast<DWORD>(header.size()));
            if (!FlushFileBuffers(file)) throw_last_error();
            CloseHandle(file); file = INVALID_HANDLE_VALUE;
            journal << "{\"track\":\"" << role << "\",\"file\":\"" << filename << "\",\"startFrame\":" << segment_begin << ",\"frames\":" << count << "}\n";
            journal.flush(); FlushFile(directory/L"audio-journal.jsonl");
        }
        void Append(const float* samples, uint64_t count) {
            std::array<int16_t,4096*channels> pcm{};
            while (count) {
                if (file == INVALID_HANDLE_VALUE) Open();
                const auto n = static_cast<uint32_t>(std::min<uint64_t>({count,4096,segment_frames-(written-segment_begin)}));
                for (uint32_t i = 0; i < n*channels; ++i) {
                    float value = samples ? samples[i] : 0;
                    if (!std::isfinite(value)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
                    pcm[i] = static_cast<int16_t>(std::clamp(std::lround(std::clamp(value,-1.0f,1.0f)*32768.0f),-32768L,32767L));
                }
                Write(pcm.data(),n*frame_bytes); written += n; count -= n;
                if (samples) samples += static_cast<size_t>(n)*channels;
                if (written-segment_begin == segment_frames) Commit();
            }
        }
    public:
        uint64_t gap_frames = 0, overlap_frames = 0, silent_packet_frames = 0, packets = 0, discontinuities = 0;
        Track(fs::path folder,std::ofstream& log,std::string track) : directory(std::move(folder)),journal(log),role(std::move(track)) {}
        ~Track() { if (file != INVALID_HANDLE_VALUE) CloseHandle(file); }
        uint64_t Frames() const { return written; }
        void SilenceUntil(uint64_t end) {
            if (end > maximum_frames) throw hresult_error(E_INVALIDARG);
            if (end > written) { gap_frames += end-written; Append(nullptr,end-written); }
        }
        void Packet(uint64_t begin,const float* samples,uint32_t count) {
            if (begin > maximum_frames || count > maximum_frames-begin) throw hresult_error(E_INVALIDARG);
            SilenceUntil(begin);
            const auto skipped = std::min<uint64_t>(count,written-begin);
            overlap_frames += skipped; count -= static_cast<uint32_t>(skipped);
            if (samples) samples += skipped*channels; else silent_packet_frames += count;
            Append(samples,count);
        }
        void Finish(uint64_t end) {
            // Keep any already committed audio past the final video sample intact.
            // Import trims the independently committed tracks to common coverage.
            SilenceUntil(end); Commit();
        }
        void Metrics(std::ostream& out,const AudioPacketClock& mapping) const {
            out << "{\"frames\":" << written << ",\"packets\":" << packets << ",\"gapFilledFrames\":" << gap_frames
                << ",\"overlapTrimmedFrames\":" << overlap_frames << ",\"silentPacketFrames\":" << silent_packet_frames
                << ",\"discontinuities\":" << discontinuities << ",\"maximumClockResidual100ns\":" << mapping.maximum_error_ticks
                << ",\"clockCorrectionLimitedPackets\":" << mapping.limited_packets << ",\"clockEpochs\":" << mapping.epochs << "}";
        }
    };
    struct Packet { int64_t qpc; uint32_t frames; std::vector<float> samples; };
    struct DiagnosticPacket { int64_t qpc, mapped, received; uint64_t position, offset; uint32_t frames; DWORD flags; };
    enum class WorkKind { packet, heartbeat, flush };
    struct Work {
        WorkKind kind = WorkKind::packet;
        bool microphone = false;
        int64_t qpc = 0, received = 0;
        uint64_t position = 0, safe_frame = 0;
        uint32_t frames = 0;
        DWORD flags = 0;
        std::vector<float> samples;
    };
    struct Endpoint {
        com_ptr<IAudioClient> client;
        com_ptr<IAudioCaptureClient> capture;
        bool running = false, microphone = false;
        int64_t last_packet = 0, last_heartbeat = 0, last_drain = 0;
        ~Endpoint() { if (running && client) client->Stop(); }
    };
    struct StoredEndpoint {
        std::unique_ptr<Track> track;
        bool microphone = false;
        std::optional<Packet> pending;
        AudioPacketClock mapping;
        std::vector<float> diagnostic_pcm;
        std::vector<DiagnosticPacket> diagnostic_packets;
        uint64_t diagnostic_omitted = 0;
    };
    fs::path directory;
    AudioSelection selection;
    std::shared_ptr<RecordingTimeline> clock;
    std::thread worker;
    std::mutex stop_mutex;
    std::atomic<bool> stopping{false};
    std::atomic<int64_t> final_frames{0};
    std::atomic<HRESULT> error{S_OK};
    std::promise<void> initialized;
    // 768000 bytes is 2 seconds of stereo float for one track, 1 second for two.
    BoundedAudioQueue<Work,512,768000> queue;
    std::ofstream packet_trace;
    bool trace_pcm = false, diagnostics = false;
    DWORD mmcss_error = 0;
    std::wstring test_fault;
    bool fault_triggered = false;
    int64_t maximum_write_work_ticks = 0;
    int64_t maximum_queue_wait_ticks = 0;
    std::atomic<int64_t> maximum_drain_gap_ticks{0}, maximum_buffer_owned_ticks{0};
    void Fail(HRESULT code) { HRESULT expected=S_OK; error.compare_exchange_strong(expected,code); }
    void Enqueue(Work work) {
        const auto cost = static_cast<size_t>(work.frames)*channels*sizeof(float);
        if (!queue.TryPush(std::move(work),cost)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));
    }
    void SaveDiagnostic(const StoredEndpoint& endpoint) {
        if (!trace_pcm || !endpoint.track) return;
        const auto role = endpoint.microphone ? L"microphone" : L"system";
        std::ofstream raw(directory/(std::wstring(role)+L"-raw-48000.f32"),std::ios::binary);
        raw.exceptions(std::ios::badbit|std::ios::failbit);
        raw.write(reinterpret_cast<const char*>(endpoint.diagnostic_pcm.data()),static_cast<std::streamsize>(endpoint.diagnostic_pcm.size()*sizeof(float)));
        std::ofstream trace(directory/(std::wstring(role)+L"-raw-packets.jsonl"));
        trace.exceptions(std::ios::badbit|std::ios::failbit);
        for (const auto& p : endpoint.diagnostic_packets)
            trace << "{\"qpc100ns\":" << p.qpc << ",\"mappedQpc100ns\":" << p.mapped << ",\"received100ns\":" << p.received << ",\"devicePosition\":" << p.position
                << ",\"offsetFrames\":" << p.offset << ",\"frames\":" << p.frames << ",\"flags\":" << p.flags << "}\n";
        std::ofstream info(directory/(std::wstring(role)+L"-raw-info.json"));
        info << "{\"schemaVersion\":1,\"sampleRate\":48000,\"channels\":2,\"frames\":" << endpoint.diagnostic_pcm.size()/2
            << ",\"omittedPackets\":" << endpoint.diagnostic_omitted << ",\"scope\":\"Bounded opt-in accepted PCM before time mapping; diagnostic only\"}";
    }
    void Emit(StoredEndpoint& endpoint,int64_t end,const std::array<float,2>& following) {
        if (!endpoint.pending) return;
        const auto& packet = *endpoint.pending;
        for (const auto& block : MapAudioPacket(*clock,packet.qpc,end,packet.frames,packet.samples,following))
            endpoint.track->Packet(block.first_frame,block.samples.data(),static_cast<uint32_t>(block.samples.size()/channels));
        endpoint.pending.reset();
    }
    void FlushPending(StoredEndpoint& endpoint) {
        if (!endpoint.pending) return;
        const auto& p = *endpoint.pending;
        std::array<float,2> last{};
        if (!p.samples.empty()) last = {p.samples[p.samples.size()-2],p.samples.back()};
        Emit(endpoint,p.qpc+(static_cast<int64_t>(p.frames)*10'000'000+rate-1)/rate,last);
    }
    void Consume(StoredEndpoint& endpoint,Work&& work) {
        if (work.kind == WorkKind::flush) { FlushPending(endpoint); return; }
        if (work.kind == WorkKind::heartbeat) {
            if (endpoint.pending && work.received-endpoint.pending->qpc > 1'000'000) FlushPending(endpoint);
            endpoint.track->SilenceUntil(work.safe_frame); return;
        }
        if (!fault_triggered && !test_fault.empty() && endpoint.track->Frames() >= segment_frames) {
            fault_triggered = true;
            // Explicit test-only fault at the storage boundary, after a committed
            // 5-second WAV. No fault is enabled by diagnostics alone.
            { std::ofstream checkpoint(directory/L"audio-test-checkpoint.json");checkpoint.exceptions(std::ios::badbit|std::ios::failbit);
              checkpoint << "{\"committedFrames\":" << endpoint.track->Frames() << ",\"beforeFault100ns\":" << Clock100ns() << "}"; }
            const auto deadline=Clock100ns()+15'000'000;
            while(!fs::exists(directory/L"audio-test-continue")) {
                if(Clock100ns()>deadline)throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                Sleep(5);
            }
            if (test_fault == L"stall") Sleep(3000);
            else if (test_fault == L"write") throw hresult_error(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT));
        }
        const auto mapped = endpoint.mapping.Observe(work.qpc,work.position,work.frames,(work.flags&AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY)!=0);
        if (trace_pcm) {
            if (endpoint.diagnostic_pcm.size()+static_cast<size_t>(work.frames)*channels<=rate*channels*20 && endpoint.diagnostic_packets.size()<6000) {
                endpoint.diagnostic_packets.push_back({work.qpc,mapped.begin,work.received,work.position,endpoint.diagnostic_pcm.size()/channels,work.frames,work.flags});
                if(work.samples.empty())endpoint.diagnostic_pcm.insert(endpoint.diagnostic_pcm.end(),static_cast<size_t>(work.frames)*channels,0.0f);
                else endpoint.diagnostic_pcm.insert(endpoint.diagnostic_pcm.end(),work.samples.begin(),work.samples.end());
            } else ++endpoint.diagnostic_omitted;
        }
        if(packet_trace)packet_trace << "{\"role\":\"" << (work.microphone ? "microphone" : "system") << "\",\"qpc100ns\":" << work.qpc
            << ",\"received100ns\":" << work.received << ",\"devicePosition\":" << work.position << ",\"frames\":" << work.frames << ",\"flags\":" << work.flags << "}\n";
        ++endpoint.track->packets;
        if(work.flags&AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY)++endpoint.track->discontinuities;
        if(work.flags&AUDCLNT_BUFFERFLAGS_SILENT)endpoint.track->silent_packet_frames+=work.frames;
        if(endpoint.pending) {
            if(mapped.contiguous)Emit(endpoint,mapped.begin,work.samples.empty() ? std::array<float,2>{} : std::array<float,2>{work.samples[0],work.samples[1]});
            else FlushPending(endpoint);
        }
        endpoint.pending = Packet{mapped.begin,work.frames,std::move(work.samples)};
    }
    void WriteLoop(std::promise<void>& ready) noexcept {
        bool signalled = false;
        std::ofstream journal;
        StoredEndpoint mic,system; mic.microphone=true;
        std::array<StoredEndpoint*,2> endpoints{&mic,&system};
        try {
            journal.open(directory/L"audio-journal.jsonl",std::ios::binary);
            journal.exceptions(std::ios::badbit|std::ios::failbit);
            if(diagnostics)packet_trace.open(directory/L"audio-packets.jsonl",std::ios::binary);
            for(auto endpoint:endpoints) {
                if(!(endpoint->microphone ? selection.microphone : selection.system))continue;
                endpoint->track=std::make_unique<Track>(directory,journal,endpoint->microphone ? "microphone" : "system");
                if(trace_pcm){endpoint->diagnostic_pcm.reserve(rate*channels*20);endpoint->diagnostic_packets.reserve(6000);}
            }
            { std::ofstream info(directory/L"audio-info.json");info.exceptions(std::ios::badbit|std::ios::failbit);
              info << "{\"schemaVersion\":1,\"sampleRate\":48000,\"channels\":2,\"format\":\"pcm_s16le\",\"microphone\":"
                   << (selection.microphone ? "true" : "false") << ",\"system\":" << (selection.system ? "true" : "false") << "}";
              info.close();FlushFile(directory/L"audio-info.json"); }
            ready.set_value();signalled=true;
            Work work;
            while(queue.WaitPop(work)) {
                const auto started=Clock100ns();
                if(work.received>0)maximum_queue_wait_ticks=std::max(maximum_queue_wait_ticks,started-work.received);
                Consume(work.microphone ? mic : system,std::move(work));
                maximum_write_work_ticks=std::max(maximum_write_work_ticks,Clock100ns()-started);
            }
            for(auto endpoint:endpoints)if(endpoint->track) {
                FlushPending(*endpoint);
                endpoint->track->Finish(FAILED(error.load()) ? endpoint->track->Frames() : static_cast<uint64_t>(std::max<int64_t>(0,final_frames)));
            }
        } catch (...) {
            Fail(to_hresult());queue.Abort();
            if(!signalled)ready.set_exception(std::current_exception());
            // An incomplete WAV is not journaled after a failed write. Previously
            // committed WAVs and their journal lines stay untouched.
        }
        try {
            for(auto endpoint:endpoints)SaveDiagnostic(*endpoint);
            const auto stats=queue.Stats();
            std::ofstream metrics(directory/L"audio-performance.json");
            metrics << "{\"schemaVersion\":2,\"sampleRate\":48000,\"writerQueue\":{\"maximumItems\":512,\"maximumPcmBytes\":768000,\"peakItems\":" << stats.peak_items
                << ",\"peakPcmBytes\":" << stats.peak_weight << ",\"accepted\":" << stats.accepted << ",\"consumed\":" << stats.consumed << ",\"rejected\":" << stats.rejected
                << ",\"discarded\":" << stats.discarded << ",\"maximumWork100ns\":" << maximum_write_work_ticks << ",\"captureMmcssError\":" << mmcss_error
                << ",\"maximumQueueWait100ns\":" << maximum_queue_wait_ticks << ",\"maximumCaptureDrainGap100ns\":" << maximum_drain_gap_ticks.load()
                << ",\"maximumDeviceBufferOwned100ns\":" << maximum_buffer_owned_ticks.load()
                << ",\"faultInjected\":" << (fault_triggered ? "true" : "false") << "},\"tracks\":{";
            if(mic.track){metrics << "\"microphone\":";mic.track->Metrics(metrics,mic.mapping);}
            if(mic.track&&system.track)metrics << ',';
            if(system.track){metrics << "\"system\":";system.track->Metrics(metrics,system.mapping);}
            metrics << "}}";
        } catch (...) { Fail(to_hresult()); }
    }
    void InitializeEndpoint(Endpoint& e,IMMDeviceEnumerator* devices,const std::wstring& id,bool microphone) {
        e.microphone=microphone;
        com_ptr<IMMDevice> device;
        if(id.empty())check_hresult(devices->GetDefaultAudioEndpoint(microphone ? eCapture : eRender,eConsole,device.put()));
        else check_hresult(devices->GetDevice(id.c_str(),device.put()));
        EDataFlow flow{};check_hresult(device.as<IMMEndpoint>()->GetDataFlow(&flow));
        if(flow!=(microphone ? eCapture : eRender))throw hresult_error(E_INVALIDARG);
        check_hresult(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,e.client.put_void()));
        WAVEFORMATEX format{WAVE_FORMAT_IEEE_FLOAT,channels,rate,rate*channels*4,channels*4,32,0};
        DWORD flags=AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if(!microphone)flags|=AUDCLNT_STREAMFLAGS_LOOPBACK;
        check_hresult(e.client->Initialize(AUDCLNT_SHAREMODE_SHARED,flags,2'000'000,0,&format,nullptr));
        check_hresult(e.client->GetService(__uuidof(IAudioCaptureClient),e.capture.put_void()));
    }
    void Drain(Endpoint& endpoint) {
        const auto started=Clock100ns();
        if(endpoint.last_drain>0)maximum_drain_gap_ticks=std::max(maximum_drain_gap_ticks.load(),started-endpoint.last_drain);
        endpoint.last_drain=started;
        for(int bounded=0;bounded<64;++bounded) {
            uint32_t available=0;check_hresult(endpoint.capture->GetNextPacketSize(&available));if(!available)break;
            BYTE* data=nullptr;uint32_t count=0;DWORD flags=0;uint64_t position=0,qpc=0;
            check_hresult(endpoint.capture->GetBuffer(&data,&count,&flags,&position,&qpc));if(!count)break;
            const auto acquired=Clock100ns();
            Work work;work.microphone=endpoint.microphone;work.position=position;work.frames=count;work.flags=flags;
            try {
                if(count>rate || qpc>static_cast<uint64_t>(INT64_MAX) || (flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR))throw hresult_error(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
                if(!(flags&AUDCLNT_BUFFERFLAGS_SILENT)) {
                    if(!data)throw hresult_error(E_POINTER);
                    work.samples.resize(static_cast<size_t>(count)*channels);memcpy(work.samples.data(),data,work.samples.size()*sizeof(float));
                }
            }catch(...){endpoint.capture->ReleaseBuffer(count);throw;}
            check_hresult(endpoint.capture->ReleaseBuffer(count));
            maximum_buffer_owned_ticks=std::max(maximum_buffer_owned_ticks.load(),Clock100ns()-acquired);
            work.qpc=static_cast<int64_t>(qpc);work.received=Clock100ns();
            const auto end=work.qpc+(static_cast<int64_t>(count)*10'000'000+rate-1)/rate;
            if(work.qpc<=0 || end>work.received+500'000 || work.qpc<work.received-50'000'000)throw hresult_error(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
            endpoint.last_packet=work.received;Enqueue(std::move(work));
        }
    }
    void Run() noexcept {
        bool signalled=false;
        std::thread writer;
        std::promise<void> writer_ready;
        try {
            Apartment apartment;
            auto devices=create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
            fs::create_directory(directory);
            wchar_t option[16]{};
            diagnostics=GetEnvironmentVariableW(L"GLIDE_DEV_DIAGNOSTICS",option,16)==1 && option[0]==L'1';
            trace_pcm=GetEnvironmentVariableW(L"GLIDE_AUDIO_TRACE_PCM",option,16)==1 && option[0]==L'1';
            if(diagnostics) {
                const auto length=GetEnvironmentVariableW(L"GLIDE_AUDIO_TEST_FAULT",option,16);
                if(length>0 && length<16 && (std::wstring(option)==L"stall" || std::wstring(option)==L"write"))test_fault=option;
            }
            Endpoint mic,system;
            if(selection.microphone)InitializeEndpoint(mic,devices.get(),selection.microphone_id,true);
            if(selection.system)InitializeEndpoint(system,devices.get(),selection.system_id,false);
            std::array<Endpoint*,2> endpoints{&mic,&system};
            DWORD task_index=0;
            struct Priority { HANDLE value;~Priority(){if(value)AvRevertMmThreadCharacteristics(value);} } priority{AvSetMmThreadCharacteristicsW(L"Audio",&task_index)};
            if(!priority.value)mmcss_error=GetLastError();
            auto ready=writer_ready.get_future();writer=std::thread([&]{WriteLoop(writer_ready);});ready.get();
            initialized.set_value();signalled=true;
            while(!stopping && SUCCEEDED(error.load())) {
                const bool active=clock->Active();
                for(auto endpoint:endpoints) {
                    if(!endpoint->client)continue;
                    if(active && !endpoint->running) {
                        check_hresult(endpoint->client->Reset());check_hresult(endpoint->client->Start());
                        endpoint->running=true;endpoint->last_packet=Clock100ns();endpoint->last_drain=endpoint->last_packet;
                    }
                    if(endpoint->running)Drain(*endpoint);
                    if(!active && endpoint->running) {
                        check_hresult(endpoint->client->Stop());endpoint->running=false;
                        Work flush;flush.kind=WorkKind::flush;flush.microphone=endpoint->microphone;Enqueue(std::move(flush));
                    }
                    if(active) {
                        const auto now=Clock100ns();
                        if(endpoint->microphone && now-endpoint->last_packet>5'000'000)throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                        if(now-endpoint->last_heartbeat>=500'000) {
                            const auto safe=std::max<int64_t>(0,clock->Duration(now)-2'000'000);
                            Work tick;tick.kind=WorkKind::heartbeat;tick.microphone=endpoint->microphone;tick.received=now;
                            tick.safe_frame=static_cast<uint64_t>(std::min<int64_t>(safe*rate/10'000'000,maximum_frames));
                            Enqueue(std::move(tick));endpoint->last_heartbeat=now;
                        }
                    }
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(3));
            }
            if(SUCCEEDED(error.load()))for(auto endpoint:endpoints)if(endpoint->client && endpoint->running) {
                Drain(*endpoint);check_hresult(endpoint->client->Stop());endpoint->running=false;
            }
        }catch(...) {
            Fail(to_hresult());
            if(!signalled)initialized.set_exception(std::current_exception());
        }
        queue.Close();if(writer.joinable())writer.join();
        if(FAILED(error.load()))try {
            std::ofstream report(directory/L"audio-error.json");report << "{\"hresult\":" << error.load() << "}";
        }catch(...){}
    }
public:
    AudioCapture(fs::path folder,AudioSelection selected,std::shared_ptr<RecordingTimeline> timeline)
        :directory(std::move(folder)),selection(std::move(selected)),clock(std::move(timeline)){}
    ~AudioCapture(){Finish(0);}
    void Start(){auto future=initialized.get_future();worker=std::thread([this]{Run();});future.get();}
    HRESULT Error()const{return error;}
    HRESULT Finish(int64_t duration_ticks) {
        std::lock_guard lock(stop_mutex);
        final_frames=(std::clamp<int64_t>(duration_ticks,0,18'000'000'000)*rate+5'000'000)/10'000'000;
        stopping=true;if(worker.joinable())worker.join();return error;
    }
};
