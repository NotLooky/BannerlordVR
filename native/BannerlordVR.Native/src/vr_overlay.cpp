#include "vr_overlay.h"
#include "bvr_api.h"     /* BvrCalibState / BvrCalibCommand - one set of values,
                            named once, rather than the panel and the managed
                            side each carrying their own copy of 0..7 */
#include "bvr_log.h"
#include "ngx_hook.h"   /* TEST 797: the DLSS preset */
#include "vr_sharpen.h"
#include "vrs.h"        /* TEST 427: the panel switches it live */
#include "xr_context.h" /* wrist_hud_set_width (TEST 377e) */
#include "xr_input.h"   /* input_read - the controllers drive the panel too */

#include <windows.h>
#include <d3d11.h>

#include "imgui.h"
#include "backends/imgui_impl_dx11.h"

#include <algorithm>
#include <cstdio>
#include <atomic>
#include <mutex>

namespace bvr {
namespace {

/* Panel size in pixels. Chosen rather than derived: this is the resolution the
   compositor samples the quad at, and it is independent of the eye render
   target, so a player who drops render_scale to claw back frame rate does not
   also make the menu unreadable. */
constexpr uint32_t kPanelWidth  = 1024;
constexpr uint32_t kPanelHeight = 760;   /* room for a tab bar and a pinned footer */

/* The default ImGui font is sized for a monitor at arm's length. A quad layer
   a metre away through headset optics is a much coarser display than that, so
   everything is scaled up rather than shipped unreadable. */
constexpr float kFontScale = 2.0f;

struct OverlayState
{
    bool initialised = false;
    bool initFailed = false;

    ImGuiContext* imgui = nullptr;

    ID3D11Texture2D* texture = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;

    std::atomic<bool> visible{ false };
    std::atomic<bool> justOpened{ false };

    /* Set on every opening so the panel window takes keyboard/gamepad focus,
       and controller navigation works without a mouse click first. */
    std::atomic<bool> navFocus{ false };

    /* Wheel notches from the game thread, taken by the next panel frame. */
    std::atomic<float> scroll{ 0.0f };

    /* TEST 378: the laser, when it is on the panel. Written and read on the
       render thread (the laser is worked out after the frame's quads are
       placed, the panel drawn at the start of the next). */
    bool  laserOn = false;
    float laserU = 0.0f;
    float laserV = 0.0f;
    bool  laserDown = false;

    /* Guards the settings mirror. Written by the render thread (the panel) and
       by the game thread (the managed push), read by both. Two floats and a
       flag do not justify anything more elaborate than a lock held for the
       length of a copy. */
    std::mutex settingsLock;
    OverlaySettings settings;
    bool settingsChanged = false;

    LARGE_INTEGER lastTick{};
    LARGE_INTEGER frequency{};
};

OverlayState g_ov;

/* --------------------------------------------------------------------------
 * Mouse
 *
 * The panel's coordinate space is its own pixels, and the desktop cursor is
 * mapped onto it through the game window's client area. Whatever fraction of
 * the way across the window the cursor sits, it sits the same fraction of the
 * way across the panel - so the mapping survives any window size, any
 * resolution, and the game being windowed or full screen, without needing to
 * know which.
 *
 * The cursor is clamped rather than allowed to leave: a cursor that walks off
 * the panel while the player is dragging a slider would silently drop the drag.
 * ------------------------------------------------------------------------ */
void feed_mouse(ImGuiIO& io)
{
    /* TEST 378: a controller pointing at the panel beats the desktop cursor. */
    if (g_ov.laserOn)
    {
        io.AddMousePosEvent(g_ov.laserU * kPanelWidth, g_ov.laserV * kPanelHeight);
        io.AddMouseButtonEvent(0, g_ov.laserDown);
        const float wheel = g_ov.scroll.exchange(0.0f, std::memory_order_relaxed);
        if (wheel != 0.0f)
            io.AddMouseWheelEvent(0.0f, wheel);
        return;
    }

    POINT cursor{};
    if (!GetCursorPos(&cursor))
        return;

    HWND window = GetForegroundWindow();
    if (window == nullptr)
        return;

    RECT client{};
    if (!GetClientRect(window, &client))
        return;

    const LONG width  = client.right - client.left;
    const LONG height = client.bottom - client.top;
    if (width <= 0 || height <= 0)
        return;

    if (!ScreenToClient(window, &cursor))
        return;

    float u = static_cast<float>(cursor.x) / static_cast<float>(width);
    float v = static_cast<float>(cursor.y) / static_cast<float>(height);

    u = u < 0.0f ? 0.0f : (u > 1.0f ? 1.0f : u);
    v = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);

    io.AddMousePosEvent(u * kPanelWidth, v * kPanelHeight);

    /* GetAsyncKeyState rather than a message hook: we are on the render thread
       inside Present, with no message pump of our own, and installing a WndProc
       hook to read one button is a much larger commitment than this costs.
       Swapped buttons are honoured because Windows reports VK_LBUTTON as the
       PRIMARY button, not the physical left one. */
    const bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    io.AddMouseButtonEvent(0, down);

    const bool rightDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
    io.AddMouseButtonEvent(1, rightDown);

    const float notches = g_ov.scroll.exchange(0.0f, std::memory_order_relaxed);
    if (notches != 0.0f)
        io.AddMouseWheelEvent(0.0f, notches);
}

/* --------------------------------------------------------------------------
 * Controllers
 *
 * ImGui's own gamepad navigation, fed from the OpenXR controllers: either
 * stick is the D-pad (move between controls, and left/right tweaks an active
 * slider), A or X is Activate, B or Y is Cancel, and the grips are L1/R1 -
 * slower and faster slider tweaks. Either hand does the same thing, so the
 * panel works one-handed. The managed pad is silent while the panel is open,
 * so none of this also reaches the game.
 * ------------------------------------------------------------------------ */
void feed_gamepad(ImGuiIO& io)
{
    BvrInputState in{};
    if (!input_read(&in))
        return;

    const BvrHandState* hands[2] = { &in.left, &in.right };

    float x = 0.0f, y = 0.0f;
    bool activate = false, cancel = false, slow = false, fast = false;
    for (const BvrHandState* h : hands)
    {
        if (h->isActive == 0)
            continue;
        if (h->thumbstickX * h->thumbstickX + h->thumbstickY * h->thumbstickY > x * x + y * y)
        {
            x = h->thumbstickX;
            y = h->thumbstickY;
        }
        activate |= (h->buttons & (1 << 0)) != 0;   /* A / X */
        cancel   |= (h->buttons & (1 << 1)) != 0;   /* B / Y */
        if (h->grip >= 0.5f)
            (h == &in.left ? slow : fast) = true;
    }

    /* 0.5 to press a direction; ImGui repeats a held one on its own. */
    constexpr float kStick = 0.5f;
    io.AddKeyEvent(ImGuiKey_GamepadDpadLeft,  x <= -kStick);
    io.AddKeyEvent(ImGuiKey_GamepadDpadRight, x >=  kStick);
    io.AddKeyEvent(ImGuiKey_GamepadDpadUp,    y >=  kStick);
    io.AddKeyEvent(ImGuiKey_GamepadDpadDown,  y <= -kStick);
    /* TEST 525: with the laser on the panel, A is its mouse click; as Activate
       too, one press would click the hovered control AND the nav item. */
    io.AddKeyEvent(ImGuiKey_GamepadFaceDown,  activate && !g_ov.laserOn);
    io.AddKeyEvent(ImGuiKey_GamepadFaceRight, cancel);
    io.AddKeyEvent(ImGuiKey_GamepadL1,        slow);
    io.AddKeyEvent(ImGuiKey_GamepadR1,        fast);
}

bool create_target(ID3D11Device* device)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = kPanelWidth;
    desc.Height = kPanelHeight;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    /* BGRA to match the swapchains, and UNORM rather than the SRGB variant they
       use. CopyResource will move bits between two formats of the same typeless
       family without touching them, which is exactly what is wanted: ImGui's
       colours are already sRGB-encoded, so a copy that reinterprets them as sRGB
       displays them as authored. Converting would darken the whole panel. */
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    if (FAILED(device->CreateTexture2D(&desc, nullptr, &g_ov.texture)))
    {
        BVR_ERR("Overlay: could not create the panel texture.");
        return false;
    }

    if (FAILED(device->CreateRenderTargetView(g_ov.texture, nullptr, &g_ov.rtv)))
    {
        BVR_ERR("Overlay: could not create the panel render target view.");
        return false;
    }

    return true;
}

