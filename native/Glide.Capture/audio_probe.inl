#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <winrt/base.h>
#include <atomic>
#include <thread>
#include <future>
#include <cmath>
#include <vector>

// Disposable fixture output, never microphone input. Its quiet test tones are
// rendered through the actual selected default Windows speaker endpoint.
class ProbeTone {
    std::thread worker;
    std::atomic<bool> stopping{false};
    std::atomic<double> frequency{440};
    std::promise<void> ready;
    std::atomic<HRESULT> error{S_OK};
    std::vector<float> reference;
    struct Poll { uint64_t now, position, qpc, submitted; UINT32 padding; HRESULT result; double hz; };
    std::vector<Poll> polls;
    uint64_t clock_frequency = 0;
    UINT32 buffer_capacity = 0;
    DWORD priority_error = 0;
    uint64_t event_timeouts = 0;
    static uint64_t Qpc() {
        LARGE_INTEGER now{}, f{}; QueryPerformanceCounter(&now); QueryPerformanceFrequency(&f);
        return static_cast<uint64_t>(now.QuadPart/f.QuadPart*10'000'000 + now.QuadPart%f.QuadPart*10'000'000/f.QuadPart);
    }
public:
    ~ProbeTone() { Stop(); }
    void Start() {
        auto future = ready.get_future();
        worker = std::thread([this] {
            bool signalled = false;
            try {
                winrt::init_apartment(winrt::apartment_type::multi_threaded);
                struct Cleanup { ~Cleanup() { winrt::uninit_apartment(); } } cleanup;
                auto devices = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
                winrt::com_ptr<IMMDevice> device;
                winrt::check_hresult(devices->GetDefaultAudioEndpoint(eRender,eConsole,device.put()));
                winrt::com_ptr<IAudioClient> client;
                winrt::check_hresult(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,client.put_void()));
                winrt::handle refill(CreateEventW(nullptr,FALSE,FALSE,nullptr));
                if (!refill) winrt::throw_last_error();
                WAVEFORMATEX format{WAVE_FORMAT_IEEE_FLOAT,2,48000,48000*8,8,32,0};
                winrt::check_hresult(client->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY|AUDCLNT_STREAMFLAGS_EVENTCALLBACK,500'000,0,&format,nullptr));
                winrt::check_hresult(client->SetEventHandle(refill.get()));
                winrt::com_ptr<IAudioRenderClient> render;
                winrt::check_hresult(client->GetService(__uuidof(IAudioRenderClient),render.put_void()));
                winrt::com_ptr<IAudioClock> device_clock;
                winrt::check_hresult(client->GetService(__uuidof(IAudioClock),device_clock.put_void()));
                winrt::check_hresult(device_clock->GetFrequency(&clock_frequency));
                UINT32 capacity = 0; winrt::check_hresult(client->GetBufferSize(&capacity));
                buffer_capacity = capacity; reference.reserve(48000*2*20); polls.reserve(10000);
                double phase = 0;
                auto fill = [&](UINT32 count) {
                    if (!count) return;
                    BYTE* bytes = nullptr; winrt::check_hresult(render->GetBuffer(count,&bytes));
                    auto samples = reinterpret_cast<float*>(bytes);
                    const double step = frequency.load()*6.283185307179586/48000;
                    for (UINT32 i = 0; i < count; ++i) {
                        const float sample = static_cast<float>(std::sin(phase)*.05);
                        samples[i*2] = samples[i*2+1] = sample;
                        phase += step; if (phase >= 6.283185307179586) phase -= 6.283185307179586;
                    }
                    if (reference.size()+static_cast<size_t>(count)*2 > 48000*2*20) {
                        render->ReleaseBuffer(count,0); throw winrt::hresult_error(E_UNEXPECTED);
                    }
                    reference.insert(reference.end(),samples,samples+static_cast<size_t>(count)*2);
                    winrt::check_hresult(render->ReleaseBuffer(count,0));
                };
                DWORD task_index = 0;
                struct Priority { HANDLE value; ~Priority() { if (value) AvRevertMmThreadCharacteristics(value); } } priority{AvSetMmThreadCharacteristicsW(L"Audio",&task_index)};
                if (!priority.value) priority_error = GetLastError();
                fill(capacity); winrt::check_hresult(client->Start());
                ready.set_value(); signalled = true;
                while (!stopping) {
                    UINT32 padding = 0; winrt::check_hresult(client->GetCurrentPadding(&padding));
                    UINT64 position = 0, qpc = 0;
                    const HRESULT status = device_clock->GetPosition(&position,&qpc); winrt::check_hresult(status);
                    if (polls.size() >= 10000 || padding > capacity) throw winrt::hresult_error(E_UNEXPECTED);
                    polls.push_back({Qpc(),position,qpc,reference.size()/2,padding,status,frequency.load()});
                    fill(capacity-padding);
                    const auto wait = WaitForSingleObject(refill.get(),100);
                    if (wait == WAIT_FAILED) winrt::throw_last_error();
                    if (wait == WAIT_TIMEOUT) ++event_timeouts;
                }
                winrt::check_hresult(client->Stop());
            } catch (...) { error = winrt::to_hresult(); if (!signalled) ready.set_exception(std::current_exception()); }
        });
        future.get();
    }
    void Tone(double hz) { frequency = hz; }
    HRESULT Error() const { return error; }
    void Stop() { stopping = true; if (worker.joinable()) worker.join(); }
    void SaveReference(const std::filesystem::path& path) {
        std::ofstream out(path,std::ios::binary); out.exceptions(std::ios::badbit|std::ios::failbit);
        out.write(reinterpret_cast<const char*>(reference.data()),static_cast<std::streamsize>(reference.size()*sizeof(float)));
        std::ofstream trace(path.parent_path()/L"fixture-render-clock.jsonl");
        trace.exceptions(std::ios::badbit|std::ios::failbit);
        for (const auto& p : polls)
            trace << "{\"qpc100ns\":" << p.now << ",\"devicePosition\":" << p.position << ",\"deviceQpc100ns\":" << p.qpc
                << ",\"submittedFrames\":" << p.submitted << ",\"paddingFrames\":" << p.padding << ",\"clockResult\":" << p.result << ",\"toneHz\":" << p.hz << "}\n";
        std::ofstream info(path.parent_path()/L"fixture-render-info.json");
        info << "{\"schemaVersion\":1,\"sampleRate\":48000,\"channels\":2,\"clockFrequency\":" << clock_frequency
            << ",\"bufferFrames\":" << buffer_capacity << ",\"submittedFrames\":" << reference.size()/2
            << ",\"eventDriven\":true,\"mmcssError\":" << priority_error << ",\"eventTimeouts\":" << event_timeouts
            << ",\"scope\":\"Device clock and zero-padding observations; not proof of uninterrupted physical speaker output\"}";
    }
};
