#include "vr_overlay.h"
#include "bvr_api.h"     /* BvrCalibState / BvrCalibCommand - one set of values,
                            named once, rather than the panel and the managed
                            side each carrying their own copy of 0..7 */
#include "bvr_log.h"
#include "vr_sharpen.h"

#include <windows.h>
#include <d3d11.h>

#include "imgui.h"
#include "backends/imgui_impl_dx11.h"

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
 * Motion controller calibration.
 *
 * WHY THIS LIVES HERE AND NOT BEHIND A KEY
 *
 * Calibration used to be a hidden mode opened with Home, driven with the sticks,
 * with nothing on screen. That is a bad shape for a job whose entire content is
 * "look at the result and adjust" - the player could not see which hand was
 * being edited, whether anything had been captured, or what the buttons did.
 * Every one of those is a fact the panel can simply show.
 *
 * IT ASKS RATHER THAN ACTS
 *
 * This runs on the render thread and can see neither a skeleton nor a controller
 * pose. So the buttons set a COMMAND, and the managed half performs it on the
 * game thread against a live agent. The panel is a display and a set of
 * requests; it is not where calibration happens.
 *
 * TWO OPTIONS, AND THEY ANSWER DIFFERENT QUESTIONS
 *
 * AUTOMATIC works out the ORIENTATION on its own, by watching where the game's
 * own animation puts the hand over a couple of seconds and averaging it. That is
 * the part nobody can eyeball, because it depends on how the skeleton's artist
 * oriented a bone against how OpenXR orients a grip pose.
 *
 * MANUAL is for the part the game has no opinion about: how YOU hold a
 * controller. It moves the hand under the sticks while you watch it.
 *
 * Automatic first, manual to taste. The wobble figure is what says whether to
 * trust the automatic one.
 * ------------------------------------------------------------------------ */
void build_display_ui(OverlaySettings& local, bool& changed);