bool ensure_initialised(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (g_ov.initialised)
        return true;
    if (g_ov.initFailed || device == nullptr || context == nullptr)
        return false;

    IMGUI_CHECKVERSION();
    g_ov.imgui = ImGui::CreateContext();
    ImGui::SetCurrentContext(g_ov.imgui);

    ImGuiIO& io = ImGui::GetIO();
    /* No ini file. This process is a game; leaving a stray imgui.ini beside its
       executable is litter, and the panel has no layout worth restoring. */
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.DisplaySize = ImVec2(static_cast<float>(kPanelWidth),
                            static_cast<float>(kPanelHeight));
    io.FontGlobalScale = kFontScale;

    /* ImGui draws the cursor itself. There is no system cursor inside a headset
       to borrow, and the desktop one is somewhere else entirely. */
    io.MouseDrawCursor = true;

    /* Controller navigation; see feed_gamepad. */
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
    io.BackendFlags |= ImGuiBackendFlags_HasGamepad;

    ImGui::StyleColorsDark();
    ImGuiStyle& style = ImGui::GetStyle();
    style.WindowRounding = 8.0f;
    style.GrabRounding = 4.0f;
    style.FrameRounding = 4.0f;
    style.ScrollbarSize = 24.0f;
    /* Everything a finger-sized cursor has to hit gets bigger. */
    style.FramePadding = ImVec2(10.0f, 8.0f);
    style.ItemSpacing = ImVec2(10.0f, 10.0f);
    style.GrabMinSize = 20.0f;

    if (!ImGui_ImplDX11_Init(device, context))
    {
        BVR_ERR("Overlay: ImGui_ImplDX11_Init failed.");
        g_ov.initFailed = true;
        return false;
    }

    if (!create_target(device))
    {
        g_ov.initFailed = true;
        return false;
    }

    QueryPerformanceFrequency(&g_ov.frequency);
    QueryPerformanceCounter(&g_ov.lastTick);

    g_ov.initialised = true;
    BVR_INFO("Overlay ready: %ux%u panel, ImGui %s.",
             kPanelWidth, kPanelHeight, IMGUI_VERSION);
    return true;
}

float elapsed_seconds()
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);

    double dt = 1.0 / 90.0;
    if (g_ov.frequency.QuadPart > 0)
    {
        dt = static_cast<double>(now.QuadPart - g_ov.lastTick.QuadPart) /
             static_cast<double>(g_ov.frequency.QuadPart);
    }
    g_ov.lastTick = now;

    /* A frame that took a second - a level load, a breakpoint - would make every
       ImGui timer jump. Clamped to something a real frame could be. */
    if (dt <= 0.0 || dt > 0.25)
        dt = 1.0 / 90.0;

    return static_cast<float>(dt);
}

/* --------------------------------------------------------------------------
 * Panel helpers.
 *
 * Every setting is a heading, one short line of grey text, then the control.
 * Hints wrap to the panel instead of being hand-split across TextDisabled
 * lines, so rewording one never means re-breaking a paragraph.
 * ------------------------------------------------------------------------ */
const ImVec4 kWarn{ 1.0f, 0.80f, 0.25f, 1.0f };

void hint(const char* text)
{
    ImGui::PushTextWrapPos(0.0f);
    ImGui::TextDisabled("%s", text);
    ImGui::PopTextWrapPos();
}

void warn(const char* text)
{
    ImGui::PushStyleColor(ImGuiCol_Text, kWarn);
    ImGui::PushTextWrapPos(0.0f);
    ImGui::TextUnformatted(text);
    ImGui::PopTextWrapPos();
    ImGui::PopStyleColor();
}

void section(const char* title)
{
    ImGui::Spacing();
    ImGui::SeparatorText(title);
}

bool slider(const char* id, float* value, float lo, float hi, const char* format)
{
    ImGui::SetNextItemWidth(-1.0f);
    return ImGui::SliderFloat(id, value, lo, hi, format);
}

/* --------------------------------------------------------------------------
 * Render tab: how the headset image is made. Stereo mode, resolution and
 * sharpening belong together - they are the three things traded against
 * frame rate and clarity.
 * ------------------------------------------------------------------------ */
void build_render_ui(OverlaySettings& local, bool& changed)
{
    section("Stereo mode");

    /* One decision, so radio buttons. Stacked rather than on one line: the
       labels do not fit across the panel at double font scale. */
    int mode = local.stereoMode;
    if (ImGui::RadioButton("AFR - alternate eyes", &mode, 0))
        changed = true;
    if (ImGui::RadioButton("Depth reprojection - warp the other eye", &mode, 2))
        changed = true;
    if (ImGui::RadioButton("Native stereo - render both eyes", &mode, 6))
        changed = true;
    local.stereoMode = mode;

    if (local.stereoMode == 0)
        hint("Eyes take turns each frame. Most stable, lightest on the GPU.");
    else if (local.stereoMode == 2)
        hint("The other eye is rebuilt from depth. Smoother motion, soft edges around near objects.");
    else if (local.stereoMode == 6)
        hint("Both eyes fully rendered every frame. Best image, heaviest on the GPU.");

    if (local.stereoPending != 0)
        warn("Takes effect at the next mission.");

    section("Resolution");
    hint("Lower is faster, and applies as you drag. Raising it rebuilds the eye "
         "images when you let go - the view blacks out for a moment.");
    if (slider("##renderscale", &local.renderScale, 0.40f, 2.00f, "%.2f"))
        changed = true;

    /* The size this slider (and the fov crop below) comes out at, by the
       rebuild's own rule, live while dragging. Above the allocated eye images
       there is nothing to shade into until they are rebuilt, so letting go
       does that - the same as the fov crop sliders (TEST 770). */
    uint32_t eyeW = 0, eyeH = 0, nowW = 0, nowH = 0;
    const bool detailedCrop = local.fovCropDetailed != 0;
    const bool haveSize = detailedCrop
        ? bvr::xr_eye_size_at(local.renderScale, local.fovCropLeft, local.fovCropRight, local.fovCropUp,
                              local.fovCropDown, &eyeW, &eyeH)
        : bvr::xr_eye_size_at(local.renderScale, local.fovCropH, local.fovCropH, local.fovCropV,
                              local.fovCropV, &eyeW, &eyeH);
    const bool haveNow = bvr::xr_recommended_size(&nowW, &nowH);
    const bool aboveNow = haveSize && haveNow && (eyeW > nowW || eyeH > nowH);
    if (ImGui::IsItemDeactivatedAfterEdit() && aboveNow && local.resolutionState != 1)
    {
        local.resolutionApply = 1;
        changed = true;
    }
    if (haveSize)
    {
        char size[96];
        if (aboveNow)
            std::snprintf(size, sizeof(size), "%u x %u per eye (now %u x %u)", eyeW, eyeH, nowW, nowH);
        else
            std::snprintf(size, sizeof(size), "%u x %u per eye", eyeW, eyeH);
        ImGui::TextUnformatted(size);
    }

    /* THE BUTTON IS NOT THE SLIDER'S TWIN, and the asymmetry is the honest part.
     *
       Dragging DOWN is already live: the scene is shaded at a smaller fraction
       of the same eye target and rgl upscales into it, so there is nothing to
       rebuild and nothing to wait for. Dragging UP has no detail to ask for
       until the target itself is bigger, and that is a reallocation - the eye
       textures, the swapchains and everything derived from them.
     *
       So letting go above the allocated size rebuilds on its own, and the
       button stays for the other direction: "I dragged it down and want the
       memory back too" is a real thing to want and the rebuild serves it. */
    const bool rebuilding = local.resolutionState == 1;

    if (rebuilding)
        ImGui::BeginDisabled();

    if (ImGui::Button("Apply now", ImVec2(280.0f, 0.0f)))
    {
        local.resolutionApply = 1;
        changed = true;
    }

    if (rebuilding)
        ImGui::EndDisabled();

    if (local.resolutionState == 1)
        warn("Rebuilding the eye images...");
    else if (local.resolutionState == 2)
        hint("Live at the new size.");
    else if (local.resolutionState == 3)
        warn("The rebuild failed - see the log. Restart to be sure of the size.");

    section("Sharpening");
    hint("0 is off. Works with or without DLSS. RCAS goes to 1.50.");
    if (slider("##sharpen", &local.sharpen, 0.00f, local.sharpenMode == 1 ? 1.50f : 1.00f, "%.2f"))
        changed = true;
    /* TEST 790. SAME ORDER as vr_sharpen's modes. */
    static const char* const kSharpeners[] = { "CAS", "RCAS (FSR) - cleaner edges, spares noise" };
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::Combo("##sharpenmode", &local.sharpenMode, kSharpeners, IM_ARRAYSIZE(kSharpeners)))
    {
        if (local.sharpenMode == 0 && local.sharpen > 1.0f)
            local.sharpen = 1.0f;   /* TEST 791: CAS stops at 1 */
        changed = true;
    }

    section("DLSS preset");
    hint("Applies at once (both eyes rebuild their DLSS - one soft frame). Auto = the preset "
         "DLSS Swapper or the NVIDIA App set, else K.");
    /* TEST 797. kPresetValues[i] is the cfg's dlss_preset for kPresets[i]. */
    static const char* const kPresets[] = { "Auto", "J", "K", "L", "M", "DLSS's own choice" };
    static const int kPresetValues[] = { 0, 10, 11, 12, 13, -1 };
    int presetIndex = 0;
    for (int i = 0; i < IM_ARRAYSIZE(kPresetValues); ++i)
        if (kPresetValues[i] == local.dlssPreset)
            presetIndex = i;
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::Combo("##dlsspreset", &presetIndex, kPresets, IM_ARRAYSIZE(kPresets)))
    {
        local.dlssPreset = kPresetValues[presetIndex];
        changed = true;
    }

    section("Field of view");
    /* TEST 770: the fov follows the slider live; letting go rebuilds the eye
       images at the cropped size, which is where the frame time comes from. */
    hint("FOV crop: draw less of the edge of the lens, like Virtual Desktop's FOV "
         "tangent multiplier. 1.00 is the full view. The edges go black and the "
         "eye images shrink with them - faster, same sharpness. Letting go of a "
         "slider rebuilds the eye images (a short black flash).");
    /* TEST 785: detailed = one slider per edge. Turning it on starts the four
       from the simple pair, so nothing moves until a slider does. */
    bool detailed = local.fovCropDetailed != 0;
    if (ImGui::Checkbox("Detailed crop (each edge on its own)", &detailed))
    {
        if (detailed)
        {
            local.fovCropLeft = local.fovCropRight = local.fovCropH;
            local.fovCropUp = local.fovCropDown = local.fovCropV;
        }
        local.fovCropDetailed = detailed ? 1 : 0;
        if (local.resolutionState != 1)
            local.resolutionApply = 1;
        changed = true;
    }

    struct CropSlider { const char* label; const char* id; float* value; };
    const CropSlider simple[] = {
        { "Horizontal", "##fovcroph", &local.fovCropH },
        { "Vertical",   "##fovcropv", &local.fovCropV },
    };
    const CropSlider edges[] = {
        { "Left (left eye, outer edge)",   "##fovcropl", &local.fovCropLeft },
        { "Right (right eye, outer edge)", "##fovcropr", &local.fovCropRight },
        { "Top",    "##fovcropu", &local.fovCropUp },
        { "Bottom", "##fovcropd", &local.fovCropDown },
    };
    const CropSlider* crops = detailed ? edges : simple;
    const int cropCount = detailed ? IM_ARRAYSIZE(edges) : IM_ARRAYSIZE(simple);
    for (int i = 0; i < cropCount; ++i)
    {
        ImGui::TextUnformatted(crops[i].label);
        if (slider(crops[i].id, crops[i].value, 0.50f, 1.00f, "%.2f"))
            changed = true;
        if (ImGui::IsItemDeactivatedAfterEdit() && local.resolutionState != 1)
        {
            local.resolutionApply = 1;
            changed = true;
        }
    }


    /* TEST 427. SAME ORDER as kRings in vrs.cpp. */
    section("Foveated rendering (NVIDIA VRS)");
    hint("Shades the edges of the view at lower detail, where the lens blurs them anyway. "
         "Wide keeps the most at full detail; Narrow is the fastest.");
    static const char* const kVrs[] = { "Off", "Wide", "Balanced", "Narrow" };
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::Combo("##vrs", &local.vrs, kVrs, IM_ARRAYSIZE(kVrs)))
        changed = true;
    if (local.vrs > 0 && bvr::vrs_supported() == 0)
        warn("Not available on this GPU - it needs an NVIDIA RTX 20 / GTX 16 or newer.");
    else if (local.vrs > 0 && local.stereoMode != 6)
        warn("Works in native stereo only.");

    /* TEST 428: greyed rather than hidden while VRS is off, like the HUD
       sliders - a vanishing checkbox hides that it exists. */
    ImGui::BeginDisabled(local.vrs == 0);
    bool followEyes = local.vrsEye != 0;
    if (ImGui::Checkbox("Follow my eyes (eye tracking)", &followEyes))
    {
        local.vrsEye = followEyes ? 1 : 0;
        changed = true;
    }
    if (!followEyes)
        hint("The sharp area stays in the middle of the view.");
    else if (!bvr::input_gaze_bound())
        hint("No eye tracker reported by the headset - the sharp area stays in the middle. "
             "PSVR2 needs PSVR2Toolkit.");
    else if (bvr::input_gaze(nullptr, nullptr))
        hint("Following your gaze: the sharp area is wherever you look.");
    else
        warn("Eye tracker found, but no gaze right now - the sharp area stays in the middle.");
    ImGui::EndDisabled();

    section("World scale");
    hint("Higher makes the world look smaller.");
    if (slider("##worldscale", &local.worldScale, 0.30f, 3.00f, "%.2f"))
        changed = true;
}

