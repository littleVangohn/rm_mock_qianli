using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Captures the active simulator scene with live MuJoCo robot and shared markers.</summary>
public class SeasonMapController : MonoBehaviour
{
    GameObject fullMapRoot;
    RectTransform mapRect;
    ControlInput controls;
    OfficialPanelController panels;
    TacticalCommandController tactical;
    bool visible;

    public void Initialize(Canvas canvas, MujocoClient client, ControlInput input,
        OfficialPanelController panelController, Sprite sharedMarkerSprite)
    {
        controls = input;
        panels = panelController;
        Texture2D mapTexture = SimulatorMapCapture.AuthoredMap();
        if (!mapTexture)
        {
            Debug.LogError("SeasonMap: Resources/mujoco-2026-map is missing.");
            return;
        }
        Debug.Log($"[Map] M panel uses {mapTexture.name} {mapTexture.width}x{mapTexture.height}");

        fullMapRoot = new GameObject("Runtime Simulator Field Map Overlay");
        RectTransform root = fullMapRoot.AddComponent<RectTransform>();
        root.SetParent(canvas.transform, false);
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;

        Image shade = fullMapRoot.AddComponent<Image>();
        shade.color = new Color(.04f, .06f, .08f, .82f);
        shade.raycastTarget = false;
        CanvasGroup overlayOpacity = fullMapRoot.AddComponent<CanvasGroup>();
        overlayOpacity.alpha = 1f;

        RectTransform map = new GameObject("Runtime Simulator Field Map").AddComponent<RectTransform>();
        map.SetParent(root, false);
        map.anchorMin = map.anchorMax = new Vector2(.5f, .5f);
        map.pivot = new Vector2(.5f, .5f);
        map.sizeDelta = new Vector2(1728f, 972f);
        mapRect = map;
        Image mapImage = map.gameObject.AddComponent<Image>();
        mapImage.sprite = Sprite.Create(mapTexture, new Rect(0f, 0f, mapTexture.width, mapTexture.height),
            new Vector2(.5f, .5f));
        mapImage.preserveAspect = false;
        mapImage.color = Color.white;
        mapImage.raycastTarget = true;
        var markerClick = map.gameObject.AddComponent<MapMarkerClick>();
        markerClick.client = client;
        markerClick.mapRect = map;

        Image lightWash = new GameObject("Light Map Wash").AddComponent<Image>();
        lightWash.transform.SetParent(map, false);
        lightWash.rectTransform.anchorMin = Vector2.zero;
        lightWash.rectTransform.anchorMax = Vector2.one;
        lightWash.rectTransform.offsetMin = lightWash.rectTransform.offsetMax = Vector2.zero;
        lightWash.color = new Color(.82f, .94f, 1f, .32f);
        lightWash.raycastTarget = false;

        RuntimeMiniMap runtime = map.gameObject.AddComponent<RuntimeMiniMap>();
        runtime.mapRect = map;
        runtime.sharedMarkerSprite = sharedMarkerSprite;
        runtime.markerSize = 24f;
        runtime.Bind(client);

        Text hint = new GameObject("Close Hint").AddComponent<Text>();
        hint.transform.SetParent(root, false);
        hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(.5f, 0f);
        hint.rectTransform.anchoredPosition = new Vector2(0f, 26f);
        hint.rectTransform.sizeDelta = new Vector2(600f, 32f);
        hint.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hint.fontSize = 20;
        hint.alignment = TextAnchor.MiddleCenter;
        hint.color = Color.white;
        hint.text = "当前模拟器场地图    M / Esc 关闭";
        hint.raycastTarget = false;

        fullMapRoot.SetActive(false);
    }

    void Update()
    {
        if (!fullMapRoot) return;
        if (tactical && tactical.IsVisible) return;
        if (Input.GetKeyDown(KeyCode.M) && (panels == null || !panels.IsModal)) SetVisible(!visible);
        if (visible && Input.GetKeyDown(KeyCode.Escape)) SetVisible(false);
    }

    public void SetTacticalController(TacticalCommandController value) => tactical = value;
    public void HideForExternalOverlay() { if (visible) SetVisible(false); }

    void SetVisible(bool value)
    {
        visible = value;
        if (fullMapRoot) fullMapRoot.SetActive(value);
        // The full map is clickable for marking, so it owns the cursor.
        if (controls) controls.SetUiBlocked(value, value, mapRect);
        if (panels) panels.SetExternalOverlay(value);
    }

    void OnDestroy()
    {
        if (controls) controls.SetUiBlocked(false);
    }
}

/// <summary>Left-click on the full M map to place the shared marker.</summary>
public sealed class MapMarkerClick : MonoBehaviour, IPointerClickHandler
{
    public MujocoClient client;
    public RectTransform mapRect;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (client == null || mapRect == null ||
            eventData.button != PointerEventData.InputButton.Left) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                mapRect, eventData.position, eventData.pressEventCamera, out Vector2 local)) return;
        FrameBounds bounds = client.State?.frame?.bounds;
        if (bounds == null) return;
        Rect rect = mapRect.rect;
        float x = Mathf.Clamp01((local.x - rect.xMin) / rect.width);
        float y = Mathf.Clamp01((local.y - rect.yMin) / rect.height);
        client.MarkFieldPoint(
            Mathf.Lerp(bounds.xMin, bounds.xMax, x),
            Mathf.Lerp(bounds.yMin, bounds.yMax, y));    }
}
