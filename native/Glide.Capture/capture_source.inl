// Shared WGC producer. No files, encoder, or presentation window belong to this class.
// Consumers receive owned GPU textures, never frames borrowed from WGC's pool.
int64_t Clock100ns() {
    LARGE_INTEGER now{}, frequency{}; QueryPerformanceCounter(&now); QueryPerformanceFrequency(&frequency);
    return now.QuadPart / frequency.QuadPart * 10'000'000 + now.QuadPart % frequency.QuadPart * 10'000'000 / frequency.QuadPart;
}
struct SourceBuffer { com_ptr<ID3D11Texture2D> texture; };
struct SourceFrame {
    std::shared_ptr<SourceBuffer> owner;
    com_ptr<ID3D11Texture2D> texture;
    uint32_t width = 0, height = 0;
    uint64_t sequence = 0;
    uint64_t epoch = 0;
    int64_t pts = 0;
    int64_t acquired_at = 0, published_at = 0;
};
struct SourcePointer { double x = .5, y = .5; bool visible = false, left = false, right = false; };
class CaptureSource : public std::enable_shared_from_this<CaptureSource> {
    struct WakeSignal { std::condition_variable changed; };
    struct SessionSignal {
        std::atomic<bool> available{false}, closed{false};
        std::shared_ptr<WakeSignal> wake;
        explicit SessionSignal(std::shared_ptr<WakeSignal> value) : wake(std::move(value)) {}
    };
    HWND window; HMONITOR monitor;
    std::thread worker;
    std::mutex mutex, stop_mutex;
    std::shared_ptr<WakeSignal> wake = std::make_shared<WakeSignal>();
    std::atomic<bool> stopping{false}, desired{false}, active{false};
    std::atomic<HRESULT> error{S_OK};
    std::atomic<uint64_t> epoch{0};
    std::atomic<uint64_t> received_frames{0}, coalesced_frames{0}, busy_buffers{0}, allocated_buffers{0};
    std::promise<void> initialized;
    SourceFrame latest;
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
public:
    CaptureSource(HWND w, HMONITOR m) : window(w), monitor(m) {}
    ~CaptureSource() { Stop(); }
    void Start() { auto future = initialized.get_future(); worker = std::thread([this] { Run(); }); future.get(); }
    void Stop() { std::lock_guard lock(stop_mutex); stopping = true; wake->changed.notify_all(); if (worker.joinable()) worker.join(); }
    void Enable(bool enabled) {
        const HRESULT source_error = error.load();
        if (enabled && FAILED(source_error)) throw hresult_error(source_error);
        { std::lock_guard lock(mutex); if (desired.exchange(enabled) != enabled) { ++epoch; latest = {}; } }
        wake->changed.notify_all();
    }
    bool Active() const { return active && desired && SUCCEEDED(error); }
    HRESULT Error() const { return error; }
    HWND Window() const { return window; }
    HMONITOR Monitor() const { return monitor; }
    ID3D11Device* Device() const { return device.get(); }
    ID3D11DeviceContext* Context() const { return context.get(); }
    void WriteMetrics(std::ostream& out) const {
        out << "{\"framesReceived\":" << received_frames << ",\"coalescedFrames\":" << coalesced_frames
            << ",\"busyBufferSkips\":" << busy_buffers << ",\"buffersAllocated\":" << allocated_buffers << "}";
    }
    SourceFrame Snapshot() { std::lock_guard lock(mutex); return Active() && latest.epoch == epoch ? latest : SourceFrame{}; }
    RECT Bounds() const {
        RECT r{};
        if (window) {
            if (FAILED(DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof(r)))) GetWindowRect(window, &r);
        } else { MONITORINFO info{sizeof(info)}; if (GetMonitorInfoW(monitor, &info)) r = info.rcMonitor; }
        return r;
    }
    SourcePointer Pointer() const {
        SourcePointer p;
        if (!Active() || (window && (!IsWindow(window) || IsIconic(window)))) return p;
        CURSORINFO cursor{sizeof(cursor)};
        if (!GetCursorInfo(&cursor)) return p;
        RECT b = Bounds();
        p.x = (cursor.ptScreenPos.x - b.left) / static_cast<double>(std::max(1L, b.right - b.left));
        p.y = (cursor.ptScreenPos.y - b.top) / static_cast<double>(std::max(1L, b.bottom - b.top));
        p.visible = (cursor.flags & CURSOR_SHOWING) && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
        p.x = std::clamp(p.x, 0.0, 1.0); p.y = std::clamp(p.y, 0.0, 1.0);
        p.left = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; p.right = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        return p;
    }
    bool ReadPixels(const SourceFrame& frame, com_ptr<ID3D11Texture2D>& staging,
        std::vector<uint8_t>& pixels, const std::atomic<bool>* cancel = nullptr) {
        if (!frame.texture) throw hresult_error(HRESULT_FROM_WIN32(ERROR_NO_DATA));
        D3D11_TEXTURE2D_DESC desc{}; frame.texture->GetDesc(&desc);
        desc.BindFlags = 0; desc.MiscFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        D3D11_TEXTURE2D_DESC cached{}; if (staging) staging->GetDesc(&cached);
        if (!staging || cached.Width != desc.Width || cached.Height != desc.Height) {
            staging = nullptr; check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
        }
        // Allocate before Map so an allocation failure cannot leave a resource mapped.
        pixels.resize(static_cast<size_t>(frame.width) * frame.height * 4);
        context->CopyResource(staging.get(), frame.texture.get());
        context->Flush();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        for (;;) {
            if (cancel && cancel->load()) return false;
            // Blocking Map holds the immediate-context thread-safety lock during GPU
            // waits. Polling lets the WGC producer and presentation submit their work.
            const HRESULT hr = context->Map(staging.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            if (hr != DXGI_ERROR_WAS_STILL_DRAWING) { check_hresult(hr); break; }
            if (std::chrono::steady_clock::now() >= deadline) throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        for (uint32_t y = 0; y < frame.height; ++y)
            memcpy(pixels.data() + static_cast<size_t>(y) * frame.width * 4, static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, frame.width * 4);
        context->Unmap(staging.get(), 0); return true;
    }
    std::vector<uint8_t> ReadPixels(const SourceFrame& frame) {
        com_ptr<ID3D11Texture2D> staging; std::vector<uint8_t> pixels;
        ReadPixels(frame, staging, pixels); return pixels;
    }
private:
    void Run() noexcept {
        bool signalled = false;
        std::unique_ptr<Apartment> apartment;
        Direct3D11CaptureFramePool pool{nullptr}; GraphicsCaptureSession session{nullptr};
        try {
            apartment = std::make_unique<Apartment>();
            if (!GraphicsCaptureSession::IsSupported()) throw hresult_error(E_NOTIMPL);
            D3D_FEATURE_LEVEL feature;
            check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION, device.put(), &feature, context.put()));
            context.as<ID3D10Multithread>()->SetMultithreadProtected(TRUE);
            auto dxgi = device.as<IDXGIDevice>(); com_ptr<IInspectable> inspectable;
            check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
            auto rt = inspectable.as<IDirect3DDevice>();
            initialized.set_value(); signalled = true;
            uint64_t sequence = 0;
            while (!stopping) {
                if (!desired) {
                    std::unique_lock lock(mutex); wake->changed.wait_for(lock, std::chrono::milliseconds(100), [&] { return desired || stopping; });
                    continue;
                }
                const uint64_t session_epoch = epoch;
                auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
                GraphicsCaptureItem item{nullptr};
                if (window) {
                    if (!IsWindow(window)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
                    if (IsIconic(window)) { desired = false; continue; }
                    check_hresult(interop->CreateForWindow(window, guid_of<GraphicsCaptureItem>(), put_abi(item)));
                } else check_hresult(interop->CreateForMonitor(monitor, guid_of<GraphicsCaptureItem>(), put_abi(item)));
                auto size = item.Size();
                if (size.Width < 2 || size.Height < 2 || size.Width > 7680 || size.Height > 4320) throw hresult_error(E_INVALIDARG);
                pool = Direct3D11CaptureFramePool::CreateFreeThreaded(rt, DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
                session = pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled(false);
                // Revocation does not make an already executing callback own this stack
                // frame. Callbacks retain only independent signal storage, never this.
                auto signal = std::make_shared<SessionSignal>(wake);
                auto arrived = pool.FrameArrived(auto_revoke, [signal](auto const&, auto const&) noexcept {
                    signal->available = true; signal->wake->changed.notify_all();
                });
                auto closed = item.Closed(auto_revoke, [signal](auto const&, auto const&) noexcept {
                    signal->closed = true; signal->wake->changed.notify_all();
                });
                session.StartCapture(); active = true;
                // Pool ownership is separate from consumers. A texture is reusable only
                // when no published snapshot or consumer still retains its owner.
                std::vector<std::shared_ptr<SourceBuffer>> buffers;
                const auto began = std::chrono::steady_clock::now(); bool received = false;
                while (desired && epoch == session_epoch && !stopping && SUCCEEDED(error)) {
                    if (window && !IsWindow(window)) { error = HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE); desired = false; break; }
                    if (window && IsIconic(window)) { desired = false; break; }
                    if (monitor) { MONITORINFO info{sizeof(info)}; if (!GetMonitorInfoW(monitor, &info)) { error = HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED); break; } }
                    {
                        std::unique_lock lock(mutex); wake->changed.wait_for(lock, std::chrono::milliseconds(30), [&] { return signal->available || signal->closed || !desired || stopping; });
                    }
                    if (signal->closed) { error = HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE); desired = false; break; }
                    if (!desired || epoch != session_epoch || stopping) break;
                    if (!signal->available.exchange(false)) {
                        if (!received && std::chrono::steady_clock::now() - began > std::chrono::seconds(10)) throw hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                        continue;
                    }
                    auto frame = pool.TryGetNextFrame(); if (!frame) continue;
                    ++received_frames;
                    // FrameArrived notifications coalesce. Drain a bounded backlog so
                    // a stalled consumer cannot leave us displaying the oldest frame.
                    for (int pending = 0; pending < 3; ++pending) {
                        auto next = pool.TryGetNextFrame(); if (!next) break;
                        frame.Close(); frame = std::move(next); ++received_frames; ++coalesced_frames;
                    }
                    const auto acquired_at = Clock100ns();
                    auto changedSize = frame.ContentSize();
                    if (changedSize.Width != size.Width || changedSize.Height != size.Height) {
                        frame.Close(); size = changedSize;
                        if (size.Width < 2 || size.Height < 2 || size.Width > 7680 || size.Height > 4320) throw hresult_error(E_INVALIDARG);
                        { std::lock_guard lock(mutex); latest = {}; }
                        buffers.clear();
                        pool.Recreate(rt, DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size); continue;
                    }
                    auto access = frame.Surface().as<IDirect3DDxgiInterfaceAccess>(); com_ptr<ID3D11Texture2D> original;
                    check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), original.put_void()));
                    D3D11_TEXTURE2D_DESC desc{}; original->GetDesc(&desc);
                    desc.Width = static_cast<uint32_t>(size.Width) & ~1u; desc.Height = static_cast<uint32_t>(size.Height) & ~1u;
                    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE; desc.MiscFlags = 0; desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0;
                    SourceFrame copy;
                    for (const auto& buffer : buffers) {
                        if (buffer.use_count() == 1) { copy.owner = buffer; break; }
                    }
                    if (!copy.owner && buffers.size() < 4) {
                        copy.owner = std::make_shared<SourceBuffer>();
                        check_hresult(device->CreateTexture2D(&desc, nullptr, copy.owner->texture.put()));
                        buffers.push_back(copy.owner); ++allocated_buffers;
                    }
                    if (!copy.owner) { ++busy_buffers; frame.Close(); continue; }
                    copy.texture = copy.owner->texture;
                    D3D11_BOX box{0, 0, 0, desc.Width, desc.Height, 1}; context->CopySubresourceRegion(copy.texture.get(), 0, 0, 0, 0, original.get(), 0, &box);
                    copy.width = desc.Width; copy.height = desc.Height; copy.sequence = ++sequence; copy.epoch = session_epoch; copy.pts = frame.SystemRelativeTime().count();
                    frame.Close(); received = true;
                    copy.acquired_at = acquired_at; copy.published_at = Clock100ns();
                    { std::lock_guard lock(mutex); if (desired) latest = std::move(copy); }
                }
                active = false; session.Close(); session = nullptr; arrived.revoke(); closed.revoke(); pool.Close(); pool = nullptr;
                { std::lock_guard lock(mutex); latest = {}; }
                if (FAILED(error)) break;
            }
        } catch (...) {
            error = to_hresult(); if (!signalled) initialized.set_exception(std::current_exception());
            try { if (session) session.Close(); if (pool) pool.Close(); } catch (...) {}
        }
        active = false; desired = false;
        { std::lock_guard lock(mutex); latest = {}; }
    }
};