/* --------------------------------------------------------------------------
 * Display tab: where things sit and how big they look.
 * ------------------------------------------------------------------------ */
void build_display_ui(OverlaySettings& local, bool& changed)
{
    section("Menu screen");
    hint("The screen for the main menu, campaign map and pause menus.");

    ImGui::TextUnformatted("Size");
    if (slider("##screenwidth", &local.screenWidth, 0.60f, 8.00f, "%.2f m"))
        changed = true;

    ImGui::TextUnformatted("Distance");
    if (slider("##screendistance", &local.screenDistance, 0.50f, 8.00f, "%.2f m"))
        changed = true;
    hint("Changing distance re-centres the screen in front of you.");

    /* TEST 711 */
    bool recenterNew = local.uiRecenterNewScreen != 0;
    if (ImGui::Checkbox("Re-centre when a new screen opens", &recenterNew))
    {
        local.uiRecenterNewScreen = recenterNew ? 1 : 0;
        changed = true;
    }
    ImGui::BeginDisabled(!recenterNew);
    ImGui::TextUnformatted("Only if turned away by more than");
    if (slider("##uirecenterangle", &local.uiRecenterAngle, 0.0f, 180.0f, "%.0f deg"))
        changed = true;
    ImGui::EndDisabled();
    hint("A new menu, popup or results screen comes in front of you only when your head is "
         "turned further than this from the screen. 0 always re-centres.");

    section("Campaign map");
    bool mapVr = local.mapVr != 0;
    if (ImGui::Checkbox("Campaign map in VR", &mapVr))
    {
        local.mapVr = mapVr ? 1 : 0;
        changed = true;
    }
    hint("On: the map is a 3D table in the room. Off: the map is on the menu screen. "
         "A map already in the headset changes at the next campaign load.");

    section("Menu pointer");
    hint("How fast the thumbstick drags the cursor in menus, in screen widths "
         "per second. Lower is easier to land on a button.");
    if (slider("##stickmousespeed", &local.stickMouseSpeed, 0.20f, 3.00f, "%.2f"))
        changed = true;

    section("Battle HUD");

    bool uiShow = local.uiShow != 0;
    if (ImGui::Checkbox("Show HUD", &uiShow))
    {
        local.uiShow = uiShow ? 1 : 0;
        changed = true;
    }
    hint("Health, wheels, scoreboard. The escape menu always shows.");

    /* Greyed rather than hidden while off: a vanishing slider takes the
       layout with it and hides that the settings exist. */
    ImGui::BeginDisabled(!uiShow);

    /* TEST 620: under the split this sizes the head-locked HUD only (order bar,
       notifications). The markers in the world have their own sliders below. */
    ImGui::TextUnformatted("HUD size");
    if (slider("##uiwidth", &local.uiWidth, 0.60f, 6.00f, "%.2f m"))
        changed = true;

    ImGui::TextUnformatted("Battle markers size");
    if (slider("##uiworldbattle", &local.uiWorldScaleBattle, 0.50f, 2.00f, "x%.2f"))
        changed = true;
    hint("Formation and objective markers over the battlefield, on foot and at sea.");

    /* TEST 648 */
    ImGui::TextUnformatted("Pre-battle markers size");
    if (slider("##uiworlddeploy", &local.uiWorldScaleDeploy, 0.15f, 0.60f, "x%.2f"))
        changed = true;
    hint("The formation markers while you deploy. They share a layer with the palette's "
         "cards, so past the size the cards fit they stop growing.");

    ImGui::TextUnformatted("Campaign map labels size");
    if (slider("##uiworldmap", &local.uiWorldScaleMap, 0.50f, 2.00f, "x%.2f"))
        changed = true;
    hint("Town, village and party labels on the 3D campaign map.");

    ImGui::TextUnformatted("Distance");
    if (slider("##uidistance", &local.uiDistance, 0.50f, 6.00f, "%.2f m"))
        changed = true;

    /* TEST 377e: health on the left wrist, kills and army strength on the right. */
    ImGui::TextUnformatted("Battle wrist HUD size");
    if (slider("##wristhudwidth", &local.wristHudWidth, 0.05f, 0.30f, "%.2f m"))
        changed = true;

    /* TEST 527: the campaign map bar on the left hand, sized on its own. */
    ImGui::TextUnformatted("Campaign hand UI size");
    if (slider("##maphanduiwidth", &local.mapHandUiWidth, 0.05f, 0.40f, "%.2f m"))
        changed = true;

    /* TEST 462: two pieces of the flat HUD the headset hides by default. */
    bool hitMarker = local.hudHitMarker != 0;
    if (ImGui::Checkbox("Hit marker", &hitMarker))
    {
        local.hudHitMarker = hitMarker ? 1 : 0;
        changed = true;
    }
    hint("Flashes in front of you when your hit lands. The crosshair itself stays hidden.");

    bool prompts = local.hudPrompts != 0;
    if (ImGui::Checkbox("Pickup prompt (item name and button)", &prompts))
    {
        local.hudPrompts = prompts ? 1 : 0;
        changed = true;
    }
    hint("Looking at a weapon on the ground shows its name and the button that picks it up.");

    ImGui::EndDisabled();

    /* TEST 551: "Hide my body" / "Show hands only" are off the panel - known
       issues, not fixed yet. hide_body / hands_only still work from the cfg. */
}

