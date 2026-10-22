using UnityEngine;

public static class CoordinateMapper
{
    public static Vector3 World(float[] p) => p == null || p.Length < 3 ? Vector3.zero : new Vector3(p[0], p[2], p[1]);
    // Same mapping without building an array, for the per-frame HUD paths.
    public static Vector3 World(float x, float y, float z) => new Vector3(x, z, y);
    public static Vector3 WorldVector(float[] p) => World(p);
    public static Vector2 Map(float x, float y, FrameBounds b) => new Vector2(Mathf.InverseLerp(b.xMin, b.xMax, x), 1f - Mathf.InverseLerp(b.yMin, b.yMax, y));
}
