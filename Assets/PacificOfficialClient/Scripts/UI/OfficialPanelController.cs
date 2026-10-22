using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Opens the official client's own panels on their documented hotkeys.
///
/// Every panel here is a node that already exists in the reversed layout — opening one is
/// just SetActive(true) on the official subtree, so the panel's look, its child layout and
/// its text all come from the client's own assets. The only thing this file draws itself is
/// the key-rebinding strip, which is our feature and has no official equivalent.
///
/// Hotkey → manual page → official node:
///   Tab (hold)  p19 战况总览        TabItem
///   Esc / P     p21 设置面板        SettingPanel
///   O           p22 17mm 补弹       SupplyItem2024
///   I           p23 42mm 补弹       HeroSupplyItem2024
///   M           本赛季 MuJoCo 实时大地图
///   H           p13 远程兑换 / 工程车兑换
/// Unsupported static module/help art is hidden until a source rule backs it.
/// </summary>
public class OfficialPanelController : MonoBehaviour
{
    const string SettingsPath = "SetModulePanel/SettingPanel";
    const float PopupOpacity = .6f;
    OfficialLayout layout;
    ControlInput input;
    MujocoClient client;
    Font font;

    readonly Dictionary<KeyCode, string> modals = new Dictionary<KeyCode, string>();
    GameObject modal;
    string modalPath;
    GameObject rebindStrip;
    RectTransform tabPanel;
    Text hintLabel;
    readonly Dictionary<BindableAction, Text> bindingLabels = new Dictionary<BindableAction, Text>();
    BindableAction? waitingFor;
    bool externalOverlay;
    int ignoreInputFrame = -1;
    Dropdown robotDropdown;
    Text robotStatus;
    Action<int> switchRobot;
    readonly List<int> availableRobotIds = new List<int>();

    static readonly BindableAction[] Actions =
    {
        BindableAction.Forward, BindableAction.Backward, BindableAction.StrafeLeft, BindableAction.StrafeRight,
        BindableAction.Boost, BindableAction.Brake, BindableAction.Fire,
        BindableAction.AutoLob, BindableAction.CameraOperator, BindableAction.CameraOverview, BindableAction.CameraBallistics,
    };

    public bool IsModal => modal != null;

    public void Initialize(Canvas canvas, Font uiFont, MujocoClient source, ControlInput controls,
        OfficialLayout sharedLayout)
    {
        layout = sharedLayout;
        input = controls;
        client = source;
        font = uiFont;

        modals[KeyCode.O] = "SupplyItem2024";             // p22
        modals[KeyCode.I] = "HeroSupplyItem2024";         // p23
        // Original simulator has no dart/deployment rule implementation.
        // Do not expose artwork-only controls as if they were operational.
        modals[KeyCode.H] = "RemoteExchange";             // p13
        modals[KeyCode.P] = SettingsPath;               // p21
        modals[KeyCode.Equals] = SettingsPath;          // + / =
        modals[KeyCode.Minus] = SettingsPath;
        modals[KeyCode.KeypadPlus] = SettingsPath;
        modals[KeyCode.KeypadMinus] = SettingsPath;
        modals[KeyCode.Asterisk] = SettingsPath;
        modals[KeyCode.Slash] = SettingsPath;

        tabPanel = layout.Find("TabItem");                // p19, held rather than toggled
        BuildRebindStrip(canvas);
        FixImportedPanelLayers();
        ConfigureSupportedSettings();
        ApplyPopupOpacity();
        layout.SetActive("MapPanelFullScreen", false);

        foreach (string path in modals.Values) layout.SetActive(path, false);

        string[] missing = System.Array.FindAll(new List<string>(modals.Values).ToArray(),
            path => layout.Find(path) == null);
        if (missing.Length > 0) Debug.LogWarning("OfficialPanel: 缺少面板节点 " + string.Join(", ", missing));
        Debug.Log($"OfficialPanel: modals={modals.Count} tab={(tabPanel ? "ok" : "缺失")}");
    }