/* --------------------------------------------------------------------------
 * VR tab (TEST 293): where the head camera sits on the body and how far the head
 * turns before the body follows. TEST 443: Recenter lives in the footer now,
 * beside Reset to defaults, so it is reachable from every tab.
 * ------------------------------------------------------------------------ */
void build_vr_ui(OverlaySettings& local, bool& changed)
{
    section("Head camera");
    hint("Moves the view against the body. Your hands move with it, so they stay "
         "where you feel them.");

    ImGui::TextUnformatted("Height on foot (up / down)");
    if (slider("##headup", &local.headUp, -0.30f, 0.30f, "%.2f m"))
        changed = true;

    /* TEST 442: the saddle has its own height. */
    ImGui::TextUnformatted("Height on a mount (up / down)");
    if (slider("##headupmounted", &local.headUpMounted, -0.30f, 0.30f, "%.2f m"))
        changed = true;

    ImGui::TextUnformatted("Forward / back");
    if (slider("##headforward", &local.headForward, -0.30f, 0.30f, "%.2f m"))
        changed = true;

    section("Body turning");
    hint("How far you can turn your head before the body turns with it. "
         "0 turns the body with every glance.");
    if (slider("##bodyturn", &local.bodyTurnDeg, 0.0f, 180.0f, "%.0f deg"))
        changed = true;

    /* TEST 356: the character is scaled so its eyes are at yours. */
    section("Your height");
    if (local.playerHeight > 0.0f)
        ImGui::Text("Character size x%.2f", local.bodyScale);
    else
        ImGui::TextUnformatted("Character size: the game's own (not measured yet)");
    hint("Stand up straight and press Measure, or set your height by hand. "
         "The character is scaled to match.");
    if (ImGui::Button("Measure my height", ImVec2(280.0f, 0.0f)))
    {
        local.heightMeasure = 1;
        changed = true;
    }
    float height = local.playerHeight > 0.0f ? local.playerHeight : 1.75f;
    if (slider("##playerheight", &height, 1.40f, 2.20f, "%.2f m"))
    {
        local.playerHeight = height;
        changed = true;
    }
    if (ImGui::Button("Use the game's height", ImVec2(280.0f, 0.0f)))
    {
        local.playerHeight = 0.0f;
        changed = true;
    }

    /* TEST 447. TEST 551: the arm length slider (TEST 450) is off the panel -
       known issues, not fixed yet; arm_length_scale still works from the cfg. */
    section("Your body");
    ImGui::TextUnformatted("Upper body weight");
    hint("How slowly the chest follows your head's turns and bends. 0 is instant.");
    if (slider("##bodyweight", &local.bodyWeight, 0.0f, 0.60f, "%.2f s"))
        changed = true;

    /* TEST 485. */
    section("Hands");
    bool fingers = local.handFingers != 0;
    if (ImGui::Checkbox("Five-finger hands", &fingers))
    {
        local.handFingers = fingers ? 1 : 0;
        changed = true;
    }
    hint("Your gloves bend finger by finger and close on what you hold. Off: the game's own hands.");

    section("Controller");
    bool thumbrest = local.dpadThumbrest != 0;
    if (ImGui::Checkbox("D-pad: left thumbrest + right stick", &thumbrest))
    {
        local.dpadThumbrest = thumbrest ? 1 : 0;
        changed = true;
    }
    hint("Quest Touch: rest your left thumb on the thumbrest and push the right stick for the "
         "D-pad. The right stick does not turn the camera while the thumb rests there. "
         "Holding the left stick in works either way.");
}

/* --------------------------------------------------------------------------
 * Motion controls tab.
 *
 * IT ASKS RATHER THAN ACTS
 *
 * This runs on the render thread and can see neither a skeleton nor a controller
 * pose. So the buttons set a COMMAND, and the managed half performs it on the
 * game thread against a live agent. The panel is a display and a set of
 * requests; it is not where calibration happens.
 *
 * AUTOMATIC works out the ORIENTATION by watching where the game's own animation
 * puts the hand and averaging it - the part nobody can eyeball. MANUAL is for
 * how YOU hold a controller. Automatic first, manual to taste.
 * ------------------------------------------------------------------------ */
