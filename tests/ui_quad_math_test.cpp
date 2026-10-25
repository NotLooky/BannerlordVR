// Checks ui_quad_math.h: the wrist HUD's placement and fade gate (TEST 377) and
// the laser's ray-to-quad hit (TEST 378). Run: tools\run-ui-quad-math-test.ps1
#include "ui_quad_math.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>

using namespace bvr::uiq;

static int g_failures = 0;

static void check(bool ok, const char* what)
{
    std::printf("%s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok)
        ++g_failures;
}

static bool near(float a, float b, float eps = 1e-3f) { return std::fabs(a - b) < eps; }

// A grip pose from where its axes point in the stage: +X (palm normal), and the
// forward axis -Z. +Y follows (right-handed).
static XrPosef grip(XrVector3f x, XrVector3f minusZ, XrVector3f at)
{
    const XrVector3f z = scale(minusZ, -1.0f);
    const XrVector3f y = cross(z, x);
    XrPosef p;
    p.orientation = from_basis(x, y, z);
    p.position = at;
    return p;
}

// The shipping default offsets (xr_context.cpp wrist_offset()). TEST 377b: roll -90
// turns the text to run ACROSS the wrist with its top toward the fingers.
static const WristOffset kDefault{ 0.03f, 0.0f, 0.12f, 90.0f, 0.0f, -90.0f };

// One way of looking at a wrist: where the palm faces, where the fingers point.
// The panel must show, read upright (up = wantUp) and left to right (wantRight),
// and hide again once the palm is turned away.
static void wrist_case(const char* name, int hand, XrVector3f palmOut, XrVector3f fingers,
                       XrVector3f at, XrVector3f wantUp, XrVector3f wantRight,
                       float cosFace, float cosGaze)
{
    // The grip's +X is the palm normal OUT of the left palm and INTO the right one.
    const XrVector3f x = hand == 0 ? palmOut : scale(palmOut, -1.0f);
    const XrPosef g = grip(x, fingers, at);
    const XrPosef w = compose(g, wrist_pose_in_grip(kDefault, hand));

    const XrVector3f head{ 0.0f, 1.60f, 0.0f };
    const XrVector3f look = sub(w.position, head);
    char what[160];

    std::snprintf(what, sizeof(what), "%s: shown when looked at", name);
    check(wrist_in_view(w, head, look, cosFace, cosGaze), what);

    std::snprintf(what, sizeof(what), "%s: hidden when looking straight ahead", name);
    check(!wrist_in_view(w, head, { 0, 0, -1 }, cosFace, cosGaze), what);

    std::snprintf(what, sizeof(what), "%s: text reads upright", name);
    check(dot(rotate(w.orientation, { 0, 1, 0 }), wantUp) > 0.7f, what);

    std::snprintf(what, sizeof(what), "%s: text runs left to right", name);
    check(dot(rotate(w.orientation, { 1, 0, 0 }), wantRight) > 0.7f, what);

    const XrPosef down = compose(grip(scale(x, -1.0f), fingers, at), wrist_pose_in_grip(kDefault, hand));
    std::snprintf(what, sizeof(what), "%s, palm turned away: hidden", name);
    check(!wrist_in_view(down, head, sub(down.position, head), cosFace, cosGaze), what);
}

int main()
{
    const float cosFace = std::cos(50.0f * 3.14159265f / 180.0f);
    const float cosGaze = std::cos(30.0f * 3.14159265f / 180.0f);

    // from_basis round trip.
    {
        const XrQuaternionf q = from_euler_deg(37.0f, -12.0f, 81.0f);
        const XrQuaternionf r = from_basis(rotate(q, { 1, 0, 0 }), rotate(q, { 0, 1, 0 }),
                                           rotate(q, { 0, 0, 1 }));
        const XrVector3f a = rotate(q, { 0.3f, -0.5f, 0.8f });
        const XrVector3f b = rotate(r, { 0.3f, -0.5f, 0.8f });
        check(near(a.x, b.x) && near(a.y, b.y) && near(a.z, b.z), "from_basis inverts a rotation");
    }

    // The right hand's offset is the left's mirrored across grip X.
    {
        const XrPosef l = wrist_pose_in_grip(kDefault, 0);
        const XrPosef r = wrist_pose_in_grip(kDefault, 1);
        const XrVector3f ln = rotate(l.orientation, { 0, 0, 1 });
        const XrVector3f rn = rotate(r.orientation, { 0, 0, 1 });
        check(near(l.position.x, -r.position.x) && near(l.position.z, r.position.z),
              "right wrist position mirrors the left");
        check(near(ln.x, 1.0f) && near(rn.x, -1.0f),
              "left panel faces +X (out of the left palm), right faces -X");
    }

    // TEST 377b: the two ways people actually check a wrist. The panel reads
    // across the wrist, its top toward the fingers, in both.
    //   Palm toward the face, fingers up: top is up.
    //   Forearm forward, palm up, looking down at it: top is away from you.
    wrist_case("left, palm to the face, fingers up", 0, { 0, 0, 1 }, { 0, 1, 0 },
               { -0.05f, 1.45f, -0.30f }, { 0, 1, 0 }, { 1, 0, 0 }, cosFace, cosGaze);
    wrist_case("left, forearm forward, palm up", 0, { 0, 1, 0 }, { 0, 0, -1 },
               { -0.10f, 1.15f, -0.35f }, { 0, 0, -1 }, { 1, 0, 0 }, cosFace, cosGaze);
    wrist_case("right, palm to the face, fingers up", 1, { 0, 0, 1 }, { 0, 1, 0 },
               { 0.05f, 1.45f, -0.30f }, { 0, 1, 0 }, { 1, 0, 0 }, cosFace, cosGaze);
    wrist_case("right, forearm forward, palm up", 1, { 0, 1, 0 }, { 0, 0, -1 },
               { 0.10f, 1.15f, -0.35f }, { 0, 0, -1 }, { 1, 0, 0 }, cosFace, cosGaze);

    // Ray against a 2 x 1 m quad two metres ahead, facing the player.
    {
        XrPosef quad;
        quad.orientation = { 0, 0, 0, 1 };
        quad.position = { 0.0f, 1.5f, -2.0f };
        float u = 0, v = 0, t = 0;

        check(ray_quad_uv({ 0.5f, 1.75f, 0.0f }, { 0, 0, -1 }, quad, 2.0f, 1.0f, &u, &v, &t) &&
                  near(u, 0.75f) && near(v, 0.25f) && near(t, 2.0f),
              "ray hits the upper right quarter at u 0.75, v 0.25");
        check(!ray_quad_uv({ 1.5f, 1.5f, 0.0f }, { 0, 0, -1 }, quad, 2.0f, 1.0f, &u, &v, &t),
              "ray past the edge misses");
        check(!ray_quad_uv({ 0.0f, 1.5f, 0.0f }, { 0, 0, 1 }, quad, 2.0f, 1.0f, &u, &v, &t),
              "quad behind the ray misses");
        check(!ray_quad_uv({ 0.0f, 1.5f, 0.0f }, { 1, 0, 0 }, quad, 2.0f, 1.0f, &u, &v, &t),
              "ray parallel to the quad misses");
    }

    // The beam strip runs from the hand to the hit and faces the head.
    {
        float len = 0;
        const XrPosef b = beam_pose({ 0.2f, 1.0f, -0.3f }, { 0.2f, 1.0f, -2.3f },
                                    { 0.0f, 1.6f, 0.0f }, &len);
        const XrVector3f x = rotate(b.orientation, { 1, 0, 0 });
        const XrVector3f z = rotate(b.orientation, { 0, 0, 1 });
        check(near(len, 2.0f) && near(b.position.z, -1.3f), "beam is 2 m long, centred");
        check(near(x.z, -1.0f), "beam X runs along the ray");
        check(dot(z, normalize(sub({ 0.0f, 1.6f, 0.0f }, b.position))) > 0.0f, "beam faces the head");
    }

    // TEST 378b: the laser's one-euro filter, at 90 Hz with the shipping 1.5 Hz / 4.
    {
        const float dt = 1.0f / 90.0f;
        const auto deg = [](float d) { return d * 3.14159265f / 180.0f; };
        const auto dirAt = [](float yaw) { return XrVector3f{ std::sin(yaw), 0.0f, -std::cos(yaw) }; };
        const auto angle = [](XrVector3f a, XrVector3f b) {
            float d = dot(a, b);
            return std::acos(d > 1.0f ? 1.0f : d);
        };

        // Hand held still with +-0.5 deg tremor alternating each frame.
        OneEuroDir f;
        f.reset(dirAt(0.0f));
        float worst = 0.0f;
        for (int i = 0; i < 180; ++i)
        {
            const XrVector3f out = f.step(dirAt(deg(i % 2 ? 0.5f : -0.5f)), dt, 1.5f, 4.0f);
            if (i > 90)
                worst = std::fmax(worst, angle(out, dirAt(0.0f)));
        }
        check(worst < deg(0.25f), "laser filter: 0.5 deg tremor at rest shrinks below 0.25 deg");

        // A deliberate sweep at 90 deg/s: the ray keeps up within 3 deg.
        OneEuroDir g;
        g.reset(dirAt(0.0f));
        XrVector3f out{};
        float yaw = 0.0f;
        for (int i = 0; i < 45; ++i)
        {
            yaw += deg(90.0f) * dt;
            out = g.step(dirAt(yaw), dt, 1.5f, 4.0f);
        }
        check(angle(out, dirAt(yaw)) < deg(3.0f), "laser filter: a 90 deg/s sweep lags under 3 deg");
    }

    // TEST 400: a pinned menu turns about the vertical to face the head.
    {
        const XrVector3f at{ 0.0f, 1.6f, -2.0f };
        const XrVector3f head{ 1.0f, 1.7f, 0.0f };
        const XrQuaternionf q = yaw_facing(at, head);

        const XrVector3f face = rotate(q, XrVector3f{ 0.0f, 0.0f, 1.0f });
        const XrVector3f toHead = normalize(XrVector3f{ head.x - at.x, 0.0f, head.z - at.z });
        check(dot(face, toHead) > 0.999f, "menu facing: its face (+Z) points at the head, flattened");

        const XrVector3f up = rotate(q, XrVector3f{ 0.0f, 1.0f, 0.0f });
        check(near(up.y, 1.0f), "menu facing: stays upright");

        const XrQuaternionf over = yaw_facing(at, XrVector3f{ at.x, 3.0f, at.z });
        const float n = over.x * over.x + over.y * over.y + over.z * over.z + over.w * over.w;
        check(std::isfinite(over.w) && near(n, 1.0f), "menu facing: head straight overhead stays a unit rotation");
    }

    // TEST 523: the floating hand panel faces the head with no roll, from below,
    // above and beside it.
    {
        const XrVector3f head{ 0.0f, 1.6f, 0.0f };
        const XrVector3f hands[3] = { { 0.1f, 1.0f, -0.4f }, { -0.2f, 2.0f, -0.3f }, { 0.5f, 1.6f, -0.1f } };
        const char* names[3] = { "below", "above", "beside" };
        for (int i = 0; i < 3; ++i)
        {
            const XrQuaternionf q = face_point(hands[i], head);
            const XrVector3f face = rotate(q, { 0.0f, 0.0f, 1.0f });
            const XrVector3f x = rotate(q, { 1.0f, 0.0f, 0.0f });
            const XrVector3f y = rotate(q, { 0.0f, 1.0f, 0.0f });
            char what[96];
            std::snprintf(what, sizeof(what), "hand panel %s the head: faces it, X level, Y up", names[i]);
            check(dot(face, normalize(sub(head, hands[i]))) > 0.999f && near(x.y, 0.0f) && y.y > 0.0f, what);
        }
    }

    // TEST 524: a laser hit on the panel maps back to the source pixel the blit
    // drew there, and the gaps between stacked rects are not hits.
    {
        const float src[3][4] = { { 745, 1021, 430, 59 }, { 1379, 931, 541, 149 }, { 0, 1015, 620, 65 } };
        float dst[4][4];
        const int n = wrist_layout(src, 3, 1024.0f, 512.0f, 3.0f, dst);
        check(n == 3, "wrist layout: three rects laid out");

        bool round = true;
        for (int i = 0; i < n; ++i)
        {
            // A point a third of the way into each rect, in source pixels.
            const float wantX = src[i][0] + src[i][2] / 3.0f;
            const float wantY = src[i][1] + src[i][3] / 3.0f;
            const float px = dst[i][0] + (wantX - src[i][0]) * dst[i][2] / src[i][2];
            const float py = dst[i][1] + (wantY - src[i][1]) * dst[i][3] / src[i][3];
            float sx = 0.0f, sy = 0.0f;
            round = round && wrist_hit_to_source(px / 1024.0f, py / 512.0f, src, dst, n, 1024.0f, 512.0f, &sx, &sy) &&
                    near(sx, wantX, 0.05f) && near(sy, wantY, 0.05f);
        }
        check(round, "wrist layout: a hit in each rect lands on the same source pixel");

        const float gapY = dst[0][1] + dst[0][3] + 1.0f;   // between rect 0 and rect 1
        float sx = 0.0f, sy = 0.0f;
        check(gapY < dst[1][1] &&
              !wrist_hit_to_source(0.5f, gapY / 512.0f, src, dst, n, 1024.0f, 512.0f, &sx, &sy),
              "wrist layout: a hit in the gap between rects is no hit");

        const float deployment[4][4] = {
            { 1113, 40, 333, 200 }, { 0, 20, 420, 1413 },
            { 2140, 20, 400, 1413 }, { 907, 1357, 707, 60 }
        };
        float palette[4][4];
        check(deployment_layout(deployment, 4, 2048.0f, 1024.0f, 6.0f, palette, 2.0f) == 4 &&
              palette[1][0] + palette[1][2] < palette[2][0] &&
              palette[0][1] + palette[0][3] < palette[1][1] &&
              palette[2][1] + palette[2][3] < palette[3][1] &&
              palette[3][1] + palette[3][3] <= 1024.01f,
              "deployment palette: both formation columns fit together below the captain and above actions");
        float oldPalette[4][4];
        deployment_layout(deployment, 4, 1024.0f, 512.0f, 3.0f, oldPalette, 2.0f);
        bool samePhysicalLayout = true;
        for (int i = 0; i < 4; ++i)
            for (int j = 0; j < 4; ++j)
                samePhysicalLayout = samePhysicalLayout && near(palette[i][j], oldPalette[i][j] * 2.0f, 0.05f);
        check(samePhysicalLayout, "deployment palette: doubled pixels keep the same physical layout");
        bool paletteHits = true;
        for (int i = 0; i < 4; ++i)
        {
            const float wantX = deployment[i][0] + deployment[i][2] / 2;
            const float wantY = deployment[i][1] + deployment[i][3] / 2;
            paletteHits = paletteHits &&
                wrist_hit_to_source((palette[i][0] + palette[i][2] / 2) / 2048.0f,
                                    (palette[i][1] + palette[i][3] / 2) / 1024.0f,
                                    deployment, palette, 4, 2048.0f, 1024.0f, &sx, &sy) &&
                near(sx, wantX, 0.05f) && near(sy, wantY, 0.05f);
        }
        check(paletteHits, "deployment palette: laser maps every region to its source widget");
    }

    std::printf(g_failures == 0 ? "\nALL CHECKS PASSED\n" : "\n%d CHECK(S) FAILED\n", g_failures);
    return g_failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
