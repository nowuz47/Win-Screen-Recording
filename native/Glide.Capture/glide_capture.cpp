#define NOMINMAX
#define GLIDE_CAPTURE_BUILD
#include "glide_capture.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <dwmapi.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <codecapi.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <array>
#include "recording_timeline.h"
#include "audio_resample.h"
#include "audio_packet_clock.h"
#include "bounded_audio_queue.h"
#include <optional>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cmath>
#include <deque>
#include <filesystem>
#include <fstream>
#include <future>
#include <functional>
#include <iomanip>
#include <map>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;
namespace fs = std::filesystem;

namespace {
void FlushFile(const fs::path& path) {
    HANDLE handle = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) throw_last_error();
    BOOL flushed = FlushFileBuffers(handle);
    DWORD error = flushed ? ERROR_SUCCESS : GetLastError();
    CloseHandle(handle);
    if (error != ERROR_SUCCESS) throw hresult_error(HRESULT_FROM_WIN32(error));
}

struct MediaRuntime {
    MediaRuntime() { check_hresult(MFStartup(MF_VERSION)); }
    ~MediaRuntime() { MFShutdown(); }
};
struct Apartment {
    Apartment() { init_apartment(apartment_type::multi_threaded); }
    ~Apartment() { uninit_apartment(); }
};
struct VideoFrame {
    com_ptr<ID3D11Texture2D> texture;
    int64_t pts;
    POINT cursor;
    bool visible;
    bool left;
    bool right;
};

