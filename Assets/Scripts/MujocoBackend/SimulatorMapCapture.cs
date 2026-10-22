using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SimulatorMapCapture
{
    private static readonly Dictionary<int, Texture2D> Cache = new Dictionary<int, Texture2D>();
    private static Bounds capturedMapBounds;
    private static bool hasCapturedMapBounds;

    public static void Invalidate()
    {
        Cache.Clear();
        hasCapturedMapBounds = false;
    }

    // The legacy build uses the original simulator's own top-down artwork
    // (Assets/Textures/Map.png, copied into Resources). Its MapUI prefab draws
    // it as a 290 x 155 world-unit rectangle centred on the arena origin:
    // the 280 x 150 RMUC field plus a small margin.
    private const float LegacyMapWorldWidth = 290f;
    private const float LegacyMapWorldDepth = 155f;

    public static Vector2 WorldToMap(Vector3 world)
    {
        if (!hasCapturedMapBounds)
        {
            // No runtime bake: use the authored legacy map's world rectangle.
            return new Vector2(
                Mathf.Clamp01(world.x / LegacyMapWorldWidth + 0.5f),
                Mathf.Clamp01(world.z / LegacyMapWorldDepth + 0.5f));
        }
        // Match the actual top-down camera frustum (including aspect-fit padding),
        // not just the field renderers' bounds. This keeps marker and texture
        // projections aligned for both the HUD map and the large tactical map.
        float x = Mathf.InverseLerp(capturedMapBounds.min.x, capturedMapBounds.max.x, world.x);
        float y = Mathf.InverseLerp(capturedMapBounds.min.z, capturedMapBounds.max.z, world.z);
        return new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
    }

    /// <summary>
    /// Authored field map shipped with the project. The legacy build uses the
    /// original simulator's own top-down map so every mode shows the arena it
    /// actually drives on.
    /// </summary>
    public static Texture2D AuthoredMap()
    {
        // The authored artwork is the RMUC arena; the practice range is a
        // different layout. Render the range itself once and use that as its
        // minimap so the markers and the picture agree.
        if (SceneManager.GetActiveScene().name == "Training Ground")
        {
            Texture2D training = Capture(1024, 1024);
            if (training != null) return training;
        }
        Texture2D legacy = Resources.Load<Texture2D>("legacy_map");
        if (legacy != null) return legacy;
        Texture2D official = Resources.Load<Texture2D>("OfficialUI/2026map");
        if (official != null) return official;
        return Resources.Load<Texture2D>("mujoco-2026-map");
    }

    public static Texture2D Capture(int width, int height)
    {
        // Headless benchmark runs have no graphics device; the HUD works
        // without the baked map texture.
        if (Application.isBatchMode) return null;
        int key = (width << 16) ^ height;
        if (Cache.TryGetValue(key, out Texture2D cached) && cached != null) return cached;
        var root = new GameObject("RMUC2024 Map Capture Camera");
        root.hideFlags = HideFlags.HideAndDontSave;
        var camera = root.AddComponent<Camera>();
        camera.orthographic = true;
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool hasBounds = false;
        // The field is the original simulator RMUC2024 scene. Never derive its
        // map rectangle from every renderer in the loaded scene: that includes
        // robot prefabs, UI/world-space props, cameras and distant scenery and
        // makes the MuJoCo-normalized live markers disagree with the image.
        Renderer[] fieldRenderers = FindFieldRenderers();
        Renderer[] renderers = fieldRenderers.Length > 0
            ? fieldRenderers
            : Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled || renderer is ParticleSystemRenderer || renderer.GetComponentInParent<Canvas>() != null) continue;
            if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        if (!hasBounds) bounds = new Bounds(Vector3.zero, new Vector3(30f, 1f, 20f));
        // RMUC2024 field geometry uses Unity X/Z as horizontal axes. Bounds
        // come only from the original field prefab renderers, so a selected
        // robot cannot zoom or recenter the map.
        float fieldHalfWidth = Mathf.Max(10f, bounds.size.x * 0.5f + 1f);
        float fieldHalfDepth = Mathf.Max(10f, bounds.size.z * 0.5f + 1f);
        camera.aspect = (float)width / height;
        camera.orthographicSize = Mathf.Max(fieldHalfDepth, fieldHalfWidth / camera.aspect);
        float mapHalfHeight = camera.orthographicSize;
        float mapHalfWidth = mapHalfHeight * camera.aspect;
        capturedMapBounds = new Bounds(bounds.center, new Vector3(mapHalfWidth * 2f, bounds.size.y, mapHalfHeight * 2f));
        hasCapturedMapBounds = true;
        camera.transform.position = new Vector3(bounds.center.x, bounds.max.y + 35f, bounds.center.z);
        camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.08f, .10f, .13f, 1f);
        camera.cullingMask = Physics.AllLayers;
        camera.enabled = false;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        target.Create(); camera.targetTexture = target;
        // The map artwork is the simulator field only. Do not bake the spawned
        // player robot, spectator props, UI or particles into the map texture;
        // live MuJoCo robot markers are drawn separately by RuntimeMiniMap.
        var sceneRenderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var restoreEnabled = new List<Renderer>();
        if (fieldRenderers.Length > 0)
        {
            var allowed = new HashSet<Renderer>(fieldRenderers);
            foreach (Renderer renderer in sceneRenderers)
            {
                if (renderer != null && renderer.enabled && !allowed.Contains(renderer))
                {
                    renderer.enabled = false;
                    restoreEnabled.Add(renderer);
                }
            }
        }
        try { camera.Render(); }
        finally
        {
            foreach (Renderer renderer in restoreEnabled) if (renderer != null) renderer.enabled = true;
        }
        RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply(false, false);
        RenderTexture.active = previous; camera.targetTexture = null; target.Release();
        Object.DestroyImmediate(target); Object.DestroyImmediate(root);
        texture.name = "Runtime simulator field map";
        Cache[key] = texture;
        return texture;
    }

    private static Renderer[] FindFieldRenderers()
    {
        foreach (Transform candidate in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (candidate.name != "MuJoCo 2026 Visual Field") continue;
            Renderer[] mesh = candidate.GetComponentsInChildren<Renderer>(true);
            if (mesh.Length > 0) return mesh;
        }
        // Training Ground's practice range is authored as a root named Field.
        foreach (Transform candidate in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (candidate.name != "Field") continue;
            var results = new List<Renderer>();
            foreach (Renderer renderer in candidate.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer || renderer.GetComponentInParent<Canvas>() != null) continue;
                results.Add(renderer);
            }
            if (results.Count > 0) return results.ToArray();
        }
        foreach (Transform candidate in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (candidate.name != "RMUC2024" && candidate.name != "RMUC2024(Clone)") continue;
            var results = new List<Renderer>();
            foreach (Renderer renderer in candidate.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer || renderer.GetComponentInParent<Canvas>() != null) continue;
                results.Add(renderer);
            }
            if (results.Count > 0) return results.ToArray();
        }
        return System.Array.Empty<Renderer>();
    }
}
