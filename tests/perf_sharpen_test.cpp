// Include the actual implementation so allocation lifetimes are inspectable.
// The runner also compiles this fixture with the untouched checkpoint source.
#ifndef BVR_SHARPEN_SOURCE
#define BVR_SHARPEN_SOURCE "../native/BannerlordVR.Native/src/vr_sharpen.cpp"
#endif
#include BVR_SHARPEN_SOURCE
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <cstdarg>

static bool legacy = false;
static bool direct = true;
namespace bvr {
bool config_bool(const char* key, bool fallback) { return strcmp(key, "perf_legacy") == 0 ? legacy : fallback; }
float config_float(const char* key, float fallback) { return strcmp(key, "sharpen_direct") == 0 ? (direct ? 1.f : 0.f) : fallback; }
void logf(const char*, const char*, ...) {}
StereoIgnore::StereoIgnore() {}
StereoIgnore::~StereoIgnore() {}
}
static void require(bool ok, const char* what)
{
    if (!ok) { std::fprintf(stderr, "FAIL: %s\n", what); std::exit(1); }
}
static void hr(HRESULT result, const char* what) { require(SUCCEEDED(result), what); }
static std::vector<unsigned char> readback(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex)
{
    D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.BindFlags = 0; d.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    hr(dev->CreateTexture2D(&d, nullptr, &staging), "create readback");
    ctx->CopyResource(staging, tex);
    D3D11_MAPPED_SUBRESOURCE map = {};
    hr(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &map), "map readback");
    std::vector<unsigned char> bytes(d.Width * d.Height * 4);
    for (UINT row = 0; row < d.Height; ++row)
        memcpy(bytes.data() + row * d.Width * 4, static_cast<unsigned char*>(map.pData) + row * map.RowPitch, d.Width * 4);
    ctx->Unmap(staging, 0); staging->Release(); return bytes;
}
int main(int argc, char** argv)
{
    require(argc == 5, "usage: test output.bin legacy(0/1) direct(0/1) hardware(0/1)");
    legacy = atoi(argv[2]) != 0; direct = atoi(argv[3]) != 0;
    FILE* file = nullptr; require(fopen_s(&file, argv[1], "wb") == 0, "open pixel output");
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL level;
    hr(D3D11CreateDevice(nullptr, atoi(argv[4]) ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_WARP,
                        nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &level, &ctx), "create D3D11 device");
    require(level >= D3D_FEATURE_LEVEL_11_0, "compute support");
    unsigned cases = 0;
    const DXGI_FORMAT formats[] = { DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        DXGI_FORMAT_R8G8B8A8_TYPELESS, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        DXGI_FORMAT_B8G8R8A8_TYPELESS, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_R10G10B10A2_TYPELESS };
    for (auto format : formats)
    for (float strength : { 0.f, 0.2f, 1.f })
    {
        bvr::vr_sharpen_release();
        bvr::vr_sharpen_set(strength);
        // Includes resize, then a same-size direct -> fallback transition.
        for (int step = 0; step < 4; ++step)
        for (unsigned eye = 0; eye < 2; ++eye)
        {
            const UINT w = step == 0 ? 35 : 63, h = step == 0 ? 29 : 17;
            const bool canBind = step != 2;
            std::vector<uint32_t> source(w * h);
            for (unsigned i = 0; i < source.size(); ++i) source[i] = i * 2654435761u + eye * 17011u;
            D3D11_TEXTURE2D_DESC d = {};
            d.Width = w; d.Height = h; d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
            d.Format = format; d.Usage = D3D11_USAGE_DEFAULT;
            d.BindFlags = canBind ? D3D11_BIND_SHADER_RESOURCE : 0;
            D3D11_SUBRESOURCE_DATA init = { source.data(), w * 4, 0 };
            ID3D11Texture2D* input = nullptr;
            hr(dev->CreateTexture2D(&d, &init, &input), "create source");

            // Sentinel context state must survive sharpening unchanged.
            D3D11_BUFFER_DESC cbDesc = {}; cbDesc.ByteWidth = 16; cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            ID3D11Buffer* sentinelCb = nullptr;
            hr(dev->CreateBuffer(&cbDesc, nullptr, &sentinelCb), "sentinel CB");
            ctx->CSSetConstantBuffers(0, 1, &sentinelCb);
            auto output = bvr::vr_sharpen_apply(dev, ctx, eye, input);
            require((output == input) == (strength == 0), "sharpen output / disabled passthrough");
            ID3D11Buffer* afterCb = nullptr; ctx->CSGetConstantBuffers(0, 1, &afterCb);
            require(afterCb == sentinelCb, "constant buffer state restored");
            afterCb->Release(); sentinelCb->Release();
            ID3D11ShaderResourceView* afterSrv = nullptr; ctx->CSGetShaderResources(0, 1, &afterSrv);
            ID3D11UnorderedAccessView* afterUav = nullptr; ctx->CSGetUnorderedAccessViews(0, 1, &afterUav);
            ID3D11ComputeShader* afterCs = nullptr; ctx->CSGetShader(&afterCs, nullptr, nullptr);
            require(!afterSrv && !afterUav && !afterCs, "shader / texture state restored");
            auto bytes = readback(dev, ctx, output);
            require(fwrite(bytes.data(), 1, bytes.size(), file) == bytes.size(), "write pixel evidence");
            if (strength > 0 && step < 2)
            {
                bool expectInput = true;
#ifndef BVR_BASELINE_SOURCE
                expectInput = legacy || !direct || format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                              format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
#endif
                require((bvr::g_eye[eye].in != nullptr) == expectInput, "fallback input allocated only when required");
            }
            if (strength > 0 && step == 2) require(bvr::g_eye[eye].in != nullptr, "late fallback available");
#ifndef BVR_BASELINE_SOURCE
            // Direct runtime output must match the original, with incompatible views falling back.
            for (int targetCase = 0; targetCase < 3; ++targetCase)
            {
                DXGI_FORMAT family, view;
                require(bvr::formats_for(format, &family, &view), "target format family");
                D3D11_TEXTURE2D_DESC td = d;
                td.Format = family; td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
                if (targetCase == 1) ++td.Width;
                if (targetCase == 2) {
                    td.Format = family == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_B8G8R8A8_TYPELESS : DXGI_FORMAT_R8G8B8A8_TYPELESS;
                    view = td.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
                }
                ID3D11Texture2D* target = nullptr; ID3D11UnorderedAccessView* uav = nullptr;
                hr(dev->CreateTexture2D(&td, nullptr, &target), "destination texture");
                D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {}; uv.Format = view; uv.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
                hr(dev->CreateUnorderedAccessView(target, &uv, &uav), "destination view");
                const UINT clear[4] = {}; ctx->ClearUnorderedAccessViewUint(uav, clear);
                const auto untouched = readback(dev, ctx, target);
                auto selected = bvr::vr_sharpen_apply(dev, ctx, eye, input, uav);
                require((selected == target) == (targetCase == 0 && strength > 0), "compatible direct destination only");
                require(readback(dev, ctx, selected) == bytes, "direct/fallback pixels equal original output");
                if (selected != target) require(readback(dev, ctx, target) == untouched, "unused destination untouched");
                if (targetCase == 0 && strength > 0) {
                    const auto beforeAlias = readback(dev, ctx, target);
                    auto aliasBaseline = bvr::vr_sharpen_apply(dev, ctx, eye, target);
                    const auto expectedAlias = readback(dev, ctx, aliasBaseline);
                    auto aliasResult = bvr::vr_sharpen_apply(dev, ctx, eye, target, uav);
                    require(aliasResult != target, "never sharpen input in place");
                    require(readback(dev, ctx, aliasResult) == expectedAlias && readback(dev, ctx, target) == beforeAlias,
                            "alias fallback preserves output and source");
                }
                uav->Release(); target->Release();
            }
            afterCb = nullptr; ctx->CSGetConstantBuffers(0, 1, &afterCb);
            require(afterCb == sentinelCb, "direct/fallback CB restored"); afterCb->Release();
            ctx->CSGetShaderResources(0, 1, &afterSrv); ctx->CSGetUnorderedAccessViews(0, 1, &afterUav);
            ctx->CSGetShader(&afterCs, nullptr, nullptr);
            require(!afterSrv && !afterUav && !afterCs, "direct/fallback compute state restored");
#endif
            require(readback(dev, ctx, input) == std::vector<unsigned char>(reinterpret_cast<unsigned char*>(source.data()),
                    reinterpret_cast<unsigned char*>(source.data()) + source.size() * 4), "source pixels unchanged");
            input->Release(); ++cases;
        }
    }
#ifndef BVR_BASELINE_SOURCE
    // TEST 790: RCAS leaves flat colour alone and differs from CAS. Not written
    // to the pixel evidence.
    for (int flat = 0; flat < 2; ++flat)
    {
        const UINT w = 33, h = 21;
        std::vector<uint32_t> source(w * h);
        // Low-contrast detail (~0x70-0x8F per channel): where 1.5's extra lands.
        for (unsigned i = 0; i < source.size(); ++i) source[i] = flat ? 0xFF4080C0u : 0xFF707070u | ((i * 2654435761u) & 0x001F1F1Fu);
        D3D11_TEXTURE2D_DESC d = {};
        d.Width = w; d.Height = h; d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
        d.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA init = { source.data(), w * 4, 0 };
        ID3D11Texture2D* input = nullptr;
        hr(dev->CreateTexture2D(&d, &init, &input), "create RCAS source");
        bvr::vr_sharpen_set(1.f);
        bvr::vr_sharpen_set_mode(0);
        const auto cas = readback(dev, ctx, bvr::vr_sharpen_apply(dev, ctx, 0, input));
        bvr::vr_sharpen_set_mode(1);
        const auto rcas = readback(dev, ctx, bvr::vr_sharpen_apply(dev, ctx, 0, input));
        // TEST 791: 1.5 is RCAS only; CAS stays at its 1.0.
        bvr::vr_sharpen_set(1.5f);
        const auto rcasMax = readback(dev, ctx, bvr::vr_sharpen_apply(dev, ctx, 0, input));
        bvr::vr_sharpen_set_mode(0);
        const auto casMax = readback(dev, ctx, bvr::vr_sharpen_apply(dev, ctx, 0, input));
        require(casMax == cas, "CAS stops at 1.0");
        if (flat) require(rcas == readback(dev, ctx, input) && rcasMax == rcas, "RCAS leaves flat colour alone");
        if (!flat) require(rcas != cas && rcasMax != rcas, "RCAS differs from CAS, and 1.5 from 1.0");
        input->Release();
    }
#endif
    bvr::vr_sharpen_release();
    require(!bvr::g_eye[0].in && !bvr::g_eye[1].in && !bvr::g_eye[0].out && !bvr::g_eye[1].out, "teardown");
    ctx->ClearState(); ctx->Flush();
#ifdef BVR_BASELINE_SOURCE
    if (bvr::g_cs) bvr::g_cs->Release();
#else
    for (auto* cs : bvr::g_cs) if (cs) cs->Release();
#endif
    if (bvr::g_cb) bvr::g_cb->Release();
    ctx->Release(); dev->Release(); fclose(file);
    std::printf("PASS: %u sharpening cases, source preservation, fallback, resizing, context state, teardown.\n", cases);
    return 0;
}