void build_calibration_ui(OverlaySettings& local, bool& changed)
{
    if (local.calibState == BVR_CALIB_AUTO)
    {
        section("Calibrating");
        hint("Stand still, holding the controllers like a hilt and a shield strap.");

        ImGui::ProgressBar(local.calibProgress, ImVec2(-1.0f, 0.0f));
        ImGui::Spacing();

        if (ImGui::Button("Cancel", ImVec2(220.0f, 0.0f)))
        {
            local.calibCommand = BVR_CALIB_CMD_CANCEL;
            changed = true;
        }

        return;
    }

    if (local.calibState == BVR_CALIB_MANUAL)
    {
        section("Manual calibration");
        hint("Movement is paused. Double-click a slider to type a value.");

        bool moved = false;

        /* A PUSH MUST NOT FIGHT A DRAG.
         *
         * The managed side publishes these six numbers every frame so the sliders
         * follow a hand switch, a reset, or an automatic pass. That same push
         * lands in the middle of a drag. The CyberpunkVR mod hit the sharper
         * version of this - sliders that kept compiled-in defaults and pushed
         * them over a saved calibration on first touch. While a control is being
         * held, it is the authority. */
        const bool dragging = ImGui::IsAnyItemActive();

        static float heldX, heldY, heldZ, heldYaw, heldPitch, heldRoll;

        if (dragging)
        {
            local.calibX = heldX;
            local.calibY = heldY;
            local.calibZ = heldZ;
            local.calibYaw = heldYaw;
            local.calibPitch = heldPitch;
            local.calibRoll = heldRoll;
        }

        int hand = local.calibHand;
        if (ImGui::RadioButton("Weapon hand", &hand, 0) && hand != local.calibHand)
        {
            local.calibCommand = BVR_CALIB_CMD_SELECT_MAIN;
            changed = true;
        }
        ImGui::SameLine();
        if (ImGui::RadioButton("Shield hand", &hand, 1) && hand != local.calibHand)
        {
            local.calibCommand = BVR_CALIB_CMD_SELECT_OFF;
            changed = true;
        }

        section("Position (metres)");
        if (ImGui::SliderFloat("Left / right", &local.calibX, -0.50f, 0.50f, "%.3f"))
            moved = true;
        if (ImGui::SliderFloat("Back / forward", &local.calibY, -0.50f, 0.50f, "%.3f"))
            moved = true;
        if (ImGui::SliderFloat("Down / up", &local.calibZ, -0.50f, 0.50f, "%.3f"))
            moved = true;

        section("Angle (degrees)");
        if (ImGui::SliderFloat("Yaw", &local.calibYaw, -180.0f, 180.0f, "%.1f"))
            moved = true;
        if (ImGui::SliderFloat("Pitch", &local.calibPitch, -180.0f, 180.0f, "%.1f"))
            moved = true;
        if (ImGui::SliderFloat("Roll", &local.calibRoll, -180.0f, 180.0f, "%.1f"))
            moved = true;

        heldX = local.calibX;
        heldY = local.calibY;
        heldZ = local.calibZ;
        heldYaw = local.calibYaw;
        heldPitch = local.calibPitch;
        heldRoll = local.calibRoll;

        if (moved)
        {
            local.calibCommand = BVR_CALIB_CMD_ADJUST;
            changed = true;
        }

        ImGui::Spacing();
        ImGui::Spacing();

        if (ImGui::Button("Save", ImVec2(180.0f, 0.0f)))
        {
            local.calibCommand = BVR_CALIB_CMD_ACCEPT;
            changed = true;
        }
        ImGui::SameLine();
        if (ImGui::Button("Cancel", ImVec2(180.0f, 0.0f)))
        {
            local.calibCommand = BVR_CALIB_CMD_CANCEL;
            changed = true;
        }
        ImGui::SameLine();
        if (ImGui::Button("Reset this hand", ImVec2(260.0f, 0.0f)))
        {
            local.calibCommand = BVR_CALIB_CMD_RESET;
            changed = true;
        }

        return;
    }

    /* Idle. TEST 551: ONE switch for motion controls. Hands on the controllers,
       swing and guard (TEST 245-252), physical collision combat and the body
       following you (TEST 253-257, room-scale steps ride with it on the managed
       side) were four checkboxes nobody wanted apart. It shows on only when all
       four are on; clicking a partial state turns all four on. Each field still
       goes to managed on its own, so nothing there changed but room scale. */
    section("Motion controls");

    bool full = local.motionHands != 0 && local.motionMelee != 0 &&
                local.physicalCombat != 0 && local.bodyIk != 0;
    if (ImGui::Checkbox("Motion controls - full physical combat", &full))
    {
        const int v = full ? 1 : 0;
        local.motionHands = local.motionMelee = local.physicalCombat = local.bodyIk = v;
        changed = true;
    }
    hint("On: your hands hold the weapons, swings and shield blocks land for real, "
         "and your body and room-scale steps follow you. Off: the game's own "
         "combat on the trigger and sticks.");

    /* Greyed rather than hidden while off, like the HUD sliders. */
    ImGui::BeginDisabled(!full);

    /* Enemy physics (docs/enemy-physics.md). -1 from managed hides it: the
       physics is built in but enemy_physics = 0 in the cfg. */
    if (local.enemyPhysics >= 0)
    {
        bool physics = local.enemyPhysics != 0;
        if (ImGui::Checkbox("Enemy physics", &physics))
        {
            local.enemyPhysics = physics ? 1 : 0;
            changed = true;
        }
        hint("On: blades meet the bodies they touch - stabs stay in, blades stop and "
             "recoil, ragdolls and dismemberment. Off: the mod's own physical combat.");
    }

    bool toggleGrip = local.gripToggle != 0;
    if (ImGui::Checkbox("Toggle battle grips", &toggleGrip))
    {
        local.gripToggle = toggleGrip ? 1 : 0;
        changed = true;
    }
    hint("Press to hold, press again to release: weapons, two-handed grips, reins "
         "and other battle holds. Bowstrings stay hold-to-draw, release-to-fire.");

    /* Friendly fire (TEST 261). A separate decision from the melee itself:
       the swings can be on while friends stay un-hittable, which is the
       default - vanilla's own single-player rule, applied to the mod's hits. */
    bool friendlyFire = local.friendlyFire != 0;
    if (ImGui::Checkbox("My swings can hit friends and neutrals", &friendlyFire))
    {
        local.friendlyFire = friendlyFire ? 1 : 0;
        changed = true;
    }
    hint("Off: only enemies can be hit (the vanilla rule). On: a physical swing "
         "lands on anyone - a mission that forbids friendly fire still refuses.");

    /* Body holsters (TEST 271). */
    bool holster = local.holster != 0;
    if (ImGui::Checkbox("Body holsters", &holster))
    {
        local.holster = holster ? 1 : 0;
        changed = true;
    }
    hint("On: trigger over a shoulder or at the left hip draws or puts away a "
         "weapon, and the trigger no longer attacks. Off: the trigger attacks again.");

    /* TEST 645: shield bashing (TEST 642). */
    bool bash = local.ownShieldBash != 0;
    if (ImGui::Checkbox("Weapon hits my own shield", &bash))
    {
        local.ownShieldBash = bash ? 1 : 0;
        changed = true;
    }
    hint("On: your weapon stops on your own shield and clangs - bash it. "
         "Off: it passes through your shield.");

    ImGui::EndDisabled();

    section("Controller");  /* TEST 627: the auto/manual calibration is off the panel */

    /* Chosen, not detected: PSVR2 and Quest both report the Touch profile
       through SteamVR. SAME ORDER as ControllerSuffix in VrHandCalibration.cs. */
    static const char* const kControllers[] = {
        "Generic", "PSVR2", "Quest",
    };
    if (ImGui::Combo("Controller", &local.gripController, kControllers,
                     IM_ARRAYSIZE(kControllers)))
    {
        changed = true;
    }
    hint("Each controller keeps its own grip. A new one starts from Generic.");
}

/* --------------------------------------------------------------------------
 * Calibration tool tab (TEST 750): the weapon tuner and the hand grip on
 * sliders. Managed pushes the held weapon's pose every frame the panel is open,
 * so picking one up fills the sliders; dropping it greys them and leaves the
 * saves, so the last weapon can still go to its item or its class.
 *
 * Each row is [-] slider [+]. The range is long so nothing is ever out of
 * reach; the buttons step finely and repeat while held, which is what a long
 * slider is too coarse for.
 * ------------------------------------------------------------------------ */
bool tool_row(const char* label, int index, float* v, float lo, float hi,
              float step, const char* format, bool& active)
{
    bool moved = false;
    const float button = 64.0f;

    ImGui::PushID(index);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(label);
    ImGui::SameLine(230.0f);

    ImGui::PushItemFlag(ImGuiItemFlags_ButtonRepeat, true);
    if (ImGui::Button("-", ImVec2(button, 0.0f)))
    {
        *v -= step;
        moved = true;
    }
    active |= ImGui::IsItemActive();

    ImGui::SameLine();
    ImGui::SetNextItemWidth(ImGui::GetContentRegionAvail().x - button -
                            ImGui::GetStyle().ItemSpacing.x);
    if (ImGui::SliderFloat("##v", v, lo, hi, format, ImGuiSliderFlags_NoRoundToFormat))
        moved = true;
    active |= ImGui::IsItemActive();

    ImGui::SameLine();
    if (ImGui::Button("+", ImVec2(button, 0.0f)))
    {
        *v += step;
        moved = true;
    }
    active |= ImGui::IsItemActive();
    ImGui::PopItemFlag();
    ImGui::PopID();

    *v = (std::min)(hi, (std::max)(lo, *v));
    return moved;
}

