using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Pacific / 千里战队 visual identity for the simulator shell.
///
/// The Welcome scene keeps its original menu layout, but the background is
/// painted with the same black-to-green gradient as Assets/image.png (drawn as
/// a resolution-independent gradient, not the 1280x719 bitmap), and the title
/// becomes 千里战队RM模拟器 with a small white team mark beside it.
/// </summary>
public static class MujocoBranding
{
    public const string ProjectTitle = "千里战队RM模拟器";

    private static Sprite gradientSprite;
    private static Sprite iconSprite;
    private static Sprite whiteSprite;
    private static Font uiFont;

    public static Font UiFont()
    {
        if (uiFont == null)
            uiFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, 24);
        if (uiFont == null) uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return uiFont;
    }

    /// <summary>Vertical black → dark green → mint gradient sampled from image.png.</summary>
    public static Sprite GradientSprite()
    {
        if (gradientSprite != null) return gradientSprite;
        const int height = 256;
        var texture = new Texture2D(1, height, TextureFormat.RGBA32, false)
        {
            name = "Qiandui Gradient",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        Color top = new Color(10f / 255f, 10f / 255f, 10f / 255f, 1f);
        Color shoulder = new Color(14f / 255f, 23f / 255f, 17f / 255f, 1f);
        Color green = new Color(41f / 255f, 101f / 255f, 60f / 255f, 1f);
        Color mint = new Color(92f / 255f, 251f / 255f, 143f / 255f, 1f);
        for (int y = 0; y < height; y++)
        {
            // u = 0 at the top of the image, 1 at the bottom.
            float u = 1f - y / (float)(height - 1);
            Color color;
            if (u < 0.55f) color = top;
            else if (u < 0.62f) color = Color.Lerp(top, shoulder, (u - 0.55f) / 0.07f);
            else if (u < 0.75f) color = Color.Lerp(shoulder, green, (u - 0.62f) / 0.13f);
            else color = Color.Lerp(green, mint, (u - 0.75f) / 0.25f);
            texture.SetPixel(0, height - 1 - y, color);
        }
        texture.Apply(false, true);
        gradientSprite = Sprite.Create(texture, new Rect(0, 0, 1, height), new Vector2(.5f, .5f), 100f);
        gradientSprite.name = "Qiandui Gradient";
        return gradientSprite;
    }

    /// <summary>
    /// The source mark is black on white. Convert luminance to alpha and paint
    /// the RGB white, giving a clean white silhouette on any background.
    /// </summary>
    public static Sprite IconSprite()
    {
        if (iconSprite != null) return iconSprite;
        Texture2D source = Resources.Load<Texture2D>("Branding/team-icon");
        if (source == null)
        {
            Debug.LogWarning("[Branding] Resources/Branding/team-icon is missing.");
            return null;
        }
        try
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);

            Color32[] pixels = readable.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                // The mark is already white with a transparent background; keep
                // its alpha and drop the source colour entirely.
                pixels[i] = new Color32(255, 255, 255, pixels[i].a);
            }
            var white = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
            {
                name = "Qiandui Team Mark",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            white.SetPixels32(pixels);
            white.Apply(false, true);
            iconSprite = Sprite.Create(white, new Rect(0, 0, white.width, white.height),
                new Vector2(.5f, .5f), 100f);
            iconSprite.name = "Qiandui Team Mark";
            return iconSprite;
        }
        catch (System.Exception exception)
        {
            // Headless/batchmode runs have no graphics device to blit through.
            Debug.LogWarning("[Branding] team mark unavailable: " + exception.Message);
            return null;
        }
    }

    public static Sprite WhiteSprite()
    {
        if (whiteSprite != null) return whiteSprite;
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "Qiandui White" };
        texture.SetPixel(0, 0, Color.white);
        texture.Apply(false, true);
        whiteSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f), 100f);
        return whiteSprite;
    }

    public static Text Label(string name, Transform parent, string value, int size,
        TextAnchor alignment, Color color)
    {
        var text = new GameObject(name, typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        text.transform.SetParent(parent, false);
        text.font = UiFont();
        text.text = value;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        return text;
    }
}

