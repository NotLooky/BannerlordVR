/* =============================================================================
 * Pure geometry for the quads that are not the interface panel: the wrist HUDs
 * (TEST 377) and the laser pointer (TEST 378).
 *
 * Header-only and free of D3D and of g_xr, so tests/ui_quad_math_test.cpp can
 * check it without a headset. Everything is in OpenXR stage space: metres,
 * Y up, -Z forward.
 *
 * THE GRIP AXES THESE RELY ON (OpenXR spec, grip pose): -Z runs forward through
 * the fist, +X is the palm normal - pointing OUT of the palm on the left hand
 * and INTO it on the right - and +Y is up out of the top of the fist, the thumb
 * side. So the outward palm normal is +X on the left hand and -X on the right,
 * and a left-hand offset becomes the right hand's by mirroring across X.
 * ========================================================================== */
#ifndef BVR_UI_QUAD_MATH_H
#define BVR_UI_QUAD_MATH_H

#include <openxr/openxr.h>

#include <algorithm>
#include <cmath>

namespace bvr {
namespace uiq {

inline XrVector3f add(const XrVector3f& a, const XrVector3f& b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
inline XrVector3f sub(const XrVector3f& a, const XrVector3f& b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
inline XrVector3f scale(const XrVector3f& a, float s) { return { a.x * s, a.y * s, a.z * s }; }
inline float dot(const XrVector3f& a, const XrVector3f& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline XrVector3f cross(const XrVector3f& a, const XrVector3f& b)
{
    return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x };
}
inline float length(const XrVector3f& a) { return std::sqrt(dot(a, a)); }
inline XrVector3f normalize(const XrVector3f& a)
{
    const float l = length(a);
    return l > 1e-6f ? scale(a, 1.0f / l) : XrVector3f{ 0.0f, 0.0f, 0.0f };
}

/* Hamilton product: (a * b) rotates by b, then by a. */
inline XrQuaternionf mul(const XrQuaternionf& a, const XrQuaternionf& b)
{
    return { a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
             a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
             a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
             a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z };
}

inline XrQuaternionf conj(const XrQuaternionf& q) { return { -q.x, -q.y, -q.z, q.w }; }

inline XrVector3f rotate(const XrQuaternionf& q, const XrVector3f& v)
{
    const XrVector3f u{ q.x, q.y, q.z };
    const XrVector3f uv = cross(u, v);
    const XrVector3f uuv = cross(u, uv);
    return add(v, scale(add(scale(uv, q.w), uuv), 2.0f));
}

inline XrQuaternionf axis_angle(const XrVector3f& axis, float degrees)
{
    const float h = degrees * 0.5f * 3.14159265358979f / 180.0f;
    const float s = std::sin(h);
    return { axis.x * s, axis.y * s, axis.z * s, std::cos(h) };
}

/* Yaw about Y, then pitch about the yawed X, then roll about the result's Z. */
inline XrQuaternionf from_euler_deg(float yaw, float pitch, float roll)
{
    return mul(axis_angle({ 0, 1, 0 }, yaw),
               mul(axis_angle({ 1, 0, 0 }, pitch), axis_angle({ 0, 0, 1 }, roll)));
}

/* The rotation whose columns are x, y, z (an orthonormal right-handed basis). */
inline XrQuaternionf from_basis(const XrVector3f& x, const XrVector3f& y, const XrVector3f& z)
{
    const float tr = x.x + y.y + z.z;
    XrQuaternionf q{};
    if (tr > 0.0f)
    {
        const float s = std::sqrt(tr + 1.0f) * 2.0f;
        q = { (y.z - z.y) / s, (z.x - x.z) / s, (x.y - y.x) / s, 0.25f * s };
    }
    else if (x.x > y.y && x.x > z.z)
    {
        const float s = std::sqrt(1.0f + x.x - y.y - z.z) * 2.0f;
        q = { 0.25f * s, (y.x + x.y) / s, (z.x + x.z) / s, (y.z - z.y) / s };
    }
    else if (y.y > z.z)
    {
        const float s = std::sqrt(1.0f + y.y - x.x - z.z) * 2.0f;
        q = { (y.x + x.y) / s, 0.25f * s, (z.y + y.z) / s, (z.x - x.z) / s };
    }
    else
    {
        const float s = std::sqrt(1.0f + z.z - x.x - y.y) * 2.0f;
        q = { (z.x + x.z) / s, (z.y + y.z) / s, 0.25f * s, (x.y - y.x) / s };
    }
    return q;
}

/* child expressed in parent's frame -> the same pose in parent's space. */
inline XrPosef compose(const XrPosef& parent, const XrPosef& child)
{
    XrPosef out;
    out.orientation = mul(parent.orientation, child.orientation);
    out.position = add(parent.position, rotate(parent.orientation, child.position));
    return out;
}

/* --- the wrist HUD (TEST 377) ---------------------------------------------- */

/* The quad's pose in a hand's GRIP space, from the LEFT-hand offset. The right
   hand is the mirror image across the grip's X: x negated, yaw and roll
   flipped, pitch kept. */
struct WristOffset { float x, y, z, yaw, pitch, roll; };

inline XrPosef wrist_pose_in_grip(const WristOffset& left, int hand)
{
    const float m = hand == 1 ? -1.0f : 1.0f;
    XrPosef p;
    p.position = { left.x * m, left.y, left.z };
    p.orientation = from_euler_deg(left.yaw * m, left.pitch, left.roll * m);
    return p;
}

/* Whether the panel is turned toward the eyes AND the eyes are on it. The
   quad's visible face is its +Z. */
inline bool wrist_in_view(const XrPosef& wristStage, const XrVector3f& head,
                          const XrVector3f& headForward, float cosFace, float cosGaze)
{
    const XrVector3f toHead = normalize(sub(head, wristStage.position));
    const XrVector3f normal = rotate(wristStage.orientation, { 0.0f, 0.0f, 1.0f });
    return dot(normal, toHead) > cosFace &&
           dot(normalize(headForward), scale(toHead, -1.0f)) > cosGaze;
}

/* --- the laser (TEST 378) -------------------------------------------------- */

/* Where a ray meets a quad of w x h metres, as image coordinates (0,0 top-left,
   1,1 bottom-right) and the distance along the ray. False when it misses, runs
   parallel, or the quad is behind the ray's origin. `dir` need not be unit
   length; `t` is then in units of it. */
inline bool ray_quad_uv(const XrVector3f& origin, const XrVector3f& dir,
                        const XrPosef& quad, float w, float h,
                        float* u, float* v, float* t)
{
    const XrVector3f n = rotate(quad.orientation, { 0.0f, 0.0f, 1.0f });
    const float denom = dot(dir, n);
    if (std::fabs(denom) < 1e-6f)
        return false;

    const float hitT = dot(sub(quad.position, origin), n) / denom;
    if (!(hitT > 0.0f))
        return false;

    const XrVector3f hit = add(origin, scale(dir, hitT));
    const XrVector3f local = rotate(conj(quad.orientation), sub(hit, quad.position));

    const float uu = local.x / w + 0.5f;
    const float vv = 0.5f - local.y / h;
    if (uu < 0.0f || uu > 1.0f || vv < 0.0f || vv > 1.0f)
        return false;

    *u = uu;
    *v = vv;
    *t = hitT;
    return true;
}

/* A strip from `from` to `to`: its X runs along the beam and its face is turned
   about that axis toward the head, so it never shows up edge-on. The quad is
   then `*outLength` long. */
inline XrPosef beam_pose(const XrVector3f& from, const XrVector3f& to,
                         const XrVector3f& head, float* outLength)
{
    const XrVector3f along = sub(to, from);
    *outLength = length(along);
    const XrVector3f x = normalize(along);
    const XrVector3f centre = add(from, scale(along, 0.5f));

    XrVector3f toHead = sub(head, centre);
    XrVector3f z = normalize(sub(toHead, scale(x, dot(toHead, x))));
    if (length(z) < 0.5f)   /* looking straight down the beam */
        z = normalize(cross(x, std::fabs(x.y) < 0.9f ? XrVector3f{ 0, 1, 0 }
                                                     : XrVector3f{ 1, 0, 0 }));
    const XrVector3f y = cross(z, x);

    XrPosef p;
    p.position = centre;
    p.orientation = from_basis(x, y, z);
    return p;
}

/* TEST 400: an upright quad at `at`, turned about the vertical so its face (+Z)
   looks at `head`. Pitch and roll are never taken, so a menu does not tip over
   when you crouch beside it; directly overhead falls back to facing +Z. */
inline XrQuaternionf yaw_facing(const XrVector3f& at, const XrVector3f& head)
{
    float fx = at.x - head.x;   /* head -> quad, flattened */
    float fz = at.z - head.z;
    const float len = std::sqrt(fx * fx + fz * fz);
    if (len < 1e-4f)
    {
        fx = 0.0f;
        fz = -1.0f;
    }
    else
    {
        fx /= len;
        fz /= len;
    }

    const float half = 0.5f * std::atan2(-fx, -fz);
    return { 0.0f, std::sin(half), 0.0f, std::cos(half) };
}

/* TEST 523: a quad at `at` whose face (+Z) looks straight at `head`, tipped as
   needed, with its +X kept horizontal so it never rolls. Head straight above or
   below falls back to yaw_facing. */
inline XrQuaternionf face_point(const XrVector3f& at, const XrVector3f& head)
{
    const XrVector3f z = normalize(sub(head, at));
    const XrVector3f x = cross({ 0.0f, 1.0f, 0.0f }, z);
    if (length(x) < 1e-3f)
        return yaw_facing(at, head);
    const XrVector3f xn = normalize(x);
    return from_basis(xn, cross(z, xn), z);
}

/* --- the wrist panel's layout (TEST 377, shared with the laser since 524) --

   Up to four source rects stacked top-down into a dstW x dstH texture, each
   centred, enlarged to fill it - up to maxScale, so a single small widget does
   not become a blur - and shrunk when the stack overflows. Returns how many
   were laid out. */
inline int wrist_layout(const float (*src)[4], int count, float dstW, float dstH,
                        float maxScale, float (*dst)[4], float pixelAspect = 1.0f)
{
    const int n = count > 4 ? 4 : (count < 0 ? 0 : count);
    const float gap = 8.0f;
    float widest = 1.0f;
    float tall = gap * static_cast<float>(n - 1);
    for (int i = 0; i < n; ++i)
    {
        widest = src[i][2] > widest ? src[i][2] : widest;
        tall += src[i][3];
    }

    float s = maxScale > 1.0f ? maxScale : 1.0f;
    if (widest * s * pixelAspect > dstW) s = dstW / (widest * pixelAspect);
    if (tall * s > dstH) s = dstH / tall;

    float y = 0.0f;
    for (int i = 0; i < n; ++i)
    {
        const float w = src[i][2] * s * pixelAspect;
        const float h = src[i][3] * s;
        dst[i][0] = (dstW - w) * 0.5f;
        dst[i][1] = y;
        dst[i][2] = w > 1.0f ? w : 1.0f;
        dst[i][3] = h > 1.0f ? h : 1.0f;
        y += h + gap * s;
    }
    return n;
}

/* Deployment: captain above two formation columns, actions below. The same
   rectangles drive the blit and the laser's inverse mapping. */
inline int deployment_layout(const float (*src)[4], int count, float dstW, float dstH,
                             float maxScale, float (*dst)[4], float pixelAspect)
{
    if (count != 4)
        return wrist_layout(src, count, dstW, dstH, maxScale, dst, pixelAspect);

    const float gap = 8.0f;
    const float middleH = std::max(src[1][3], src[2][3]);
    const float middleW = src[1][2] + src[2][2] + gap;
    const float width = std::max(std::max(src[0][2], src[3][2]), middleW);
    const float height = src[0][3] + middleH + src[3][3] + 2.0f * gap;
    const float s = std::min(std::min(std::max(maxScale, 1.0f), dstW / (width * pixelAspect)),
                             dstH / height);
    const float yMiddle = (src[0][3] + gap) * s;
    const float xMiddle = (dstW - middleW * s * pixelAspect) * 0.5f;
    for (int i = 0; i < 4; ++i)
    {
        const float w = src[i][2] * s * pixelAspect;
        const float h = src[i][3] * s;
        dst[i][0] = i == 1 ? xMiddle : i == 2 ? xMiddle + (src[1][2] + gap) * s * pixelAspect
                                     : (dstW - w) * 0.5f;
        dst[i][1] = i == 0 ? 0.0f : i == 3 ? yMiddle + middleH * s + gap * s : yMiddle;
        dst[i][2] = w;
        dst[i][3] = h;
    }
    return 4;
}

/* TEST 524: the inverse - a hit at (u, v) on the panel back to the source
   pixel the blit drew there. False in the gaps and margins. */
inline bool wrist_hit_to_source(float u, float v, const float (*src)[4], const float (*dst)[4],
                                int n, float dstW, float dstH, float* sx, float* sy)
{
    const float px = u * dstW;
    const float py = v * dstH;
    for (int i = 0; i < n; ++i)
    {
        if (px < dst[i][0] || py < dst[i][1] ||
            px >= dst[i][0] + dst[i][2] || py >= dst[i][1] + dst[i][3])
            continue;
        *sx = src[i][0] + (px - dst[i][0]) * (src[i][2] / dst[i][2]);
        *sy = src[i][1] + (py - dst[i][1]) * (src[i][3] / dst[i][3]);
        return true;
    }
    return false;
}

/* TEST 378b: a one-euro filter on a unit direction. The cutoff rises with the
   turning speed, so tremor at rest is smoothed hard (minCutoff Hz) while a
   deliberate sweep passes nearly untouched (beta: higher = less lag moving). */
struct OneEuroDir
{
    XrVector3f value{ 0.0f, 0.0f, -1.0f };
    float speed = 0.0f;
    bool primed = false;

    void reset(const XrVector3f& raw)
    {
        value = raw;
        speed = 0.0f;
        primed = true;
    }

    XrVector3f step(const XrVector3f& raw, float dt, float minCutoff, float beta)
    {
        if (!primed || !(dt > 0.0f))
        {
            reset(raw);
            return value;
        }

        const auto alpha = [dt](float cutoff) {
            const float tau = 1.0f / (2.0f * 3.14159265f * cutoff);
            return 1.0f / (1.0f + tau / dt);
        };

        /* Turning speed in rad/s (a chord is the angle for small steps), smoothed. */
        speed += alpha(1.0f) * (length(sub(raw, value)) / dt - speed);
        value = normalize(add(value, scale(sub(raw, value), alpha(minCutoff + beta * speed))));
        return value;
    }
};

} // namespace uiq
} // namespace bvr

#endif /* BVR_UI_QUAD_MATH_H */
