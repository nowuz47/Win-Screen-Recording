#define NOMINMAX
#include "glide_capture.h"
#include <windows.h>
#include <dwmapi.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <chrono>
#include <string>
#include "audio_probe.inl"

static int frame_number = 0;
static bool fixture_paint_failed = false;
static std::ofstream paint_log;
struct FixtureBuffer {
    HDC dc = nullptr; HBITMAP bitmap = nullptr; HGDIOBJ original = nullptr;
    LONG width = 0, height = 0;
    ~FixtureBuffer() { Reset(); }
    void Reset() {
        if (dc && original) SelectObject(dc, original);
        if (bitmap) DeleteObject(bitmap);
        if (dc) DeleteDC(dc);
        dc = nullptr; bitmap = nullptr; original = nullptr;
    }
    bool Prepare(HDC target, LONG w, LONG h) {
        if (dc && w == width && h == height) return true;
        Reset(); width = w; height = h;
        dc = CreateCompatibleDC(target); bitmap = CreateCompatibleBitmap(target,w,h);
        if (!dc || !bitmap) { Reset(); return false; }
        original = SelectObject(dc,bitmap); return original != nullptr;
    }
} fixture_buffer;
LRESULT CALLBACK Fixture(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_TIMER) { ++frame_number; InvalidateRect(window, nullptr, FALSE); return 0; }
    if (message == WM_PAINT) {
        PAINTSTRUCT paint{}; HDC target = BeginPaint(window, &paint); RECT bounds; GetClientRect(window, &bounds);
        if (!fixture_buffer.Prepare(target,bounds.right,bounds.bottom)) {
            fixture_paint_failed = true; EndPaint(window,&paint); return 0;
        }
        HDC dc = fixture_buffer.dc;
        HBRUSH background = CreateSolidBrush(RGB(18, 28, 35)); FillRect(dc, &bounds, background); DeleteObject(background);
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(232, 240, 242));
        std::wstring title = L"GLIDE CAPTURE VERIFICATION"; TextOutW(dc, 28, 30, title.c_str(), static_cast<int>(title.size()));
        auto label = L"Frame " + std::to_wstring(frame_number); TextOutW(dc, 28, 60, label.c_str(), static_cast<int>(label.size()));
        const COLORREF colors[] = {RGB(238, 78, 84), RGB(34, 192, 155), RGB(62, 133, 246), RGB(246, 196, 55)};
        for (int i = 0; i < 4; ++i) {
            RECT rect{28 + i * 138, 120, 146 + i * 138, 220};
            HBRUSH brush = CreateSolidBrush(colors[i]); FillRect(dc, &rect, brush); DeleteObject(brush);
        }
        int x = 28 + (frame_number * 3) % 540;
        RECT moving{x, 265, x + 38, 303}; HBRUSH brush = CreateSolidBrush(RGB(245, 245, 245)); FillRect(dc, &moving, brush); DeleteObject(brush);
        // Single completed bitmap publication avoids exposing FillRect's intermediate
        // blank background to WGC. A checksummed frame ID makes repeats observable.
        const auto id = static_cast<uint32_t>(frame_number);
        const uint32_t checksum = (id ^ (id >> 8) ^ (id >> 16) ^ (id >> 24) ^ 0xa5u) & 255u;
        for (int cell = 0; cell < 44; ++cell) {
            bool bit = cell < 4 ? cell % 2 == 0 : cell < 36 ? ((id >> (cell-4)) & 1u) != 0 : ((checksum >> (cell-36)) & 1u) != 0;
            RECT rect{28+cell*12,330,40+cell*12,348};
            FillRect(dc,&rect,static_cast<HBRUSH>(GetStockObject(bit ? WHITE_BRUSH : BLACK_BRUSH)));
        }
        if (!BitBlt(target,0,0,bounds.right,bounds.bottom,dc,0,0,SRCCOPY)) fixture_paint_failed = true;
        GdiFlush();
        if (paint_log) {
            LARGE_INTEGER now{}, frequency{}; QueryPerformanceCounter(&now); QueryPerformanceFrequency(&frequency);
            const int64_t ticks = now.QuadPart / frequency.QuadPart * 10'000'000 + now.QuadPart % frequency.QuadPart * 10'000'000 / frequency.QuadPart;
            paint_log << "{\"frame\":" << id << ",\"qpc100ns\":" << ticks << "}\n";
        }
        EndPaint(window, &paint); return 0;
    }
    if (message == WM_DESTROY) { fixture_buffer.Reset(); PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}