/// <summary>Applies the project title, white mark and gradient to the Welcome scene.</summary>
public sealed class MujocoWelcomeBranding : MonoBehaviour
{
    private static bool installed;
    private static GameObject brandingRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (installed) return;
        installed = true;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Apply();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // MenuManager keeps the Menu alive across scenes (DontDestroyOnLoad).
        // The gradient belongs to the start screen only; without this toggle
        // it would become the background of every later interface.
        bool welcome = scene.name == "Welcome";
        if (brandingRoot != null) brandingRoot.SetActive(welcome);
        if (welcome) Apply();
    }

    private static void Apply()
    {
        if (SceneManager.GetActiveScene().name != "Welcome") return;
        GameObject menu = GameObject.Find("Menu");
        if (menu == null) return;
        Transform existing = menu.transform.Find("Qiandui Branding Root");
        if (existing != null)
        {
            brandingRoot = existing.gameObject;
            brandingRoot.SetActive(true);
            return;
        }
        Transform main = menu.transform.Find("MainMenu");
        if (main == null) main = menu.transform;

        var root = new GameObject("Qiandui Branding Root", typeof(RectTransform));
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.SetParent(menu.transform, false);
        rootRect.SetAsFirstSibling();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
        brandingRoot = root;

        var gradient = new GameObject("Qiandui Gradient", typeof(RectTransform), typeof(Image));
        RectTransform gradientRect = gradient.GetComponent<RectTransform>();
        gradientRect.SetParent(rootRect, false);
        gradientRect.anchorMin = Vector2.zero;
        gradientRect.anchorMax = Vector2.one;
        gradientRect.offsetMin = gradientRect.offsetMax = Vector2.zero;
        Image gradientImage = gradient.GetComponent<Image>();
        gradientImage.sprite = MujocoBranding.GradientSprite();
        gradientImage.type = Image.Type.Simple;
        gradientImage.preserveAspect = false;
        gradientImage.raycastTarget = false;

        var shade = new GameObject("Qiandui Shade", typeof(RectTransform), typeof(Image));
        RectTransform shadeRect = shade.GetComponent<RectTransform>();
        shadeRect.SetParent(rootRect, false);
        shadeRect.anchorMin = Vector2.zero;
        shadeRect.anchorMax = Vector2.one;
        shadeRect.offsetMin = shadeRect.offsetMax = Vector2.zero;
        Image shadeImage = shade.GetComponent<Image>();
        shadeImage.color = new Color(0f, 0f, 0f, .16f);
        shadeImage.raycastTarget = false;

        Transform oldTitle = main.Find("Title");
        if (oldTitle != null) oldTitle.gameObject.SetActive(false);

        var branding = new GameObject("Qiandui Branding", typeof(RectTransform));
        RectTransform brandingRect = branding.GetComponent<RectTransform>();
        brandingRect.SetParent(rootRect, false);
        brandingRect.anchorMin = brandingRect.anchorMax = new Vector2(.5f, .5f);
        brandingRect.anchoredPosition = new Vector2(0f, 196f);
        brandingRect.sizeDelta = new Vector2(640f, 64f);

        Sprite mark = MujocoBranding.IconSprite();
        if (mark != null)
        {
            var icon = new GameObject("Team Mark", typeof(RectTransform), typeof(Image));
            RectTransform iconRect = icon.GetComponent<RectTransform>();
            iconRect.SetParent(brandingRect, false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(.5f, .5f);
            iconRect.anchoredPosition = new Vector2(-220f, 0f);
            iconRect.sizeDelta = new Vector2(56f, 56f);
            Image iconImage = icon.GetComponent<Image>();
            iconImage.sprite = mark;
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
        }

        Text title = MujocoBranding.Label("Qiandui Title", brandingRect,
            MujocoBranding.ProjectTitle, 44, TextAnchor.MiddleCenter, Color.white);
        RectTransform titleRect = title.rectTransform;
        titleRect.anchorMin = titleRect.anchorMax = new Vector2(.5f, .5f);
        titleRect.anchoredPosition = new Vector2(30f, 0f);
        titleRect.sizeDelta = new Vector2(500f, 64f);
        title.fontStyle = FontStyle.Bold;
        var outline = title.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, .55f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);
        Debug.Log("[Branding] Welcome gradient, title and team mark applied.");
    }
}

/// <summary>Full-screen loading page with a white progress bar, shown after Spawn.</summary>
public sealed class MujocoLoadingScreen : MonoBehaviour
{
    private static MujocoLoadingScreen current;
    private CanvasGroup group;
    private Image fill;
    private Text percent;
    private Text status;
    private bool hiding;

    public static MujocoLoadingScreen Show()
    {
        if (current != null) return current;
        var root = new GameObject("Qiandui Loading Screen",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster), typeof(CanvasGroup));
        Object.DontDestroyOnLoad(root);
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = .5f;