void build_tool_ui(OverlaySettings& local, bool& changed)
{
    /* A push must not fight a drag - the same rule as the manual calibration.
       Keyed on OUR rows only, so a scrollbar drag never shows stale values. */
    static bool wasActive;
    static float held[6];
    if (wasActive)
        for (int i = 0; i < 6; ++i)
            local.toolValue[i] = held[i];

    /* TEST 751: three sets of saved weapon poses. A profile starts as a copy of
       Original and only differs where something was saved in it. */
    section("Pose profile");
    static const char* const kProfiles[] = { "Original", "Profile 1", "Profile 2" };
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::Combo("##toolprofile", &local.toolProfile, kProfiles, IM_ARRAYSIZE(kProfiles)))
        changed = true;
    hint(local.toolProfile == 0
             ? "The mod's own poses. Pick Profile 1 or 2 to experiment without touching them."
             : "Starts as Original; what you save here stays in this profile only. "
               "Switch back to Original at any time - nothing is lost.");

    section("What to align");
    int mode = local.toolMode;
    if (ImGui::RadioButton("Weapon in hand", &mode, 0))
        changed = true;
    ImGui::SameLine(0.0f, 40.0f);
    if (ImGui::RadioButton("Hand on the controller", &mode, 1))
        changed = true;
    local.toolMode = mode;

    int hand = local.toolHand;
    if (ImGui::RadioButton("Main hand", &hand, 0))
        changed = true;
    ImGui::SameLine(0.0f, 40.0f);
    if (ImGui::RadioButton("Off hand", &hand, 1))
        changed = true;
    local.toolHand = hand;

    const bool weapon = local.toolMode == 0;
    const bool live = local.toolLive == 1;

    /* TEST 751: wield one of your own weapons without closing the panel. */
    if (weapon)
    {
        static const char kNone[] = "Nothing (weapons put away)";
        const int w = local.toolWielded;
        const char* preview = w >= 0 && w < 4 && local.toolSlot[w][0] != 0
                            ? local.toolSlot[w] : kNone;
        ImGui::AlignTextToFramePadding();
        ImGui::TextUnformatted("Equip");
        ImGui::SameLine(230.0f);
        ImGui::SetNextItemWidth(-1.0f);
        if (ImGui::BeginCombo("##toolequip", preview))
        {
            for (int i = 0; i < 4; ++i)
            {
                char text[64];
                snprintf(text, sizeof text, "%d: %s##slot%d", i + 1,
                         local.toolSlot[i][0] != 0 ? local.toolSlot[i] : "(empty)", i);
                ImGui::BeginDisabled(local.toolSlot[i][0] == 0);
                if (ImGui::Selectable(text, w == i))
                {
                    local.toolCommand = BVR_TOOL_CMD_EQUIP_0 + i;
                    changed = true;
                }
                ImGui::EndDisabled();
            }
            if (ImGui::Selectable(kNone, w < 0))
            {
                local.toolCommand = BVR_TOOL_CMD_SHEATHE;
                changed = true;
            }
            ImGui::EndCombo();
        }
    }

    ImGui::Spacing();
    if (live)
        ImGui::TextColored(ImVec4(0.45f, 1.0f, 0.55f, 1.0f), "%s", local.toolLabel);
    else
        ImGui::TextDisabled("%s", local.toolLabel);
    if (local.toolStatus[0] != '\0')
        hint(local.toolStatus);

    bool active = false;
    bool moved = false;

    ImGui::BeginDisabled(!live);
    section(weapon ? "Position (metres, in the hand)" : "Position (metres, in your body's frame)");
    moved |= tool_row(weapon ? "X" : "Left / right", 0, &local.toolValue[0], -1.5f, 1.5f, 0.001f, "%.3f m", active);
    moved |= tool_row(weapon ? "Y" : "Back / forward", 1, &local.toolValue[1], -1.5f, 1.5f, 0.001f, "%.3f m", active);
    moved |= tool_row(weapon ? "Z" : "Down / up", 2, &local.toolValue[2], -1.5f, 1.5f, 0.001f, "%.3f m", active);

    section("Rotation (degrees)");
    moved |= tool_row("Yaw", 3, &local.toolValue[3], -360.0f, 360.0f, 0.5f, "%.1f deg", active);
    moved |= tool_row("Pitch", 4, &local.toolValue[4], -360.0f, 360.0f, 0.5f, "%.1f deg", active);
    moved |= tool_row("Roll", 5, &local.toolValue[5], -360.0f, 360.0f, 0.5f, "%.1f deg", active);
    ImGui::EndDisabled();

    hint("Changes show live and are not saved until you press a Save button. "
         "Double-click a slider to type a value; hold - or + to step finely.");

    for (int i = 0; i < 6; ++i)
        held[i] = local.toolValue[i];
    wasActive = active;

    if (moved)
    {
        local.toolCommand = BVR_TOOL_CMD_ADJUST;
        changed = true;
    }

    ImGui::Spacing();
    ImGui::Spacing();

    /* Saves stay usable after a drop (toolLive 0); Zero and Revert are edits,
       so they need the weapon in hand. */
    auto command = [&](const char* text, float width, int cmd, bool enabled)
    {
        ImGui::BeginDisabled(!enabled);
        if (ImGui::Button(text, ImVec2(width, 0.0f)))
        {
            local.toolCommand = cmd;
            changed = true;
        }
        ImGui::EndDisabled();
    };

    if (weapon)
    {
        command("Save to this item", 300.0f, BVR_TOOL_CMD_SAVE_ITEM, local.toolLive >= 0);
        ImGui::SameLine();
        command("Save to its class", 300.0f, BVR_TOOL_CMD_SAVE_CLASS, local.toolLive >= 0);
        hint("Item: only this weapon. Class: every weapon of its type that has no "
             "pose of its own.");
    }
    else
    {
        command("Save hand", 300.0f, BVR_TOOL_CMD_SAVE_HAND, live);
    }

    command("Zero", 180.0f, BVR_TOOL_CMD_ZERO, live);
    ImGui::SameLine();
    command("Revert to saved", 300.0f, BVR_TOOL_CMD_REVERT, live);
}

/* --------------------------------------------------------------------------
 * The panel itself.
 * ------------------------------------------------------------------------ */
