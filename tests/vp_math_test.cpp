/* =============================================================================
 * vp_math_test - proves the substitution VP' = D * VP * S is arithmetically the
 * same thing as rendering from the eye with the eye's frustum.
 *
 * This matters more than a normal unit test. The whole VP patcher rests on one
 * claim: that the replacement can be built WITHOUT knowing rgl's depth range,
 * handedness, or reversed-Z. If that claim is wrong the failure mode is a black
 * screen in a headset, twenty minutes per attempt to find out. So it is checked
 * here instead, against projections built with each of those conventions
 * deliberately different.
 *
 * The test includes vp_patch.cpp directly, so it exercises the SHIPPING
 * functions rather than a copy of them that can drift.
 *
 * Build (from the repo root, in a VS developer prompt):
 *   cl /std:c++17 /EHsc /nologo /Fe:build\tests\vp_math_test.exe ^
 *      /I native\BannerlordVR.Native\src /I native\BannerlordVR.Native\include ^
 *      /I native\thirdparty\minhook\include ^
 *      tests\vp_math_test.cpp native\BannerlordVR.Native\src\bvr_log.cpp ^
 *      native\BannerlordVR.Native\src\bvr_config.cpp ^
 *      build\native-cmake\minhook\Release\minhook.x64.lib user32.lib shell32.lib
 * ========================================================================== */

#include "vp_patch.cpp"

#include <stdio.h>

/* vp_install_hooks() reaches for the engine's device to hook the deferred
   context vtable as well. None of the math under test touches it, and stubbing
   it here keeps the test out of the whole D3D device lifecycle in
   d3d_hooks.cpp. */
namespace bvr {
ID3D11Device* engine_device() { return nullptr; }
bool present_hook_ready() { return false; }
bool vtable_hook(void**, int, void*, void**) { return false; }
} // namespace bvr

