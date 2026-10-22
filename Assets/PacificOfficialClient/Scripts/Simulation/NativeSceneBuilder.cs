using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Runtime-only helpers for the pieces the official HUD needs that the imported
/// field does not provide. The MuJoCo backend owns every other visual.
/// </summary>
public static class NativeSceneBuilder
{
    public static GameObject BuildFriendlyProjectile(Transform parent)
    {
        Material orange = Emissive("Projectile", new Color(1f, 0.22f, 0.005f), 3.5f, false);
        Material orangeHalo = Emissive("Projectile Halo", new Color(1f, 0.32f, 0.01f, 0.72f), 2.2f, true);
        GameObject projectile = BuildProjectile(parent, orange, orangeHalo);
        projectile.name = "MuJoCo Visible Projectile";
        return projectile;
    }

    static GameObject BuildProjectile(Transform parent, Material projectile, Material halo)
    {
        var root = new GameObject("Projectile 42mm");
        root.transform.SetParent(parent, false);
        // Geometry is authored in metres: the 42 mm round is 42.5 mm in the
        // source simulator.  Keep the emissive halo only slightly larger than
        // the physical ball; the old 0.42 m halo made it ten times too wide.
        Sphere("Projectile Core", root.transform, Vector3.zero, 0.0425f, projectile);
        // Keep only a five-percent bloom shell. Visibility comes primarily
        // from emission and the trail, not from an oversized fake sphere.
        Sphere("Projectile Tracer", root.transform, Vector3.zero, 0.044625f, halo);
        var trail = root.AddComponent<TrailRenderer>();
        trail.sharedMaterial = projectile;
        // A short tracer makes the true high-speed flight readable without
        // leaving a large, apparently slow orange object hanging in mid-air.
        trail.time = 0.09f;
        trail.minVertexDistance = 0.02f;
        trail.startWidth = 0.0425f;
        trail.endWidth = 0.0085f;
        trail.alignment = LineAlignment.View;
        trail.textureMode = LineTextureMode.Stretch;
        trail.shadowCastingMode = ShadowCastingMode.Off;
        trail.receiveShadows = false;
        trail.emitting = false;
        root.SetActive(false);
        return root;
    }

    static GameObject Sphere(string name, Transform parent, Vector3 position, float diameter, Material material)
        => Primitive(PrimitiveType.Sphere, name, parent, position, Vector3.one * diameter, material, Quaternion.identity);

    static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Material material, Quaternion rotation)
    {
        var obj = GameObject.CreatePrimitive(type);
        obj.name = name;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localRotation = rotation;
        obj.transform.localScale = scale;
        Object.Destroy(obj.GetComponent<Collider>());
        obj.GetComponent<Renderer>().sharedMaterial = material;
        return obj;
    }

    static Material Material(string name, Color color, float metallic, float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                        Shader.Find("Universal Render Pipeline/Simple Lit") ??
                        Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var material = new Material(shader) { name = name, color = color };
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        return material;
    }

    static Material Emissive(string name, Color color, float emission, bool transparent)
    {
        var material = Material(name, color, 0.05f, 0.4f);
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", new Color(color.r, color.g, color.b) * emission);
        if (transparent)
        {
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
        }
        return material;
    }
}