void build_ui()
{
    ImGui::SetNextWindowPos(ImVec2(0.0f, 0.0f));
    ImGui::SetNextWindowSize(ImVec2(static_cast<float>(kPanelWidth),
                                    static_cast<float>(kPanelHeight)));
    if (g_ov.navFocus.exchange(false, std::memory_order_relaxed))
        ImGui::SetNextWindowFocus();

    ImGui::Begin("BannerlordVR", nullptr,
                 ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoResize |
                 ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings |
                 ImGuiWindowFlags_NoTitleBar);

    ImGui::TextUnformatted("BannerlordVR");
    ImGui::Spacing();

    OverlaySettings local;
    {
        std::lock_guard<std::mutex> guard(g_ov.settingsLock);
        local = g_ov.settings;
    }

    bool changed = false;

    /* Tabs outside the scroll region, so they stay put while a long tab
       scrolls. The body scrolls; the footer does not: a quad layer has a fixed
       size, and content that outgrows it would run off the bottom and take the
       Close button with it. */
    if (!ImGui::BeginTabBar("##tabs", ImGuiTabBarFlags_None))
    {
        ImGui::End();
        return;
    }

    const float footer = ImGui::GetFrameHeightWithSpacing()
                       + ImGui::GetTextLineHeightWithSpacing()
                       + ImGui::GetStyle().ItemSpacing.y * 4.0f;

    /* Forced onto Motion controls whenever a calibration is running, so the
       panel cannot be showing other sliders while a hand is being moved. */
    const bool calibBusy = local.calibState != BVR_CALIB_IDLE;

    if (ImGui::BeginTabItem("Render"))
    {
        ImGui::BeginChild("##render", ImVec2(0.0f, -footer), false);
        build_render_ui(local, changed);
        ImGui::EndChild();
        ImGui::EndTabItem();
    }

    if (ImGui::BeginTabItem("Display"))
    {
        ImGui::BeginChild("##display", ImVec2(0.0f, -footer), false);
        build_display_ui(local, changed);
        ImGui::EndChild();
        ImGui::EndTabItem();
    }

    if (ImGui::BeginTabItem("Motion controls", nullptr,
                            calibBusy ? ImGuiTabItemFlags_SetSelected : 0))
    {
        ImGui::BeginChild("##motion", ImVec2(0.0f, -footer), false);
        build_calibration_ui(local, changed);
        ImGui::EndChild();
        ImGui::EndTabItem();
    }

    if (ImGui::BeginTabItem("VR"))
    {
        ImGui::BeginChild("##vr", ImVec2(0.0f, -footer), false);
        build_vr_ui(local, changed);
        ImGui::EndChild();
        ImGui::EndTabItem();
    }

    if (ImGui::BeginTabItem("Calibration tool"))
    {
        ImGui::BeginChild("##tool", ImVec2(0.0f, -footer), false);
        build_tool_ui(local, changed);
        ImGui::EndChild();
        ImGui::EndTabItem();
    }

    ImGui::EndTabBar();

    ImGui::Separator();
    ImGui::Spacing();

    if (ImGui::Button("Reset to defaults", ImVec2(280.0f, 0.0f)))
    {
        local.worldScale = 1.0f;
        local.renderScale = 0.9f;
        local.sharpen = 0.0f;
        local.sharpenMode = 0;
        local.dlssPreset = 0;
        local.gripToggle = 0;
        local.vrs = 0;
        local.stereoMode = 0;   /* back to the tested path, not just the numbers */
        changed = true;
    }

    /* TEST 443: the F10 key, from any tab (it was inside the VR tab). The
       same one-shot as before; holding the left stick in for 3 s does it too. */
    ImGui::SameLine();
    if (ImGui::Button("Recenter", ImVec2(200.0f, 0.0f)))
    {
        local.recenter = 1;
        changed = true;
    }
    if (ImGui::IsItemHovered())
        ImGui::SetTooltip("Puts the view back on the body, facing where you look. Same as F10 "
                          "or holding the left stick in for 3 s. Stand up straight: it also "
                          "measures your height.");

    ImGui::SameLine();
    if (ImGui::Button("Close", ImVec2(180.0f, 0.0f)))
        g_ov.visible.store(false, std::memory_order_relaxed);

    ImGui::TextDisabled("Hold both triggers or End: close   A/X: select   B/Y: back   Hold left stick in 3 s: recenter");

    if (changed)
    {
        std::lock_guard<std::mutex> guard(g_ov.settingsLock);

        /* Only the panel's own fields. Everything else in `local` is a stale copy
           of what managed pushed a frame ago, and writing it back would have the
           panel arguing with the game about whose calibration state is current. */
        g_ov.settings.worldScale = local.worldScale;
        g_ov.settings.renderScale = local.renderScale;
        g_ov.settings.stereoMode = local.stereoMode;

        /* EVERY CONTROL THE PANEL OWNS HAS TO BE LISTED HERE.
         *
         * This is the one place a new control can be added and appear to work
         * while doing nothing at all: it draws, it moves, it sets `changed`, so
         * the poll fires - and hands managed back the value from the last push,
         * because the edit was never copied out of `local`. The setting looks
         * like it refuses to apply, which is exactly how it was reported, and
         * nothing in the log says otherwise.
         *
         * The body and motion-hands toggles were added without this line and
         * were inert for the same reason. Anything added to the panel in future
         * belongs here in the same commit. */
        g_ov.settings.hideBody = local.hideBody;
        g_ov.settings.handsOnly = local.handsOnly;
        g_ov.settings.motionHands = local.motionHands;
        g_ov.settings.physicalCombat = local.physicalCombat;
        g_ov.settings.bodyIk = local.bodyIk;
        g_ov.settings.motionMelee = local.motionMelee;
        g_ov.settings.friendlyFire = local.friendlyFire;
        g_ov.settings.gripController = local.gripController;
        g_ov.settings.holster = local.holster;
        g_ov.settings.mapVr = local.mapVr;
        g_ov.settings.headUp = local.headUp;
        g_ov.settings.headForward = local.headForward;
        g_ov.settings.bodyTurnDeg = local.bodyTurnDeg;
        g_ov.settings.recenter = local.recenter;
        g_ov.settings.playerHeight = local.playerHeight;
        g_ov.settings.heightMeasure = local.heightMeasure;
        g_ov.settings.uiShow = local.uiShow;
        g_ov.settings.uiWidth = local.uiWidth;
        g_ov.settings.uiDistance = local.uiDistance;
        g_ov.settings.wristHudWidth = local.wristHudWidth;
        g_ov.settings.screenWidth = local.screenWidth;
        g_ov.settings.screenDistance = local.screenDistance;
        g_ov.settings.sharpen = local.sharpen;
        g_ov.settings.sharpenMode = local.sharpenMode;
        g_ov.settings.dlssPreset = local.dlssPreset;
        g_ov.settings.gripToggle = local.gripToggle;
        g_ov.settings.enemyPhysics = local.enemyPhysics;
        g_ov.settings.vrs = local.vrs;
        g_ov.settings.vrsEye = local.vrsEye;
        g_ov.settings.headUpMounted = local.headUpMounted;
        g_ov.settings.armScale = local.armScale;
        g_ov.settings.bodyWeight = local.bodyWeight;
        g_ov.settings.hudHitMarker = local.hudHitMarker;
        g_ov.settings.hudPrompts = local.hudPrompts;
        g_ov.settings.handFingers = local.handFingers;
        g_ov.settings.mapHandUiWidth = local.mapHandUiWidth;
        g_ov.settings.dpadThumbrest = local.dpadThumbrest;
        g_ov.settings.uiWorldScaleBattle = local.uiWorldScaleBattle;
        g_ov.settings.uiWorldScaleMap = local.uiWorldScaleMap;
        g_ov.settings.ownShieldBash = local.ownShieldBash;
        g_ov.settings.uiWorldScaleDeploy = local.uiWorldScaleDeploy;
        g_ov.settings.uiRecenterNewScreen = local.uiRecenterNewScreen;
        g_ov.settings.uiRecenterAngle = local.uiRecenterAngle;
        g_ov.settings.fovTrueVertical = local.fovTrueVertical;
        g_ov.settings.fovCropH = local.fovCropH;
        g_ov.settings.fovCropV = local.fovCropV;
        g_ov.settings.fovCropDetailed = local.fovCropDetailed;
        g_ov.settings.fovCropLeft = local.fovCropLeft;
        g_ov.settings.fovCropRight = local.fovCropRight;
        g_ov.settings.fovCropUp = local.fovCropUp;
        g_ov.settings.fovCropDown = local.fovCropDown;
        g_ov.settings.stickMouseSpeed = local.stickMouseSpeed;

        /* TEST 750: the Calibration tool. */
        g_ov.settings.toolMode = local.toolMode;
        g_ov.settings.toolHand = local.toolHand;
        g_ov.settings.toolProfile = local.toolProfile;
        for (int i = 0; i < 6; ++i)
            g_ov.settings.toolValue[i] = local.toolValue[i];
        /* A drag after a click must not swallow the click: the values ride
           along with every command, so a pending save outranks an adjust. */
        if (local.toolCommand > BVR_TOOL_CMD_ADJUST || g_ov.settings.toolCommand == 0)
            g_ov.settings.toolCommand = local.toolCommand;

        /* The click, which the panel raises and managed consumes. Listed here
           for the reason the block comment above gives: a control that is not
           copied out of `local` draws, moves and does nothing. */
        g_ov.settings.resolutionApply = local.resolutionApply;

        /* Applied here, on the render thread, the moment it moves: it is a
           setting judged by looking, and the submit that uses it is ours. */
        bvr::vr_sharpen_set(local.sharpen);
        bvr::vr_sharpen_set_mode(local.sharpenMode);
        ngx_set_preset(local.dlssPreset);
        bvr::wrist_hud_set_width(local.wristHudWidth);
        bvr::map_hand_ui_set_width(local.mapHandUiWidth);
        bvr::vrs_set_preset(local.vrs);
        bvr::vrs_set_eye(local.vrsEye != 0);

        if (local.calibCommand != 0)
        {
            g_ov.settings.calibCommand = local.calibCommand;
            g_ov.settings.calibX = local.calibX;
            g_ov.settings.calibY = local.calibY;
            g_ov.settings.calibZ = local.calibZ;
            g_ov.settings.calibYaw = local.calibYaw;
            g_ov.settings.calibPitch = local.calibPitch;
            g_ov.settings.calibRoll = local.calibRoll;
        }

        g_ov.settingsChanged = true;
    }

    ImGui::End();
}

} /* namespace */

void overlay_toggle()
{
    const bool wasVisible = g_ov.visible.load(std::memory_order_relaxed);
    g_ov.visible.store(!wasVisible, std::memory_order_relaxed);

    if (!wasVisible)
    {
        g_ov.justOpened.store(true, std::memory_order_relaxed);
        g_ov.navFocus.store(true, std::memory_order_relaxed);
    }

    BVR_INFO("Overlay %s.", wasVisible ? "closed" : "open");
}

bool overlay_visible()
{
    return g_ov.visible.load(std::memory_order_relaxed);
}

void overlay_show(bool visible)
{
    const bool wasVisible = g_ov.visible.exchange(visible, std::memory_order_relaxed);
    if (wasVisible == visible)
        return;

    if (visible)
    {
        g_ov.justOpened.store(true, std::memory_order_relaxed);
        g_ov.navFocus.store(true, std::memory_order_relaxed);
    }

    BVR_INFO("Overlay %s.", visible ? "open" : "closed");
}

void overlay_set_pointer(bool onPanel, float u, float v, bool down)
{
    g_ov.laserOn = onPanel;
    g_ov.laserU = u;
    g_ov.laserV = v;
    g_ov.laserDown = onPanel && down;
}

void overlay_scroll(float notches)
{
    /* Dropped while hidden, so a wheel turned in the game is not replayed
       into the panel the next time it opens. */
    if (!g_ov.visible.load(std::memory_order_relaxed))
        return;

    float seen = g_ov.scroll.load(std::memory_order_relaxed);
    while (!g_ov.scroll.compare_exchange_weak(seen, seen + notches, std::memory_order_relaxed))
    {
    }
}

bool overlay_take_just_opened()
{
    return g_ov.justOpened.exchange(false, std::memory_order_relaxed);
}

