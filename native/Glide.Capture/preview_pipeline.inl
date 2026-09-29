// One MTA worker owns decoder/GPU resources for the lifetime of an editor preview.
// Requests are serialized and the caller retains its buffer until the result is returned.
class PreviewSession {
    uint32_t sw, sh, width, height;
    std::thread worker;
    std::mutex calls, mutex;
    std::condition_variable ready;
    bool stopping = false;
    std::function<void()> pending;
    std::promise<void> initialized;
    std::unique_ptr<RenderReader> reader;
    std::wstring source;
    int64_t last_us = -1, pixels_pts = -1;
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<ID3D11Texture2D> input, output, staging;
    com_ptr<ID3D11RenderTargetView> target;
    std::unique_ptr<GpuCompositor> compositor;
    void Initialize() {
        D3D_FEATURE_LEVEL feature;
        check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),&feature,context.put()));
        compositor = std::make_unique<GpuCompositor>(device.get(),context.get());
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = sw; desc.Height = sh; desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        check_hresult(device->CreateTexture2D(&desc,nullptr,input.put()));
        desc.Width = width; desc.Height = height; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        check_hresult(device->CreateTexture2D(&desc,nullptr,output.put()));
        check_hresult(device->CreateRenderTargetView(output.get(),nullptr,target.put()));
        desc.BindFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        check_hresult(device->CreateTexture2D(&desc,nullptr,staging.put()));
    }
    void Draw(const std::wstring& path, const GlideRenderFrame& frame, uint8_t* pixels) {
        if (!reader || source != path || frame.source_us < last_us) {
            reader = std::make_unique<RenderReader>(path.c_str(),sw,sh); source = path; pixels_pts = -1;
        }
        const auto& decoded = reader->At(frame.source_us); last_us = frame.source_us;
        if (decoded.pts != pixels_pts) {
            const auto bytes = ToBgra(decoded,sw,sh); context->UpdateSubresource(input.get(),0,nullptr,bytes.data(),sw*4,0); pixels_pts = decoded.pts;
        }
        compositor->Draw(input.get(),target.get(),frame,width,height);
        context->CopyResource(staging.get(),output.get());
        D3D11_MAPPED_SUBRESOURCE map{}; check_hresult(context->Map(staging.get(),0,D3D11_MAP_READ,0,&map));
        for (uint32_t y=0;y<height;++y) memcpy(pixels+static_cast<size_t>(y)*width*4,static_cast<const uint8_t*>(map.pData)+static_cast<size_t>(y)*map.RowPitch,width*4);
        context->Unmap(staging.get(),0);
    }
    void Run() noexcept {
        bool signalled = false;
        try {
            Apartment apartment; MediaRuntime media; Initialize(); initialized.set_value(); signalled = true;
            while (true) {
                std::function<void()> job;
                { std::unique_lock lock(mutex); ready.wait(lock,[&]{return stopping || static_cast<bool>(pending);}); if (stopping && !pending) break; job=std::move(pending); pending={}; }
                job();
            }
            reader.reset(); compositor.reset(); target=nullptr; input=nullptr; output=nullptr; staging=nullptr; context=nullptr; device=nullptr;
        } catch (...) { if (!signalled) initialized.set_exception(std::current_exception()); }
    }
public:
    PreviewSession(uint32_t sourceWidth,uint32_t sourceHeight,uint32_t w,uint32_t h) : sw(sourceWidth),sh(sourceHeight),width(w),height(h) {}
    ~PreviewSession() { {std::lock_guard lock(mutex);stopping=true;} ready.notify_all();if(worker.joinable())worker.join(); }
    void Start() { auto future=initialized.get_future();worker=std::thread([this]{Run();});future.get(); }
    HRESULT Frame(const wchar_t* path,const GlideRenderFrame& frame,uint8_t* pixels,uint32_t bytes) {
        if (bytes!=static_cast<uint64_t>(width)*height*4) return E_INVALIDARG;
        ValidateFrame(frame,width,height);
        std::lock_guard call(calls); std::promise<HRESULT> done; auto result=done.get_future();
        { std::lock_guard lock(mutex); if(stopping)return E_UNEXPECTED; pending=[&,path=std::wstring(path)] { try {Draw(path,frame,pixels);done.set_value(S_OK);}catch(...){done.set_value(to_hresult());} }; }
        ready.notify_one();return result.get();
    }
};
