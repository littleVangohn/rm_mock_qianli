using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Pacific.Mujoco.Editor
{
    /// <summary>
    /// Bakes the 千里战队 start screen into Assets/Scenes/Welcome.unity so it is
    /// visible in the editor before Play. The runtime branding script detects the
    /// "Qiandui Branding Root" node and leaves it alone.
    /// </summary>
    public static class WelcomeSceneBranding
    {
        private const string RootName = "Qiandui Branding Root";
        private const string BrandingFolder = "Assets/PacificOfficialClient/Resources/Branding/";

        public static void ApplyAndSave()
        {
            ConfigureSprite(BrandingFolder + "welcome-gradient.png");
            ConfigureSprite(BrandingFolder + "welcome-title.png");
            ConfigureSprite(BrandingFolder + "team-icon-white.png");

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Welcome.unity", OpenSceneMode.Single);
            GameObject menu = FindDeep(scene.GetRootGameObjects(), "Menu");
            if (menu == null)
            {
                Debug.LogError("[Branding] Welcome has no Menu object.");
                return;
            }
            Transform old = menu.transform.Find(RootName);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            Transform main = menu.transform.Find("MainMenu");
            Transform oldTitle = main != null ? FindDeep(main, "Title") : null;
            if (oldTitle != null) oldTitle.gameObject.SetActive(false);

            var root = new GameObject(RootName, typeof(RectTransform));
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.SetParent(menu.transform, false);
            rootRect.SetAsFirstSibling();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;

            Image gradient = AddImage("Q Gradient", rootRect,
                BrandingFolder + "welcome-gradient.png", Color.white);
            Fill(gradient.rectTransform);
            gradient.preserveAspect = false;

            Image shade = AddImage("Q Shade", rootRect, null, new Color(0f, 0f, 0f, .16f));
            Fill(shade.rectTransform);

            Image icon = AddImage("Q Team Mark", rootRect,
                BrandingFolder + "team-icon-white.png", Color.white);
            RectTransform iconRect = icon.rectTransform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(.5f, .5f);
            iconRect.anchoredPosition = new Vector2(-220f, 196f);
            iconRect.sizeDelta = new Vector2(56f, 56f);
            icon.preserveAspect = true;

            Image title = AddImage("Q Title", rootRect,
                BrandingFolder + "welcome-title.png", Color.white);
            RectTransform titleRect = title.rectTransform;
            titleRect.anchorMin = titleRect.anchorMax = new Vector2(.5f, .5f);
            titleRect.anchoredPosition = new Vector2(30f, 196f);
            titleRect.sizeDelta = new Vector2(500f, 75f);
            title.preserveAspect = true;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Branding] Welcome scene baked: gradient, title and white team mark.");
        }

        /// <summary>Diagnostic: confirm the authored map is visible to Resources.Load.</summary>
        public static void ProbeMap()
        {
            var map = Resources.Load<Texture2D>("mujoco-2026-map");
            var official = Resources.Load<Texture2D>("OfficialUI/2026map");
            Debug.Log($"[MapProbe] mujoco-2026-map={(map != null ? map.name + " " + map.width + "x" + map.height : "NULL")} " +
                      $"OfficialUI/2026map={(official != null ? official.name + " " + official.width + "x" + official.height : "NULL")}");
        }

        private static Image AddImage(string name, Transform parent, string spritePath, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            if (!string.IsNullOrEmpty(spritePath))
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void Fill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static void ConfigureSprite(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.spritePixelsPerUnit = 100f;
            importer.SaveAndReimport();
        }

        private static GameObject FindDeep(GameObject[] roots, string name)
        {
            foreach (GameObject root in roots)
            {
                Transform found = FindDeep(root.transform, name);
                if (found != null) return found.gameObject;
            }
            return null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
