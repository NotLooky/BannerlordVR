// Reads runtime mask geometry without beginning a session or rendering frames.
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

static void require(bool ok, const char* what)
{
    if (!ok) { std::fprintf(stderr, "FAIL: %s\n", what); std::exit(1); }
}
static void xrcheck(XrResult r, const char* what)
{
    if (XR_FAILED(r)) { std::fprintf(stderr, "FAIL: %s (%d)\n", what, int(r)); std::exit(1); }
}
int main(int argc, char** argv)
{
    require(argc == 1 || (argc == 2 && !strcmp(argv[1], "--check-submit")), "usage: probe [--check-submit]");
    uint32_t count = 0;
    xrcheck(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr), "extension count");
    require(count < 4096, "extension count bound");
    std::vector<XrExtensionProperties> extensions(count, { XR_TYPE_EXTENSION_PROPERTIES });
    xrcheck(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, extensions.data()), "extensions");
    bool haveMask = false, haveD3D = false;
    for (const auto& e : extensions)
    {
        haveMask |= !strcmp(e.extensionName, XR_KHR_VISIBILITY_MASK_EXTENSION_NAME);
        haveD3D |= !strcmp(e.extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME);
    }
    require(haveMask && haveD3D, "runtime must support D3D11 and visibility masks");
    const char* names[] = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME, XR_KHR_VISIBILITY_MASK_EXTENSION_NAME };
    XrInstanceCreateInfo ci{ XR_TYPE_INSTANCE_CREATE_INFO };
    ci.enabledExtensionCount = 2; ci.enabledExtensionNames = names;
    ci.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    strcpy_s(ci.applicationInfo.applicationName, "BannerlordVR mask probe");
    XrInstance instance = XR_NULL_HANDLE;
    xrcheck(xrCreateInstance(&ci, &instance), "create instance");
    XrInstanceProperties properties{ XR_TYPE_INSTANCE_PROPERTIES };
    xrcheck(xrGetInstanceProperties(instance, &properties), "runtime identity");
    std::fprintf(stderr, "Runtime: %s, version %u.%u.%u\n",
                 properties.runtimeName, unsigned(XR_VERSION_MAJOR(properties.runtimeVersion)),
                 unsigned(XR_VERSION_MINOR(properties.runtimeVersion)), unsigned(XR_VERSION_PATCH(properties.runtimeVersion)));
    XrSystemGetInfo si{ XR_TYPE_SYSTEM_GET_INFO }; si.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId system = XR_NULL_SYSTEM_ID;
    xrcheck(xrGetSystem(instance, &si, &system), "headset system");
    PFN_xrGetD3D11GraphicsRequirementsKHR getRequirements = nullptr;
    PFN_xrGetVisibilityMaskKHR getMask = nullptr;
    xrcheck(xrGetInstanceProcAddr(instance, "xrGetD3D11GraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&getRequirements)), "D3D entry point");
    xrcheck(xrGetInstanceProcAddr(instance, "xrGetVisibilityMaskKHR", reinterpret_cast<PFN_xrVoidFunction*>(&getMask)), "mask entry point");
    require(getRequirements && getMask, "extension function pointers");
    XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    xrcheck(getRequirements(instance, system, &req), "graphics requirements");
    IDXGIFactory1* factory = nullptr;
    require(SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory))), "DXGI factory");
    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; ; ++i)
    {
        require(SUCCEEDED(factory->EnumAdapters1(i, &adapter)), "runtime adapter exists");
        DXGI_ADAPTER_DESC1 desc = {};
        require(SUCCEEDED(adapter->GetDesc1(&desc)), "adapter description");
        if (desc.AdapterLuid.LowPart == req.adapterLuid.LowPart && desc.AdapterLuid.HighPart == req.adapterLuid.HighPart) break;
        adapter->Release(); adapter = nullptr;
    }
    ID3D11Device* device = nullptr;
    D3D_FEATURE_LEVEL level;
    require(SUCCEEDED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, &level, nullptr)), "D3D11 device");
    require(level >= req.minFeatureLevel, "runtime feature level");
    XrGraphicsBindingD3D11KHR binding{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR }; binding.device = device;
    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO }; sci.next = &binding; sci.systemId = system;
    XrSession session = XR_NULL_HANDLE;
    xrcheck(xrCreateSession(instance, &sci, &session), "create session (not begun)");
    std::puts("{\"masks\":[");
    bool comma = false;
    for (uint32_t eye = 0; eye < 2; ++eye)
    for (auto type : { XR_VISIBILITY_MASK_TYPE_HIDDEN_TRIANGLE_MESH_KHR, XR_VISIBILITY_MASK_TYPE_VISIBLE_TRIANGLE_MESH_KHR })
    {
        XrVisibilityMaskKHR mask{ XR_TYPE_VISIBILITY_MASK_KHR };
        xrcheck(getMask(session, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, eye, type, &mask), "mask sizes");
        require(mask.vertexCountOutput <= 100000 && mask.indexCountOutput <= 300000, "mask size bounds");
        require(mask.indexCountOutput % 3 == 0, "triangle index count");
        std::vector<XrVector2f> vertices(mask.vertexCountOutput);
        std::vector<uint32_t> indices(mask.indexCountOutput);
        if (!vertices.empty() && !indices.empty())
        {
            mask.vertexCapacityInput = static_cast<uint32_t>(vertices.size()); mask.vertices = vertices.data();
            mask.indexCapacityInput = static_cast<uint32_t>(indices.size()); mask.indices = indices.data();
            xrcheck(getMask(session, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, eye, type, &mask), "mask geometry");
            require(mask.vertexCountOutput <= vertices.size() && mask.indexCountOutput <= indices.size(), "returned sizes");
            vertices.resize(mask.vertexCountOutput); indices.resize(mask.indexCountOutput);
        }
        for (auto i : indices) require(i < vertices.size(), "valid vertex index");
        std::printf("%s{\"eye\":%u,\"kind\":\"%s\",\"vertices\":[", comma ? ",\n" : "", eye,
                    type == XR_VISIBILITY_MASK_TYPE_HIDDEN_TRIANGLE_MESH_KHR ? "hidden" : "visible");
        for (size_t i = 0; i < vertices.size(); ++i)
            std::printf("%s[%.9g,%.9g]", i ? "," : "", vertices[i].x, vertices[i].y);
        std::printf("],\"indices\":[");
        for (size_t i = 0; i < indices.size(); ++i) std::printf("%s%u", i ? "," : "", indices[i]);
        std::printf("]}"); comma = true;
    }
    std::puts("\n]}");
    if (argc == 2)
    {
        // Check the exact sRGB format used by the game, preserving its compositor interpretation.
        XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT |
                          XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_UNORDERED_ACCESS_BIT |
                          XR_SWAPCHAIN_USAGE_MUTABLE_FORMAT_BIT;
        info.format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        info.sampleCount = info.arraySize = info.faceCount = info.mipCount = 1;
        info.width = info.height = 64;
        XrSwapchain sc = XR_NULL_HANDLE;
        xrcheck(xrCreateSwapchain(session, &info, &sc), "create sRGB swapchain with UAV/mutable usage");
        uint32_t n = 0;
        xrcheck(xrEnumerateSwapchainImages(sc, 0, &n, nullptr), "swapchain image count");
        require(n > 0 && n <= 64, "bounded nonempty swapchain");
        std::vector<XrSwapchainImageD3D11KHR> images(n, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        xrcheck(xrEnumerateSwapchainImages(sc, n, &n, reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data())), "swapchain images");
        for (const auto& image : images)
        {
            D3D11_TEXTURE2D_DESC desc = {}; image.texture->GetDesc(&desc);
            ID3D11UnorderedAccessView* uav = nullptr;
            D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {};
            uv.Format = DXGI_FORMAT_B8G8R8A8_UNORM; uv.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
            require(SUCCEEDED(device->CreateUnorderedAccessView(image.texture, &uv, &uav)), "UNORM UAV on runtime image");
            ID3D11RenderTargetView* rtv = nullptr;
            D3D11_RENDER_TARGET_VIEW_DESC rv = {};
            rv.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB; rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            require(SUCCEEDED(device->CreateRenderTargetView(image.texture, &rv, &rtv)), "sRGB RTV on runtime image");
            std::fprintf(stderr, "Submit capability: image format %u bind 0x%X; UNORM UAV and sRGB RTV OK\n", desc.Format, desc.BindFlags);
            rtv->Release(); uav->Release();
        }
        xrcheck(xrDestroySwapchain(sc), "destroy probe swapchain");
    }
    xrcheck(xrDestroySession(session), "destroy session");
    xrcheck(xrDestroyInstance(instance), "destroy instance");
    device->Release(); adapter->Release(); factory->Release();
    return 0;
}
