using System;
using Godot;

namespace HitboxClone;

/// <summary>
/// Small math helpers, ported from <c>H:\BattleArena\Core.cs</c> so tuning values and feel
/// carry over unchanged. Godot's own <see cref="Mathf"/> covers some of this, but keeping the
/// same signatures means gameplay code ported from BattleArena needs no edits.
/// </summary>
public static class MathU
{
    public const float TAU = MathF.PI * 2f;

    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    public static float Clamp01(float v) => Clamp(v, 0f, 1f);
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Frame-rate independent exponential smoothing toward a target.</summary>
    public static float Damp(float cur, float target, float rate, float dt)
        => Lerp(target, cur, MathF.Exp(-rate * dt));

    public static Vector2 Damp(Vector2 cur, Vector2 target, float rate, float dt)
    {
        float t = MathF.Exp(-rate * dt);
        return target + (cur - target) * t;
    }

    public static Vector2 FromAngle(float rad, float len = 1f)
        => new(MathF.Cos(rad) * len, MathF.Sin(rad) * len);

    public static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

    public static Vector2 Norm(Vector2 v)
    {
        float l = v.Length();
        return l > 1e-5f ? v / l : Vector2.Zero;
    }

    public static Vector2 ClampLen(Vector2 v, float max)
    {
        float l = v.Length();
        return l > max && l > 1e-5f ? v / l * max : v;
    }

    /// <summary>
    /// Radial deadzone that rescales the surviving range back to 0..1, so there is no
    /// discontinuity at the deadzone edge and slow precise aiming still works.
    /// </summary>
    public static Vector2 Deadzone(Vector2 v, float dead)
    {
        float len = v.Length();
        if (len <= 1e-5f || len < dead) return Vector2.Zero;
        return v / len * Clamp01((len - dead) / MathF.Max(1e-5f, 1f - dead));
    }

    /// <summary>Shortest signed angular difference, in radians, wrapped to [-PI, PI].</summary>
    public static float AngleDiff(float a, float b)
    {
        float d = (a - b) % TAU;
        if (d > MathF.PI) d -= TAU;
        if (d < -MathF.PI) d += TAU;
        return d;
    }

    public static float MoveAngleToward(float cur, float target, float maxStep)
    {
        float d = AngleDiff(target, cur);
        if (MathF.Abs(d) <= maxStep) return target;
        return cur + MathF.Sign(d) * maxStep;
    }
}
