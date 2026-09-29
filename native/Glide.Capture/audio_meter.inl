// A test owns a shared WASAPI stream, drains packets and reports the endpoint's
// measured peak. It neither writes media nor plays microphone audio back.
class AudioMeter {
    std::thread worker;
    std::atomic<bool> stopping{false};
    std::atomic<float> peak{0};
    std::atomic<HRESULT> failure{S_OK};
    std::promise<void> ready;
public:
    AudioMeter(std::wstring id, uint32_t role) {
        auto initialized = ready.get_future();
        worker = std::thread([this, id = std::move(id), role] {
            bool signalled = false;
            try {
                Apartment apartment;
                auto enumerator = create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
                com_ptr<IMMDevice> device; check_hresult(enumerator->GetDevice(id.c_str(), device.put()));
                com_ptr<IMMEndpoint> endpoint; check_hresult(device->QueryInterface(__uuidof(IMMEndpoint), endpoint.put_void()));
                EDataFlow flow; check_hresult(endpoint->GetDataFlow(&flow));
                if (flow != (role == 1 ? eCapture : eRender)) throw hresult_error(E_INVALIDARG);
                com_ptr<IAudioClient> client; check_hresult(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()));
                WAVEFORMATEX* raw = nullptr; check_hresult(client->GetMixFormat(&raw));
                std::unique_ptr<WAVEFORMATEX, decltype(&CoTaskMemFree)> format(raw, CoTaskMemFree);
                check_hresult(client->Initialize(AUDCLNT_SHAREMODE_SHARED, role == 2 ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0, 1'000'000, 0, raw, nullptr));
                com_ptr<IAudioCaptureClient> capture; check_hresult(client->GetService(__uuidof(IAudioCaptureClient), capture.put_void()));
                com_ptr<IAudioMeterInformation> meter; check_hresult(device->Activate(__uuidof(IAudioMeterInformation), CLSCTX_ALL, nullptr, meter.put_void()));
                check_hresult(client->Start()); ready.set_value(); signalled = true;
                while (!stopping) {
                    UINT32 count = 0; check_hresult(capture->GetNextPacketSize(&count));
                    while (count && !stopping) {
                        BYTE* bytes; DWORD flags; UINT32 frames;
                        check_hresult(capture->GetBuffer(&bytes, &frames, &flags, nullptr, nullptr));
                        check_hresult(capture->ReleaseBuffer(frames));
                        check_hresult(capture->GetNextPacketSize(&count));
                    }
                    float value = 0; check_hresult(meter->GetPeakValue(&value));
                    peak = std::clamp(value, 0.0f, 1.0f);
                    std::this_thread::sleep_for(std::chrono::milliseconds(30));
                }
                client->Stop();
            } catch (...) { failure = to_hresult(); if (!signalled) ready.set_exception(std::current_exception()); }
        });
        try { initialized.get(); } catch (...) { stopping = true; worker.join(); throw; }
    }
    ~AudioMeter() { stopping = true; if (worker.joinable()) worker.join(); }
    HRESULT Read(float& value) { value = peak; return failure; }
};
static std::mutex meters_mutex;
static std::map<uint64_t, std::shared_ptr<AudioMeter>> meters;
static uint64_t next_meter = 1;
int32_t glide_audio_meter_start(const wchar_t* device, uint32_t role, uint64_t* id) {
    if (id) *id = 0;
    if (!id || !device || !*device || wcsnlen_s(device, 32768) == 32768 || (role != 1 && role != 2)) return E_INVALIDARG;
    try {
        auto meter = std::make_shared<AudioMeter>(device, role);
        std::lock_guard lock(meters_mutex); *id = next_meter++; meters.emplace(*id, std::move(meter)); return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_audio_meter_read(uint64_t id, float* peak) {
    if (!peak) return E_INVALIDARG;
    std::lock_guard lock(meters_mutex); auto found = meters.find(id);
    return found == meters.end() ? E_INVALIDARG : found->second->Read(*peak);
}
void glide_audio_meter_release(uint64_t id) {
    std::shared_ptr<AudioMeter> meter;
    { std::lock_guard lock(meters_mutex); auto found = meters.find(id); if (found == meters.end()) return; meter = std::move(found->second); meters.erase(found); }
}