int wmain(int argc, wchar_t** argv) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const bool fixture_only = argc == 2 && std::wstring(argv[1]) == L"--fixture-only";
    const bool crash_test = argc == 3 && std::wstring(argv[2]) == L"--crash-after-8";
    const bool close_test = argc == 3 && std::wstring(argv[2]) == L"--close-after-8";
    const bool resize_test = argc == 3 && std::wstring(argv[2]) == L"--resize-after-8";
    const bool audio_stall = argc == 3 && std::wstring(argv[2]) == L"--audio-writer-stall";
    const bool audio_write = argc == 3 && std::wstring(argv[2]) == L"--audio-write-failure";
    const bool audio_fault = audio_stall || audio_write;
    const bool audio_test = audio_fault || (argc == 3 && std::wstring(argv[2]) == L"--system-audio");
    if (argc != 2 && !crash_test && !close_test && !resize_test && !audio_test) { std::cerr << "Usage: glide-capture-probe <empty-output-directory> [--crash-after-8 | --close-after-8 | --resize-after-8 | --system-audio | --audio-writer-stall | --audio-write-failure] | --fixture-only\n"; return 2; }
    auto instance = GetModuleHandleW(nullptr);
    WNDCLASSW cls{}; cls.lpfnWndProc = Fixture; cls.hInstance = instance; cls.lpszClassName = L"GlideCaptureFixture"; cls.hCursor = LoadCursorW(nullptr, IDC_ARROW); RegisterClassW(&cls);
    HWND window = CreateWindowExW(0, cls.lpszClassName, L"Glide capture test fixture", WS_OVERLAPPEDWINDOW, 100, 100, 660, 420, nullptr, nullptr, instance, nullptr);
    ShowWindow(window, SW_SHOW); UpdateWindow(window); SetTimer(window, 1, 16, nullptr);
    if (fixture_only) {
        auto shown = std::chrono::steady_clock::now();
        while (IsWindow(window) && std::chrono::steady_clock::now() - shown < std::chrono::seconds(180)) {
            MSG msg{}; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            Sleep(5);
        }
        if (IsWindow(window)) DestroyWindow(window);
        return fixture_paint_failed ? 1 : 0;
    }
    uint64_t id = 0;
    ProbeTone tone;
    if (audio_test) {
        SetEnvironmentVariableW(L"GLIDE_AUDIO_TEST_FAULT",audio_stall ? L"stall" : audio_write ? L"write" : nullptr);
        if(audio_fault)SetEnvironmentVariableW(L"GLIDE_DEV_DIAGNOSTICS",L"1");
        try { tone.Start(); } catch (...) { std::cerr << "Audio fixture start failed\n"; DestroyWindow(window); return 1; }
    }
    GlideAudioOptions audio{sizeof(GlideAudioOptions),0,nullptr,L""};
    HRESULT hr = audio_test ? glide_capture_start_with_audio(reinterpret_cast<uintptr_t>(window),0,argv[1],30,&audio,&id)
        : glide_capture_start(reinterpret_cast<uintptr_t>(window), 0, argv[1], 30, &id);
    if (FAILED(hr)) { std::cerr << "Capture start failed: " << std::hex << hr << "\n"; DestroyWindow(window); return 1; }
    {
        RECT captured{}; POINT client{}; ClientToScreen(window,&client);
        if (FAILED(DwmGetWindowAttribute(window,DWMWA_EXTENDED_FRAME_BOUNDS,&captured,sizeof(captured)))) GetWindowRect(window,&captured);
        std::ofstream fixture_info(std::filesystem::path(argv[1]) / L"probe-fixture.json");
        fixture_info << "{\"schemaVersion\":2,\"bufferedPaint\":true,\"barcode\":{\"x\":" << client.x-captured.left+28
            << ",\"y\":" << client.y-captured.top+330 << ",\"cellWidth\":12,\"cellHeight\":18,\"cells\":44}}";
        paint_log.open(std::filesystem::path(argv[1]) / L"fixture-paints.jsonl");
    }
    auto started = std::chrono::steady_clock::now();
    bool injected = false, automatic_stop = false; int audio_stage = 0;
    while (std::chrono::steady_clock::now() - started < std::chrono::seconds(crash_test ? 8 : 12)) {
        MSG msg{}; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (audio_test) {
            const auto elapsed = std::chrono::steady_clock::now()-started;
            if (audio_stage == 0 && elapsed >= std::chrono::seconds(4)) { if (FAILED(glide_capture_pause(id,1))) return 1; audio_stage = 1; }
            if (audio_stage == 1 && elapsed >= std::chrono::milliseconds(4200)) { tone.Tone(880); audio_stage = 2; }
            if (audio_stage == 2 && elapsed >= std::chrono::milliseconds(5600)) { tone.Tone(660); audio_stage = 3; }
            if (audio_stage == 3 && elapsed >= std::chrono::seconds(6)) { if (FAILED(glide_capture_pause(id,0))) return 1; audio_stage = 4; }
        }
        if ((close_test || resize_test) && !injected && std::chrono::steady_clock::now() - started >= std::chrono::seconds(8)) {
            injected = true;
            if (close_test) DestroyWindow(window);
            else SetWindowPos(window, nullptr, 0, 0, 760, 520, SWP_NOMOVE | SWP_NOZORDER);
        }
        if (injected) {
            GlideCaptureStats current{}; current.size = sizeof(current);
            if (SUCCEEDED(glide_capture_stats(id, &current)) && current.state != 1) { automatic_stop = true; break; }
        }
        if(audio_fault && audio_stage==4) {
            GlideCaptureStats current{};current.size=sizeof(current);
            if(SUCCEEDED(glide_capture_stats(id,&current)) && current.state==3){automatic_stop=true;break;}
        }
        Sleep(5);
    }
    if (crash_test) {
        paint_log.flush();
        std::ofstream checkpoint(std::filesystem::path(argv[1]) / L"probe-crash.json");
        checkpoint.exceptions(std::ios::badbit | std::ios::failbit);
        checkpoint << "{\"exitCode\":86,\"wallUsSinceCaptureReady\":"
            << std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count() << "}";
        checkpoint.close();
        // Intentionally bypass destructors and capture finalization in this disposable test process.
        if (!TerminateProcess(GetCurrentProcess(), 86)) return 1;
        return 86;
    }
    hr = glide_capture_stop(id);
    tone.Stop();
    if (audio_test && (FAILED(tone.Error()) || audio_stage != 4)) return 1;
    if (audio_test) tone.SaveReference(std::filesystem::path(argv[1])/L"fixture-tone-stereo-48000.f32");
    const HRESULT second_stop = glide_capture_stop(id);
    GlideCaptureStats stats{}; stats.size = sizeof(stats); glide_capture_stats(id, &stats); glide_capture_release(id);
    const HRESULT released_status = glide_capture_stats(id, &stats);
    if (IsWindow(window)) DestroyWindow(window);
    std::cout << "Capture result: " << std::hex << hr << std::dec << ", frames=" << stats.frames << ", dropped=" << stats.dropped << ", duration_us=" << stats.duration_us << ", dimensions=" << stats.width << "x" << stats.height << "\n";
    const HRESULT expected = audio_stall ? HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW) : audio_write ? HRESULT_FROM_WIN32(ERROR_WRITE_FAULT)
        : close_test ? HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE) : resize_test ? HRESULT_FROM_WIN32(ERROR_RETRY) : S_OK;
    if (hr != expected || second_stop != hr || released_status != E_INVALIDARG || stats.frames < 30 || ((close_test || resize_test || audio_fault) && !automatic_stop)) return 1;
    uint64_t decoded_total = 0; int segments = 0;
    for (auto const& entry : std::filesystem::directory_iterator(argv[1])) {
        if (entry.path().extension() != L".mp4") continue;
        uint64_t frames = 0; int64_t duration = 0;
        hr = glide_verify_video(entry.path().c_str(), &frames, &duration);
        std::wcout << entry.path().filename().wstring() << L": decode_hr=" << std::hex << hr << std::dec << L", frames=" << frames << L", duration_us=" << duration << L"\n";
        if (FAILED(hr)) return 1; decoded_total += frames; ++segments;
    }
    std::ofstream report(std::filesystem::path(argv[1]) / L"probe-result.json");
    bool passed = segments >= 2 && decoded_total == stats.frames && !fixture_paint_failed;
    report << "{\"passed\":" << (passed ? "true" : "false") << ",\"encodedFrames\":" << stats.frames << ",\"decodedFrames\":" << decoded_total << ",\"segments\":" << segments
        << ",\"automaticStop\":" << (automatic_stop ? "true" : "false") << ",\"captureHresult\":" << expected
        << ",\"environment\":\"Windows functional probe; not hardware certification\"}";
    std::cout << (passed ? "PASS" : "FAIL") << " capture -> H.264 segments -> full native decode\n";
    return passed ? 0 : 1;
}