    public void ConfigureRobotSwitch(Action<int> onSwitch, int activeActualId, IEnumerable<int> availableIds = null)
    {
        switchRobot = onSwitch;
        availableRobotIds.Clear();
        if (availableIds != null) availableRobotIds.AddRange(availableIds);
        else for (int side = 0; side < 2; side++)
            for (int id = 1; id <= 7; id++) availableRobotIds.Add(id + (side == 0 ? 0 : 20));
        availableRobotIds.Sort();
        RectTransform panel = layout.Find(SettingsPath);
        Transform selector = panel != null ? panel.Find("LoginWidget/DropDownRobotID") : null;
        Transform login = panel != null ? panel.Find("LoginWidget/BtnLogin") : null;
        robotStatus = panel != null ? panel.Find("LoginWidget/TextStatus")?.GetComponent<Text>() : null;
        if (selector == null || login == null) return;
        robotDropdown = selector.GetComponent<Dropdown>();
        if (robotDropdown == null) robotDropdown = selector.gameObject.AddComponent<Dropdown>();
        robotDropdown.targetGraphic = selector.GetComponent<Image>();
        if (robotDropdown.targetGraphic != null) robotDropdown.targetGraphic.raycastTarget = true;
        robotDropdown.template = selector.Find("Template") as RectTransform;
        robotDropdown.captionText = selector.Find("Label")?.GetComponent<Text>();
        Transform item = selector.Find("Template/Viewport/Content/Item");
        robotDropdown.itemText = item != null ? item.Find("Item Label")?.GetComponent<Text>() : null;
        // The extracted artwork has the dropdown's visual hierarchy but no
        // Unity Toggle. Dropdown.Show refuses to open without this component.
        if (item != null)
        {
            Image itemGraphic = item.GetComponent<Image>();
            if (itemGraphic == null) itemGraphic = item.gameObject.AddComponent<Image>();
            itemGraphic.raycastTarget = true;
            Toggle itemToggle = item.GetComponent<Toggle>();
            if (itemToggle == null) itemToggle = item.gameObject.AddComponent<Toggle>();
            itemToggle.targetGraphic = itemGraphic;
            itemToggle.graphic = item.Find("Item Checkmark")?.GetComponent<Image>();
        }
        ScrollRect scroll = selector.Find("Template")?.GetComponent<ScrollRect>();
        if (scroll != null)
        {
            scroll.viewport = selector.Find("Template/Viewport") as RectTransform;
            scroll.content = selector.Find("Template/Viewport/Content") as RectTransform;
            scroll.horizontal = false;
        }
        robotDropdown.options.Clear();
        foreach (int actualId in availableRobotIds)
        {
            int displayId = actualId >= 20 ? actualId - 20 : actualId;
            robotDropdown.options.Add(new Dropdown.OptionData($"{(actualId >= 20 ? "Blue" : "Red")}{displayId} {RoleName(displayId)}"));
        }
        robotDropdown.value = Mathf.Max(0, availableRobotIds.IndexOf(activeActualId));
        robotDropdown.RefreshShownValue();
        Button button = login.GetComponent<Button>();
        if (button == null) button = login.gameObject.AddComponent<Button>();
        if (button != null)
        {
            Image graphic = login.GetComponent<Image>();
            if (graphic != null) { graphic.raycastTarget = true; button.targetGraphic = graphic; }
            button.onClick.RemoveListener(SwitchSelectedRobot);
            button.onClick.AddListener(SwitchSelectedRobot);
        }
        if (robotStatus != null) robotStatus.text = "已连接：选择车辆后点击 Login 切换";
    }

    private void ConfigureSupportedSettings()
    {
        // The source-client art includes capture cards, serial ports and
        // nickname fields. This simulator has no such hardware endpoints;
        // leaving their default values visible implied working controls.
        string[] unsupported =
        {
            "SetModulePanel/SettingPanel/VideoCaptureWidget",
            "SetModulePanel/SettingPanel/PortWidget",
            "SetModulePanel/SettingPanel/CustomSettingWidget",
            "SetModulePanel/SettingPanel/VideoSetting",
            "SetModulePanel/SettingPanel/SetGunTypeWidget",
            "SetModulePanel/SettingPanel/SightWidght",
            "SetModulePanel/SettingPanel/LoginWidget/InputRobotName"
        };
        foreach (string path in unsupported) layout.SetActive(path, false);

        Slider sensitivity = ConfigureSlider("SetModulePanel/SettingPanel/MouseSensitiveWidget/Slider",
            input != null ? Mathf.InverseLerp(.05f, 2f, input.mouseYawSensitivity) : .36f);
        if (sensitivity != null)
            sensitivity.onValueChanged.AddListener(value =>
            {
                if (input == null) return;
                input.mouseYawSensitivity = Mathf.Lerp(.05f, 2f, value);
                input.mousePitchSensitivity = input.mouseYawSensitivity * (.55f / .75f);
            });
        Slider volume = ConfigureSlider("SetModulePanel/SettingPanel/SoundWidget/SliderEarPhone",
            AudioListener.volume);
        if (volume != null) volume.onValueChanged.AddListener(value => AudioListener.volume = value);
    }