void overlay_set_settings(const OverlaySettings& in)
{
    std::lock_guard<std::mutex> guard(g_ov.settingsLock);

    /* A pending command belongs to the PANEL, not to the push, and a push lands
       once a frame. Copying the struct wholesale would overwrite a click with a
       zero somewhere between the button and the poll that was going to act on
       it - a button that works most of the time, which is worse than one that
       never does. */
    const int32_t pending = g_ov.settings.calibCommand;

    /* The sliders belong to the panel for as long as an edit is unconsumed, for
       the same reason the command does: managed pushes once a frame, and a push
       that landed between a slider moving and the poll that was going to act on
       it would hand back the value the player just changed. */
    const OverlaySettings held = g_ov.settings;

    /* Taken BEFORE the copy below overwrites the flag's subject. */
    const bool unconsumed = g_ov.settingsChanged;

    g_ov.settings = in;
    g_ov.settings.calibCommand = pending;

    if (pending != 0)
    {
        g_ov.settings.calibX = held.calibX;
        g_ov.settings.calibY = held.calibY;
        g_ov.settings.calibZ = held.calibZ;
        g_ov.settings.calibYaw = held.calibYaw;
        g_ov.settings.calibPitch = held.calibPitch;
        g_ov.settings.calibRoll = held.calibRoll;
    }

    /* AN UNCONSUMED EDIT SURVIVES THE PUSH.
     *
     * The wholesale copy above is right for everything managed owns and wrong
     * for anything the player has just moved: a push lands once a frame, and one
     * that arrives between a slider moving and the poll that was going to act on
     * it would hand back the value the player just changed - a setting that
     * applies most of the time, which is harder to diagnose than one that never
     * does.
     *
     * The calibration sliders already had this protection, keyed on a pending
     * command. Generalising it to the flag itself covers every control the panel
     * owns, including the ones added after this was written. */
    if (unconsumed)
    {
        g_ov.settings.worldScale = held.worldScale;
        g_ov.settings.renderScale = held.renderScale;
        g_ov.settings.stereoMode = held.stereoMode;
        g_ov.settings.hideBody = held.hideBody;
        g_ov.settings.handsOnly = held.handsOnly;
        g_ov.settings.motionHands = held.motionHands;
        g_ov.settings.physicalCombat = held.physicalCombat;
        g_ov.settings.bodyIk = held.bodyIk;
        g_ov.settings.motionMelee = held.motionMelee;
        g_ov.settings.friendlyFire = held.friendlyFire;
        g_ov.settings.gripController = held.gripController;
        g_ov.settings.holster = held.holster;
        g_ov.settings.mapVr = held.mapVr;
        g_ov.settings.headUp = held.headUp;
        g_ov.settings.headForward = held.headForward;
        g_ov.settings.bodyTurnDeg = held.bodyTurnDeg;
        g_ov.settings.playerHeight = held.playerHeight;
        g_ov.settings.uiShow = held.uiShow;
        g_ov.settings.uiWidth = held.uiWidth;
        g_ov.settings.uiDistance = held.uiDistance;
        g_ov.settings.wristHudWidth = held.wristHudWidth;
        g_ov.settings.screenWidth = held.screenWidth;
        g_ov.settings.screenDistance = held.screenDistance;
        g_ov.settings.sharpen = held.sharpen;
        g_ov.settings.sharpenMode = held.sharpenMode;
        g_ov.settings.dlssPreset = held.dlssPreset;
        g_ov.settings.gripToggle = held.gripToggle;
        g_ov.settings.enemyPhysics = held.enemyPhysics;
        g_ov.settings.vrs = held.vrs;
        g_ov.settings.vrsEye = held.vrsEye;
        g_ov.settings.headUpMounted = held.headUpMounted;
        g_ov.settings.armScale = held.armScale;
        g_ov.settings.bodyWeight = held.bodyWeight;
        g_ov.settings.hudHitMarker = held.hudHitMarker;
        g_ov.settings.hudPrompts = held.hudPrompts;
        g_ov.settings.handFingers = held.handFingers;
        g_ov.settings.mapHandUiWidth = held.mapHandUiWidth;
        g_ov.settings.dpadThumbrest = held.dpadThumbrest;
        g_ov.settings.uiWorldScaleBattle = held.uiWorldScaleBattle;
        g_ov.settings.uiWorldScaleMap = held.uiWorldScaleMap;
        g_ov.settings.ownShieldBash = held.ownShieldBash;
        g_ov.settings.uiWorldScaleDeploy = held.uiWorldScaleDeploy;
        g_ov.settings.uiRecenterNewScreen = held.uiRecenterNewScreen;
        g_ov.settings.uiRecenterAngle = held.uiRecenterAngle;
        g_ov.settings.fovTrueVertical = held.fovTrueVertical;
        g_ov.settings.fovCropH = held.fovCropH;
        g_ov.settings.fovCropV = held.fovCropV;
        g_ov.settings.fovCropDetailed = held.fovCropDetailed;
        g_ov.settings.fovCropLeft = held.fovCropLeft;
        g_ov.settings.fovCropRight = held.fovCropRight;
        g_ov.settings.fovCropUp = held.fovCropUp;
        g_ov.settings.fovCropDown = held.fovCropDown;
        g_ov.settings.stickMouseSpeed = held.stickMouseSpeed;
        g_ov.settings.toolMode = held.toolMode;
        g_ov.settings.toolHand = held.toolHand;
        g_ov.settings.toolProfile = held.toolProfile;
        for (int i = 0; i < 6; ++i)
            g_ov.settings.toolValue[i] = held.toolValue[i];
        g_ov.settings.toolCommand = held.toolCommand;

        /* A click that has not been polled yet outlives the push, for exactly
           the reason the block above exists: the probe runs once, so a lost
           click is a button that did nothing. */
        g_ov.settings.tableauProbe = held.tableauProbe;
        g_ov.settings.resolutionApply = held.resolutionApply;
        g_ov.settings.recenter = held.recenter;
        g_ov.settings.heightMeasure = held.heightMeasure;

        /* Still unconsumed, so the next poll still has something to take. */
        return;
    }

    /* Deliberately does NOT raise the changed flag. This is the managed side
       telling us what is already true; echoing it straight back would have the
       config file rewritten once for every push. */
    g_ov.settingsChanged = false;
}

bool overlay_take_settings(OverlaySettings* out)
{
    if (out == nullptr)
        return false;

    std::lock_guard<std::mutex> guard(g_ov.settingsLock);

    /* The probe counts as a command for the same reason the calibration ones
       do: the button can be clicked without any SETTING having changed, and a
       poll that only fires on settingsChanged would drop it. */
    const bool hasCommand = g_ov.settings.calibCommand != 0 ||
                            g_ov.settings.tableauProbe != 0 ||
                            g_ov.settings.resolutionApply != 0 ||
                            g_ov.settings.recenter != 0 ||
                            g_ov.settings.heightMeasure != 0 ||
                            g_ov.settings.toolCommand != 0;
    if (!g_ov.settingsChanged && !hasCommand)
        return false;

    *out = g_ov.settings;

    g_ov.settingsChanged = false;
    g_ov.settings.calibCommand = 0;   /* one click, one request */
    g_ov.settings.tableauProbe = 0;   /* likewise - it runs once */
    g_ov.settings.resolutionApply = 0;   /* and likewise - one click, one rebuild */
    g_ov.settings.recenter = 0;          /* one click, one recenter (TEST 293) */
    g_ov.settings.heightMeasure = 0;     /* one click, one measurement (TEST 356) */
    g_ov.settings.toolCommand = 0;       /* one click, one save (TEST 750) */
    return true;
}

ID3D11Texture2D* overlay_texture()
{
    return g_ov.visible.load(std::memory_order_relaxed) ? g_ov.texture : nullptr;
}

uint32_t overlay_width()  { return kPanelWidth; }
uint32_t overlay_height() { return kPanelHeight; }

void overlay_render(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (!g_ov.visible.load(std::memory_order_relaxed))
        return;

    if (!ensure_initialised(device, context))
        return;

    ImGui::SetCurrentContext(g_ov.imgui);

    ImGuiIO& io = ImGui::GetIO();
    io.DeltaTime = elapsed_seconds();
    io.DisplaySize = ImVec2(static_cast<float>(kPanelWidth),
                            static_cast<float>(kPanelHeight));
    feed_mouse(io);
    feed_gamepad(io);

    ImGui_ImplDX11_NewFrame();
    ImGui::NewFrame();
    build_ui();
    ImGui::Render();

    /* The engine's render targets, saved and put back around our own draw.
       ImGui's D3D11 backend restores most of the pipeline it touches, but the
       bound render targets are ours to manage - and leaving the engine drawing
       into a 1024x640 menu texture is not a subtle failure. */
    ID3D11RenderTargetView* savedRtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView* savedDsv = nullptr;
    context->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedRtv, &savedDsv);

    const float transparent[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
    context->OMSetRenderTargets(1, &g_ov.rtv, nullptr);
    context->ClearRenderTargetView(g_ov.rtv, transparent);

    D3D11_VIEWPORT viewport{};
    viewport.Width = static_cast<float>(kPanelWidth);
    viewport.Height = static_cast<float>(kPanelHeight);
    viewport.MaxDepth = 1.0f;
    context->RSSetViewports(1, &viewport);

    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

    context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedRtv, savedDsv);

    for (auto& rtv : savedRtv)
    {
        if (rtv != nullptr)
            rtv->Release();
    }
    if (savedDsv != nullptr)
        savedDsv->Release();
}

void overlay_shutdown()
{
    if (!g_ov.initialised)
        return;

    ImGui::SetCurrentContext(g_ov.imgui);
    ImGui_ImplDX11_Shutdown();
    ImGui::DestroyContext(g_ov.imgui);
    g_ov.imgui = nullptr;

    if (g_ov.rtv != nullptr)
    {
        g_ov.rtv->Release();
        g_ov.rtv = nullptr;
    }
    if (g_ov.texture != nullptr)
    {
        g_ov.texture->Release();
        g_ov.texture = nullptr;
    }

    g_ov.initialised = false;
    BVR_INFO("Overlay shut down.");
}

} /* namespace bvr */
