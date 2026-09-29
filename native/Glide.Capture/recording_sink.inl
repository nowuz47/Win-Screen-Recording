class RecordingSink : public IRecording {
    std::shared_ptr<CaptureSource> source;
    fs::path directory;
    uint32_t fps;
    bool owns_source;
    std::function<GlideLivePose()> pose;
    std::thread worker;
    std::mutex stop_mutex, clock_mutex;
    std::atomic<bool> stopping{false}, paused{false};
    std::atomic<uint32_t> state{1}, width{0}, height{0};
    std::atomic<uint64_t> frames{0}, dropped{0};
    std::atomic<int64_t> duration{0};
    std::atomic<HRESULT> failure{S_OK};
    int64_t origin = 0, paused_at = 0, paused_ticks = 0;
    uint64_t clock_generation = 0;
    std::promise<void> initialized;
    AudioSelection audio_selection;
    std::shared_ptr<RecordingTimeline> audio_clock = std::make_shared<RecordingTimeline>();
    std::unique_ptr<AudioCapture> audio;
public:
    RecordingSink(std::shared_ptr<CaptureSource> s, fs::path path, uint32_t rate, bool owns, std::function<GlideLivePose()> camera = {}, AudioSelection selected = {})
        : source(std::move(s)), directory(std::move(path)), fps(rate), owns_source(owns), pose(std::move(camera)), audio_selection(std::move(selected)) {}
    ~RecordingSink() override { Stop(); }
    void Start() override { auto future = initialized.get_future(); worker = std::thread([this] { Run(); }); future.get(); }
    int32_t Stop() override {
        std::lock_guard lock(stop_mutex); stopping = true;
        { std::lock_guard clock_lock(clock_mutex); audio_clock->Stop(Clock100ns()); }
        if (worker.joinable()) worker.join();
        if (owns_source) source->Stop();
        return failure;
    }
    int32_t Pause(bool value) override {
        std::lock_guard lock(clock_mutex);
        if (state != 1 || stopping) return E_UNEXPECTED;
        if (paused == value) return S_OK;
        int64_t now = Clock100ns();
        try { audio_clock->Pause(value,now); } catch (...) { return to_hresult(); }
        if (value) paused_at = now; else paused_ticks += now - paused_at;
        paused = value; ++clock_generation; return S_OK;
    }
    void Stats(GlideCaptureStats& out) const override {
        out.state = state; out.frames = frames; out.dropped = dropped; out.duration_us = duration;
        out.width = width; out.height = height; out.error = failure;
    }
private:
    void Run() noexcept {
        bool signalled = false, owns_directory = false;
        // Finalize an interrupted segment before MFShutdown/CoUninitialize.
        std::unique_ptr<Apartment> apartment;
        std::unique_ptr<MediaRuntime> media;
        std::unique_ptr<VideoWriter> writer;
        com_ptr<ID3D11Texture2D> staging;
        std::vector<uint8_t> pixels;
        std::ofstream pointers, journal, camera, timing_trace;
        CaptureTiming readback_time, encode_time, finalize_time, create_time, source_age;
        uint64_t repeated_source = 0, previous_source = 0;
        uint64_t cached_sequence = 0, cached_epoch = 0, reused_pixels = 0;
        auto saveMetrics = [&] {
            if (!owns_directory) return;
            std::ofstream out(directory / L"capture-performance.json", std::ios::binary);
            out.exceptions(std::ios::badbit | std::ios::failbit);
            out << "{\"schemaVersion\":2,\"units\":\"microseconds\",\"percentiles\":\"bounded histogram upper bounds\",\"readback\":";
            readback_time.Write(out); out << ",\"encode\":"; encode_time.Write(out);
            out << ",\"finalizeAndFlush\":"; finalize_time.Write(out); out << ",\"createWriter\":"; create_time.Write(out);
            out << ",\"sourceAge\":"; source_age.Write(out);
            out << ",\"source\":"; source->WriteMetrics(out);
            out << ",\"repeatedSourceSamples\":" << repeated_source << ",\"pixelCacheHits\":" << reused_pixels << ",\"encodedFrames\":" << frames
                << ",\"schedulerMissedFrames\":" << dropped << "}";
        };
        int64_t segment_start = 0, last_end = 0;
        std::string segment_name;
        auto finalize = [&] {
            if (!writer) return;
            const auto began = Clock100ns();
            writer->Finish(); writer.reset(); pointers.flush(); if (pose) camera.flush();
            FlushFile(directory / segment_name); FlushFile(directory / L"cursor.jsonl");
            if (pose) FlushFile(directory / L"presentation.jsonl");
            journal << "{\"file\":\"" << segment_name << "\",\"startUs\":" << segment_start / 10
                << ",\"endUs\":" << last_end / 10 << "}\n";
            journal.flush(); FlushFile(directory / L"capture-journal.jsonl");
            finalize_time.Add(Clock100ns() - began);
        };
        try {
            apartment = std::make_unique<Apartment>(); media = std::make_unique<MediaRuntime>();
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            SourceFrame first;
            while (!stopping && !(first = source->Snapshot()).texture) {
                check_hresult(source->Error());
                if (std::chrono::steady_clock::now() >= deadline) throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            if (stopping) throw hresult_error(HRESULT_FROM_WIN32(ERROR_CANCELLED));
            width = first.width; height = first.height;
            fs::create_directories(directory);
            if (!fs::is_empty(directory)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));
            owns_directory = true;
            {
                std::ofstream info(directory / L"capture-info.json", std::ios::binary);
                info.exceptions(std::ios::badbit | std::ios::failbit);
                info << "{\"schemaVersion\":" << (audio_selection.Enabled() ? 2 : 1) << ",\"width\":" << width << ",\"height\":" << height << ",\"fps\":" << fps
                    << ",\"microphone\":" << (audio_selection.microphone ? "true" : "false") << ",\"systemAudio\":" << (audio_selection.system ? "true" : "false") << "}";
                info.close(); FlushFile(directory / L"capture-info.json");
            }
            if (audio_selection.Enabled()) {
                audio = std::make_unique<AudioCapture>(directory/L"audio",audio_selection,audio_clock); audio->Start();
            }
            pointers.open(directory / L"cursor.jsonl", std::ios::binary); journal.open(directory / L"capture-journal.jsonl", std::ios::binary);
            pointers.exceptions(std::ios::badbit | std::ios::failbit); journal.exceptions(std::ios::badbit | std::ios::failbit); pointers << std::setprecision(17);
            if (pose) { camera.open(directory / L"presentation.jsonl", std::ios::binary); camera.exceptions(std::ios::badbit | std::ios::failbit); camera << std::setprecision(17); }
            wchar_t diagnostic[2]{};
            if (GetEnvironmentVariableW(L"GLIDE_DEV_DIAGNOSTICS", diagnostic, 2) == 1 && diagnostic[0] == L'1')
                timing_trace.open(directory / L"capture-timings.jsonl", std::ios::binary);
            uint32_t segment = 0;
            auto createWriter = [&] {
                const auto began = Clock100ns();
                char name[40]; sprintf_s(name, "screen-%06u.mp4", segment++); segment_name = name;
                const char* stage = "record"; writer = std::make_unique<VideoWriter>(directory / segment_name, width, height, fps, stage);
                create_time.Add(Clock100ns() - began);
            };
            createWriter();
            // Pay initial resource creation/GPU synchronization before reporting ready.
            if (!source->ReadPixels(first, staging, pixels, &stopping)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_CANCELLED));
            cached_sequence = first.sequence; cached_epoch = first.epoch;
            first = {}; // Do not pin an otherwise reusable source buffer for the session.
            { std::lock_guard lock(clock_mutex); origin = Clock100ns(); audio_clock->Start(origin); }
            initialized.set_value(); signalled = true;
            int64_t next_index = 0; bool first_sample = true;
            while (!stopping) {
                if (audio) check_hresult(audio->Error());
                int64_t pts; uint64_t generation;
                {
                    std::lock_guard lock(clock_mutex);
                    pts = (paused ? paused_at : Clock100ns()) - origin - paused_ticks;
                    generation = clock_generation;
                }
                if (paused) { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }
                if (pts >= 30LL * 60 * 10'000'000) break;
                int64_t frame_index = pts * fps / 10'000'000;
                if (frame_index < next_index) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
                // A live output owns source availability. Its cover/target-loss pause may
                // race this consumer; keep the recording open until an explicit resume/stop.
                if (!owns_source && (FAILED(source->Error()) || !source->Active())) { Pause(true); continue; }
                if (owns_source) check_hresult(source->Error());
                auto frame = source->Snapshot();
                if (!frame.texture) {
                    if (!owns_source && (FAILED(source->Error()) || !source->Active())) { Pause(true); continue; }
                    check_hresult(source->Error());
                    if (!source->Active()) throw hresult_error(HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED));
                    std::this_thread::sleep_for(std::chrono::milliseconds(5)); continue;
                }
                if (frame.width != width || frame.height != height) throw hresult_error(HRESULT_FROM_WIN32(ERROR_RETRY));
                auto pointer = source->Pointer();
                const auto read_begin = Clock100ns();
                // Source sequences identify immutable owned textures. Re-encoding an
                // unchanged source needs no GPU synchronization or CPU pixel copy.
                // Key on the session epoch as well so cover/resume cannot reuse an
                // earlier session's pixels. Validate geometry above even on a hit.
                const bool pixels_cached = cached_sequence == frame.sequence && cached_epoch == frame.epoch;
                if (!pixels_cached) {
                    if (!source->ReadPixels(frame, staging, pixels, &stopping)) break;
                    cached_sequence = frame.sequence; cached_epoch = frame.epoch;
                }
                const auto read_end = Clock100ns();
                if (!pixels_cached) readback_time.Add(read_end - read_begin);
                source_age.Add(read_begin - frame.pts);
                // Stop/pause may have arrived during GPU readback. Never write that in-flight frame.
                std::lock_guard lock(clock_mutex);
                if (paused || stopping || generation != clock_generation) continue;
                if (first_sample) { frame_index = 0; first_sample = false; }
                if (previous_source == frame.sequence) ++repeated_source;
                if (pixels_cached) ++reused_pixels;
                previous_source = frame.sequence;
                if (frame_index > next_index) dropped += static_cast<uint64_t>(frame_index-next_index);
                next_index = frame_index+1;
                // Pacing jitter must not overlap fixed-duration samples or segment ranges.
                // Quantize timestamps to the recording clock's rational frame grid.
                pts = frame_index * 10'000'000 / fps;
                const int64_t sample_end = next_index * 10'000'000 / fps;
                if (pts - segment_start >= 50'000'000) { finalize(); segment_start = pts; createWriter(); }
                const auto encode_begin = Clock100ns();
                writer->Write(pixels.data(), width * 4, pts - segment_start, sample_end-pts);
                const auto encode_end = Clock100ns(); encode_time.Add(encode_end - encode_begin);
                if (timing_trace) timing_trace << "{\"frame\":" << frame_index << ",\"sourceSequence\":" << frame.sequence
                    << ",\"sourceEpoch\":" << frame.epoch << ",\"pixelsCached\":" << (pixels_cached ? "true" : "false")
                    << ",\"sourceQpc100ns\":" << frame.pts << ",\"sourceAcquiredQpc100ns\":" << frame.acquired_at
                    << ",\"sourcePublishedQpc100ns\":" << frame.published_at << ",\"readBeginQpc100ns\":" << read_begin
                    << ",\"readEndQpc100ns\":" << read_end << ",\"encodeBeginQpc100ns\":" << encode_begin
                    << ",\"encodeEndQpc100ns\":" << encode_end << ",\"recordingOriginQpc100ns\":" << origin
                    << ",\"pausedTicks100ns\":" << paused_ticks
                    << ",\"sourceAgeUs\":" << (read_begin-frame.pts)/10 << ",\"readbackUs\":" << (pixels_cached ? 0 : (read_end-read_begin)/10)
                    << ",\"rolloverUs\":" << (encode_begin-read_end)/10 << ",\"encodeUs\":" << (encode_end-encode_begin)/10 << "}\n";
                pointers << "{\"timeUs\":" << pts / 10 << ",\"x\":" << pointer.x << ",\"y\":" << pointer.y
                    << ",\"visible\":" << (pointer.visible ? "true" : "false") << ",\"left\":" << (pointer.left ? "true" : "false")
                    << ",\"right\":" << (pointer.right ? "true" : "false") << "}\n";
                if (pose) {
                    const auto p = pose();
                    camera << "{\"timeUs\":" << pts / 10 << ",\"x\":" << p.x << ",\"y\":" << p.y << ",\"scale\":" << p.scale << "}\n";
                }
                last_end = sample_end; ++frames; duration = sample_end / 10;
            }
            if (frames == 0) throw hresult_error(HRESULT_FROM_WIN32(ERROR_NO_DATA));
            { std::lock_guard lock(clock_mutex); audio_clock->Stop(Clock100ns()); }
            if (audio) check_hresult(audio->Finish(last_end));
            finalize(); state = 2;
            try { saveMetrics(); } catch (...) {} // Optional timing evidence cannot invalidate committed media.
            std::ofstream summary(directory / L"capture.json", std::ios::binary);
            summary << "{\"schemaVersion\":1,\"frames\":" << frames << ",\"dropped\":" << dropped << ",\"durationUs\":" << duration
                << ",\"width\":" << width << ",\"height\":" << height << ",\"fps\":" << fps << ",\"cursorEmbedded\":false,\"audio\":" << (audio_selection.Enabled() ? "true" : "false") << ",\"error\":0}";
        } catch (...) {
            failure = to_hresult(); state = 3;
            try { audio_clock->Stop(Clock100ns()); if (audio) audio->Finish(last_end); } catch (...) {}
            try { if (frames > 0) finalize(); } catch (...) {}
            if (!signalled) initialized.set_exception(std::current_exception());
            if (owns_directory) {
                try { saveMetrics(); } catch (...) {}
                try { std::ofstream report(directory / L"capture-error.json"); report << "{\"stage\":\"recording-sink\",\"hresult\":" << failure << "}"; } catch (...) {}
            }
        }
    }
};