    private Slider ConfigureSlider(string path, float initialValue)
    {
        RectTransform rect = layout.Find(path);
        if (rect == null) return null;
        Image hitArea = rect.GetComponent<Image>();
        if (hitArea == null) hitArea = rect.gameObject.AddComponent<Image>();
        hitArea.color = Color.clear;
        hitArea.raycastTarget = true;
        Slider slider = rect.GetComponent<Slider>();
        if (slider == null) slider = rect.gameObject.AddComponent<Slider>();
        slider.targetGraphic = hitArea;
        slider.fillRect = rect.Find("Fill Area/Fill") as RectTransform;
        slider.handleRect = rect.Find("Handle Slide Area/Handle") as RectTransform;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.SetValueWithoutNotify(Mathf.Clamp01(initialValue));
        return slider;
    }

    private static string RoleName(int id) => id == 1 ? "Hero" : id == 2 ? "Engineer" : id == 7 ? "Sentry" : "Infantry";

    private void SwitchSelectedRobot()
    {
        if (robotDropdown == null || switchRobot == null) return;
        int value = robotDropdown.value;
        if (value < 0 || value >= availableRobotIds.Count) return;
        int actualId = availableRobotIds[value];
        switchRobot(actualId);
        if (robotStatus != null) robotStatus.text = $"切换中：{(actualId >= 20 ? "Blue" : "Red")}{(actualId >= 20 ? actualId - 20 : actualId)}";
        CloseModal();
    }

    void Update()
    {
        if (layout == null) return;
        if (externalOverlay || Time.frameCount == ignoreInputFrame) return;

        if (waitingFor.HasValue)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { RefreshBinding(waitingFor.Value); waitingFor = null; return; }
            if (Input.anyKeyDown && TryReadInputKey(out KeyCode captured))
            {
                input.SetBinding(waitingFor.Value, captured);
                RefreshBinding(waitingFor.Value);
                waitingFor = null;
            }
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CloseModal();
            if (tabPanel) tabPanel.gameObject.SetActive(false);
            RectTransform damage = layout.Find("SundryItem");
            if (damage) damage.gameObject.SetActive(false);
            RectTransform help = layout.Find("HelpItem");
            if (help) help.gameObject.SetActive(false);
            return;
        }

        foreach (KeyValuePair<KeyCode, string> entry in modals)
        {
            if (!Input.GetKeyDown(entry.Key)) continue;
            string path = entry.Value;
            if (entry.Key == KeyCode.H && SceneManager.GetActiveScene().name == "RMUC2024")
            {
                int id = PacificSelectionState.SelectedActualRobotId;
                foreach (RefereeController robot in FindObjectsByType<RefereeController>(FindObjectsSortMode.None))
                    if (robot != null && robot.IsSpawned && robot.RobotID.Value == id &&
                        robot.robotClass.Value == RobotClass.Engineer)
                    {
                        path = "ExchangeItem2024";
                        break;
                    }
            }
            Toggle(path);
            return;
        }

