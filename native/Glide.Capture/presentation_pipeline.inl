class LivePresentation : public std::enable_shared_from_this<LivePresentation> {
    std::shared_ptr<CaptureSource> source;
    HMONITOR output_monitor;
    std::thread worker;
    std::mutex mutex, stop_mutex, recording_mutex;
    std::atomic<bool> stopping{false}, closed{false};
    std::atomic<uint32_t> state{1};
    std::atomic<HRESULT> failure{S_OK};
    std::atomic<uint64_t> presented{0};
    std::atomic<HWND> output{nullptr};
    HWND surface = nullptr;
    GlideLivePose pose{.5,.5,1,0xFF141820,1,.06,1.5,0};
    std::promise<void> initialized;
    std::weak_ptr<RecordingSink> recording;
    std::shared_ptr<RecordingSink> Recording() { std::lock_guard lock(recording_mutex); return recording.lock(); }
    com_ptr<IDXGISwapChain1> swapchain;
    com_ptr<ID3D11RenderTargetView> target;
    std::unique_ptr<GpuCompositor> compositor;
    static LRESULT CALLBACK Procedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
        auto self = reinterpret_cast<LivePresentation*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) { self = static_cast<LivePresentation*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams); SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self)); }
        if (self) {
            if (message == WM_WINDOWPOSCHANGING && self->source->Monitor() && !self->output_monitor) {
                auto position = reinterpret_cast<WINDOWPOS*>(lp);
                position->x = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN) + 64;
                position->y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            }
            if (message == WM_CLOSE) { self->closed = true; self->state = 4; self->source->Enable(false); ShowWindow(hwnd, SW_HIDE); return 0; }
            if (message == WM_SIZE) { self->Layout(hwnd); return 0; }
            if (message == WM_SETCURSOR) { SetCursor(nullptr); return TRUE; }
            if (message == WM_PAINT) {
                PAINTSTRUCT paint{}; HDC dc = BeginPaint(hwnd, &paint); RECT rect{}; GetClientRect(hwnd, &rect);
                FillRect(dc, &rect, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
                SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(230,233,238));
                auto font = CreateFontW(-24,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");
                auto previous = SelectObject(dc, font);
                const wchar_t* label = self->state == 4 ? L"Glide · 발표 종료" : L"Glide · 잠시만 기다려 주세요";
                DrawTextW(dc, label, -1, &rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                SelectObject(dc, previous); DeleteObject(font); EndPaint(hwnd, &paint); return 0;
            }
        }
        return DefWindowProcW(hwnd, message, wp, lp);
    }
    void Layout(HWND window) {
        if (!surface) return;
        RECT rect{}; GetClientRect(window, &rect);
        int w = std::max(1L, rect.right), h = std::max(1L, rect.bottom);
        int contentW = std::min(w, h * 16 / 9), contentH = contentW * 9 / 16;
        SetWindowPos(surface, nullptr, (w-contentW)/2, (h-contentH)/2, contentW, contentH, SWP_NOACTIVATE | SWP_NOZORDER);
    }
    void CreateOutput() {
        WNDCLASSW cls{}; cls.lpfnWndProc = Procedure; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"GlidePresentationOutput";
        cls.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        if (!RegisterClassW(&cls) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) throw_last_error();
        HMONITOR placement = output_monitor ? output_monitor : MonitorFromWindow(source->Window(), MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO info{sizeof(info)}; if (!GetMonitorInfoW(placement, &info)) throw_last_error();
        RECT r = output_monitor ? info.rcMonitor : info.rcWork;
        DWORD style = output_monitor ? WS_POPUP : WS_OVERLAPPEDWINDOW;
        int w = output_monitor ? r.right-r.left : std::max(1L, std::min(960L, r.right-r.left-48));
        int h = output_monitor ? r.bottom-r.top : std::max(1L, std::min(600L, r.bottom-r.top-48));
        // Start the floating audience window at the work area's bottom right,
        // clear of taskbars. Only its initial position is anchored; it remains movable.
        int x = output_monitor ? r.left : std::max(r.left, r.right-w-24);
        int y = output_monitor ? r.top : std::max(r.top, r.bottom-h-24);
        auto hwnd = CreateWindowExW(WS_EX_NOACTIVATE, cls.lpszClassName, L"Glide Presentation", style | WS_CLIPCHILDREN,
            x, y, w, h, nullptr, nullptr, cls.hInstance, this);
        if (!hwnd) throw_last_error(); output = hwnd;
        // A window shared by a meeting app must remain capturable. For whole-
        // monitor input keep the output outside the virtual desktop, where it
        // cannot recursively enter its own source (do not apply WDA exclusion).
        if (source->Monitor() && !output_monitor)
            SetWindowPos(hwnd, nullptr, GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN) + 64,
                GetSystemMetrics(SM_YVIRTUALSCREEN), w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        surface = CreateWindowExW(0, L"STATIC", L"", WS_CHILD, 0,0,w,h,hwnd,nullptr,cls.hInstance,nullptr);
        if (!surface) throw_last_error(); Layout(hwnd);
        com_ptr<IDXGIDevice> dxgi; check_hresult(source->Device()->QueryInterface(__uuidof(IDXGIDevice), dxgi.put_void()));
        com_ptr<IDXGIAdapter> adapter; check_hresult(dxgi->GetAdapter(adapter.put()));
        com_ptr<IDXGIFactory2> factory; check_hresult(adapter->GetParent(__uuidof(IDXGIFactory2), factory.put_void()));
        DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = 1920; desc.Height = 1080; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        check_hresult(factory->CreateSwapChainForHwnd(source->Device(), surface, &desc, nullptr, nullptr, swapchain.put()));
        factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);
        com_ptr<ID3D11Texture2D> backbuffer; check_hresult(swapchain->GetBuffer(0, __uuidof(ID3D11Texture2D), backbuffer.put_void()));
        check_hresult(source->Device()->CreateRenderTargetView(backbuffer.get(), nullptr, target.put()));
        compositor = std::make_unique<GpuCompositor>(source->Device(), source->Context());
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
    void Run() noexcept {
        bool signalled = false, showing = false;
        try {
            Apartment apartment; CreateOutput(); initialized.set_value(); signalled = true;
            bool held = false; double clickX = .5, clickY = .5; int64_t clickAt = 0;
            while (!stopping) {
                MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                if (output_monitor) {
                    MONITORINFO info{sizeof(info)};
                    if (!GetMonitorInfoW(output_monitor, &info)) { failure = HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED); state = 5; source->Enable(false); }
                }
                if (state == 2 && FAILED(source->Error())) { failure = source->Error(); state = 5; }
                if (state == 2 && source->Window() && (!IsWindow(source->Window()) || IsIconic(source->Window()))) { state = 3; source->Enable(false); }
                auto frame = source->Snapshot();
                bool canShow = state == 2 && frame.texture;
                if (canShow) {
                    const auto p = Pose(); const auto cursor = source->Pointer(); int64_t now = Clock100ns();
                    if ((cursor.left || cursor.right) && !held && cursor.visible) { clickX = cursor.x; clickY = cursor.y; clickAt = now; }
                    held = cursor.left || cursor.right;
                    double scale = std::min((1920-2*1080*p.padding)/frame.width, (1080-2*1080*p.padding)/frame.height);
                    GlideRenderFrame f{}; f.crop_w = f.crop_h = 1/p.scale; f.crop_x = p.x-f.crop_w/2; f.crop_y = p.y-f.crop_h/2;
                    f.dest_w = frame.width*scale; f.dest_h = frame.height*scale; f.dest_x = (1920-f.dest_w)/2; f.dest_y = (1080-f.dest_h)/2;
                    f.cursor_x = f.dest_x+(cursor.x-f.crop_x)/f.crop_w*f.dest_w; f.cursor_y = f.dest_y+(cursor.y-f.crop_y)/f.crop_h*f.dest_h;
                    f.cursor_visible = cursor.visible && cursor.x >= f.crop_x && cursor.x <= f.crop_x+f.crop_w && cursor.y >= f.crop_y && cursor.y <= f.crop_y+f.crop_h;
                    f.cursor_scale = p.cursor_scale; f.background_argb = p.background_argb; f.corner_radius = p.corner_radius;
                    f.click_x = f.dest_x+(clickX-f.crop_x)/f.crop_w*f.dest_w; f.click_y = f.dest_y+(clickY-f.crop_y)/f.crop_h*f.dest_h;
                    f.click_amount = p.show_clicks ? std::clamp(1-(now-clickAt)/2'500'000.0,0.0,1.0) : 0;
                    compositor->Draw(frame.texture.get(), target.get(), f, 1920, 1080);
                    check_hresult(swapchain->Present(1,0)); ++presented;
                }
                if (showing != canShow) { showing = canShow; ShowWindow(surface, canShow ? SW_SHOWNOACTIVATE : SW_HIDE); InvalidateRect(output, nullptr, TRUE); }
                if (!canShow) held = false;
                if (state != 2) { if (auto sink = Recording()) sink->Pause(true); }
                // Present(1) already waits for the display; adding another frame delay halves cadence.
                if (!canShow) std::this_thread::sleep_for(std::chrono::milliseconds(25));
            }
        } catch (...) {
            failure = to_hresult(); state = 5; source->Enable(false);
            if (!signalled) initialized.set_exception(std::current_exception());
            if (surface) ShowWindow(surface, SW_HIDE);
            if (auto sink = Recording()) sink->Pause(true);
            // Keep a neutral output window available to a meeting share after a renderer failure.
            if (signalled && output) {
                InvalidateRect(output, nullptr, TRUE);
                while (!stopping) {
                    MSG message{}; while (PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                    std::this_thread::sleep_for(std::chrono::milliseconds(25));
                }
            }
        }
        compositor.reset(); target = nullptr; swapchain = nullptr;
        if (output) { DestroyWindow(output); output = nullptr; } surface = nullptr;
    }
public:
    LivePresentation(std::shared_ptr<CaptureSource> s, HMONITOR destination) : source(std::move(s)), output_monitor(destination) {}
    ~LivePresentation() { Stop(); }
    void Start() { auto future = initialized.get_future(); worker = std::thread([this] { Run(); }); future.get(); }
    void Stop() {
        std::lock_guard lock(stop_mutex); stopping = true; source->Enable(false);
        if (worker.joinable()) worker.join();
        if (auto sink = Recording()) sink->Stop();
        source->Stop();
    }
    void Command(uint32_t command) {
        if (command == 1) {
            if (state != 1 && state != 3) throw hresult_error(E_UNEXPECTED);
            source->Enable(true); state = 2;
        } else if (command == 2 || command == 3) {
            if (auto sink = Recording()) sink->Pause(true);
            state = command == 2 ? 3 : 4; source->Enable(false);
        } else throw hresult_error(E_INVALIDARG);
    }
    GlideLivePose Pose() { std::lock_guard lock(mutex); return pose; }
    void SetPose(const GlideLivePose& p) {
        const double values[]{p.x,p.y,p.scale,p.padding,p.cursor_scale,p.corner_radius};
        for (double v : values) if (!std::isfinite(v)) throw hresult_error(E_INVALIDARG);
        double half = .5/p.scale;
        if (p.scale < 1 || p.scale > 2 || p.x < half-1e-7 || p.x > 1-half+1e-7 || p.y < half-1e-7 || p.y > 1-half+1e-7 ||
            p.padding < 0 || p.padding > .25 || p.cursor_scale < .5 || p.cursor_scale > 4 || p.corner_radius < 0 || p.corner_radius > 80 || p.background_argb >> 24 != 255 || p.show_clicks > 1)
            throw hresult_error(E_INVALIDARG);
        std::lock_guard lock(mutex); pose = p;
    }
    void Stats(GlideLiveStats& out) {
        const auto f = source->Snapshot(); const auto p = source->Pointer();
        out.state = state; out.width = f.width; out.height = f.height; out.frames = presented;
        out.cursor_x = p.x; out.cursor_y = p.y; out.cursor_visible = p.visible; out.buttons = (p.left ? 1u : 0u) | (p.right ? 2u : 0u);
        out.error = failure; out.closed = closed; out.output_window = reinterpret_cast<uintptr_t>(output.load());
    }
    std::shared_ptr<RecordingSink> Record(const fs::path& directory, AudioSelection selected = {}) {
        if (state != 2) throw hresult_error(E_UNEXPECTED);
        if (auto previous = Recording()) { GlideCaptureStats stats{}; previous->Stats(stats); if (stats.state == 1) throw hresult_error(E_UNEXPECTED); }
        auto sink = std::make_shared<RecordingSink>(source, directory, 30, false, [weak = weak_from_this()] {
            if (auto live = weak.lock()) return live->Pose();
            return GlideLivePose{.5,.5,1,0xFF141820,1,.06,1.5,0};
        },std::move(selected));
        sink->Start(); { std::lock_guard lock(recording_mutex); recording = sink; } return sink;
    }
};