void build_calibration_ui(OverlaySettings& local, bool& changed)
{
    ImGui::TextUnformatted("Motion controller calibration");

    const bool haveMain = (local.calibHave & 1) != 0;
    const bool haveOff = (local.calibHave & 2) != 0;

    if (local.calibState == BVR_CALIB_AUTO)
    {
        ImGui::TextDisabled("Hold still. Stand in a neutral pose and hold the");
        ImGui::TextDisabled("controllers the way you would hold a hilt and a strap.");

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
        ImGui::TextDisabled("Movement is suspended while this is open.");
        ImGui::Spacing();

        /* Sliders rather than the sticks this replaced.
         *
         * Nudging with a stick is a rate, and a rate has no home: you cannot see
         * how far you have gone, you cannot get back to nought, and two people
         * holding the stick for different lengths of time get different answers
         * to the same question. A slider is the value itself. It shows where the
         * hand is, it can be dragged straight to a number, and double-clicking
         * types one in - which matters because "my hand is four centimetres too
         * far forward" is a thing a player can actually measure by looking.
         *
         * Position first, because that is the complaint these are here for. The
         * angles sit underneath and start at nought: the automatic pass has
         * already answered the orientation question, and these compose on top of
         * its answer rather than replacing it. */
        bool moved = false;

        /* A PUSH MUST NOT FIGHT A DRAG.
         *
         * The managed side publishes these six numbers every frame so the sliders
         * follow a hand switch, a reset, or an automatic pass. That same push
         * lands in the middle of a drag, and then the slider is being written by
         * two people at once.
         *
         * Here it is survivable, because the value that comes back is the one that
         * was just sent. It is worth guarding anyway: the CyberpunkVR mod hit the
         * sharper version of this - its calibration sliders were one-way UI ->
         * backend, kept their compiled-in defaults after the backend loaded a saved
         * calibration, and the first touch of any slider therefore pushed fourteen
         * default values over the real one, which the next Save wrote to disk. Its
         * fix is this line, and it is the right shape whichever direction the
         * staleness runs in: while a control is being held, it is the authority. */
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

        ImGui::TextUnformatted("Position (metres, in your character's own frame)");
        if (ImGui::SliderFloat("X  left / right", &local.calibX, -0.50f, 0.50f, "%.3f"))
            moved = true;
        if (ImGui::SliderFloat("Y  back / forward", &local.calibY, -0.50f, 0.50f, "%.3f"))
            moved = true;
        if (ImGui::SliderFloat("Z  down / up", &local.calibZ, -0.50f, 0.50f, "%.3f"))
            moved = true;

        ImGui::Spacing();
        ImGui::TextUnformatted("Angle (degrees, on top of the automatic capture)");
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
        ImGui::TextDisabled("X is your left/right, Y forward, Z up - your character's own");
        ImGui::TextDisabled("directions, not the controller's, so Z is up whichever way you");
        ImGui::TextDisabled("are holding it. Double-click a slider to type a number.");
        ImGui::Spacing();

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

    /* Idle. */
    if (haveMain && haveOff)
        ImGui::TextDisabled("Both hands calibrated.");
    else if (haveMain || haveOff)
        ImGui::TextDisabled("Only the %s is calibrated.",
                            haveMain ? "weapon hand" : "shield hand");
    else
        ImGui::TextDisabled("Not calibrated yet - the hands are on their animation.");

    /* The wobble from the last automatic run, and what it means. A character who
       was walking gives tens of degrees, and the honest thing is to say so and
       offer the retry rather than to present the average as an answer. */
    if (local.calibSpread > 0.0f)
    {
        if (local.calibSpread > 12.0f)
        {
            ImGui::TextColored(ImVec4(1.0f, 0.80f, 0.25f, 1.0f),
                               "Last capture wobbled %.0f deg - you were moving.",
                               local.calibSpread);
            ImGui::TextColored(ImVec4(1.0f, 0.80f, 0.25f, 1.0f),
                               "Stand still and run it again.");
        }
        else
        {
            ImGui::TextDisabled("Last capture wobbled %.1f deg - a good sample.",
                                local.calibSpread);
        }
    }

    ImGui::Spacing();

    if (ImGui::Button("Automatic calibration", ImVec2(340.0f, 0.0f)))
    {
        local.calibCommand = BVR_CALIB_CMD_AUTO;
        changed = true;
    }
    ImGui::SameLine();
    if (ImGui::Button("Manual calibration", ImVec2(300.0f, 0.0f)))
    {
        local.calibCommand = BVR_CALIB_CMD_MANUAL;
        changed = true;
    }

    ImGui::Spacing();
    ImGui::TextDisabled("Automatic finds the ORIENTATION by watching the game's own");
    ImGui::TextDisabled("animation for two seconds - the part that cannot be");
    ImGui::TextDisabled("eyeballed. Manual adjusts how it sits in YOUR hand, which");
    ImGui::TextDisabled("the game has no opinion about. Do automatic first.");
}

/* --------------------------------------------------------------------------
 * The panel itself.
 *
 * Two settings, and they behave differently on purpose - so each one says which
 * it is rather than leaving the player to find out.
 * ------------------------------------------------------------------------ */
void build_ui()
{
    ImGui::SetNextWindowPos(ImVec2(0.0f, 0.0f));
    ImGui::SetNextWindowSize(ImVec2(static_cast<float>(kPanelWidth),
                                    static_cast<float>(kPanelHeight)));

    ImGui::Begin("BannerlordVR", nullptr,
                 ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoResize |
                 ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings |
                 ImGuiWindowFlags_NoTitleBar);

    ImGui::TextUnformatted("BannerlordVR");
    ImGui::Separator();
    ImGui::Spacing();

    OverlaySettings local;
    {
        std::lock_guard<std::mutex> guard(g_ov.settingsLock);
        local = g_ov.settings;
    }

    bool changed = false;

    /* TABS, ONCE THERE WAS A SECOND THING IN HERE.
     *
     * Calibration and display settings are visited at different times and for
     * different reasons - one when something looks wrong in the world, the other
     * when something feels wrong in your hand - and stacking them made a panel
     * that scrolled past the bottom of its own quad layer. A tab is also what
     * lets the calibration key open the panel ON calibration, rather than open a
     * panel and leave the player to find it. */
    /* The body scrolls; the footer does not.
     *
     * A quad layer has a fixed size in metres and the panel has a fixed size in
     * pixels, so content that outgrows it does not push the window - it runs off
     * the bottom edge and takes the Close button with it. Which is how you end up
     * unable to shut a menu from inside a headset. Reserving the footer's height
     * and giving the rest to a scrolling child makes that impossible however much
     * either tab grows. */
    const float footer = ImGui::GetFrameHeightWithSpacing()
                       + ImGui::GetTextLineHeightWithSpacing() * 2.0f
                       + ImGui::GetStyle().ItemSpacing.y * 4.0f;

    ImGui::BeginChild("##body", ImVec2(0.0f, -footer), false);

    if (!ImGui::BeginTabBar("##tabs", ImGuiTabBarFlags_None))
    {
        ImGui::EndChild();
        ImGui::End();
        return;
    }

    /* Forced onto calibration whenever a calibration is actually running, so the
       panel cannot be showing sliders while the sticks are moving a hand. */
    const bool calibBusy = local.calibState != BVR_CALIB_IDLE;

    if (ImGui::BeginTabItem("Display"))
    {
        build_display_ui(local, changed);
        ImGui::EndTabItem();
    }

    if (ImGui::BeginTabItem("Calibration", nullptr,
                            calibBusy ? ImGuiTabItemFlags_SetSelected : 0))
    {
        build_calibration_ui(local, changed);
        ImGui::EndTabItem();
    }

    ImGui::EndTabBar();
    ImGui::EndChild();

    ImGui::Separator();
    ImGui::Spacing();

    if (ImGui::Button("Reset to defaults", ImVec2(280.0f, 0.0f)))
    {
        local.worldScale = 1.0f;
        local.renderScale = 0.9f;
        local.sharpen = 0.0f;
        local.stereoMode = 0;   /* back to the tested path, not just the numbers */
        changed = true;
    }

    ImGui::SameLine();
    if (ImGui::Button("Close", ImVec2(180.0f, 0.0f)))
        g_ov.visible.store(false, std::memory_order_relaxed);

    ImGui::Spacing();
    ImGui::TextDisabled("Every change is saved to BannerlordVR.cfg as you make it.");
    ImGui::TextDisabled("End closes this panel. F10 recentres.");

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
        g_ov.settings.motionHands = local.motionHands;
        g_ov.settings.uiShow = local.uiShow;
        g_ov.settings.uiWidth = local.uiWidth;
        g_ov.settings.uiDistance = local.uiDistance;
        g_ov.settings.screenWidth = local.screenWidth;
        g_ov.settings.screenDistance = local.screenDistance;
        g_ov.settings.sharpen = local.sharpen;

        /* Applied here, on the render thread, the moment it moves: it is a
           setting judged by looking, and the submit that uses it is ours. */
        bvr::vr_sharpen_set(local.sharpen);

        /* Only ever raised here, never lowered: it is cleared on the read, in
           overlay_take_settings, the same way calibCommand is. Copying a zero
           back over it would lose a click that arrived in the same frame as
           some other edit. */
        if (local.tableauProbe != 0)
            g_ov.settings.tableauProbe = local.tableauProbe;

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

/* The display half of the panel: two settings that behave differently on
   purpose, so each one says which it is rather than leaving the player to find
   out. */
void build_display_ui(OverlaySettings& local, bool& changed)
{
    ImGui::TextUnformatted("World scale");
    ImGui::TextDisabled("Applies immediately. Higher makes the world smaller.");
    if (ImGui::SliderFloat("##worldscale", &local.worldScale, 0.30f, 3.00f, "%.2f"))
        changed = true;

    ImGui::Spacing();
    ImGui::Spacing();

    ImGui::TextUnformatted("Resolution");
    ImGui::TextDisabled("Live: the scene is shaded at the new size straight away,");
    ImGui::TextDisabled("and the eye targets keep their dimensions - nothing is");
    ImGui::TextDisabled("resized while the compositor is reading it.");
    ImGui::TextDisabled("Raising it past where it was at LAUNCH is the exception:");
    ImGui::TextDisabled("the target has no more detail to give, so that part waits.");
    if (ImGui::SliderFloat("##renderscale", &local.renderScale, 0.40f, 1.40f, "%.2f"))
        changed = true;

    ImGui::Spacing();
    ImGui::Spacing();

    ImGui::TextUnformatted("Sharpening");
    ImGui::TextDisabled("Applies immediately, in every render mode, with or without");
    ImGui::TextDisabled("DLSS. 0 is off. The game's own slider does nothing with");
    ImGui::TextDisabled("DLSS on - DLSS stopped sharpening in its 2.5.1 release.");
    if (ImGui::SliderFloat("##sharpen", &local.sharpen, 0.00f, 1.00f, "%.2f"))
        changed = true;

    ImGui::Spacing();
    ImGui::Separator();
    ImGui::Spacing();

    ImGui::TextUnformatted("Screen");
    ImGui::TextDisabled("The panel the main menu, the campaign map and the pause");
    ImGui::TextDisabled("menus appear on. A screen across the room, rather than a");
    ImGui::TextDisabled("layer on your view - so it gets its own size and distance.");
    ImGui::Spacing();

    ImGui::TextUnformatted("Size");
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::SliderFloat("##screenwidth", &local.screenWidth, 0.60f, 8.00f, "%.2f m"))
        changed = true;

    ImGui::TextUnformatted("Distance");
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::SliderFloat("##screendistance", &local.screenDistance, 0.50f, 8.00f, "%.2f m"))
        changed = true;

    ImGui::TextDisabled("Moving the distance re-places the screen in front of you,");
    ImGui::TextDisabled("because where it hangs is decided when it is put there.");

    ImGui::Spacing();
    ImGui::Separator();
    ImGui::Spacing();

    ImGui::TextUnformatted("Game interface");
    ImGui::Spacing();

    bool uiShow = local.uiShow != 0;
    if (ImGui::Checkbox("Show the game's interface", &uiShow))
    {
        local.uiShow = uiShow ? 1 : 0;
        changed = true;
    }

    ImGui::TextDisabled("The health bar, the wheels, the scoreboard and anything");
    ImGui::TextDisabled("else the game draws, keyed and laid over the world. Off");
    ImGui::TextDisabled("leaves the view clear; the escape menu still appears.");

    /* Greyed rather than hidden while it is off: a slider that vanishes takes
       the layout with it, and the player has to turn the thing back on to
       discover the settings still exist. */
    ImGui::Spacing();
    ImGui::BeginDisabled(!uiShow);

    ImGui::TextUnformatted("Size");
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::SliderFloat("##uiwidth", &local.uiWidth, 0.60f, 6.00f, "%.2f m"))
        changed = true;

    ImGui::TextUnformatted("Distance");
    ImGui::SetNextItemWidth(-1.0f);
    if (ImGui::SliderFloat("##uidistance", &local.uiDistance, 0.50f, 6.00f, "%.2f m"))
        changed = true;

    ImGui::TextDisabled("Size is how wide it hangs AT that distance, so the two");
    ImGui::TextDisabled("together decide how much of your view it covers. Further");
    ImGui::TextDisabled("away is easier on the eyes; closer is easier to read.");

    ImGui::EndDisabled();

    ImGui::Spacing();
    ImGui::Separator();
    ImGui::Spacing();

    ImGui::TextUnformatted("Body and hands");
    ImGui::Spacing();

    bool hideBody = local.hideBody != 0;
    if (ImGui::Checkbox("Hide my body", &hideBody))
    {
        local.hideBody = hideBody ? 1 : 0;
        changed = true;
    }

    ImGui::TextDisabled("Your own torso is drawn where your real one is not,");
    ImGui::TextDisabled("so looking down at it breaks the illusion more than");
    ImGui::TextDisabled("its absence does. Off shows the character's body.");

    ImGui::Spacing();

    bool motionHands = local.motionHands != 0;
    if (ImGui::Checkbox("Motion-control hands and melee (experimental)", &motionHands))
    {
        local.motionHands = motionHands ? 1 : 0;
        changed = true;
    }

    ImGui::TextDisabled("Puts your hands and weapon on the controllers. Unfinished:");
    ImGui::TextDisabled("the posing fights the game's own animation and the swing");
    ImGui::TextDisabled("detection is not done. OFF is the tested configuration.");
    ImGui::TextDisabled("The controllers work as a gamepad either way - that is a");
    ImGui::TextDisabled("separate path and this does not touch it.");

    ImGui::Spacing();
    ImGui::Separator();
    ImGui::Spacing();

    /* One decision, four radio buttons - not four independent checkboxes.
       Something has to render, so "all off" is not a state the mod can honour,
       and a UI that offers it has to quietly invent an answer.

       The hybrid is listed here as a MODE OF ITS OWN rather than as a tick-box
       under AFW, because it was previously neither: afw_still_afr defaulted to
       on, so picking AFW got the hybrid and nothing in the menu said so. Two of
       the three modes on offer were the same mode. Naming it costs one line and
       makes "try AFW and see" a question that can actually be answered. */
    ImGui::TextUnformatted("Stereo rendering");

    /* Stacked rather than on one line. Five labels this long do not fit across
       a 1024 px panel at double font scale, and a row that wraps mid-label reads
       as a bug in the menu.

       Full AFR sits directly under plain AFR because it is the same mode with
       the halving removed, and the pair reads best next to each other. */
    int mode = local.stereoMode;
    if (ImGui::RadioButton("Alternate-frame (AFR)", &mode, 0))
        changed = true;
    if (ImGui::RadioButton("Full AFR - both eyes real (needs 2x frame rate)", &mode, 4))
        changed = true;
    if (ImGui::RadioButton("Alternate-frame warping (AFW)", &mode, 2))
        changed = true;
    if (ImGui::RadioButton("Single-frame stereo (experimental)", &mode, 5))
        changed = true;
    if (ImGui::RadioButton("Native stereo - the whole pipeline, twice", &mode, 6))
        changed = true;
    local.stereoMode = mode;

    ImGui::Spacing();

    if (local.stereoMode == 0)
    {
        ImGui::TextDisabled("One render per frame, alternating eyes; the eye that");
        ImGui::TextDisabled("was not drawn is held from its own last real render.");
        ImGui::TextDisabled("Every eye is a true render, but the held one is a");
        ImGui::TextDisabled("frame old. The tested path.");
    }
    else if (local.stereoMode == 4)
    {
        ImGui::TextDisabled("Two renders per headset frame instead of one: an eye,");
        ImGui::TextDisabled("then the world held still, then the other eye - and");
        ImGui::TextDisabled("both go up together. Every eye is a true render of the");
        ImGui::TextDisabled("same instant at the full refresh rate. Nothing is held");
        ImGui::TextDisabled("from a frame ago and nothing is invented, so none of");
        ImGui::TextDisabled("the artefacts the modes above trade between exist.");
        ImGui::TextDisabled("");
        ImGui::TextDisabled("The catch is the frame rate. At 72 Hz the game has to");
        ImGui::TextDisabled("render 144 times a second. The pixel cost is the same");
        ImGui::TextDisabled("as the modes above pay for one eye each - it is the CPU");
        ImGui::TextDisabled("that has to find twice the frames, and a large battle");
        ImGui::TextDisabled("may not. When it cannot, pairs simply arrive late and");
        ImGui::TextDisabled("the compositor reprojects, as with any VR title.");
        ImGui::TextDisabled("Watch the log for the pairs-per-second line.");
        ImGui::TextDisabled("Switches instantly - no need to leave the battle.");
    }
    else if (local.stereoMode == 2)
    {
        ImGui::TextDisabled("One render per frame, but the other eye is BUILT from");
        ImGui::TextDisabled("it using the depth buffer instead of held. Both eyes");
        ImGui::TextDisabled("then come from one instant, so nothing moves between");
        ImGui::TextDisabled("them. Costs a soft edge where a near object hides");
        ImGui::TextDisabled("something the rendered eye could not see.");
        ImGui::TextDisabled("Switches instantly - no need to leave the battle.");
    }
    else if (local.stereoMode == 5)
    {
        ImGui::TextDisabled("ONE engine frame, both eyes real. The scene is drawn");
        ImGui::TextDisabled("once and every scene draw is issued a SECOND time with");
        ImGui::TextDisabled("the other eye's matrices, into targets of our own.");
        ImGui::TextDisabled("One game tick, one animation pass, one set of shadow");
        ImGui::TextDisabled("cascades - against full AFR's two of each.");
        ImGui::TextDisabled("EXPERIMENTAL: the second eye is built from the engine's");
        ImGui::TextDisabled("own G-buffer, so it is softer than the first and the");
        ImGui::TextDisabled("ground may be missing. AFR and AFW are unaffected.");
    }
    else if (local.stereoMode == 6)
    {
        ImGui::TextDisabled("ONE engine frame, both eyes real AND COMPLETE. Every");
        ImGui::TextDisabled("operation that depends on the viewpoint runs a second");
        ImGui::TextDisabled("time - the geometry, the lighting, the sky, the post -");
        ImGui::TextDisabled("each writing into a copy of its own, so the second eye");
        ImGui::TextDisabled("arrives lit and finished rather than raw.");
        ImGui::TextDisabled("");
        ImGui::TextDisabled("Nothing alternates, nothing is held, nothing is warped,");
        ImGui::TextDisabled("and each eye keeps its own temporal history at the full");
        ImGui::TextDisabled("frame rate. Shadows are rendered once and shared, as");
        ImGui::TextDisabled("they should be - both eyes see the same sun.");
        ImGui::TextDisabled("");
        ImGui::TextDisabled("Costs the GPU two eyes' worth of shading, which is what");
        ImGui::TextDisabled("stereo is, but only ONE culling pass and one animation");
        ImGui::TextDisabled("update - unlike Full AFR, which pays both twice.");
        ImGui::TextDisabled("Switches instantly - no need to leave the battle.");
    }

    if (local.stereoPending != 0)
    {
        ImGui::TextColored(ImVec4(1.0f, 0.80f, 0.25f, 1.0f),
                           "Takes effect at the next mission - changing this mid-frame");
        ImGui::TextColored(ImVec4(1.0f, 0.80f, 0.25f, 1.0f),
                           "is what crashed the stereo path before.");
    }

    ImGui::Spacing();
    ImGui::Separator();
    ImGui::Spacing();

    /* DELIBERATELY NOT A FIFTH RADIO BUTTON.
     *
       Every entry in the list above renders the headset. This renders nothing:
       it asks rgl one question about whether the mission scene can be drawn
       outside SceneView, writes the answer to a PNG, and stops. Putting it in
       the list would mean selecting it and going blind, which is the one thing
       a stereo-mode list must never let happen.

       It is a button and not a checkbox for the same reason it is an event on
       the wire: it runs once and reports. */
    ImGui::TextUnformatted("Diagnostics");

    const bool probeRunning = local.tableauProbeState == 1;

    if (probeRunning)
        ImGui::TextDisabled("Tableau probe running...");
    else if (ImGui::Button("Run tableau probe", ImVec2(280.0f, 0.0f)))
    {
        local.tableauProbe = 1;
        changed = true;
    }

    if (local.tableauProbeState == 2)
    {
        ImGui::TextColored(ImVec4(0.45f, 0.90f, 0.45f, 1.0f),
                           "Finished. bvr_tableau_probe.png is next to the logs.");
    }
    else if (local.tableauProbeState == 3)
    {
        ImGui::TextColored(ImVec4(1.0f, 0.55f, 0.45f, 1.0f),
                           "Failed - the log says at which step.");
    }

    ImGui::TextDisabled("Tests whether the engine can draw the battle from your");
    ImGui::TextDisabled("right eye WITHOUT a second scene view - the route the");
    ImGui::TextDisabled("Cyberpunk VR port used for real stereo. Renders nothing");
    ImGui::TextDisabled("to the headset and cannot change what you see.");
    ImGui::Spacing();
    ImGui::TextDisabled("SAFE HALF ONLY. It builds the target and reports what");
    ImGui::TextDisabled("the tableau system is holding. The render request that");
    ImGui::TextDisabled("goes with it killed the game outright last run, so it");
    ImGui::TextDisabled("now sits behind tableau_stereo_request = 1 in the cfg.");
    ImGui::TextDisabled("This button will not crash you; that setting will.");
}

} /* namespace */