// Initial correctness path: bounded GPU readback + BT.709 NV12. GPU conversion is a later gate.
class VideoWriter {
    com_ptr<IMFSinkWriter> writer;
    DWORD stream = 0;
    DWORD audio_stream = MAXDWORD;
    uint32_t width, height, fps;
public:
    VideoWriter(const fs::path& path, uint32_t w, uint32_t h, uint32_t rate, const char*& stage, bool audio = false) : width(w), height(h), fps(rate) {
        stage = "encoder-attributes";
        com_ptr<IMFAttributes> attributes;
        check_hresult(MFCreateAttributes(attributes.put(), 4));
        check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
        check_hresult(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
        // Hardware transforms and MF_READWRITE_DISABLE_CONVERTERS cannot be combined.
        check_hresult(attributes->SetUINT32(MF_LOW_LATENCY, TRUE));
        stage = "create-sink-writer";
        check_hresult(MFCreateSinkWriterFromURL(path.c_str(), nullptr, attributes.get(), writer.put()));
        stage = "encoder-output-type";
        com_ptr<IMFMediaType> out, input;
        check_hresult(MFCreateMediaType(out.put()));
        check_hresult(out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(out->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        check_hresult(out->SetUINT32(MF_MT_AVG_BITRATE, std::max(1'000'000u, w * h * rate / 8)));
        check_hresult(out->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        check_hresult(out->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main));
        check_hresult(out->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
        check_hresult(out->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
        check_hresult(out->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
        check_hresult(out->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        check_hresult(MFSetAttributeSize(out.get(), MF_MT_FRAME_SIZE, w, h));
        check_hresult(MFSetAttributeRatio(out.get(), MF_MT_FRAME_RATE, rate, 1));
        check_hresult(MFSetAttributeRatio(out.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        stage = "add-output-stream";
        check_hresult(writer->AddStream(out.get(), &stream));
        stage = "encoder-input-type";
        check_hresult(MFCreateMediaType(input.put()));
        check_hresult(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        check_hresult(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        check_hresult(input->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
        check_hresult(input->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
        check_hresult(input->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
        check_hresult(input->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        check_hresult(MFSetAttributeSize(input.get(), MF_MT_FRAME_SIZE, w, h));
        check_hresult(MFSetAttributeRatio(input.get(), MF_MT_FRAME_RATE, rate, 1));
        check_hresult(MFSetAttributeRatio(input.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        stage = "set-input-media-type";
        check_hresult(writer->SetInputMediaType(stream, input.get(), nullptr));
        if (audio) {
            stage = "audio-encoder-output";
            com_ptr<IMFMediaType> audio_out, audio_in;
            check_hresult(MFCreateMediaType(audio_out.put()));
            check_hresult(audio_out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
            check_hresult(audio_out->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
            check_hresult(audio_out->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
            check_hresult(audio_out->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48'000));
            check_hresult(audio_out->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
            check_hresult(audio_out->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24'000));
            check_hresult(audio_out->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0));
            check_hresult(audio_out->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29));
            check_hresult(writer->AddStream(audio_out.get(), &audio_stream));
            stage = "audio-encoder-input";
            check_hresult(MFCreateMediaType(audio_in.put()));
            check_hresult(audio_in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
            check_hresult(audio_in->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
            check_hresult(audio_in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
            check_hresult(audio_in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48'000));
            check_hresult(audio_in->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
            check_hresult(audio_in->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4));
            check_hresult(audio_in->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192'000));
            check_hresult(audio_in->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
            check_hresult(writer->SetInputMediaType(audio_stream, audio_in.get(), nullptr));
        }
        stage = "begin-writing";
        check_hresult(writer->BeginWriting());
        stage = "capture";
    }
    void Write(const uint8_t* bgra, uint32_t stride, int64_t pts, int64_t sample_duration = 0) {
        const DWORD length = width * height * 3 / 2;
        com_ptr<IMFMediaBuffer> buffer;
        check_hresult(MFCreateMemoryBuffer(length, buffer.put()));
        BYTE* data = nullptr;
        check_hresult(buffer->Lock(&data, nullptr, nullptr));
        auto byte = [](int v) { return static_cast<uint8_t>(std::clamp(v, 0, 255)); };
        for (uint32_t y = 0; y < height; ++y) {
            auto row = bgra + static_cast<size_t>(y) * stride;
            for (uint32_t x = 0; x < width; ++x) {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                data[y * width + x] = byte(16 + ((11966 * r + 40254 * g + 4064 * b + 32768) >> 16));
            }
        }
        BYTE* uv = data + width * height;
        for (uint32_t y = 0; y < height; y += 2) {
            for (uint32_t x = 0; x < width; x += 2) {
                int r = 0, g = 0, b = 0;
                for (uint32_t dy = 0; dy < 2; ++dy) for (uint32_t dx = 0; dx < 2; ++dx) {
                    const auto p = bgra + static_cast<size_t>(y + dy) * stride + (x + dx) * 4;
                    b += p[0]; g += p[1]; r += p[2];
                }
                r /= 4; g /= 4; b /= 4;
                uv[(y / 2) * width + x] = byte(128 + ((-6596 * r - 22189 * g + 28784 * b + 32768) >> 16));
                uv[(y / 2) * width + x + 1] = byte(128 + ((28784 * r - 26145 * g - 2639 * b + 32768) >> 16));
            }
        }
        check_hresult(buffer->Unlock());
        check_hresult(buffer->SetCurrentLength(length));
        com_ptr<IMFSample> sample;
        check_hresult(MFCreateSample(sample.put()));
        check_hresult(sample->AddBuffer(buffer.get()));
        check_hresult(sample->SetSampleTime(pts));
        check_hresult(sample->SetSampleDuration(sample_duration > 0 ? sample_duration : 10'000'000 / fps));
        check_hresult(writer->WriteSample(stream, sample.get()));
    }
    void WriteAudio(const int16_t* stereo, uint32_t frames, int64_t first) {
        if (audio_stream == MAXDWORD || frames == 0 || frames > 48'000 || first < 0) throw hresult_error(E_INVALIDARG);
        com_ptr<IMFMediaBuffer> buffer; check_hresult(MFCreateMemoryBuffer(frames * 4, buffer.put()));
        BYTE* data = nullptr; check_hresult(buffer->Lock(&data, nullptr, nullptr));
        memcpy(data, stereo, frames * 4); check_hresult(buffer->Unlock());
        check_hresult(buffer->SetCurrentLength(frames * 4));
        com_ptr<IMFSample> sample; check_hresult(MFCreateSample(sample.put()));
        check_hresult(sample->AddBuffer(buffer.get()));
        const int64_t pts = first * 10'000'000 / 48'000;
        check_hresult(sample->SetSampleTime(pts));
        check_hresult(sample->SetSampleDuration((first + frames) * 10'000'000 / 48'000 - pts));
        check_hresult(writer->WriteSample(audio_stream, sample.get()));
    }
    void Finish() { if (writer) { check_hresult(writer->Finalize()); writer = nullptr; } }
};

#include "render_pipeline.inl"

class IRecording {
public:
    virtual ~IRecording() = default;
    virtual void Start() = 0;
    virtual int32_t Stop() = 0;
    virtual int32_t Pause(bool paused) = 0;
    virtual void Stats(GlideCaptureStats& out) const = 0;
};
#include "capture_source.inl"
#include "capture_metrics.inl"
#include "audio_capture.inl"
#include "recording_sink.inl"
#include "gpu_compositor.inl"
#include "preview_pipeline.inl"
#include "presentation_pipeline.inl"

std::mutex sessions_mutex;
std::map<uint64_t, std::shared_ptr<IRecording>> sessions;
std::atomic<uint64_t> next_id{1};
std::shared_ptr<IRecording> Find(uint64_t id) { std::lock_guard lock(sessions_mutex); auto found = sessions.find(id); return found == sessions.end() ? nullptr : found->second; }
std::map<uint64_t, std::shared_ptr<LivePresentation>> live_sessions;
std::map<uint64_t, std::shared_ptr<PreviewSession>> preview_sessions;
std::shared_ptr<LivePresentation> FindLive(uint64_t id) { std::lock_guard lock(sessions_mutex); auto found = live_sessions.find(id); return found == live_sessions.end() ? nullptr : found->second; }
}

#include "audio_meter.inl"

uint32_t glide_capture_abi_version() { return 1; }
uint32_t glide_audio_abi_version() { return 1; }
int32_t glide_audio_devices(GlideAudioDeviceCallback callback) {
    if (!callback) return E_INVALIDARG;
    try {
        Apartment apartment;
        auto devices = create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        struct Text { LPWSTR value = nullptr; ~Text() { CoTaskMemFree(value); } };
        struct Property { PROPVARIANT value{}; ~Property() { PropVariantClear(&value); } };
        for (uint32_t role = 1; role <= 2; ++role) {
            const auto flow = role == 1 ? eCapture : eRender;
            com_ptr<IMMDevice> preferred; Text preferred_id;
            if (SUCCEEDED(devices->GetDefaultAudioEndpoint(flow,eConsole,preferred.put()))) check_hresult(preferred->GetId(&preferred_id.value));
            com_ptr<IMMDeviceCollection> collection;
            check_hresult(devices->EnumAudioEndpoints(flow,DEVICE_STATE_ACTIVE,collection.put()));
            UINT count = 0; check_hresult(collection->GetCount(&count));
            if (count > 256) throw hresult_error(E_UNEXPECTED);
            for (UINT i = 0; i < count; ++i) {
                com_ptr<IMMDevice> device; check_hresult(collection->Item(i,device.put()));
                Text id; check_hresult(device->GetId(&id.value));
                com_ptr<IPropertyStore> properties; check_hresult(device->OpenPropertyStore(STGM_READ,properties.put()));
                Property name; check_hresult(properties->GetValue(PKEY_Device_FriendlyName,&name.value));
                const wchar_t* label = name.value.vt == VT_LPWSTR && name.value.pwszVal ? name.value.pwszVal : L"Audio device";
                const bool is_default = preferred_id.value && wcscmp(preferred_id.value,id.value) == 0;
                if (callback(id.value,label,role,is_default ? 1u : 0u)) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            }
        }
        return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_capture_start(uintptr_t window, uintptr_t monitor, const wchar_t* output_directory, uint32_t fps, uint64_t* id) {
    return glide_capture_start_with_audio(window,monitor,output_directory,fps,nullptr,id);
}
int32_t glide_capture_start_with_audio(uintptr_t window, uintptr_t monitor, const wchar_t* output_directory, uint32_t fps,
    const GlideAudioOptions* audio, uint64_t* id) {
    if (id) *id = 0;
    if (!id || !output_directory || !*output_directory || (window == 0) == (monitor == 0) || (fps != 30 && fps != 60)) return E_INVALIDARG;
    try {
        const auto selection = AudioSelection::Read(audio);
        if (window && !IsWindow(reinterpret_cast<HWND>(window))) return E_INVALIDARG;
        auto source = std::make_shared<CaptureSource>(reinterpret_cast<HWND>(window), reinterpret_cast<HMONITOR>(monitor));
        source->Start(); source->Enable(true);
        auto capture = std::make_shared<RecordingSink>(source, output_directory, fps, true, std::function<GlideLivePose()>{},selection);
        capture->Start();
        std::lock_guard lock(sessions_mutex); *id = next_id++; sessions.emplace(*id, capture); return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_capture_stats(uint64_t id, GlideCaptureStats* stats) {
    if (!stats || stats->size != sizeof(GlideCaptureStats)) return E_INVALIDARG;
    auto capture = Find(id); if (!capture) return E_INVALIDARG; capture->Stats(*stats); return S_OK;
}
int32_t glide_capture_stop(uint64_t id) { auto capture = Find(id); return capture ? capture->Stop() : E_INVALIDARG; }
int32_t glide_capture_pause(uint64_t id, uint32_t paused) { auto capture = Find(id); return capture && paused <= 1 ? capture->Pause(paused != 0) : E_INVALIDARG; }
void glide_capture_release(uint64_t id) {
    std::shared_ptr<IRecording> capture;
    { std::lock_guard lock(sessions_mutex); auto found = sessions.find(id); if (found == sessions.end()) return; capture = found->second; sessions.erase(found); }
    capture->Stop();
}

uint32_t glide_live_abi_version() { return 1; }
int32_t glide_live_create(uintptr_t window, uintptr_t monitor, uintptr_t output_monitor, uint64_t* id) {
    if (id) *id = 0;
    if (!id || (window == 0) == (monitor == 0) || (monitor && output_monitor && monitor == output_monitor)) return E_INVALIDARG;
    try {
        if (window) {
            DWORD pid = 0; if (!IsWindow(reinterpret_cast<HWND>(window))) return E_INVALIDARG;
            GetWindowThreadProcessId(reinterpret_cast<HWND>(window), &pid); if (pid == GetCurrentProcessId()) return E_INVALIDARG;
        }
        if (monitor && output_monitor) {
            MONITORINFO input{sizeof(input)}, output{sizeof(output)}; RECT overlap{};
            if (!GetMonitorInfoW(reinterpret_cast<HMONITOR>(monitor), &input) || !GetMonitorInfoW(reinterpret_cast<HMONITOR>(output_monitor), &output) ||
                IntersectRect(&overlap, &input.rcMonitor, &output.rcMonitor)) return E_INVALIDARG;
        }
        auto source = std::make_shared<CaptureSource>(reinterpret_cast<HWND>(window), reinterpret_cast<HMONITOR>(monitor)); source->Start();
        auto live = std::make_shared<LivePresentation>(source, reinterpret_cast<HMONITOR>(output_monitor)); live->Start();
        std::lock_guard lock(sessions_mutex); *id = next_id++; live_sessions.emplace(*id, live); return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_live_command(uint64_t id, uint32_t command) {
    auto live = FindLive(id); if (!live) return E_INVALIDARG;
    try { live->Command(command); return S_OK; } catch (...) { return to_hresult(); }
}
int32_t glide_live_pose(uint64_t id, const GlideLivePose* pose) {
    auto live = FindLive(id); if (!live || !pose) return E_INVALIDARG;
    try { live->SetPose(*pose); return S_OK; } catch (...) { return to_hresult(); }
}
int32_t glide_live_stats(uint64_t id, GlideLiveStats* stats) {
    auto live = FindLive(id); if (!live || !stats || stats->size != sizeof(GlideLiveStats)) return E_INVALIDARG;
    live->Stats(*stats); return S_OK;
}
int32_t glide_live_record(uint64_t id, const wchar_t* directory, uint64_t* recording) {
    return glide_live_record_with_audio(id,directory,nullptr,recording);
}
int32_t glide_live_record_with_audio(uint64_t id, const wchar_t* directory, const GlideAudioOptions* audio, uint64_t* recording) {
    if (recording) *recording = 0;
    auto live = FindLive(id); if (!live || !directory || !*directory || !recording) return E_INVALIDARG;
    try { auto sink = live->Record(directory,AudioSelection::Read(audio)); std::lock_guard lock(sessions_mutex); *recording = next_id++; sessions.emplace(*recording, sink); return S_OK; }
    catch (...) { return to_hresult(); }
}
void glide_live_release(uint64_t id) {
    std::shared_ptr<LivePresentation> live;
    { std::lock_guard lock(sessions_mutex); auto found = live_sessions.find(id); if (found == live_sessions.end()) return; live = found->second; live_sessions.erase(found); }
    live->Stop();
}
int32_t glide_snapshot(uintptr_t window, uintptr_t monitor, uint32_t max_width, uint32_t max_height,
    uint8_t* pixels, uint32_t capacity, uint32_t* width, uint32_t* height) {
    if (!pixels || !width || !height || (window == 0) == (monitor == 0) || max_width < 2 || max_height < 2 || max_width > 7680 || max_height > 4320 ||
        static_cast<uint64_t>(max_width) * max_height * 4 > capacity) return E_INVALIDARG;
    *width = *height = 0;
    try {
        auto source = std::make_shared<CaptureSource>(reinterpret_cast<HWND>(window), reinterpret_cast<HMONITOR>(monitor));
        source->Start(); source->Enable(true); SourceFrame frame;
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        while (!(frame = source->Snapshot()).texture) {
            check_hresult(source->Error()); if (std::chrono::steady_clock::now() > deadline) throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        const auto raw = source->ReadPixels(frame); source->Stop();
        double scale = std::min({1.0, max_width / static_cast<double>(frame.width), max_height / static_cast<double>(frame.height)});
        *width = std::max(2u, static_cast<uint32_t>(frame.width * scale) & ~1u); *height = std::max(2u, static_cast<uint32_t>(frame.height * scale) & ~1u);
        GlideRenderFrame f{}; f.crop_w = f.crop_h = 1; f.dest_w = *width; f.dest_h = *height; f.cursor_scale = 1; f.background_argb = 0xFF141820;
        Compose(raw, frame.width, frame.height, f, *width, *height, pixels); return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_verify_video(const wchar_t* path, uint64_t* decoded_frames, int64_t* duration_us) {
    if (!path || !decoded_frames || !duration_us) return E_INVALIDARG;
    *decoded_frames = 0; *duration_us = 0;
    try {
        Apartment apartment; MediaRuntime media;
        com_ptr<IMFSourceReader> reader;
        check_hresult(MFCreateSourceReaderFromURL(path, nullptr, reader.put()));
        check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
        check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE));
        com_ptr<IMFMediaType> type;
        check_hresult(MFCreateMediaType(type.put()));
        check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        check_hresult(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.get()));
        int64_t previous = -1;
        while (true) {
            DWORD flags = 0; LONGLONG pts = 0; com_ptr<IMFSample> sample;
            check_hresult(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, nullptr, &flags, &pts, sample.put()));
            if (flags & MF_SOURCE_READERF_ERROR) return E_FAIL;
            if (sample) {
                if (pts < previous) return MF_E_INVALID_TIMESTAMP;
                previous = pts; ++*decoded_frames;
                LONGLONG duration = 0; sample->GetSampleDuration(&duration); *duration_us = (pts + duration) / 10;
                com_ptr<IMFMediaBuffer> buffer; check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
                DWORD bytes = 0; check_hresult(buffer->GetCurrentLength(&bytes)); if (bytes == 0) return E_FAIL;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        }
        return *decoded_frames > 0 ? S_OK : HRESULT_FROM_WIN32(ERROR_NO_DATA);
    } catch (...) { return to_hresult(); }
}

int32_t glide_verify_audio(const wchar_t* path, uint64_t* decoded_frames, int64_t* duration_us) {
    if (!path || !*path || !decoded_frames || !duration_us) return E_INVALIDARG;
    *decoded_frames = 0; *duration_us = 0;
    try {
        Apartment apartment; MediaRuntime media;
        com_ptr<IMFSourceReader> reader;
        check_hresult(MFCreateSourceReaderFromURL(path, nullptr, reader.put()));
        const DWORD stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
        check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
        check_hresult(reader->SetStreamSelection(stream, TRUE));
        com_ptr<IMFMediaType> type; check_hresult(MFCreateMediaType(type.put()));
        check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
        check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
        check_hresult(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
        check_hresult(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48'000));
        check_hresult(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
        check_hresult(reader->SetCurrentMediaType(stream, nullptr, type.get()));
        com_ptr<IMFMediaType> actual; check_hresult(reader->GetCurrentMediaType(stream, actual.put()));
        UINT32 channels = 0, rate = 0, bits = 0, alignment = 0;
        check_hresult(actual->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels));
        check_hresult(actual->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &rate));
        check_hresult(actual->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits));
        check_hresult(actual->GetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, &alignment));
        if (channels != 2 || rate != 48'000 || bits != 16 || alignment != 4) throw hresult_error(MF_E_INVALIDMEDIATYPE);
        int64_t previous_end = -1;
        while (true) {
            DWORD flags = 0; LONGLONG pts = 0; com_ptr<IMFSample> sample;
            check_hresult(reader->ReadSample(stream, 0, nullptr, &flags, &pts, sample.put()));
            if (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED)) throw hresult_error(E_FAIL);
            if (sample) {
                com_ptr<IMFMediaBuffer> buffer; check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
                DWORD bytes = 0; check_hresult(buffer->GetCurrentLength(&bytes));
                if (!bytes || bytes % 4 || bytes > 192'000 || pts < 0 ||
                    (previous_end < 0 ? pts > 230'000 : std::abs(pts - previous_end) > 1'000))
                    throw hresult_error(MF_E_INVALID_TIMESTAMP);
                *decoded_frames += bytes / 4;
                if (*decoded_frames > 86'410'000) throw hresult_error(E_INVALIDARG);
                previous_end = pts + static_cast<int64_t>(bytes / 4) * 10'000'000 / 48'000;
                *duration_us = previous_end / 10;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        }
        return *decoded_frames > 0 ? S_OK : HRESULT_FROM_WIN32(ERROR_NO_DATA);
    } catch (...) { return to_hresult(); }
}

uint32_t glide_render_abi_version() { return 2; }
int32_t glide_preview_create(uint32_t sw, uint32_t sh, uint32_t w, uint32_t h, uint64_t* id) {
    if (!id) return E_INVALIDARG; *id=0;
    try { ValidateGeometry(sw,sh,w,h); auto preview=std::make_shared<PreviewSession>(sw,sh,w,h); preview->Start();
        std::lock_guard lock(sessions_mutex); *id=next_id++; preview_sessions.emplace(*id,preview); return S_OK; }
    catch (...) { return to_hresult(); }
}
int32_t glide_preview_frame(uint64_t id, const wchar_t* source, const GlideRenderFrame* frame, uint8_t* pixels, uint32_t bytes) {
    if (!source || !*source || !frame || !pixels) return E_INVALIDARG;
    std::shared_ptr<PreviewSession> preview;
    { std::lock_guard lock(sessions_mutex); auto it=preview_sessions.find(id); if(it==preview_sessions.end())return E_INVALIDARG; preview=it->second; }
    try {return preview->Frame(source,*frame,pixels,bytes);}catch(...){return to_hresult();}
}
void glide_preview_release(uint64_t id) {
    std::shared_ptr<PreviewSession> preview;
    { std::lock_guard lock(sessions_mutex); auto it=preview_sessions.find(id); if(it==preview_sessions.end())return; preview=it->second;preview_sessions.erase(it); }
}
uint32_t glide_render_error_stage() { return render_stage; }
static_assert(sizeof(GlideRenderFrame) == 152);
static_assert(sizeof(GlideRenderSource) == 16);
static_assert(sizeof(GlideLiveStats) == 64);
static_assert(sizeof(GlideLivePose) == 56);
int32_t glide_render_frame(const wchar_t* source, const GlideRenderFrame* frame,
    uint32_t sw, uint32_t sh, uint32_t w, uint32_t h, uint8_t* bgra, uint32_t bytes) {
    if (!source || !*source || !frame || !bgra || static_cast<uint64_t>(w) * h * 4 != bytes) return E_INVALIDARG;
    try {
        ValidateGeometry(sw, sh, w, h); ValidateFrame(*frame, w, h);
        Apartment apartment; MediaRuntime media;
        RenderReader reader(source, sw, sh);
        auto pixels = ToBgra(reader.At(frame->source_us), sw, sh);
        Compose(pixels, sw, sh, *frame, w, h, bgra); return S_OK;
    } catch (...) { return to_hresult(); }
}
int32_t glide_render_video(const wchar_t* output, const GlideRenderSource* sources, uint32_t source_count,
    const GlideRenderFrame* frames, uint32_t count, uint32_t sw, uint32_t sh, uint32_t w, uint32_t h,
    uint32_t fps, int64_t duration_us, GlideRenderProgress progress) {
    return glide_render_video_with_audio(output, sources, source_count, frames, count, sw, sh, w, h, fps, duration_us, progress, nullptr);
}
uint32_t glide_render_audio_abi_version() { return 1; }
int32_t glide_render_video_with_audio(const wchar_t* output, const GlideRenderSource* sources, uint32_t source_count,
    const GlideRenderFrame* frames, uint32_t count, uint32_t sw, uint32_t sh, uint32_t w, uint32_t h,
    uint32_t fps, int64_t duration_us, GlideRenderProgress progress, GlideRenderAudio audio) {
    if (!output || !*output || !sources || !frames || source_count == 0 || source_count > 10'000 || count == 0 || count > 108'006 ||
        (fps != 30 && fps != 60) || duration_us <= 0 || duration_us > 1'800'100'000) return E_INVALIDARG;
    bool owns_output = false;
    try {
        ValidateGeometry(sw, sh, w, h);
        if (count != (duration_us * fps + 999'999) / 1'000'000) throw hresult_error(E_INVALIDARG);
        for (uint32_t i = 0; i < source_count; ++i)
            if (!sources[i].path || !*sources[i].path || sources[i].duration_us <= 0 || sources[i].duration_us > 1'800'100'000) throw hresult_error(E_INVALIDARG);
        for (uint32_t i = 0; i < count; ++i) {
            const auto& f = frames[i]; ValidateFrame(f, w, h);
            if (f.source_index >= source_count || f.source_us >= sources[f.source_index].duration_us || f.output_us != static_cast<int64_t>(i) * 1'000'000 / fps ||
                (i && (f.source_index < frames[i - 1].source_index || (f.source_index == frames[i - 1].source_index && f.source_us < frames[i - 1].source_us))))
                throw hresult_error(E_INVALIDARG);
        }
        if (progress && progress(0, count)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_CANCELLED));
        HANDLE reserved = CreateFileW(output, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (reserved == INVALID_HANDLE_VALUE) throw_last_error();
        CloseHandle(reserved); owns_output = true;
        {
            Apartment apartment; MediaRuntime media;
            render_stage = 30;
            const char* stage = "render"; VideoWriter writer(output, w, h, fps, stage, audio != nullptr);
            // Editor playback and offline export use the same decoder/compositor pipeline.
            PreviewSession compositor(sw,sh,w,h); compositor.Start();
            std::vector<uint8_t> composed(static_cast<size_t>(w) * h * 4);
            std::array<int16_t, 3200> pcm{};
            int64_t audio_frame = 0;
            for (uint32_t i = 0; i < count; ++i) {
                if (progress && progress(i, count)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_CANCELLED));
                const auto& f = frames[i];
                check_hresult(compositor.Frame(sources[f.source_index].path,f,composed.data(),static_cast<uint32_t>(composed.size())));
                int64_t pts = static_cast<int64_t>(i) * 10'000'000 / fps;
                int64_t end = std::min(duration_us * 10, static_cast<int64_t>(i + 1) * 10'000'000 / fps);
                writer.Write(composed.data(), w * 4, pts, end - pts);
                if (audio) {
                    // Feed audio alongside each video frame. This bounds muxer
                    // buffering and gives both streams the same output clock.
                    int64_t audio_end = (end * 48'000 + 9'999'999) / 10'000'000;
                    while (audio_frame < audio_end) {
                        uint32_t block = static_cast<uint32_t>(std::min<int64_t>(1600, audio_end - audio_frame));
                        render_stage = 31; check_hresult(audio(audio_frame, block, pcm.data()));
                        render_stage = 32; writer.WriteAudio(pcm.data(), block, audio_frame);
                        audio_frame += block;
                    }
                }
            }
            render_stage = 33;
            writer.Finish();
        }
        FlushFile(output);
        if (progress && progress(count, count)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_CANCELLED));
        return S_OK;
    } catch (...) {
        auto error = to_hresult();
        if (owns_output) DeleteFileW(output);
        return error;
    }
}
