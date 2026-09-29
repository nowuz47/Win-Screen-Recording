class GpuCompositor {
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<ID3D11VertexShader> vertex;
    com_ptr<ID3D11PixelShader> pixel;
    com_ptr<ID3D11Buffer> constants;
    com_ptr<ID3D11SamplerState> sampler;
    com_ptr<ID3D11RasterizerState> raster;
    struct Parameters { float crop[4], dest[4], cursor[4], click[4], background[4], canvas[4]; };
public:
    GpuCompositor(ID3D11Device* d, ID3D11DeviceContext* c) {
        device.copy_from(d); context.copy_from(c);
        static constexpr char shader[] = R"hlsl(
cbuffer Scene : register(b0) { float4 crop; float4 dest; float4 cursor; float4 click; float4 background; float4 canvas; }
Texture2D picture : register(t0); SamplerState linearSampler : register(s0);
float4 VS(uint id : SV_VertexID) : SV_POSITION {
    float2 p = float2((id << 1) & 2, id & 2); return float4(p * float2(2,-2) + float2(-1,1), 0, 1);
}
float4 PS(float4 position : SV_POSITION) : SV_TARGET {
    float2 p = position.xy;
    float2 uv = (p - dest.xy) / dest.zw;
    if (any(uv < 0) || any(uv >= 1)) return background;
    float radius = min(canvas.z, min(dest.z, dest.w) * .5);
    float2 corner = max(radius - min(p - dest.xy, dest.xy + dest.zw - p), 0);
    if (dot(corner,corner) > radius * radius) return background;
    float4 result = picture.Sample(linearSampler, crop.xy + uv * crop.zw); result.a = 1;
    if (click.z > 0) {
        float r = (12 + 12 * (1 - click.z)) * canvas.y / 1080;
        float a = saturate(2 - abs(length(p - click.xy) - r)) * click.z;
        result.rgb = lerp(result.rgb, float3(73,216,180)/255, a);
    }
    if (cursor.w > 0) {
        float2 q = (p - cursor.xy) / (cursor.z * canvas.y / 1080);
        if (q.x >= -1 && q.x <= 17 && q.y >= -1 && q.y <= 27) {
            const float2 v[7] = {float2(0,0),float2(0,21),float2(5,16),float2(9,25),float2(12,24),float2(8,15),float2(15,15)};
            bool inside = false; float distance = 1000;
            [unroll] for (int i = 0; i < 7; ++i) {
                int j = i == 0 ? 6 : i-1; float2 a = v[i], b = v[j];
                if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x-a.x)*(q.y-a.y)/(b.y-a.y)+a.x) inside = !inside;
                float2 delta = b-a; float t = saturate(dot(q-a,delta)/dot(delta,delta));
                distance = min(distance,length(q-a-t*delta));
            }
            if (inside || distance <= .8) result.rgb = inside && distance > .8 ? float3(.98,.98,.98) : float3(.06,.06,.06);
        }
    }
    return result;
}
)hlsl";
        com_ptr<ID3DBlob> vs, ps, error;
        check_hresult(D3DCompile(shader, sizeof(shader), nullptr, nullptr, nullptr, "VS", "vs_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, vs.put(), error.put()));
        error = nullptr;
        check_hresult(D3DCompile(shader, sizeof(shader), nullptr, nullptr, nullptr, "PS", "ps_4_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, ps.put(), error.put()));
        check_hresult(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, vertex.put()));
        check_hresult(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, pixel.put()));
        D3D11_BUFFER_DESC cb{}; cb.ByteWidth = sizeof(Parameters); cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        check_hresult(device->CreateBuffer(&cb, nullptr, constants.put()));
        D3D11_SAMPLER_DESC sample{}; sample.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU = sample.AddressV = sample.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sample.MaxLOD = D3D11_FLOAT32_MAX;
        check_hresult(device->CreateSamplerState(&sample, sampler.put()));
        D3D11_RASTERIZER_DESC rasterDesc{}; rasterDesc.FillMode = D3D11_FILL_SOLID; rasterDesc.CullMode = D3D11_CULL_NONE; rasterDesc.DepthClipEnable = TRUE;
        check_hresult(device->CreateRasterizerState(&rasterDesc, raster.put()));
    }
    void Draw(ID3D11Texture2D* texture, ID3D11RenderTargetView* target, const GlideRenderFrame& f, uint32_t width, uint32_t height) {
        Parameters p{};
        const double values[][4] = {{f.crop_x,f.crop_y,f.crop_w,f.crop_h}, {f.dest_x,f.dest_y,f.dest_w,f.dest_h},
            {f.cursor_x,f.cursor_y,f.cursor_scale,static_cast<double>(f.cursor_visible)}, {f.click_x,f.click_y,f.click_amount,0}};
        for (int i = 0; i < 4; ++i) {
            p.crop[i] = static_cast<float>(values[0][i]); p.dest[i] = static_cast<float>(values[1][i]);
            p.cursor[i] = static_cast<float>(values[2][i]); p.click[i] = static_cast<float>(values[3][i]);
        }
        p.background[0] = ((f.background_argb >> 16) & 255) / 255.0f; p.background[1] = ((f.background_argb >> 8) & 255) / 255.0f;
        p.background[2] = (f.background_argb & 255) / 255.0f; p.background[3] = 1;
        p.canvas[0] = static_cast<float>(width); p.canvas[1] = static_cast<float>(height); p.canvas[2] = static_cast<float>(f.corner_radius);
        com_ptr<ID3D11ShaderResourceView> view; check_hresult(device->CreateShaderResourceView(texture, nullptr, view.put()));
        context->UpdateSubresource(constants.get(), 0, nullptr, &p, 0, 0);
        ID3D11Buffer* cb = constants.get(); ID3D11SamplerState* ss = sampler.get(); ID3D11ShaderResourceView* srv = view.get();
        context->OMSetRenderTargets(1, &target, nullptr); context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        D3D11_VIEWPORT viewport{0,0,static_cast<float>(width),static_cast<float>(height),0,1};
        context->RSSetViewports(1, &viewport); context->RSSetState(raster.get());
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.get(), nullptr, 0); context->PSSetShader(pixel.get(), nullptr, 0);
        context->PSSetConstantBuffers(0, 1, &cb); context->PSSetSamplers(0, 1, &ss); context->PSSetShaderResources(0, 1, &srv);
        context->Draw(3, 0);
        srv = nullptr; context->PSSetShaderResources(0, 1, &srv); context->OMSetRenderTargets(0, nullptr, nullptr);
    }
};
