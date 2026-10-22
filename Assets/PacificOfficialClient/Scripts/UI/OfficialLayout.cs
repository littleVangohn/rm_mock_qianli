using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the HUD straight from the layout reversed out of the official client.
///
/// Nothing in here positions anything by hand: every anchor, pivot, offset, size, colour,
/// sprite name and string comes from <c>Resources/OfficialUI/layout.txt</c>, which the
/// extractor writes out of the official serialized assets. Node lookup by path is how the
/// data binders find the widgets they drive.
/// </summary>
public class OfficialLayout
{
    public RectTransform Root { get; private set; }
    public readonly Dictionary<string, List<RectTransform>> ByPath = new Dictionary<string, List<RectTransform>>();
    public readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

    Font font;
    string resourceFolder;
    readonly List<string> warnings = new List<string>();
    public IReadOnlyList<string> Warnings => warnings;

    public static OfficialLayout Build(Transform parent, Font uiFont, string resourceFolder = "OfficialUI")
    {
        var layout = new OfficialLayout { font = uiFont, resourceFolder = resourceFolder };
        layout.LoadSprites(resourceFolder);
        layout.LoadTree(parent, resourceFolder);
        return layout;
    }

    // ------------------------------------------------------------------ sprites

    void LoadSprites(string folder)
    {
        var manifest = Resources.Load<TextAsset>(folder + "/sprites");
        if (manifest != null)
        {
            string[] lines = manifest.text.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                if (f.Length < 6) continue;

                var texture = Resources.Load<Texture2D>(folder + "/" + f[0]);
                if (!texture)
                {
                    warnings.Add("贴图缺失: " + f[0]);
                    continue;
                }

                Vector2 pivot = ParseVector2(f[3], new Vector2(.5f, .5f));
                Vector4 border = ParseBorder(f[4]);
                float ppu = ParseFloat(f[5], 100f);

                // The exported PNG is already cropped to the sprite rect, so the sprite
                // covers the whole texture; the official border still applies for 9-slice.
                var sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    pivot, ppu, 0, SpriteMeshType.FullRect, border);
                sprite.name = f[0];
                Sprites[f[0]] = sprite;
            }
        }
        else
        {
            warnings.Add("找不到 " + folder + "/sprites.txt");
        }
    }

    public Sprite GetSprite(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (Sprites.TryGetValue(name, out Sprite sprite)) return sprite;

        // A handful of role portraits are runtime-selected and therefore do not
        // occur in layout.txt/sprites.txt, although their extracted PNGs are in the
        // resource folder. Load those on demand so dynamic Tab/result rows are not
        // left with the intentionally empty placeholder portrait.
        Texture2D texture = Resources.Load<Texture2D>(resourceFolder + "/" + name);
        if (!texture) return null;
        sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
            Vector2.one * .5f, 100f, 0, SpriteMeshType.FullRect);
        sprite.name = name;
        Sprites[name] = sprite;
        return sprite;
    }

    // --------------------------------------------------------------------- tree

    void LoadTree(Transform parent, string folder)
    {
        var asset = Resources.Load<TextAsset>(folder + "/layout");
        if (asset == null)
        {
            warnings.Add("找不到 " + folder + "/layout.txt");
            Root = new GameObject("OfficialLayout(missing)").AddComponent<RectTransform>();
            Root.SetParent(parent, false);
            return;
        }

        var canvasRoot = new GameObject("Official HUD (reversed)").AddComponent<RectTransform>();
        canvasRoot.SetParent(parent, false);
        canvasRoot.anchorMin = Vector2.zero;
        canvasRoot.anchorMax = Vector2.one;
        canvasRoot.offsetMin = canvasRoot.offsetMax = Vector2.zero;
        Root = canvasRoot;

        // Stack of (depth, transform, path) so a flat file rebuilds the nesting.
        var stack = new List<(int depth, RectTransform rect, string path)>
        {
            (-1, canvasRoot, "")
        };

        string[] lines = asset.text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f.Length < 8)
            {
                warnings.Add("第 " + (i + 1) + " 行字段不足");
                continue;
            }

            int depth = ParseInt(f[0], 0);
            string name = Unescape(f[1]);
            bool active = f[2] != "0";

            while (stack.Count > 1 && stack[stack.Count - 1].depth >= depth)
                stack.RemoveAt(stack.Count - 1);

            var parentEntry = stack[stack.Count - 1];
            string path = parentEntry.path.Length == 0 ? name : parentEntry.path + "/" + name;

            var go = new GameObject(name.Length == 0 ? "node" : name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parentEntry.rect, false);
            ApplyTransform(rect, f[3]);
            ApplyImage(go, rect, f[4]);
            ApplyText(go, rect, f[5]);
            ApplyComponents(go, f[6], f[7]);

            go.SetActive(active);

            if (!ByPath.TryGetValue(path, out List<RectTransform> bucket))
                ByPath[path] = bucket = new List<RectTransform>();
            bucket.Add(rect);

            stack.Add((depth, rect, path));
        }
    }

    static void ApplyTransform(RectTransform rect, string field)
    {
        if (string.IsNullOrEmpty(field)) return;
        string[] v = field.Split(',');
        if (v.Length < 13) return;
        var anchorMin = new Vector2(ParseFloat(v[0], .5f), ParseFloat(v[1], .5f));
        var anchorMax = new Vector2(ParseFloat(v[2], .5f), ParseFloat(v[3], .5f));
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(ParseFloat(v[8], .5f), ParseFloat(v[9], .5f));
        rect.sizeDelta = new Vector2(ParseFloat(v[6], 0f), ParseFloat(v[7], 0f));
        rect.anchoredPosition = new Vector2(ParseFloat(v[4], 0f), ParseFloat(v[5], 0f));
        rect.localScale = new Vector3(ParseFloat(v[10], 1f), ParseFloat(v[11], 1f), 1f);
        float z = ParseFloat(v[12], 0f);
        if (z != 0f) rect.localRotation = Quaternion.Euler(0, 0, z);
    }

    void ApplyImage(GameObject go, RectTransform rect, string field)
    {
        if (string.IsNullOrEmpty(field)) return;
        string[] v = field.Split('|');
        if (v.Length < 6) return;

        var image = go.AddComponent<Image>();
        image.sprite = GetSprite(Unescape(v[0]));
        image.color = ParseColor(v[1], Color.white);
        image.type = (Image.Type)ParseInt(v[2], 0);
        if (v[3].Length > 0) image.fillMethod = (Image.FillMethod)ParseInt(v[3], 0);
        if (v[4].Length > 0) image.fillAmount = ParseFloat(v[4], 1f);
        image.preserveAspect = v[5].Contains("p");
        // An enabled Image with no sprite paints a white quad; the official client leaves lots
        // of those placeholders disabled, so respect the flag.
        image.enabled = !v[5].Contains("x");
        // Our own input handles clicks; keep the HUD out of the raycast path so a
        // full-screen official backdrop cannot swallow marker placement.
        image.raycastTarget = false;
        if (image.type == Image.Type.Sliced && (image.sprite == null || image.sprite.border == Vector4.zero))
            image.type = Image.Type.Simple;
    }

    void ApplyText(GameObject go, RectTransform rect, string field)
    {
        if (string.IsNullOrEmpty(field)) return;
        string[] v = field.Split('|');
        if (v.Length < 12) return;

        var text = go.AddComponent<Text>();
        text.font = font;
        text.text = Unescape(v[0]);
        text.fontSize = Mathf.RoundToInt(ParseFloat(v[1], 24f));
        text.alignment = (TextAnchor)ParseInt(v[2], 4);
        text.color = ParseColor(v[3], Color.white);
        text.fontStyle = (FontStyle)ParseInt(v[4], 0);
        text.horizontalOverflow = (HorizontalWrapMode)ParseInt(v[5], 0);
        text.verticalOverflow = (VerticalWrapMode)ParseInt(v[6], 1);
        if (v[7] == "b")
        {
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = Mathf.RoundToInt(ParseFloat(v[8], 1f));
            text.resizeTextMaxSize = Mathf.RoundToInt(ParseFloat(v[9], 40f));
        }
        if (v[10].Length > 0) text.lineSpacing = ParseFloat(v[10], 1f);
        text.supportRichText = true;
        text.raycastTarget = false;
        text.enabled = v.Length < 13 || v[12] != "x";

        // Unity's serialized ContentSizeFitter stores its sizing modes on the component,
        // not on RectTransform.  The extractor only knew that the component existed, so
        // imported labels whose authored rect was 0x0 stayed invisible.  Those labels all
        // use fixed anchors; recover the same runtime geometry from Text's preferred size.
        Vector2 size = rect.sizeDelta;
        if (rect.anchorMin == rect.anchorMax)
        {
            // Width must be committed before asking Unity for preferredHeight.  With a
            // zero-width rect the generator wraps CJK text one glyph per line, producing
            // a huge box whose middle-aligned label appears hundreds of pixels too low.
            if (Mathf.Abs(size.x) < .01f)
            {
                size.x = Mathf.Ceil(text.preferredWidth) + 2f;
                rect.sizeDelta = size;
            }
            if (Mathf.Abs(size.y) < .01f)
            {
                size.y = Mathf.Ceil(text.preferredHeight) + 2f;
                rect.sizeDelta = size;
            }
        }
    }

    void ApplyComponents(GameObject go, string field, string layout)
    {
        if (!string.IsNullOrEmpty(field))
        {
            foreach (string cls in field.Split(','))
            {
                switch (cls)
                {
                    case "Mask":
                        go.AddComponent<Mask>().showMaskGraphic = true;
                        break;
                    case "RectMask2D":
                        go.AddComponent<RectMask2D>();
                        break;
                    case "Outline":
                        go.AddComponent<Outline>();
                        break;
                    case "Shadow":
                        go.AddComponent<Shadow>();
                        break;
                    case "ContentSizeFitter":
                        go.AddComponent<ContentSizeFitter>();
                        break;
                    case "LayoutElement":
                        go.AddComponent<LayoutElement>();
                        break;
                    case "CanvasGroup":
                        go.AddComponent<CanvasGroup>();
                        break;
                    // Layout groups are applied below, from the measured settings.
                    case "GridLayoutGroup":
                    case "HorizontalLayoutGroup":
                    case "VerticalLayoutGroup":
                        break;
                }
            }
        }
        // Dropdown needs its imported item Toggle and scroll container. Do
        // not globally make every decorative Button image clickable: a button
        // without an attached rule/backend action would be a false control.
        if (!string.IsNullOrEmpty(layout) && layout.Contains("Toggle"))
            go.AddComponent<Toggle>();
        if (!string.IsNullOrEmpty(layout) && layout.Contains("ScrollRect"))
            go.AddComponent<ScrollRect>();
        ApplyLayoutGroup(go, layout);
    }

    /// <summary>Official cell metrics for the containers the client fills at runtime.</summary>
    void ApplyLayoutGroup(GameObject go, string field)
    {
        if (string.IsNullOrEmpty(field)) return;
        string[] parts = field.Split('|');
        string kind = parts[0];

        if (kind == "G" && parts.Length >= 6)
        {
            var grid = go.AddComponent<GridLayoutGroup>();
            grid.cellSize = ParseVector2(parts[1], new Vector2(100, 100));
            grid.spacing = ParseVector2(parts[2], Vector2.zero);
            grid.padding = ParseRectOffset(parts[3]);
            grid.childAlignment = (TextAnchor)ParseInt(parts[4], 0);
            grid.constraint = (GridLayoutGroup.Constraint)ParseInt(parts[5].Split(',')[0], 0);
            grid.constraintCount = Mathf.Max(1, ParseInt(parts[5].Split(',')[1], 1));
        }
        else if ((kind == "H" || kind == "V") && parts.Length >= 7)
        {
            HorizontalOrVerticalLayoutGroup group = kind == "H"
                ? (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>()
                : go.AddComponent<VerticalLayoutGroup>();
            group.spacing = ParseFloat(parts[1], 0f);
            group.padding = ParseRectOffset(parts[2]);
            group.childAlignment = (TextAnchor)ParseInt(parts[3], 0);
            string[] control = parts[4].Split(',');
            group.childControlWidth = control.Length > 0 && control[0] == "1";
            group.childControlHeight = control.Length > 1 && control[1] == "1";
            string[] expand = parts[5].Split(',');
            group.childForceExpandWidth = expand.Length > 0 && expand[0] == "1";
            group.childForceExpandHeight = expand.Length > 1 && expand[1] == "1";
        }
    }

    static RectOffset ParseRectOffset(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length < 4) return new RectOffset();
        return new RectOffset(
            ParseInt(parts[0], 0), ParseInt(parts[2], 0),
            ParseInt(parts[3], 0), ParseInt(parts[1], 0));
    }

    // --------------------------------------------------------------- lookup

    /// <summary>First node registered at this slash-separated path, or null.</summary>
    public RectTransform Find(string path)
    {
        return ByPath.TryGetValue(path, out List<RectTransform> bucket) && bucket.Count > 0 ? bucket[0] : null;
    }

    public bool SetActive(string path, bool active)
    {
        RectTransform rect = Find(path);
        if (!rect) return false;
        rect.gameObject.SetActive(active);
        return true;
    }

    /// <summary>Depth-first search for a named descendant.</summary>
    public static RectTransform FindChild(Transform root, string name)
    {
        if (!root) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == name) return child as RectTransform;
            RectTransform found = FindChild(child, name);
            if (found) return found;
        }
        return null;
    }

    public static Text FindText(Transform root, string name)
    {
        RectTransform rect = FindChild(root, name);
        return rect ? rect.GetComponent<Text>() : null;
    }

    public static Image FindImage(Transform root, string name)
    {
        RectTransform rect = FindChild(root, name);
        return rect ? rect.GetComponent<Image>() : null;
    }

    // ---------------------------------------------------------------- parsing

    static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                switch (value[i])
                {
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'p': sb.Append('|'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(value[i]); break;
                }
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    static float ParseFloat(string value, float fallback)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;
    }

    static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    }

    static Vector2 ParseVector2(string value, Vector2 fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        string[] parts = value.Split(',');
        if (parts.Length < 2) return fallback;
        return new Vector2(ParseFloat(parts[0], fallback.x), ParseFloat(parts[1], fallback.y));
    }

    static Vector4 ParseBorder(string value)
    {
        if (string.IsNullOrEmpty(value)) return Vector4.zero;
        string[] parts = value.Split(',');
        if (parts.Length < 4) return Vector4.zero;
        return new Vector4(
            ParseFloat(parts[0], 0f), ParseFloat(parts[1], 0f),
            ParseFloat(parts[2], 0f), ParseFloat(parts[3], 0f));
    }

    static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        string[] parts = value.Split(',');
        if (parts.Length < 4) return fallback;
        return new Color(
            ParseFloat(parts[0], fallback.r), ParseFloat(parts[1], fallback.g),
            ParseFloat(parts[2], fallback.b), ParseFloat(parts[3], fallback.a));
    }
}
