using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public static class OfficialUiKit
{
    public static readonly Color Blue = new Color(0.08f, 0.72f, 1f, 1f);
    public static readonly Color Red = new Color(1f, 0.18f, 0.12f, 1f);
    public static readonly Color Cyan = new Color(0.24f, 0.94f, 1f, 1f);
    public static readonly Color Green = new Color(0.38f, 1f, 0.48f, 1f);
    public static readonly Color Amber = new Color(1f, 0.72f, 0.12f, 1f);
    public static readonly Color Dim = new Color(0.55f, 0.66f, 0.70f, 0.82f);
    public static readonly Color PanelColor = new Color(0.018f, 0.026f, 0.032f, 0.94f);
    static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

    public static Sprite Sprite(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (sprites.TryGetValue(name, out Sprite cached)) return cached;
        Texture2D texture = Resources.Load<Texture2D>("OfficialUI/" + name);
        if (!texture) return null;
        cached = UnityEngine.Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f);
        sprites[name] = cached;
        return cached;
    }

    public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    public static Image Image(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size,
        string spriteName = null, Color? color = null)
    {
        var image = new GameObject(name).AddComponent<Image>();
        image.transform.SetParent(parent, false);
        image.rectTransform.anchorMin = image.rectTransform.anchorMax = anchor;
        image.rectTransform.pivot = pivot;
        image.rectTransform.anchoredPosition = position;
        image.rectTransform.sizeDelta = size;
        image.sprite = Sprite(spriteName);
        image.preserveAspect = image.sprite;
        image.color = color ?? Color.white;
        image.raycastTarget = false;
        return image;
    }

    public static Text Text(string name, Transform parent, Font font, string value, Vector2 anchor, Vector2 pivot,
        Vector2 position, Vector2 size, int fontSize, Color color, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        var text = new GameObject(name).AddComponent<Text>();
        text.transform.SetParent(parent, false);
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = anchor;
        text.rectTransform.pivot = pivot;
        text.rectTransform.anchoredPosition = position;
        text.rectTransform.sizeDelta = size;
        text.font = font;
        text.fontSize = fontSize;
        text.color = color;
        text.text = value;
        text.alignment = alignment;
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    public static Image Panel(string name, Transform parent, Vector2 size, string spriteName = null)
    {
        Image panel = Image(name, parent, Vector2.one * .5f, Vector2.one * .5f, Vector2.zero, size, spriteName,
            spriteName == null ? PanelColor : Color.white);
        if (panel.sprite) panel.type = UnityEngine.UI.Image.Type.Sliced;
        return panel;
    }

    public static Button Button(string name, Transform parent, Font font, string caption, Vector2 position, Vector2 size,
        UnityAction clicked, bool enabled = true)
    {
        Image image = Image(name, parent, Vector2.one * .5f, Vector2.one * .5f, position, size,
            "Setting_Button_Rect", enabled ? new Color(.08f, .30f, .36f, .98f) : new Color(.12f, .14f, .15f, .82f));
        image.raycastTarget = enabled;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.interactable = enabled;
        if (clicked != null) button.onClick.AddListener(clicked);
        Text("Text", image.transform, font, caption, Vector2.one * .5f, Vector2.one * .5f, Vector2.zero, size,
            16, enabled ? Color.white : Dim);
        return button;
    }

    public static Image Line(Transform parent, Vector2 from, Vector2 to, float width, Color color)
    {
        Image image = Image("Line", parent, Vector2.one * .5f, Vector2.one * .5f, (from + to) * .5f,
            new Vector2(Vector2.Distance(from, to), width), null, color);
        Vector2 delta = to - from;
        image.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        return image;
    }
}
