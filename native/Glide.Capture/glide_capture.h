#pragma once
#include <stdint.h>
#if defined(_WIN32) && defined(GLIDE_CAPTURE_BUILD)
#define GLIDE_API extern "C" __declspec(dllexport)
#elif defined(_WIN32)
#define GLIDE_API extern "C" __declspec(dllimport)
#else
#define GLIDE_API extern "C"
#endif

struct GlideCaptureStats {
    uint32_t size;
    uint32_t state; // 1 capturing, 2 finalized, 3 failed
    uint64_t frames;
    uint64_t dropped;
    int64_t duration_us;
    uint32_t width;
    uint32_t height;
    int32_t error;
};

// ABI v1. Exactly one of window/monitor must be nonzero. Only captures explicitly selected sources.
GLIDE_API uint32_t glide_capture_abi_version();
GLIDE_API int32_t glide_capture_start(uintptr_t window, uintptr_t monitor, const wchar_t* output_directory, uint32_t fps, uint64_t* id);
GLIDE_API int32_t glide_capture_stats(uint64_t id, GlideCaptureStats* stats);
GLIDE_API int32_t glide_capture_stop(uint64_t id);
GLIDE_API void glide_capture_release(uint64_t id);
GLIDE_API int32_t glide_verify_video(const wchar_t* path, uint64_t* decoded_frames, int64_t* duration_us);

// Audio ABI v1. Null device ID disables a track; empty ID explicitly selects
// the current default endpoint. Selected endpoints never change automatically.
struct GlideAudioOptions { uint32_t size, reserved; const wchar_t* microphone; const wchar_t* system; };
typedef int32_t (__cdecl *GlideAudioDeviceCallback)(const wchar_t* id, const wchar_t* name, uint32_t role, uint32_t is_default);
GLIDE_API uint32_t glide_audio_abi_version();
GLIDE_API int32_t glide_audio_devices(GlideAudioDeviceCallback callback); // role 1 microphone, 2 system loopback
GLIDE_API int32_t glide_capture_start_with_audio(uintptr_t window, uintptr_t monitor, const wchar_t* directory, uint32_t fps,
    const GlideAudioOptions* audio, uint64_t* id);
GLIDE_API int32_t glide_live_record_with_audio(uint64_t id, const wchar_t* directory, const GlideAudioOptions* audio, uint64_t* recording);

// Render ABI v2: synchronous calls on an MTA worker. Coordinates come from one immutable RenderPlan.
// BGRA frames are top-down, tightly packed. Callback returns nonzero to cancel; it must not throw.
struct GlideRenderSource { const wchar_t* path; int64_t duration_us; };
struct GlideRenderFrame {
    int64_t source_us, output_us;
    double crop_x, crop_y, crop_w, crop_h;
    double dest_x, dest_y, dest_w, dest_h;
    double cursor_x, cursor_y, cursor_scale;
    uint32_t cursor_visible, source_index;
    uint32_t background_argb, reserved;
    double corner_radius, click_amount, click_x, click_y;
};
typedef int32_t (__cdecl *GlideRenderProgress)(uint32_t completed, uint32_t total);
GLIDE_API uint32_t glide_render_abi_version();
GLIDE_API uint32_t glide_render_error_stage();
GLIDE_API int32_t glide_preview_create(uint32_t source_width, uint32_t source_height, uint32_t width, uint32_t height, uint64_t* id);
GLIDE_API int32_t glide_preview_frame(uint64_t id, const wchar_t* source, const GlideRenderFrame* frame, uint8_t* pixels, uint32_t bytes);
GLIDE_API void glide_preview_release(uint64_t id);
GLIDE_API int32_t glide_render_frame(const wchar_t* source, const GlideRenderFrame* frame,
    uint32_t source_width, uint32_t source_height, uint32_t width, uint32_t height, uint8_t* bgra, uint32_t bytes);
GLIDE_API int32_t glide_render_video(const wchar_t* output, const GlideRenderSource* sources, uint32_t source_count,
    const GlideRenderFrame* frames, uint32_t frame_count, uint32_t source_width, uint32_t source_height,
    uint32_t width, uint32_t height, uint32_t fps, int64_t duration_us, GlideRenderProgress progress);

// Audio render ABI v1, additive to video ABI v2. The synchronous callback fills
// exactly count interleaved 48 kHz stereo PCM16 frames; return an HRESULT, never throw.
typedef int32_t (__cdecl *GlideRenderAudio)(int64_t first_frame, uint32_t count, int16_t* stereo);
GLIDE_API uint32_t glide_render_audio_abi_version();
GLIDE_API int32_t glide_render_video_with_audio(const wchar_t* output, const GlideRenderSource* sources, uint32_t source_count,
    const GlideRenderFrame* frames, uint32_t frame_count, uint32_t source_width, uint32_t source_height,
    uint32_t width, uint32_t height, uint32_t fps, int64_t duration_us, GlideRenderProgress progress, GlideRenderAudio audio);
GLIDE_API int32_t glide_verify_audio(const wchar_t* path, uint64_t* decoded_frames, int64_t* duration_us);

// Live ABI v1. Preparation is covered; no capture starts until command 1.
// Source and recording lifetimes are independent. Pointer sampling is live-only.
struct GlideLiveStats {
    uint32_t size, state; // 0 preparing, 1 ready, 2 live, 3 covered, 4 ended, 5 fault
    uint32_t width, height;
    uint64_t frames;
    double cursor_x, cursor_y;
    uint32_t cursor_visible, buttons;
    int32_t error;
    uint32_t closed;
    uintptr_t output_window;
};
struct GlideLivePose { double x, y, scale; uint32_t background_argb, show_clicks; double padding, cursor_scale, corner_radius; };
GLIDE_API uint32_t glide_live_abi_version();
GLIDE_API int32_t glide_live_create(uintptr_t window, uintptr_t monitor, uintptr_t output_monitor, uint64_t* id);
GLIDE_API int32_t glide_live_command(uint64_t id, uint32_t command); // 1 begin/resume, 2 cover, 3 end
GLIDE_API int32_t glide_live_pose(uint64_t id, const GlideLivePose* pose);
GLIDE_API int32_t glide_live_stats(uint64_t id, GlideLiveStats* stats);
GLIDE_API int32_t glide_live_record(uint64_t id, const wchar_t* directory, uint64_t* recording);
GLIDE_API int32_t glide_capture_pause(uint64_t id, uint32_t paused);
GLIDE_API void glide_live_release(uint64_t id);
GLIDE_API int32_t glide_snapshot(uintptr_t window, uintptr_t monitor, uint32_t max_width, uint32_t max_height,
    uint8_t* pixels, uint32_t capacity, uint32_t* width, uint32_t* height);

// Explicit, non-persistent audio input test. No media is written.
GLIDE_API int32_t glide_audio_meter_start(const wchar_t* device, uint32_t role, uint64_t* id);
GLIDE_API int32_t glide_audio_meter_read(uint64_t id, float* peak);
GLIDE_API void glide_audio_meter_release(uint64_t id);
