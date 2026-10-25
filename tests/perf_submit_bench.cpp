// Benchmark the existing CAS shader with/without its final copy; no runtime changes.
#define main sharpen_fixture_main
#include "perf_sharpen_test.cpp"
#undef main
#include <algorithm>

static void submit(ID3D11Device* dev, ID3D11DeviceContext* ctx, unsigned eye,
                   ID3D11Texture2D* input, ID3D11Texture2D* dest,
                   ID3D11UnorderedAccessView* destUav, bool directOutput)
{
    if (!directOutput) {
        ctx->CopyResource(dest, bvr::vr_sharpen_apply(dev, ctx, eye, input));
        return;
    }
    require(bvr::vr_sharpen_apply(dev, ctx, eye, input, destUav) == dest, "direct output selected");
}

int main()
{
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL level;
    hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                        D3D11_SDK_VERSION, &dev, &level, &ctx), "hardware device");
    require(level >= D3D_FEATURE_LEVEL_11_0, "compute support");
    constexpr UINT w = 3556, h = 3372, frames = 64;
    ID3D11Texture2D* input[2] = {}; ID3D11Texture2D* dest[2] = {};
    ID3D11Texture2D* copyDest[2] = {};
    ID3D11UnorderedAccessView* uav[2] = {};
    bvr::vr_sharpen_set(0.2f);
    for (unsigned eye = 0; eye < 2; ++eye) {
        std::vector<uint32_t> pixels(w * h);
        uint32_t noise = 12345 + eye;
        for (UINT y = 0; y < h; ++y) for (UINT x = 0; x < w; ++x) {
            noise = noise * 1664525u + 1013904223u;
            pixels[y*w+x] = 0xff000000u | (((x * 255 / w) ^ (noise & 7)) << 16) |
                            (((y * 255 / h) ^ ((noise >> 8) & 7)) << 8) | ((x + y + eye) & 255);
        }
        D3D11_TEXTURE2D_DESC d = {};
        d.Width = w; d.Height = h; d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
        d.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data = { pixels.data(), w*4, 0 };
        hr(dev->CreateTexture2D(&d, &data, &input[eye]), "source");
        d.BindFlags = D3D11_BIND_RENDER_TARGET;
        hr(dev->CreateTexture2D(&d, nullptr, &copyDest[eye]), "original destination without UAV usage");
        d.BindFlags |= D3D11_BIND_UNORDERED_ACCESS;
        hr(dev->CreateTexture2D(&d, nullptr, &dest[eye]), "destination");
        D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {};
        uv.Format = DXGI_FORMAT_B8G8R8A8_UNORM; uv.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        hr(dev->CreateUnorderedAccessView(dest[eye], &uv, &uav[eye]), "destination UAV");
        submit(dev, ctx, eye, input[eye], copyDest[eye], nullptr, false);
        auto expected = readback(dev, ctx, copyDest[eye]);
        submit(dev, ctx, eye, input[eye], dest[eye], uav[eye], true);
        require(readback(dev, ctx, dest[eye]) == expected, "direct and copied output byte-identical");
    }
    ID3D11Query* dj = nullptr; ID3D11Query* t0 = nullptr; ID3D11Query* t1 = nullptr;
    D3D11_QUERY_DESC q = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
    hr(dev->CreateQuery(&q, &dj), "disjoint query"); q.Query = D3D11_QUERY_TIMESTAMP;
    hr(dev->CreateQuery(&q, &t0), "start query"); hr(dev->CreateQuery(&q, &t1), "end query");
    auto batch = [&](bool directOutput) {
        ctx->Begin(dj); ctx->End(t0);
        for (unsigned frame = 0; frame < frames; ++frame)
            for (unsigned eye = 0; eye < 2; ++eye)
                submit(dev, ctx, eye, input[eye], directOutput ? dest[eye] : copyDest[eye], uav[eye], directOutput);
        ctx->End(t1); ctx->End(dj); ctx->Flush();
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint = {};
        UINT64 begin = 0, end = 0;
        const ULONGLONG deadline = GetTickCount64() + 10000;
        auto get = [&](ID3D11Query* query, void* data, UINT bytes) {
            HRESULT status;
            while ((status = ctx->GetData(query, data, bytes, D3D11_ASYNC_GETDATA_DONOTFLUSH)) == S_FALSE) {
                require(GetTickCount64() < deadline, "GPU query timeout"); Sleep(1);
            }
            require(status == S_OK, "GPU query completed");
        };
        get(dj, &disjoint, sizeof(disjoint)); get(t0, &begin, sizeof(begin)); get(t1, &end, sizeof(end));
        require(!disjoint.Disjoint && disjoint.Frequency && end >= begin, "valid GPU timestamps");
        return double(end - begin) * 1000 / double(disjoint.Frequency) / frames;
    };
    for (int warm = 0; warm < 12; ++warm) batch(warm & 1);
    std::vector<double> oldTimes, newTimes, savings;
    std::puts("trial,copy_ms_per_stereo_frame,direct_ms_per_stereo_frame,saved_ms");
    for (int trial = 0; trial < 12; ++trial) {
        double oldMs, newMs;
        if (trial & 1) { newMs = batch(true); oldMs = batch(false); }
        else { oldMs = batch(false); newMs = batch(true); }
        oldTimes.push_back(oldMs); newTimes.push_back(newMs); savings.push_back(oldMs - newMs);
        std::printf("%d,%.6f,%.6f,%.6f\n", trial, oldMs, newMs, oldMs-newMs);
    }
    auto median = [](std::vector<double> v) { std::sort(v.begin(), v.end()); return (v[5]+v[6])*0.5; };
    std::printf("Median: copy %.6f ms, direct %.6f ms, paired saving %.6f ms\n",
                median(oldTimes), median(newTimes), median(savings));
    bvr::vr_sharpen_release(); ctx->ClearState(); ctx->Flush();
    for (unsigned eye = 0; eye < 2; ++eye) { uav[eye]->Release(); dest[eye]->Release(); copyDest[eye]->Release(); input[eye]->Release(); }
    dj->Release(); t0->Release(); t1->Release(); bvr::g_cs->Release(); bvr::g_cb->Release();
    ctx->Release(); dev->Release();
    std::puts("PASS: both eye outputs byte-identical; timings are synthetic, not headset FPS.");
}