        if (tabPanel)
        {
            // Held down, not toggled — matches the official behaviour.
            bool held = Input.GetKey(KeyCode.Tab) && modal == null;
            SetHeldPanelVisible(tabPanel, held);
        }
        if (hintLabel) hintLabel.enabled = modal != null;
    }

    public void SetExternalOverlay(bool shown)
    {
        externalOverlay = shown;
        ignoreInputFrame = Time.frameCount;
        if (!shown) return;
        waitingFor = null;
        CloseModal();
        if (tabPanel) tabPanel.gameObject.SetActive(false);
        RectTransform sundry = layout.Find("SundryItem");
        if (sundry) sundry.gameObject.SetActive(false);
        RectTransform help = layout.Find("HelpItem");
        if (help) help.gameObject.SetActive(false);
    }

    void Toggle(string path)
    {
        if (modalPath == path) { CloseModal(); return; }
        CloseModal();
        RectTransform panel = layout.Find(path);
        if (!panel)
        {
            Debug.LogWarning("OfficialPanel: 找不到 " + path);
            return;
        }
        modal = panel.gameObject;
        modalPath = path;
        if (path == SettingsPath)
        {
            layout.SetActive("SetModulePanel", true);
            RectTransform settingsRoot = layout.Find("SetModulePanel");
            if (settingsRoot) settingsRoot.SetAsLastSibling();
        }
        // The client centres these dialogs at runtime; their serialized offsets are prefab
        // defaults (SiloItem's would drop the dart panel in the top-left corner).
        if (ShouldCenter(path))
        {
            panel.anchorMin = panel.anchorMax = new Vector2(.5f, .5f);
            panel.pivot = new Vector2(.5f, .5f);
            panel.anchoredPosition = Vector2.zero;
        }
        // Every panel is its own root, so raise it above the resting HUD the way the official
        // client does — otherwise the crosshair draws over an open panel.
        modal.transform.SetAsLastSibling();
        modal.SetActive(true);
        if (rebindStrip && path == SettingsPath)
        {
            rebindStrip.transform.SetAsLastSibling();
            rebindStrip.SetActive(true);
        }
        if (input) input.SetUiBlocked(true, path == SettingsPath || HasMouseControls(panel), panel);
    }

    void CloseModal()
    {
        if (modal) modal.SetActive(false);
        if (modalPath == SettingsPath) layout.SetActive("SetModulePanel", false);
        modal = null;
        modalPath = null;
        if (rebindStrip) rebindStrip.SetActive(false);
        if (input && !externalOverlay) input.SetUiBlocked(false);
    }

    // Tab and ~ are hold-to-show panels.  They share the same canvas as the resting
    // HUD, therefore a visible held panel must be moved to the final sibling slot.
    static void SetHeldPanelVisible(RectTransform panel, bool visible)
    {
        // Once moved the panel stays in the last slot -- every panel here is a node of the
        // layout tree with a fixed index, so nothing gets appended after it. Repeating the
        // reorder on every frame of a held Tab was rebuilding the canvas for nothing.
        if (panel.gameObject.activeSelf == visible) return;
        panel.gameObject.SetActive(visible);
        if (visible) panel.SetAsLastSibling();
    }

    static bool ShouldCenter(string path)
    {
        return path == SettingsPath || path == "SupplyItem2024" ||
               path == "HeroSupplyItem2024" || path == "ExchangeItem2024" || path == "HelpItem" ||
               path == "ValidateItem";
    }

    static bool HasMouseControls(RectTransform panel)
    {
        Selectable[] controls = panel.GetComponentsInChildren<Selectable>(true);
        for (int i = 0; i < controls.Length; i++)
            if (controls[i] && controls[i].interactable) return true;
        return false;
    }

    void ApplyPopupOpacity()
    {
        var paths = new HashSet<string>(modals.Values) { "TabItem", "SundryItem", "ValidateItem" };
        foreach (string path in paths)
        {
            RectTransform panel = layout.Find(path);
            if (!panel) continue;
            CanvasGroup group = panel.GetComponent<CanvasGroup>();
            if (!group) group = panel.gameObject.AddComponent<CanvasGroup>();
            group.alpha = PopupOpacity;
            AddLightWash(panel);
        }
        if (rebindStrip)
        {
            CanvasGroup group = rebindStrip.GetComponent<CanvasGroup>();
            if (!group) group = rebindStrip.AddComponent<CanvasGroup>();
            group.alpha = PopupOpacity;
            AddLightWash((RectTransform)rebindStrip.transform);
        }
    }

    static void AddLightWash(RectTransform panel)
    {
        Transform existing = panel.Find("Popup Light Wash");
        if (existing) return;
        Image wash = new GameObject("Popup Light Wash").AddComponent<Image>();
        wash.transform.SetParent(panel, false);
        wash.rectTransform.anchorMin = Vector2.zero;
        wash.rectTransform.anchorMax = Vector2.one;
        wash.rectTransform.offsetMin = wash.rectTransform.offsetMax = Vector2.zero;
        wash.color = new Color(.78f, .92f, 1f, .28f);
        wash.raycastTarget = false;
        wash.transform.SetAsLastSibling();
    }

    /// <summary>
    /// Asset extraction preserves the serialized child list, but several official
    /// prefabs rely on runtime scripts to push their large background sprites behind
    /// the labels and controls. Recreate those small, deterministic ordering fixes.
    /// </summary>
    void FixImportedPanelLayers()
    {
        // In the serialized prefab this background follows Hardware, so without the
        // official controller's runtime reorder it completely covers the third column.
        SendChildToBack(SettingsPath, "background");
        SendChildToBack("SundryItem", "background");
        SendChildToBack("HelpItem", "background");
        SendChildToBack("ValidateItem", "background");

        FixSupplyLayers("SupplyItem2024");
        FixSupplyLayers("HeroSupplyItem2024");

        // These board sprites are backgrounds inside the active remote-exchange tab.
        SendChildToBack("RemoteExchange/Tab1", "BG2");
        SendChildToBack("RemoteExchange/Tab1", "BG1");
        SendChildToBack("RemoteExchange/Tab2", "BG2");
        SendChildToBack("RemoteExchange/Tab2", "BG1");
    }

    void FixSupplyLayers(string path)
    {
        RectTransform root = layout.Find(path);
        if (!root) return;
        Transform bg = OfficialLayout.FindChild(root, "Bg");
        Transform middle = OfficialLayout.FindChild(root, "Middle");
        if (bg) bg.SetAsFirstSibling();
        if (middle) middle.SetSiblingIndex(bg ? 1 : 0);
        Transform title = OfficialLayout.FindChild(root, "Title");
        if (title) title.gameObject.SetActive(false);

        Transform middleTitle = middle ? OfficialLayout.FindChild(middle, "MiddleTitle") : null;
        Transform available = middle ? OfficialLayout.FindChild(middle, "AvailableBullets") : null;
        if (middleTitle) middleTitle.gameObject.SetActive(false);
        if (available) available.gameObject.SetActive(false);

        // These three labels are ContentSizeFitter-driven in the official executable.
        // Recreate their runtime geometry against the panel centre while retaining the
        // exact extracted panel/button coordinates and artwork.
        string calibre = path.StartsWith("Hero") ? "42mm" : "17mm";
        Text runtimeTitle = OfficialUiKit.Text("RuntimeTitle", root, font, calibre + "弹丸补给面板",
            Vector2.one * .5f, new Vector2(0f, .5f), new Vector2(-319f, 180f),
            new Vector2(420f, 44f), 24, Color.white);
        runtimeTitle.alignment = TextAnchor.MiddleLeft;
        Text runtimeHeading = OfficialUiKit.Text("RuntimeHeading", root, font, "已有/可兑换允许发弹量",
            Vector2.one * .5f, Vector2.one * .5f, new Vector2(0f, 74.5f),
            new Vector2(360f, 32f), 18, Color.white);
        Text runtimeAmount = OfficialUiKit.Text("RuntimeAmount", root, font, "50/100",
            Vector2.one * .5f, Vector2.one * .5f, new Vector2(0f, 38f),
            new Vector2(240f, 42f), 28, Color.white);
        runtimeAmount.fontStyle = FontStyle.Bold;
        runtimeTitle.transform.SetAsLastSibling();
        runtimeHeading.transform.SetAsLastSibling();
        runtimeAmount.transform.SetAsLastSibling();
    }

    void SendChildToBack(string path, string childName)
    {
        RectTransform root = layout.Find(path);
        Transform child = root ? OfficialLayout.FindChild(root, childName) : null;
        if (child) child.SetAsFirstSibling();
    }

    // ------------------------------------------------- key rebinding (our own UI)

    void BuildRebindStrip(Canvas canvas)
    {
        // Keep this extension below the official p21 panel so none of the native
        // login/performance/video controls are obscured.
        RectTransform root = OfficialUiKit.Rect("按键绑定", canvas.transform, Vector2.one * .5f, Vector2.one * .5f,
            new Vector2(0f, -390f), new Vector2(1600f, 190f));
        rebindStrip = root.gameObject;
        Image background = root.gameObject.AddComponent<Image>();
        background.color = new Color(.012f, .02f, .026f, .93f);
        background.raycastTarget = true;

        OfficialUiKit.Text("标题", root, font, "按键绑定（冲突自动互换，可一键恢复默认）",
            new Vector2(.5f, 1f), new Vector2(.5f, 1f), new Vector2(0f, -12f), new Vector2(940f, 30f), 20, Color.white);

        for (int i = 0; i < Actions.Length; i++)
        {
            int column = i / 4, row = i % 4;
            float x = -710f + column * 360f;
            float y = -52f - row * 32f;

            OfficialUiKit.Text(Actions[i] + "Label", root, font, ActionName(Actions[i]),
                new Vector2(.5f, 1f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(150f, 26f), 14, OfficialUiKit.Dim);

            var key = OfficialUiKit.Rect(Actions[i] + "Key", root, new Vector2(.5f, 1f), new Vector2(0f, 1f),
                new Vector2(x + 150f, y), new Vector2(120f, 26f));
            Image keyBackground = key.gameObject.AddComponent<Image>();
            keyBackground.color = new Color(.08f, .30f, .36f, .98f);
            Text label = OfficialUiKit.Text("Text", key, font, "", Vector2.one * .5f, Vector2.one * .5f,
                Vector2.zero, new Vector2(120f, 26f), 14, Color.white);

            Button button = key.gameObject.AddComponent<Button>();
            button.targetGraphic = keyBackground;
            BindableAction action = Actions[i];
            button.onClick.AddListener(() => BeginRebind(action));
            bindingLabels[action] = label;
        }

        OfficialUiKit.Button("恢复默认", root, font, "恢复默认并重置模拟",
            new Vector2(685f, 30f), new Vector2(190f, 34f), ResetBindings);
        OfficialUiKit.Button("关闭", root, font, "关闭",
            new Vector2(685f, -18f), new Vector2(190f, 34f), CloseModal);

        rebindStrip.SetActive(false);
        RefreshBindings();
    }

    static string ActionName(BindableAction action)
    {
        switch (action)
        {
            case BindableAction.Forward: return "前进";
            case BindableAction.Backward: return "后退";
            case BindableAction.StrafeLeft: return "左平移";
            case BindableAction.StrafeRight: return "右平移";
            case BindableAction.SpinLeft: return "小陀螺逆时针";
            case BindableAction.SpinRight: return "小陀螺顺时针";
            case BindableAction.BarrelUp: return "抬升炮管";
            case BindableAction.BarrelDown: return "下压炮管";
            case BindableAction.Boost: return "加速";
            case BindableAction.Brake: return "刹车";
            case BindableAction.Fire: return "开火";
            case BindableAction.AutoLob: return "自动吊射";
            case BindableAction.CameraOperator: return "操作手视角";
            case BindableAction.CameraOverview: return "全局视角";
            case BindableAction.CameraBallistics: return "弹道视角";
            default: return action.ToString();
        }
    }

    void BeginRebind(BindableAction action)
    {
        waitingFor = action;
        if (bindingLabels.TryGetValue(action, out Text label)) label.text = "请按新键";
        if (input) input.SetCursorCaptured(false);
    }

    void ResetBindings()
    {
        input.ResetBindings();
        RefreshBindings();
        // The button reads "恢复默认并重置模拟", so it resets the simulation too. This is
        // the only route to the backend's reset command: the menu that used to send it is
        // gone, which had left the command unreachable.
        if (client) client.ResetSimulation();
    }

    void RefreshBindings()
    {
        foreach (BindableAction action in Actions) RefreshBinding(action);
    }

    void RefreshBinding(BindableAction action)
    {
        if (bindingLabels.TryGetValue(action, out Text label)) label.text = BindingName(input.GetBinding(action));
    }

    static string BindingName(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.Mouse0: return "鼠标左键";
            case KeyCode.Mouse1: return "鼠标右键";
            case KeyCode.Mouse2: return "鼠标中键";
            case KeyCode.LeftShift: return "左Shift";
            case KeyCode.Space: return "空格";
            case KeyCode.UpArrow: return "↑";
            case KeyCode.DownArrow: return "↓";
            case KeyCode.LeftArrow: return "←";
            case KeyCode.RightArrow: return "→";
            default: return key.ToString().ToUpperInvariant();
        }
    }

    static bool TryReadInputKey(out KeyCode result)
    {
        foreach (KeyCode key in System.Enum.GetValues(typeof(KeyCode)))
        {
            if (key == KeyCode.None || (int)key >= (int)KeyCode.JoystickButton0) continue;
            if (Input.GetKeyDown(key)) { result = key; return true; }
        }
        result = KeyCode.None;
        return false;
    }
}