namespace {

using bvr::Mat4;
using bvr::Vec3;

int g_failures = 0;

void check(bool ok, const char* what)
{
    printf("%-58s %s\n", what, ok ? "ok" : "FAILED");
    if (!ok)
        ++g_failures;
}

void check_close(float got, float want, float tol, const char* what)
{
    const bool ok = fabsf(got - want) <= tol;
    printf("%-58s %s (got %.6f, want %.6f)\n", what, ok ? "ok" : "FAILED", got, want);
    if (!ok)
        ++g_failures;
}

/* A camera frame: rows 0,1,2 = right, up, forward; row 3 = position. */
Mat4 make_camera(Vec3 right, Vec3 up, Vec3 forward, Vec3 pos)
{
    Mat4 m;
    memset(m.m, 0, sizeof(m.m));
    m.m[0] = right.x;   m.m[1] = right.y;   m.m[2] = right.z;
    m.m[4] = up.x;      m.m[5] = up.y;      m.m[6] = up.z;
    m.m[8] = forward.x; m.m[9] = forward.y; m.m[10] = forward.z;
    m.m[12] = pos.x;    m.m[13] = pos.y;    m.m[14] = pos.z;
    m.m[15] = 1.0f;
    return m;
}

/* Off-centre perspective, in whichever convention the flags ask for.
 *
 *   handed   +1 = view space looks down +z (D3D left handed)
 *            -1 = view space looks down -z (OpenGL style right handed)
 *   reversed  near maps to 1 and far to 0 rather than 0 to 1
 *
 * The point of the flags is that the patcher must not care which was used. */
Mat4 make_projection(float tanL, float tanR, float tanU, float tanD,
                     float nearZ, float farZ, int handed, bool reversed)
{
    const float w = tanR - tanL;
    const float h = tanU - tanD;

    Mat4 p;
    memset(p.m, 0, sizeof(p.m));
    p.m[0]  = 2.0f / w;
    p.m[5]  = 2.0f / h;
    p.m[8]  = -(tanR + tanL) / w * static_cast<float>(handed);
    p.m[9]  = -(tanU + tanD) / h * static_cast<float>(handed);

    float a, b;
    if (!reversed)
    {
        a = farZ / (farZ - nearZ);
        b = -nearZ * farZ / (farZ - nearZ);
    }
    else
    {
        a = nearZ / (nearZ - farZ);
        b = -farZ * nearZ / (nearZ - farZ);
    }

    p.m[10] = a * static_cast<float>(handed);
    p.m[14] = b;
    p.m[11] = static_cast<float>(handed);

    /* 2/w and 2/h stay positive in both handednesses: clip.x must grow to the
       right whichever way view space points, because NDC is NDC. */
    return p;
}

Mat4 view_from_camera(const Mat4& cam)
{
    Mat4 v;
    bvr::mat_rigid_inverse(v, cam);
    return v;
}

/* Project a world point and return NDC. Returns false behind the camera. */
bool project(const Mat4& vp, Vec3 p, float& ndcX, float& ndcY, float& ndcZ)
{
    const float x = p.x*vp.m[0] + p.y*vp.m[4] + p.z*vp.m[8]  + vp.m[12];
    const float y = p.x*vp.m[1] + p.y*vp.m[5] + p.z*vp.m[9]  + vp.m[13];
    const float z = p.x*vp.m[2] + p.y*vp.m[6] + p.z*vp.m[10] + vp.m[14];
    const float w = p.x*vp.m[3] + p.y*vp.m[7] + p.z*vp.m[11] + vp.m[15];
    if (!(w > 1e-6f))
        return false;
    ndcX = x / w; ndcY = y / w; ndcZ = z / w;
    return true;
}

Vec3 vec(float x, float y, float z) { return bvr::v3(x, y, z); }

// ---------------------------------------------------------------------------

/* One full scenario: recover the camera from VP, build the replacement, and
   check the replacement agrees with a directly built eye VP at sample points. */
void run_case(const char* name, int handed, bool reversed, bool expandFov,
              bool transposed)
{
    printf("\n--- %s ---\n", name);

    /* The engine's camera: not axis aligned, so a transposed or permuted basis
       would show up rather than cancelling by accident. */
    Vec3 fwd = vec(0.6f, 0.8f, 0.0f);
    Vec3 up  = vec(0.0f, 0.0f, 1.0f);
    Vec3 rgt = vec(0.8f, -0.6f, 0.0f);
    Vec3 pos = vec(1234.5f, -678.25f, 42.75f);

    /* THE POINT OF THE HANDEDNESS FLAG. A right-handed engine labels its third
       view axis as BACKWARD, not forward. That is a relabelling of the local
       axes, and the derivation claims it cancels out of D as long as both
       frames use it. Here the engine uses it and the published eye frame does
       NOT - which is exactly the mismatch that happens in the real mod, because
       managed code publishes the eye in the natural convention and has no idea
       what rgl's is. If the claim is wrong, these cases fail. */
    const Vec3 engLocalFwd = (handed > 0) ? fwd : vec(-fwd.x, -fwd.y, -fwd.z);

    const Mat4 engCam = make_camera(rgt, up, engLocalFwd, pos);

    /* A canted engine frustum: asymmetric on purpose, so the recovery cannot
       pass by only handling the symmetric case. */
    const float engL = -1.10f, engR = 0.95f, engU = 1.05f, engD = -1.20f;
    const Mat4 engProj = make_projection(engL, engR, engU, engD,
                                         0.05f, 12500.0f, handed, reversed);

    Mat4 vp;
    bvr::mat_mul(vp, view_from_camera(engCam), engProj);

    /* --- recovery ----------------------------------------------------- */
    bvr::Frustum f;
    check(bvr::recover(vp, f), "recover() accepts the engine matrix");

    check_close(bvr::dot3(f.forward, fwd), 1.0f, 1e-3f, "recovered forward matches");
    check_close(bvr::dot3(f.right, rgt),   1.0f, 1e-3f, "recovered right matches");
    check_close(bvr::dot3(f.up, up),       1.0f, 1e-3f, "recovered up matches");
    check_close(bvr::len3(bvr::sub3(f.position, pos)), 0.0f, 0.05f,
                "recovered position matches");
    check_close(f.tanLeft,  engL, 2e-3f, "recovered tanLeft");
    check_close(f.tanRight, engR, 2e-3f, "recovered tanRight");
    check_close(f.tanUp,    engU, 2e-3f, "recovered tanUp");
    check_close(f.tanDown,  engD, 2e-3f, "recovered tanDown");

    /* --- the eye ------------------------------------------------------ */
    /* Rotated off the engine's heading and offset by an IPD half plus some
       head translation, which is exactly what head tracking has to deliver. */
    Vec3 eFwd = vec(0.0f, 1.0f, 0.0f);
    Vec3 eUp  = vec(0.0f, 0.0f, 1.0f);
    Vec3 eRgt = vec(1.0f, 0.0f, 0.0f);
    Vec3 ePos = vec(pos.x + 0.032f, pos.y - 0.11f, pos.z + 0.25f);

    /* What managed publishes: the natural convention, always. */
    const Mat4 eyeCam = make_camera(eRgt, eUp, eFwd, ePos);

    /* The same eye, relabelled the way the engine labels its own axes. Only the
       reference answer below is allowed to know this exists. */
    const Vec3 eyeLocalFwd = (handed > 0) ? eFwd : vec(-eFwd.x, -eFwd.y, -eFwd.z);
    const Mat4 eyeCamEngineConv = make_camera(eRgt, eUp, eyeLocalFwd, ePos);

    /* The headset's frustum: canted the way VirtualDesktopXR reports. */
    const float eyeL = -1.3764f, eyeR = 0.8391f, eyeU = 0.9657f, eyeD = -1.4281f;

    bvr::VpFrame pub = {};
    memcpy(pub.eyeCamera, eyeCam.m, sizeof(eyeCam.m));
    pub.refPos[0] = pos.x; pub.refPos[1] = pos.y; pub.refPos[2] = pos.z;
    pub.refDir[0] = fwd.x; pub.refDir[1] = fwd.y; pub.refDir[2] = fwd.z;
    pub.tanLeft = eyeL; pub.tanRight = eyeR; pub.tanUp = eyeU; pub.tanDown = eyeD;
    pub.eye = 0;
    pub.valid = 1;

    bvr::g_cfgLoaded = true;
    bvr::g_cfg.enabled = true;
    bvr::g_cfg.fov = true;
    bvr::g_cfg.expand = expandFov;

    Mat4 got;
    check(bvr::build_replacement(vp, f, pub, got), "build_replacement() succeeds");

    /* --- what it SHOULD be -------------------------------------------- */
    /* Built the honest way: the eye's own view matrix times a projection with
       the frustum the eye is meant to end up with. Under expand that is the
       headset's; otherwise the engine's is kept. */
    const Mat4 wantProj = expandFov
        ? make_projection(eyeL, eyeR, eyeU, eyeD, 0.05f, 12500.0f, handed, reversed)
        : engProj;

    Mat4 want;
    bvr::mat_mul(want, view_from_camera(eyeCamEngineConv), wantProj);

    /* Compare where they PUT things, not their entries: two projection matrices
       that differ by an overall scale are the same projection. */
    const Vec3 samples[] = {
        vec(pos.x + 3.0f,  pos.y + 20.0f, pos.z + 1.0f),
        vec(pos.x - 8.0f,  pos.y + 45.0f, pos.z - 3.0f),
        vec(pos.x + 15.0f, pos.y + 200.0f, pos.z + 30.0f),
        vec(pos.x - 0.4f,  pos.y + 1.2f,  pos.z + 0.1f),
        vec(pos.x + 60.0f, pos.y + 900.0f, pos.z - 120.0f),
    };

    float worstXY = 0.0f, worstZ = 0.0f;
    int projected = 0;

    for (const Vec3& s : samples)
    {
        float gx, gy, gz, wx, wy, wz;
        if (!project(got, s, gx, gy, gz) || !project(want, s, wx, wy, wz))
            continue;
        ++projected;
        worstXY = (fabsf(gx - wx) > worstXY) ? fabsf(gx - wx) : worstXY;
        worstXY = (fabsf(gy - wy) > worstXY) ? fabsf(gy - wy) : worstXY;
        worstZ  = (fabsf(gz - wz) > worstZ)  ? fabsf(gz - wz)  : worstZ;
    }

    check(projected == 5, "all sample points are in front of both cameras");
    check_close(worstXY, 0.0f, 2e-3f, "NDC x/y agrees with a directly built eye VP");
    check_close(worstZ,  0.0f, 2e-3f, "NDC z is carried through unchanged");

    /* --- the transposed storage path ----------------------------------- */
    if (!transposed)
        return;

    Mat4 vpT;
    bvr::mat_transpose(vpT, vp);

    /* The scanner tries both storage orders, so what has to hold is that
       transposing back is lossless - not that the transposed form is rejected,
       which is merely likely. */
    Mat4 back;
    bvr::mat_transpose(back, vpT);
    bvr::Frustum f2;
    check(bvr::recover(back, f2), "transposing and back recovers it again");
    check_close(bvr::dot3(f2.forward, fwd), 1.0f, 1e-3f,
                "and recovers the same forward");

    bvr::Frustum ft;
    printf("%-58s %s\n", "  (informational) transposed form reads as a camera",
           bvr::recover(vpT, ft) ? "yes" : "no");
}

void run_rejections()
{
    printf("\n--- rejections ---\n");

    /* An orthographic shadow cascade: zero w column. This is the single most
       important rejection - patching a shadow matrix would light the scene from
       the player's head. */
    Mat4 ortho;
    bvr::mat_identity(ortho);
    ortho.m[0] = 0.01f; ortho.m[5] = 0.01f; ortho.m[10] = 0.0001f;
    bvr::Frustum f;
    check(!bvr::recover(ortho, f), "orthographic (shadow cascade) rejected");

    Mat4 zero;
    memset(zero.m, 0, sizeof(zero.m));
    check(!bvr::recover(zero, f), "all-zero block rejected");

    /* Sixteen floats of ordinary shader constants. */
    Mat4 junk;
    for (int i = 0; i < 16; ++i)
        junk.m[i] = 0.5f + i * 0.37f;
    check(!bvr::recover(junk, f), "arbitrary constants rejected");

    /* A frustum far too wide to be a camera. */
    Mat4 cam = make_camera(vec(1,0,0), vec(0,0,1), vec(0,1,0), vec(0,0,0));
    Mat4 proj = make_projection(-40.0f, 40.0f, -40.0f, 40.0f, 0.05f, 100.0f, 1, false);
    Mat4 wide;
    bvr::mat_mul(wide, view_from_camera(cam), proj);
    check(!bvr::recover(wide, f), "implausibly wide frustum rejected");
}

void run_inverse()
{
    printf("\n--- inverse detection ---\n");

    Mat4 cam = make_camera(vec(0.8f,-0.6f,0), vec(0,0,1), vec(0.6f,0.8f,0),
                           vec(100.0f, 200.0f, 5.0f));
    Mat4 proj = make_projection(-1.0f, 1.0f, -0.9f, 0.9f, 0.05f, 12500.0f, 1, false);
    Mat4 vp;
    bvr::mat_mul(vp, view_from_camera(cam), proj);

    Mat4 inv;
    check(bvr::mat_inverse(inv, vp), "mat_inverse() succeeds on a VP");
    check(bvr::is_inverse_of(inv, vp), "its inverse is recognised");
    check(!bvr::is_inverse_of(vp, vp), "the matrix itself is not");

    Mat4 identity;
    bvr::mat_identity(identity);
    check(!bvr::is_inverse_of(identity, vp), "identity is not mistaken for it");
}

} // namespace

int main()
{
    printf("vp_patch math self-test\n");
    printf("=======================\n");

    run_case("left handed, normal depth, fov kept",      1, false, false, true);
    run_case("left handed, normal depth, fov expanded",  1, false, true,  false);
    run_case("left handed, REVERSED depth, fov kept",    1, true,  false, false);
    run_case("left handed, REVERSED depth, expanded",    1, true,  true,  false);
    run_case("RIGHT handed, normal depth, fov kept",    -1, false, false, false);
    run_case("RIGHT handed, reversed depth, expanded",  -1, true,  true,  false);

    run_rejections();
    run_inverse();

    printf("\n%s (%d failure(s))\n", g_failures == 0 ? "PASS" : "FAIL", g_failures);
    return g_failures == 0 ? 0 : 1;
}