        var screen = root.AddComponent<MujocoLoadingScreen>();
        current = screen;
        screen.group = root.GetComponent<CanvasGroup>();
        screen.Build(root.transform);
        return screen;
    }

    private void Build(Transform root)
    {
        var background = new GameObject("Gradient", typeof(RectTransform), typeof(Image));
        RectTransform backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.SetParent(root, false);
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;
        Image backgroundImage = background.GetComponent<Image>();
        backgroundImage.sprite = MujocoBranding.GradientSprite();
        backgroundImage.type = Image.Type.Simple;
        backgroundImage.preserveAspect = false;
        backgroundImage.raycastTarget = true;

        Sprite mark = MujocoBranding.IconSprite();
        if (mark != null)
        {
            var icon = new GameObject("Team Mark", typeof(RectTransform), typeof(Image));
            RectTransform iconRect = icon.GetComponent<RectTransform>();
            iconRect.SetParent(root, false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(.5f, .5f);
            iconRect.anchoredPosition = new Vector2(0f, 170f);
            iconRect.sizeDelta = new Vector2(150f, 150f);
            Image iconImage = icon.GetComponent<Image>();
            iconImage.sprite = mark;
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
        }

        Text title = MujocoBranding.Label("Title", root, MujocoBranding.ProjectTitle,
            46, TextAnchor.MiddleCenter, Color.white);
        title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(.5f, .5f);
        title.rectTransform.anchoredPosition = new Vector2(0f, 45f);
        title.rectTransform.sizeDelta = new Vector2(1000f, 70f);
        title.fontStyle = FontStyle.Bold;

        status = MujocoBranding.Label("Status", root, "正在启动模拟器…",
            23, TextAnchor.MiddleCenter, new Color(.86f, .96f, .88f, 1f));
        status.rectTransform.anchorMin = status.rectTransform.anchorMax = new Vector2(.5f, .5f);
        status.rectTransform.anchoredPosition = new Vector2(0f, -30f);
        status.rectTransform.sizeDelta = new Vector2(1000f, 34f);

        var track = new GameObject("Progress Track", typeof(RectTransform), typeof(Image));
        RectTransform trackRect = track.GetComponent<RectTransform>();
        trackRect.SetParent(root, false);
        trackRect.anchorMin = trackRect.anchorMax = new Vector2(.5f, .5f);
        trackRect.anchoredPosition = new Vector2(0f, -105f);
        trackRect.sizeDelta = new Vector2(620f, 14f);
        Image trackImage = track.GetComponent<Image>();
        trackImage.sprite = MujocoBranding.WhiteSprite();
        trackImage.type = Image.Type.Simple;
        trackImage.color = new Color(0f, 0f, 0f, .45f);
        trackImage.raycastTarget = false;

        var bar = new GameObject("Progress Fill", typeof(RectTransform), typeof(Image));
        RectTransform barRect = bar.GetComponent<RectTransform>();
        barRect.SetParent(trackRect, false);
        barRect.anchorMin = Vector2.zero;
        barRect.anchorMax = Vector2.one;
        barRect.offsetMin = new Vector2(2f, 2f);
        barRect.offsetMax = new Vector2(-2f, -2f);
        fill = bar.GetComponent<Image>();
        fill.sprite = MujocoBranding.WhiteSprite();
        fill.color = Color.white;
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;

        percent = MujocoBranding.Label("Percent", root, "0%", 18,
            TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, .8f));
        percent.rectTransform.anchorMin = percent.rectTransform.anchorMax = new Vector2(.5f, .5f);
        percent.rectTransform.anchoredPosition = new Vector2(0f, -138f);
        percent.rectTransform.sizeDelta = new Vector2(200f, 26f);
    }

    public void SetProgress(float progress, string message)
    {
        if (hiding) return;
        progress = Mathf.Clamp01(progress);
        if (fill != null) fill.fillAmount = progress;
        if (percent != null) percent.text = Mathf.RoundToInt(progress * 100f) + "%";
        if (status != null && !string.IsNullOrEmpty(message)) status.text = message;
    }

    public void HideAfter(float seconds)
    {
        if (!hiding) StartCoroutine(HideRoutine(seconds));
    }

    private IEnumerator HideRoutine(float seconds)
    {
        hiding = true;
        yield return new WaitForSecondsRealtime(seconds);
        float elapsed = 0f;
        while (elapsed < .25f)
        {
            elapsed += Time.unscaledDeltaTime;
            if (group != null) group.alpha = 1f - Mathf.Clamp01(elapsed / .25f);
            yield return null;
        }
        Destroy(gameObject);
        if (current == this) current = null;
    }
}