void overlay_toggle()
{
    const bool wasVisible = g_ov.visible.load(std::memory_order_relaxed);
    g_ov.visible.store(!wasVisible, std::memory_order_relaxed);

    if (!wasVisible)
        g_ov.justOpened.store(true, std::memory_order_relaxed);

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
        g_ov.justOpened.store(true, std::memory_order_relaxed);

    BVR_INFO("Overlay %s.", visible ? "open" : "closed");
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
        g_ov.settings.motionHands = held.motionHands;
        g_ov.settings.uiShow = held.uiShow;
        g_ov.settings.uiWidth = held.uiWidth;
        g_ov.settings.uiDistance = held.uiDistance;
        g_ov.settings.screenWidth = held.screenWidth;
        g_ov.settings.screenDistance = held.screenDistance;
        g_ov.settings.sharpen = held.sharpen;

        /* A click that has not been polled yet outlives the push, for exactly
           the reason the block above exists: the probe runs once, so a lost
           click is a button that did nothing. */
        g_ov.settings.tableauProbe = held.tableauProbe;

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
                            g_ov.settings.tableauProbe != 0;
    if (!g_ov.settingsChanged && !hasCommand)
        return false;

    *out = g_ov.settings;

    g_ov.settingsChanged = false;
    g_ov.settings.calibCommand = 0;   /* one click, one request */
    g_ov.settings.tableauProbe = 0;   /* likewise - it runs once */
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
