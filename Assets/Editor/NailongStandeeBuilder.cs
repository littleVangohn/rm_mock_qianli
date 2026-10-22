using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Pacific.Nailong.Editor
{
    /// <summary>
    /// Builds the "奶龙手持三号牌" standee (thin cut-out board, metal foot and
    /// contact shadow) and bakes one instance into every playable map scene.
    /// It is a pure decoration: no colliders, no network identity.
    ///
    /// Menu:  Pacific/Nailong/Rebuild Standee In Maps
    /// Batch: -executeMethod Pacific.Nailong.Editor.NailongStandeeBuilder.RebuildBatch
    /// </summary>
    public static class NailongStandeeBuilder
    {
        public const string RootName = "Nailong Standee";

        private const string SourceTexture = "奶龙手持机械数字三号牌.png";
        private const string HitSourceTexture = "抱着数字3的忧郁红色团子角色.png";
        private const string ArtFolder = "Assets/PacificOfficialClient/Art/Nailong";
        private const string PrefabFolder = "Assets/PacificOfficialClient/Prefabs";
        private const string TexturePath = ArtFolder + "/nailong-standee.png";
        private const string HitTexturePath = ArtFolder + "/nailong-hit.png";
        private const string ShadowTexturePath = ArtFolder + "/nailong-soft-shadow.png";
        private const string FrontMaterialPath = ArtFolder + "/nailong-standee-front.mat";
        private const string BackMaterialPath = ArtFolder + "/nailong-standee-back.mat";
        private const string HitMaterialPath = ArtFolder + "/nailong-standee-hit.mat";
        private const string BaseMaterialPath = ArtFolder + "/nailong-standee-base.mat";
        private const string ShadowMaterialPath = ArtFolder + "/nailong-standee-shadow.mat";
        private const string PrefabPath = PrefabFolder + "/NailongStandee.prefab";

        // This project works in 10 Unity units per metre (the arena exporter
        // divides positions by 10). The previous bake was 4 units tall; 8
        // units (0.8 m) is the requested 2x. Every other part scales with it.
        private const float BoardHeight = 8f;
        private const float DesignBoardHeight = 2.35f;
        private const float DesignBoardThickness = 0.024f;
        private const float DesignFootHeight = 0.10f;
        private const float BoardAspect = 1297f / 1213f;

        private static float floorHeight;

        private static readonly string[] MapScenes =
        {
            "Assets/Scenes/RMUC2024.unity",
            "Assets/Scenes/RMUC2023.unity",
            "Assets/Scenes/Training Ground.unity",
        };

        [MenuItem("Pacific/Nailong/Rebuild Standee In Maps")]
        public static void RebuildFromMenu() => RebuildBatch();

        public static void RebuildBatch()
        {
            EnsureFolder(PrefabFolder);
            EnsureFolder(ArtFolder);

            Texture2D texture = ImportArtwork(SourceTexture, TexturePath);
            Texture2D hitTexture = ImportArtwork(HitSourceTexture, HitTexturePath);
            Texture2D shadow = CreateShadowTexture();
            Material front = BuildCutoutMaterial(FrontMaterialPath, "Nailong Standee Front", texture, Color.white, CullMode.Back);
            Material back = BuildCutoutMaterial(BackMaterialPath, "Nailong Standee Back", texture,
                new Color(0.36f, 0.35f, 0.37f, 1f), CullMode.Back);
            Material hit = BuildCutoutMaterial(HitMaterialPath, "Nailong Standee Hit", hitTexture, Color.white, CullMode.Back);
            Material metal = BuildMetalMaterial(BaseMaterialPath);
            Material shadowMaterial = BuildShadowMaterial(shadow);

            GameObject prefab = BuildPrefab(hitTexture, front, back, hit, metal, shadowMaterial);
            AssetDatabase.SaveAssets();

            foreach (string scenePath in MapScenes)
                PlaceInScene(scenePath, prefab);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            foreach (string scenePath in MapScenes)
                RenderPreview(scenePath);
            Debug.Log("[Nailong] Standee rebuilt and baked into: " + string.Join(", ", MapScenes));
        }

        /// <summary>Renders each baked standee so the result can be checked without opening Unity.</summary>
        private static void RenderPreview(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            GameObject standee = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == RootName) { standee = root; break; }
            if (standee == null) return;

            string tag = Path.GetFileNameWithoutExtension(scenePath).Replace(' ', '-').ToLowerInvariant();
            Vector3 position = standee.transform.position;
            Vector3 front = -standee.transform.forward;   // printed side
            Vector3 right = standee.transform.right;

            float h = BoardHeight;
            RenderCamera("nailong-" + tag + "-wide",
                position + front * (3.2f * h) + right * (0.87f * h) + Vector3.up * (1.13f * h),
                position + Vector3.up * (0.43f * h), 60f);
            RenderCamera("nailong-" + tag + "-close",
                position + front * (1.6f * h) + right * (0.4f * h) + Vector3.up * (0.57f * h),
                position + Vector3.up * (0.43f * h), 50f);
            RenderCamera("nailong-" + tag + "-field",
                position + front * (4.3f * h) + right * (2.3f * h) + Vector3.up * (0.6f * h),
                position + Vector3.up * (0.47f * h), 55f);
        }

        private static void RenderCamera(string name, Vector3 position, Vector3 target, float fov)
        {
            var root = new GameObject("Nailong Preview Camera");
            var camera = root.AddComponent<Camera>();
            camera.transform.position = position;
            camera.transform.LookAt(target);
            camera.fieldOfView = fov;
            camera.nearClipPlane = 0.5f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.Skybox;

            const int width = 1600, height = 900;
            var targetTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = targetTexture;
            camera.Render();
            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = targetTexture;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;

            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(Path.Combine("Logs", name + ".png"), image.EncodeToPNG());
            camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(image);
            UnityEngine.Object.DestroyImmediate(root);
            targetTexture.Release();
            UnityEngine.Object.DestroyImmediate(targetTexture);
            Debug.Log("[Nailong] preview Logs/" + name + ".png");
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static Texture2D ImportArtwork(string sourceFile, string assetPath)
        {
            string source = Path.GetFullPath(sourceFile);
            if (!File.Exists(source))
                throw new FileNotFoundException("Standee artwork is missing", source);
            File.Copy(source, Path.GetFullPath(assetPath), true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null) throw new InvalidOperationException("Could not import " + assetPath);
            return texture;
        }

        private static Texture2D CreateShadowTexture()
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Pow(Mathf.Clamp01(1f - radius), 1.9f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();

            File.WriteAllBytes(Path.GetFullPath(ShadowTexturePath), texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(ShadowTexturePath, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(ShadowTexturePath);
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Texture2D>(ShadowTexturePath);
        }

        private static Material GetOrCreateMaterial(string path, string name, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException(shaderName + " shader is missing");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }
            material.name = name;
            return material;
        }

        private static Material BuildCutoutMaterial(string path, string name, Texture2D texture, Color tint, CullMode cull)
        {
            Material material = GetOrCreateMaterial(path, name, "Universal Render Pipeline/Unlit");
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Cutoff", 0.45f);
            material.SetFloat("_Surface", 0f);          // opaque surface, cut-out via alpha test
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cull", (float)cull);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.Zero);
            material.SetFloat("_ZWrite", 1f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            material.doubleSidedGI = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material BuildMetalMaterial(string path)
        {
            Material material = GetOrCreateMaterial(path, "Nailong Standee Base", "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", new Color(0.075f, 0.08f, 0.095f, 1f));
            material.SetFloat("_Metallic", 0.55f);
            material.SetFloat("_Smoothness", 0.42f);
            material.SetFloat("_Cull", (float)CullMode.Back);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material BuildShadowMaterial(Texture2D shadow)
        {
            Material material = GetOrCreateMaterial(ShadowMaterialPath, "Nailong Standee Shadow",
                "Universal Render Pipeline/Unlit");
            material.SetTexture("_BaseMap", shadow);
            material.SetColor("_BaseColor", new Color(0f, 0f, 0f, 0.55f));
            material.SetFloat("_Cutoff", 0f);
            material.SetFloat("_Surface", 1f);          // transparent
            material.SetFloat("_Blend", 0f);            // alpha blend
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject BuildPrefab(Texture2D hitTexture, Material front, Material back,
            Material hit, Material metal, Material shadowMaterial)
        {
            var root = new GameObject(RootName);
            float scale = BoardHeight / DesignBoardHeight;
            float footHeight = DesignFootHeight * scale;
            float thickness = DesignBoardThickness * scale;
            float width = BoardHeight * BoardAspect;
            float hitWidth = BoardHeight * hitTexture.width / hitTexture.height;
            float boardCenterY = footHeight + BoardHeight * 0.5f;

            // The built-in quad's front face points to -Z, so the root's -Z is
            // the printed side. The second quad, turned around, is the dark back.
            GameObject frontBoard = CreateQuad("Board Front", root.transform,
                new Vector3(0f, boardCenterY, -thickness * 0.5f),
                Quaternion.identity, new Vector3(width, BoardHeight, 1f), front);
            GameObject backBoard = CreateQuad("Board Back", root.transform,
                new Vector3(0f, boardCenterY, thickness * 0.5f),
                Quaternion.Euler(0f, 180f, 0f), new Vector3(width, BoardHeight, 1f), back);
            // The hit artwork is a different (taller) shape, so it gets its own
            // aspect-correct quad instead of being stretched onto the normal one.
            GameObject hitBoard = CreateQuad("Board Front Hit", root.transform,
                new Vector3(0f, boardCenterY, -thickness * 0.5f - 0.02f),
                Quaternion.identity, new Vector3(hitWidth, BoardHeight, 1f), hit);
            hitBoard.GetComponent<MeshRenderer>().enabled = false;

            GameObject shadow = CreateQuad("Contact Shadow", root.transform,
                new Vector3(0f, 0.20f, 0.10f * scale),
                Quaternion.Euler(90f, 0f, 0f), new Vector3(width * 0.72f, 1.15f * scale, 1f), shadowMaterial);
            var shadowRenderer = shadow.GetComponent<MeshRenderer>();
            shadowRenderer.shadowCastingMode = ShadowCastingMode.Off;
            shadowRenderer.receiveShadows = false;

            GameObject basePlate = CreatePrimitive("Base", PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, footHeight * 0.5f, 0.06f * scale),
                Quaternion.identity, new Vector3(1.28f * scale, footHeight * 0.5f, 0.64f * scale), metal);
            GameObject post = CreatePrimitive("Post", PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, footHeight + 0.62f * scale, 0.115f * scale),
                Quaternion.identity, new Vector3(0.055f * scale, 0.62f * scale, 0.055f * scale), metal);

            // Trigger only: a round must fly straight through the standee, but its
            // entry flips the print for a second.
            var trigger = root.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, boardCenterY, 0f);
            trigger.size = new Vector3(width, BoardHeight, Mathf.Max(12f, thickness * 8f));

            var reaction = root.AddComponent<NailongStandeeReaction>();
            reaction.Configure(frontBoard.GetComponent<MeshRenderer>(), hitBoard.GetComponent<MeshRenderer>(), 1f);

            foreach (GameObject part in new[] { frontBoard, backBoard, hitBoard, shadow, basePlate, post })
                foreach (Collider collider in part.GetComponents<Collider>())
                    UnityEngine.Object.DestroyImmediate(collider);

            EnsureFolder(PrefabFolder);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject CreateQuad(string name, Transform parent, Vector3 localPosition,
            Quaternion localRotation, Vector3 localScale, Material material)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = localPosition;
            quad.transform.localRotation = localRotation;
            quad.transform.localScale = localScale;
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            return quad;
        }

        private static GameObject CreatePrimitive(string name, PrimitiveType type, Transform parent,
            Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localRotation = localRotation;
            primitive.transform.localScale = localScale;
            primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            return primitive;
        }

        // ------------------------------------------------------------------
        // Scene baking
        // ------------------------------------------------------------------

        private static void PlaceInScene(string scenePath, GameObject prefab)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == RootName) UnityEngine.Object.DestroyImmediate(root);

            PlacementSpot spot = ResolveSpot(scenePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = RootName;
            instance.transform.SetPositionAndRotation(spot.Position, spot.Rotation);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Nailong] Baked {RootName} into {scenePath} at {spot.Position} yaw={spot.Rotation.eulerAngles.y:F1}");
        }

        private struct PlacementSpot
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        /// <summary>
        /// Scans the field floor for the spot/heading that the largest number
        /// of in-field camera positions can actually see. Picking a corner by
        /// hand kept landing the standee on top of the base roofs.
        /// </summary>
        private static PlacementSpot ResolveSpot(string scenePath)
        {
            bool training = scenePath.IndexOf("Training", StringComparison.OrdinalIgnoreCase) >= 0;
            // RMUC field is 28 x 15 m = 280 x 150 units; stay 1 m inside.
            Rect field = training
                ? TrainingFieldRect()
                : Rect.MinMaxRect(-134f, -69f, 134f, 69f);

            floorHeight = DetermineFloorHeight(field);
            Debug.Log($"[Nailong] floor height {floorHeight:F2} units");
            List<Vector3> viewpoints = CollectViewpoints(field);
            List<Vector3> spawns = CollectSpawnPositions();
            Debug.Log($"[Nailong] scanning {field.width:F1}x{field.height:F1} field with {viewpoints.Count} viewpoints");

            float bestScore = float.NegativeInfinity;
            bool found = false;
            PlacementSpot best = default;
            int floorCount = 0, clearCount = 0, sightCount = 0, maxSight = 0;
            const float step = 17.5f;    // 1.75 m
            for (float x = field.xMin; x <= field.xMax + 0.001f; x += step)
            {
                for (float z = field.yMin; z <= field.yMax + 0.001f; z += step)
                {
                    if (!TryGroundFloor(new Vector3(x, 0f, z), out Vector3 grounded)) continue;
                    floorCount++;
                    if (NearAny(grounded, spawns, 30f)) continue;
                    for (int yaw = 0; yaw < 360; yaw += 30)
                    {
                        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
                        if (!IsClear(grounded, rotation)) continue;
                        clearCount++;
                        int visible = CountVisible(viewpoints, grounded, rotation);
                        maxSight = Mathf.Max(maxSight, visible);
                        if (visible < 2) continue;
                        sightCount++;
                        // Favour the sidelines slightly so the centre lanes stay
                        // clear, but never at the cost of a sightline.
                        float score = visible + 0.02f * Mathf.Min(Mathf.Abs(grounded.x), Mathf.Abs(grounded.z));
                        if (score <= bestScore) continue;
                        bestScore = score;
                        best = new PlacementSpot { Position = grounded, Rotation = rotation };
                        found = true;
                    }
                }
            }

            Debug.Log($"[Nailong] scan floor={floorCount} clear={clearCount} sight={sightCount} maxSight={maxSight}/{viewpoints.Count}");
            if (!found)
            {
                Debug.LogWarning($"[Nailong] No visible floor spot in {scenePath}; using fallback.");
                Vector3 fallback = new Vector3(field.center.x, floorHeight, field.center.y);
                if (TryGroundFloor(new Vector3(field.center.x, 0f, field.center.y), out Vector3 g)) fallback = g;
                return new PlacementSpot { Position = fallback, Rotation = Quaternion.identity };
            }

            Debug.Log($"[Nailong] best spot {best.Position} yaw={best.Rotation.eulerAngles.y:F0} score={bestScore:F1} " +
                      $"visible={CountVisible(viewpoints, best.Position, best.Rotation)}/{viewpoints.Count}");
            return best;
        }

        private static Rect TrainingFieldRect()
        {
            GameObject field = GameObject.Find("Field");
            if (field != null)
            {
                bool has = false;
                Bounds bounds = default;
                foreach (Renderer renderer in field.GetComponentsInChildren<Renderer>())
                {
                    if (!has) { bounds = renderer.bounds; has = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (has)
                    return Rect.MinMaxRect(bounds.min.x + 15f, bounds.min.z + 15f,
                        bounds.max.x - 15f, bounds.max.z - 15f);
            }
            RoleSelectionMenu menu = UnityEngine.Object.FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include);
            Vector3 center = menu != null && menu.spawnPoint != null ? menu.spawnPoint.position : Vector3.zero;
            return Rect.MinMaxRect(center.x - 60f, center.z - 40f, center.x + 60f, center.z + 40f);
        }

        /// <summary>
        /// The floor is the height most of the field's downward rays agree on.
        /// Raised ramps, covers and the space under a floor split the vote,
        /// so a mode is more reliable than any single probe.
        /// </summary>
        private static float DetermineFloorHeight(Rect field)
        {
            var counts = new Dictionary<int, int>();
            const float step = 20f;
            for (float x = field.xMin; x <= field.xMax + 0.001f; x += step)
            {
                for (float z = field.yMin; z <= field.yMax + 0.001f; z += step)
                {
                    if (!Physics.Raycast(new Vector3(x, 200f, z), Vector3.down, out RaycastHit hit, 500f, ~0,
                            QueryTriggerInteraction.Ignore)) continue;
                    if (hit.normal.y < 0.85f) continue;
                    if (hit.point.y < -2f || hit.point.y > 20f) continue;
                    int key = Mathf.RoundToInt(hit.point.y * 2f);
                    counts.TryGetValue(key, out int count);
                    counts[key] = count + 1;
                }
            }
            int bestKey = 0, bestCount = -1;
            foreach (KeyValuePair<int, int> entry in counts)
                if (entry.Value > bestCount) { bestCount = entry.Value; bestKey = entry.Key; }
            return bestKey / 2f;
        }

        private static List<Vector3> CollectViewpoints(Rect field)
        {
            var points = new List<Vector3>();
            const float step = 25f;      // 2.5 m
            for (float x = field.xMin; x <= field.xMax + 0.001f; x += step)
            {
                for (float z = field.yMin; z <= field.yMax + 0.001f; z += step)
                {
                    if (!TryGroundFloor(new Vector3(x, 0f, z), out Vector3 ground)) continue;
                    points.Add(ground + Vector3.up * 5f);   // 0.5 m eye height
                }
            }
            return points;
        }

        private static List<Vector3> CollectSpawnPositions()
        {
            var points = new List<Vector3>();
            foreach (SpawnManager manager in UnityEngine.Object.FindObjectsByType<SpawnManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var serialized = new SerializedObject(manager);
                foreach (string name in new[] { "redSpawnPoints", "blueSpawnPoints" })
                {
                    SerializedProperty array = serialized.FindProperty(name);
                    if (array == null) continue;
                    for (int i = 0; i < array.arraySize; i++)
                    {
                        var point = array.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                        if (point != null) points.Add(point.position);
                    }
                }
            }
            if (points.Count == 0)
            {
                RoleSelectionMenu menu = UnityEngine.Object.FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include);
                if (menu != null && menu.spawnPoint != null) points.Add(menu.spawnPoint.position);
            }
            return points;
        }

        /// <summary>Ground the point, but only on the field floor, not on a roof or ramp.</summary>
        private static bool TryGroundFloor(Vector3 candidate, out Vector3 grounded, float maxHeight = 2.5f)
        {
            grounded = candidate;
            if (!Physics.Raycast(candidate + Vector3.up * 200f, Vector3.down, out RaycastHit hit, 500f, ~0,
                    QueryTriggerInteraction.Ignore))
                return false;
            if (hit.point.y > maxHeight || hit.point.y < -2f) return false;
            if (hit.normal.y < 0.85f) return false;
            // Stay on the arena floor: a 0.3 m step is a lip; more is a cover,
            // ramp or the structure under a hole in the floor.
            if (Mathf.Abs(hit.point.y - floorHeight) > 3f) return false;
            grounded = hit.point + Vector3.up * 0.002f;
            return true;
        }

        private static bool NearAny(Vector3 point, List<Vector3> others, float radius)
        {
            float sqr = radius * radius;
            foreach (Vector3 other in others)
            {
                float dx = other.x - point.x, dz = other.z - point.z;
                if (dx * dx + dz * dz < sqr) return true;
            }
            return false;
        }

        private static int CountVisible(List<Vector3> viewpoints, Vector3 standee, Quaternion rotation)
        {
            Vector3 target = standee + Vector3.up * (BoardHeight * 0.55f);
            Vector3 normal = rotation * Vector3.back;   // printed side
            int visible = 0;
            foreach (Vector3 eye in viewpoints)
            {
                Vector3 toEye = eye - target;
                float distance = toEye.magnitude;
                if (distance < 15f || distance > 300f) continue;
                if (Vector3.Dot(toEye / distance, normal) < 0.25f) continue;
                if (Physics.Raycast(eye, -toEye / distance, distance - 1f, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                visible++;
            }
            return visible;
        }

        /// <summary>
        /// Point-samples the standee volume instead of using OverlapBox: the
        /// whole arena is one giant mesh collider, so a bounds test would
        /// always report a hit. A tiny sphere only touches colliders whose
        /// surface actually passes through the sample point.
        /// </summary>
        private static bool IsClear(Vector3 groundPosition, Quaternion rotation)
        {
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            float halfWidth = BoardHeight * BoardAspect * 0.46f;
            float halfDepth = BoardHeight * 0.13f;
            float radius = Mathf.Max(0.3f, BoardHeight * 0.03f);
            foreach (float height in new[] { BoardHeight * 0.25f, BoardHeight * 0.60f, BoardHeight * 0.92f })
            {
                foreach (float x in new[] { -halfWidth, 0f, halfWidth })
                {
                    foreach (float z in new[] { -halfDepth, halfDepth })
                    {
                        Vector3 point = groundPosition + right * x + forward * z + Vector3.up * height;
                        if (Physics.CheckSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
                            return false;
                    }
                }
            }
            return true;
        }
    }
}
