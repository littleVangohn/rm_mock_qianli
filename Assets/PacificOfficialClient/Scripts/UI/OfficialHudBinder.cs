using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives the reversed official HUD with live MuJoCo data.
///
/// Everything visible is built by <see cref="OfficialLayout"/> from the coordinates taken out
/// of the official client. The only values written by hand here are the ones the official
/// client itself sets in C# at runtime and therefore never serialised into the assets:
/// the card grids' size, the minimap's resting corner, and the gimbal-pose widget (our own
/// feature, which the official client has no equivalent of).
/// </summary>
public class OfficialHudBinder : MonoBehaviour
{
    // Official card metrics, read from TeammateItem/redTeamates GridLayoutGroup.
    const int CardsPerSide = 6;
    static readonly Vector2 CardCell = new Vector2(120f, 100f);
    const float CardRowOffsetX = 920f;   // grid anchoredPosition in the official prefab
    const float CardRowMargin = 40f;     // keeps the row off the screen edge
    // TeammateItem's serialized y (446) is a prefab default the client overrides, the same way
    // it overrides MapItem's off-screen (700,-340): taken literally it drives the cards 40px up
    // into the base bar, which the shipped UI does not do. The row's real top is the bottom
    // edge of the official base item, so derive it from that node instead of trusting 446.
    const string RowAnchorNode = "BlueBaseItem";

    public MujocoClient client;
    public CameraModeController cameras;
    public Camera operatorCamera;
    public System.Func<Vector3, Vector3> worldToScene;
    // Scene units in one metre, after worldToScene has run. The OSS simulator
    // scenes are decimetres (10); the MuJoCo-imported field uses its calibrated
    // visual scale. The funnel's metre-based mouth sizing needs this figure.
    public float unitsPerMetre = 10f;

    OfficialLayout layout;
    Font font;
    MujocoClient subscribed;

    // Bound nodes. Names are the official ones, read straight off the reversed layout.
    RectTransform minimap;
    Text matchTime, roundLabel, blueScore, redScore;
    Text economyBlue, economyRed, heroHp, baseHpBlue, baseHpRed, ammoCount, bulletAllow, robotId;
    Text fireFeedback;
    float fireFeedbackUntil;
    Text outpostHpBlue, outpostHpRed;
    Image heroHpFill, outpostFillBlue, outpostFillRed;
    Image localRobotAvatar;
    RectTransform baseFillBlue, baseFillRed;
    float baseFillWidthBlue, baseFillWidthRed;
    readonly List<CardRefs> blueCards = new List<CardRefs>();
    readonly List<CardRefs> redCards = new List<CardRefs>();
    readonly Dictionary<int, Text> liveTabRows = new Dictionary<int, Text>();
    readonly List<(RectTransform fill, float fullWidth)> baseFills = new List<(RectTransform, float)>();

    // Gimbal pose widget (our own feature, kept from the original client).
    Text poseMode;
    GameObject centerOverlay;
    int lastMatchSeconds = -1;

    class CardRefs
    {
        public int robotId;
        public Image avatar, idBadge, nameBadge, healthFill;
        public RectTransform healthMask;
        public Text bulletValue, cooling;
        public GameObject root;
    }

    public void Initialize(Canvas canvas, Font uiFont, MujocoClient source, CameraModeController cameraModes, Camera opCamera)
    {
        font = uiFont;
        client = source;
        cameras = cameraModes;
        operatorCamera = opCamera;

        layout = OfficialLayout.Build(canvas.transform, font);
        foreach (string warning in layout.Warnings) Debug.LogWarning("OfficialHud: " + warning);

        BuildCardRows();
        BuildTabRows();
        BuildResultRows();
        PlaceMinimap();
        FixBaseBars();
        FixStatusBar();
        HideEventOverlays();
        BindOutposts();
        PlaceBattleMessages();
        BuildLegacyCenterOverlay();
        BuildPoseWidget();
        BindCoreNodes();
        HideUnsupportedRoots();

        Bind(client);
        Debug.Log($"OfficialHud: paths={layout.ByPath.Count} sprites={layout.Sprites.Count} " +
                  $"cards={blueCards.Count + redCards.Count}");
    }

    void OnDestroy() => Bind(null);

    void Update()
    {
        if (fireFeedback && fireFeedback.gameObject.activeSelf && Time.unscaledTime >= fireFeedbackUntil)
            fireFeedback.gameObject.SetActive(false);
        if (centerOverlay && cameras != null)
        {
            bool wanted = cameras.ActiveMode == 1;
            if (centerOverlay.activeSelf != wanted) centerOverlay.SetActive(wanted);
        }
        // The selected-robot snapshot can be rejected while the backend is
        // switching from its default Hero (ID 1) to the picked role. Keep the
        // last good display, then explicitly repaint once matching live data
        // arrives instead of freezing UI at zero or a placeholder.
        if (client != null && client.State != null && client.State != lastAppliedState)
            Apply(client.State);
    }

    MujocoState lastAppliedState;

    public void Bind(MujocoClient value)
    {
        if (subscribed)
        {
            subscribed.StateUpdated -= Apply;
            subscribed.CommandCompleted -= ApplyCommandFeedback;
        }
        subscribed = value;
        client = value;
        if (subscribed)
        {
            subscribed.StateUpdated += Apply;
            subscribed.CommandCompleted += ApplyCommandFeedback;
        }
    }

    void ApplyCommandFeedback(CommandFeedback feedback)
    {
        if (feedback == null || feedback.name != "fire" || !fireFeedback) return;
        fireFeedback.gameObject.SetActive(true);
        fireFeedback.color = feedback.ok ? new Color(.35f, 1f, .45f, 1f) : new Color(1f, .35f, .2f, 1f);
        fireFeedback.text = feedback.ok ? "左键开火：已发射" : FireFailureText(feedback.error);
        fireFeedbackUntil = Time.unscaledTime + 3f;
    }

    static string FireFailureText(string error)
    {
        switch (error)
        {
            case "FIRE_ACK_PENDING": return "左键开火：上一发正在确认";
            case "OVERHEAT": return "左键开火：发射机构过热，等待冷却";
            case "NO_AMMO": return "左键开火：当前弹种弹药为 0";
            case "NO_REAL_AMMO": return "左键开火：实体弹仓弹药为 0";
            case "NO_MATCHING_SHOOTER": return "左键开火：该车没有当前弹种的发射机构";
            case "SHOOTER_DISABLED": return "左键开火：发射机构已禁用";
            case "ROBOT_DISABLED": return "左键开火：机器人尚未启用";
            case "ROBOT_DESTROYED": return "左键开火：机器人已被击毁";
            case "UNSUPPORTED_AMMO": return "左键开火：当前弹种不受支持";
            case "REFEREE_NOT_FOUND": return "左键开火：未找到所选车辆的裁判状态";
            case "MUJOCO_OFFLINE": return "左键开火：MuJoCo 未连接";
            case "ROBOT_NOT_SELECTED": return "左键开火：尚未选择车辆";
            case "REFEREE_BLOCKED": return "左键开火：裁判系统阻止发射";
            default: return "左键开火失败：" + (string.IsNullOrEmpty(error) ? "未知原因" : error);
        }
    }

    // ------------------------------------------------------------- robot cards

    void BuildCardRows()
    {
        // The official prefab leaves these grids 0x0 and sizes them in C#; we size them from
        // the serialised cell metrics instead. Row order matches the manual: red descending
        // (7 6 4 3 2 1), blue ascending (1 2 3 4 6 7).
        BuildCardRow("TeammateItem/redTeamates", "RedTeammate", false, redCards,
            new[] { 7, 6, 4, 3, 2, 1 });
        BuildCardRow("TeammateItem/blueTeamates", "BlueTeammate", true, blueCards,
            new[] { 1, 2, 3, 4, 6, 7 });
    }

    void BuildCardRow(string gridPath, string templateName, bool blue, List<CardRefs> into, int[] robotIds)
    {
        RectTransform grid = layout.Find(gridPath);
        RectTransform template = layout.Find(templateName);
        if (!grid || !template)
        {
            Debug.LogWarning($"OfficialHud: 缺少 {gridPath} 或 {templateName}，跳过该行");
            return;
        }

        // The row hangs off the bottom edge of the official base bar; its two grids sit at ±920.
        var rowRoot = grid.parent as RectTransform;
        if (rowRoot)
        {
            float rowTop = 0f;
            RectTransform bar = layout.Find(RowAnchorNode);
            if (bar) rowTop = bar.anchoredPosition.y - bar.pivot.y * bar.rect.height;

            rowRoot.anchorMin = rowRoot.anchorMax = new Vector2(.5f, .5f);
            rowRoot.pivot = new Vector2(.5f, .5f);
            rowRoot.anchoredPosition = new Vector2(0f, rowTop - CardCell.y * .5f);
            rowRoot.sizeDelta = new Vector2(2f * (CardRowOffsetX + CardRowMargin), CardCell.y);
        }

        grid.anchorMin = grid.anchorMax = new Vector2(.5f, .5f);
        grid.pivot = new Vector2(blue ? 1f : 0f, .5f);
        grid.anchoredPosition = new Vector2(blue ? CardRowOffsetX : -CardRowOffsetX, 0f);
        grid.sizeDelta = new Vector2(CardCell.x * CardsPerSide, CardCell.y);

        for (int i = 0; i < robotIds.Length; i++)
        {
            RectTransform card = Instantiate(template, grid, false);
            card.name = templateName + "_" + robotIds[i];
            card.gameObject.SetActive(true);
            into.Add(BindCard(card, robotIds[i], blue));
        }
        template.gameObject.SetActive(false);
    }

    CardRefs BindCard(RectTransform card, int robotId, bool blue)
    {
        string side = blue ? "blue" : "red";
        var refs = new CardRefs { robotId = robotId, root = card.gameObject };

        // The prefab serialises the card frame (bg) after the portrait, which would hide it.
        // Manual page 03 shows the portrait on top of the frame, so push the frame to the back.
        Transform frame = OfficialLayout.FindChild(card, "bg");
        if (frame) frame.SetAsFirstSibling();

        refs.avatar = OfficialLayout.FindImage(card, "avatar");
        refs.idBadge = OfficialLayout.FindImage(card, "id");
        refs.nameBadge = OfficialLayout.FindImage(card, "robotName");
        refs.bulletValue = OfficialLayout.FindText(card, "value");
        refs.cooling = OfficialLayout.FindText(card, "coolingTime");

        RectTransform mask = OfficialLayout.FindChild(card, "bloodmask");
        if (mask)
        {
            refs.healthMask = mask;
            refs.healthFill = OfficialLayout.FindImage(mask, "blood");
        }

        string role = RobotRole(robotId);
        SetSprite(refs.avatar, $"{side}_teammate_avatar_{role}");
        // Only the id badge for robot 1 and the hero name plate were exported by the client,
        // so fall back to text for the rest rather than leaving the card blank.
        SetSpriteOrText(refs.idBadge, $"{side}_teammate_id_{robotId}", robotId.ToString(), 22, Color.white);
        string nameRole = robotId == 7 ? "sentry" : role;
        SetSpriteOrText(refs.nameBadge, $"{side}_teammate_avatar_{nameRole}_name", RoleName(robotId), 20, Color.white);
        return refs;
    }

    // 1 英雄, 2 工程, 3/4/5 步兵, 6 无人机, 7 哨兵（手册标注为炮兵）
    static string RobotRole(int id)
    {
        switch (id)
        {
            case 1: return "hero";
            case 2: return "engineer";
            case 6: return "airplane";
            default: return "soldier";
        }
    }

    static string ValidateRole(int id)
    {
        switch (id)
        {
            case 1: return "hero";
            case 2: return "sapper";
            case 6: return "airplane";
            case 7: return "guard";
            default: return "infantry";
        }
    }

    static string RoleName(int id)
    {
        switch (id)
        {
            case 1: return "英雄";
            case 2: return "工程";
            case 6: return "无人机";
            case 7: return "炮兵";
            default: return "步兵";
        }
    }

    void SetSprite(Image image, string spriteName)
    {
        if (!image) return;
        Sprite sprite = layout.GetSprite(spriteName);
        if (sprite) image.sprite = sprite;
        else image.gameObject.SetActive(false);
    }

    /// <summary>Use the official plate if the client shipped one, otherwise label it in text.</summary>
    void SetSpriteOrText(Image image, string spriteName, string fallback, int fontSize, Color color)
    {
        if (!image) return;
        Sprite sprite = layout.GetSprite(spriteName);
        if (sprite)
        {
            image.sprite = sprite;
            return;
        }
        // Keep the official rect, blank the plate and label it in place.
        image.color = new Color(1f, 1f, 1f, 0f);
        OfficialUiKit.Text(image.name + "Text", image.transform, font, fallback,
            Vector2.one * .5f, Vector2.one * .5f, Vector2.zero,
            new Vector2(64f, 28f), fontSize, color);
    }

    // ---------------------------------------------------------- tab panel rows

    /// <summary>
    /// The Tab panel's per-robot table is empty in the prefab too: the client instantiates
    /// RedTabRobot/BlueTabRobot rows into TabItem/(Red|Blue)Area/Robots. Manual page 19 shows
    /// four rows a side, ordered 英雄 / 工程 / 步兵 / 步兵.
    /// </summary>
    void BuildTabRows()
    {
        // The imported rows are only serialized artwork (including 0 / 0 HP).
        // Replace them with a compact seven-vehicle roster driven by referee
        // snapshots; keep the source panel frame and team headings.
        RectTransform tab = layout.Find("TabItem");
        if (tab)
        {
            RectTransform backdrop = OfficialLayout.FindChild(tab, "background");
            if (backdrop) backdrop.SetAsFirstSibling();
        }
        BuildLiveTabSide("TabItem/RedArea", false);
        BuildLiveTabSide("TabItem/BlueArea", true);
        RectTransform redTemplate = layout.Find("RedTabRobot");
        RectTransform blueTemplate = layout.Find("BlueTabRobot");
        if (redTemplate) redTemplate.gameObject.SetActive(false);
        if (blueTemplate) blueTemplate.gameObject.SetActive(false);
    }

    void BuildLiveTabSide(string path, bool blue)
    {
        RectTransform side = layout.Find(path);
        if (!side) return;
        Transform oldGrid = OfficialLayout.FindChild(side, "Robots");
        if (oldGrid) oldGrid.gameObject.SetActive(false);
        for (int id = 1; id <= 7; id++)
        {
            Text row = OfficialUiKit.Text($"LiveRobot_{(blue ? 20 : 0) + id}", side, font, "",
                Vector2.one * .5f, Vector2.one * .5f,
                new Vector2(230f, -205f - (id - 1) * 51f), new Vector2(510f, 44f),
                20, blue ? new Color(.72f, .89f, 1f) : new Color(1f, .77f, .74f));
            row.alignment = TextAnchor.MiddleLeft;
            row.raycastTarget = false;
            row.gameObject.SetActive(false);
            liveTabRows[(blue ? 20 : 0) + id] = row;
        }
    }

    /// <summary>Populate the end-of-match portrait columns the official client builds at runtime.</summary>
    void BuildResultRows()
    {
        RectTransform result = layout.Find("ValidateItem");
        if (result)
        {
            foreach (string areaName in new[] { "RedArea", "BlueArea" })
            {
                Transform area = OfficialLayout.FindChild(result, areaName);
                Transform winner = area ? OfficialLayout.FindChild(area, "win") : null;
                if (winner) winner.SetAsFirstSibling();
            }

            // QA represents a blue-side win.  Only the winning side receives the large
            // winner plate; the extracted prefab has both variants enabled by default.
            Transform red = OfficialLayout.FindChild(result, "RedArea");
            Transform blue = OfficialLayout.FindChild(result, "BlueArea");
            Transform redWinner = red ? OfficialLayout.FindChild(red, "win") : null;
            Transform blueWinner = blue ? OfficialLayout.FindChild(blue, "win") : null;
            if (redWinner) redWinner.gameObject.SetActive(false);
            if (blueWinner) blueWinner.gameObject.SetActive(true);
            NormalizeWinner(redWinner as RectTransform, false);
            NormalizeWinner(blueWinner as RectTransform, true);
            SetResultTeam(red, "红队", "Red Team");
            SetResultTeam(blue, "蓝队", "Blue Team");
        }
        BuildResultRow("ValidateItem/RedArea/Robots", "RedValidateRobot", false);
        BuildResultRow("ValidateItem/BlueArea/Robots", "BlueValidateRobot", true);
    }

    void BuildResultRow(string gridPath, string templateName, bool blue)
    {
        RectTransform grid = layout.Find(gridPath);
        RectTransform template = layout.Find(templateName);
        if (!grid || !template) return;
        int[] ids = { 1, 3, 4, 6, 7 };
        var metrics = grid.GetComponent<GridLayoutGroup>();
        if (metrics)
        {
            metrics.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            metrics.constraintCount = 1;
        }
        // Same runtime-anchor convention as the Tab table. The official C# creates the
        // entries while the container itself remains 0x0.
        grid.sizeDelta = Vector2.zero;
        string side = blue ? "blue" : "red";
        foreach (int id in ids)
        {
            RectTransform item = Instantiate(template, grid, false);
            item.name = templateName + "_" + id;
            item.gameObject.SetActive(true);
            SetSprite(OfficialLayout.FindImage(item, "avatar"), $"{side}_validate_{ValidateRole(id)}");
            SetSprite(OfficialLayout.FindImage(item, "id"), $"{side}_validate_id_{id}");
            SetSprite(OfficialLayout.FindImage(item, "level"), $"{side}_validate_level_1");
            Text hurt = OfficialLayout.FindText(item, "hurtvalue");
            if (hurt) hurt.text = "0";

            Transform avatar = OfficialLayout.FindChild(item, "avatar");
            if (avatar) avatar.SetAsFirstSibling();
            foreach (string name in new[] { "slid", "level", "id", "hurtvalue" })
            {
                Transform child = OfficialLayout.FindChild(item, name);
                if (child) child.SetAsLastSibling();
            }
        }
        template.gameObject.SetActive(false);
    }

    static void SetResultTeam(Transform area, string chinese, string english)
    {
        if (!area) return;
        Text cn = OfficialLayout.FindText(area, "schoolName");
        Text en = OfficialLayout.FindText(area, "schoolName_EN");
        if (cn) cn.text = chinese;
        if (en) en.text = english;
    }

    static void NormalizeWinner(RectTransform winner, bool blue)
    {
        if (!winner) return;
        // The atlas export trims transparent padding from this sprite.  Its authored
        // 1480x880 rect therefore stretches the trimmed word across the entire board;
        // the official runtime presents it at its native 598x138 visual size.
        winner.anchorMin = winner.anchorMax = Vector2.one * .5f;
        winner.pivot = Vector2.one * .5f;
        winner.anchoredPosition = new Vector2(blue ? 360f : -360f, 300f);
        winner.sizeDelta = new Vector2(500f, 115f);
        Image image = winner.GetComponent<Image>();
        if (image) image.preserveAspect = true;
    }

    // ---------------------------------------------------------------- minimap

    void PlaceMinimap()
    {
        minimap = layout.Find("MapItem");
        if (!minimap)
        {
            Debug.LogWarning("OfficialHud: 找不到 MapItem");
            return;
        }
        minimap.gameObject.SetActive(true);

        // The imported official prefab serializes this root at 0x0 and fills it from code.
        // Give it the actual map bounds so normalized live markers have a usable coordinate space.
        minimap.anchorMin = minimap.anchorMax = new Vector2(1f, 0f);
        minimap.pivot = new Vector2(1f, 0f);
        // 1.2x map tucked into the lower-right safe edge.
        minimap.anchoredPosition = new Vector2(-12f, 12f);
        // Keep the tactical map readable on 16:9 and ultrawide layouts while
        // leaving a safe margin for the bottom status bar.
        minimap.sizeDelta = new Vector2(620f, 348.75f);

        // Use the original real-video demo's current-season map and live marker renderer.
        RectTransform background = OfficialLayout.FindChild(minimap, "background");
        Texture2D seasonMap = SimulatorMapCapture.AuthoredMap();
        Image backgroundImage = background ? background.GetComponent<Image>() : null;
        if (backgroundImage && seasonMap)
        {
            // The imported child keeps its original fixed prefab dimensions unless it is
            // explicitly stretched; resizing MapItem alone otherwise only enlarges the wash.
            background.anchorMin = Vector2.zero;
            background.anchorMax = Vector2.one;
            background.pivot = Vector2.one * .5f;
            background.offsetMin = Vector2.zero;
            background.offsetMax = Vector2.zero;
            background.localScale = Vector3.one;
            backgroundImage.sprite = Sprite.Create(seasonMap,
                new Rect(0f, 0f, seasonMap.width, seasonMap.height), new Vector2(.5f, .5f));
            backgroundImage.preserveAspect = false;
            // The map artwork is fully opaque. Let the scene show through so the
            // minimap reads as an overlay instead of a solid blue rectangle.
            backgroundImage.color = new Color(1f, 1f, 1f, 0.62f);
            background.SetAsFirstSibling();
        }
        else
        {
            Debug.LogWarning("OfficialHud: 找不到本赛季小地图 Resources/mujoco-2026-map");
        }

        // Official base/outpost overlays use a different coordinate model. The demo map
        // renderer owns the field image and overlays the current MuJoCo robots/markers.
        RectTransform redArea = OfficialLayout.FindChild(minimap, "RedArea");
        RectTransform blueArea = OfficialLayout.FindChild(minimap, "BlueArea");
        RectTransform publicArea = OfficialLayout.FindChild(minimap, "PublicArea");
        if (redArea) redArea.gameObject.SetActive(false);
        if (blueArea) blueArea.gameObject.SetActive(false);
        if (publicArea) publicArea.gameObject.SetActive(false);

        RuntimeMiniMap liveMap = minimap.gameObject.AddComponent<RuntimeMiniMap>();
        liveMap.mapRect = minimap;
        liveMap.sharedMarkerSprite = layout.GetSprite("map_robot_lockline_yellow");
        liveMap.markerSize = 32f;
        liveMap.Bind(client);
    }

    // ------------------------------------------------------- gimbal pose (ours)

    void BuildPoseWidget()
    {
        // Kept as the original custom client drew it — a chassis outline plus a turret
        // needle, driven by RobotPoseWidgetController. Anchored top-right, below the
        // robot-card row and above the minimap, so it overlaps nothing official.
        // No backing panel and no caption: the shape reads on its own, and the only text
        // left is the live control mode underneath it.
        RectTransform panel = OfficialUiKit.Rect("云台姿态", layout.Root, Vector2.one, Vector2.one,
            new Vector2(-30f, -250f), new Vector2(250f, 190f));

        RectTransform chassis = OfficialUiKit.Rect("底盘朝向", panel, Vector2.one * .5f, Vector2.one * .5f,
            new Vector2(0f, 5f), new Vector2(130f, 150f));
        Vector2[] body =
        {
            new Vector2(-50, -60), new Vector2(-50, 25), new Vector2(0, 60),
            new Vector2(50, 25), new Vector2(50, -60),
        };
        for (int i = 0; i < body.Length; i++)
            OfficialUiKit.Line(chassis, body[i], body[(i + 1) % body.Length], 4, new Color(1f, .20f, .18f, .96f));

        RectTransform turret = OfficialUiKit.Rect("云台朝向", panel, Vector2.one * .5f, Vector2.one * .5f,
            new Vector2(0f, 5f), new Vector2(130f, 150f));
        OfficialUiKit.Image("云台环", turret, Vector2.one * .5f, Vector2.one * .5f, Vector2.zero,
            new Vector2(52f, 52f), "crosshair_slid_circle_white", new Color(.20f, .42f, 1f, .98f));
        OfficialUiKit.Line(turret, new Vector2(0f, 8f), new Vector2(0f, 76f), 6f, new Color(.20f, .42f, 1f, .98f));

        // The widget's only text: which of the two control modes is live, so the G toggle
        // is visible without having to test whether C/V do anything.
        poseMode = OfficialUiKit.Text("操作模式", panel, font, "跟随模式", new Vector2(.5f, 0f), new Vector2(.5f, 0f),
            new Vector2(0f, 8f), new Vector2(220f, 25f), 15, new Color(.55f, .85f, 1f));

        var pose = gameObject.AddComponent<RobotPoseWidgetController>();
        pose.chassis = chassis;
        pose.turret = turret;
        pose.modeText = poseMode;
        pose.Bind(client);
    }

    /// <summary>
    /// Hides the official overlays that the client only shows on a match event. Showing them all
    /// at once is what piled icons on top of each other in the hero status bar and doubled the
    /// ring around the crosshair (both single- and double-barrel heat art were up together).
    /// </summary>
    void HideEventOverlays()
    {
        // The centre of the screen deliberately stays on the proven custom-video HUD.
        // The official crosshair prefab contains mutually-exclusive heat modes and large
        // 976x542 backplates; importing the prefab's serialized defaults made all of them
        // appear together. Disable the whole prefab instead of trying to chase every clone.
        SetRootActive("CrosshairItem", false);

        // Victory-point presentations are alternative match states, not the normal base-HP
        // bars. Keeping both roots alive is what produced the yellow bars and "胜利点" boxes
        // over the normal red/blue header.
        SetRootActive("BlueWinPointItem", false);
        SetRootActive("RedWinPointItem", false);
        SetRootActive("YellowTopImage", false);
        SetRootActive("GainPoint", false);
        // Runtime tactical cursor prefab.  Its green arrow and "(0,0)" text sit at
        // screen centre until the official client positions it; it is not part of
        // the demo shooting reticle and must not be shown in the operator view.
        SetRootActive("LocationItem", false);
        SetRootActive("MiniMapMarker", false);

        HidePathSuffix("StatusbarItem/robotInfo/tip");
        HidePathSuffix("StatusbarItem/robotInfo/warning");
        HidePathSuffix("StatusbarItem/robotInfo/locklevel");
        HidePathSuffix("StatusbarItem/blood/addbuff");
        HidePathSuffix("StatusbarItem/BulletInfo");
        HidePathSuffix("BaseItem/bloodSlipItem/shieldBloodMask");
    }

    /// <summary>
    /// The base bar ships its fill layer 880px wide over a 440px bar, left-pivoted, so taken
    /// literally it runs a bar-length past the end and over the team badge. Clamp the fill to
    /// the track's own width — that is also the value the runtime would drive from base HP.
    /// </summary>
    void FixBaseBars()
    {
        foreach (string path in new[] { "BlueBaseItem", "RedBaseItem" })
        {
            RectTransform root = layout.Find(path);
            if (!root) continue;
            RectTransform slip = OfficialLayout.FindChild(root, "bloodSlipItem");
            if (!slip) continue;

            RectTransform track = OfficialLayout.FindChild(slip, "bottom");
            RectTransform fill = OfficialLayout.FindChild(slip, "top");
            if (fill && track)
            {
                baseFills.Add((fill, track.rect.width));
                fill.sizeDelta = new Vector2(track.rect.width, fill.sizeDelta.y);

                // Unity draws later siblings on top. The reversed red prefab serialized its
                // fill before the opaque trough, so the trough hid/washed out the fill. Keep
                // the stable order explicitly: trough, HP fill, decorative border.
                track.SetAsFirstSibling();
                fill.SetSiblingIndex(1);
            }
            // Glow, invincibility and armour overlays only appear on a match event.
            foreach (string name in new[] { "light", "invincible", "middle", "armor" })
            {
                RectTransform overlay = OfficialLayout.FindChild(slip, name);
                if (overlay) overlay.gameObject.SetActive(false);
            }

            RectTransform border = OfficialLayout.FindChild(slip, "border");
            if (border) border.SetAsLastSibling();

            // Name/logo and numeric HP are HUD annotations and must be above the bar art.
            RectTransform value = OfficialLayout.FindChild(root, "bloodValueItem");
            RectTransform college = OfficialLayout.FindChild(root, "collegeItem");
            if (value) value.SetAsLastSibling();
            if (college) college.SetAsLastSibling();

            // The extracted value container has no serialized rect and its TMP-era
            // shadow material does not survive the uGUI reconstruction reliably.
            // Draw the runtime value at the exact official coordinates instead.
            bool blue = path == "BlueBaseItem";
            Text hp = OfficialUiKit.Text("RuntimeBaseHp", root, font, "5000",
                Vector2.one * .5f, blue ? new Vector2(1f, 1f) : new Vector2(0f, 1f),
                new Vector2(blue ? 180f : -180f, -6f), new Vector2(134f, 64f),
                32, Color.white);
            hp.alignment = blue ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
            hp.fontStyle = FontStyle.Bold;
            hp.raycastTarget = false;
            if (blue) baseHpBlue = hp; else baseHpRed = hp;
        }
    }

    /// <summary>Restore the official bottom-left HUD draw order and readable HP value.</summary>
    void FixStatusBar()
    {
        RectTransform root = layout.Find("StatusbarItem");
        if (!root) return;

        fireFeedback = OfficialUiKit.Text("RuntimeFireFeedback", root.parent, font, "",
            Vector2.zero, Vector2.zero, new Vector2(24f, 158f), new Vector2(620f, 38f),
            18, Color.white);
        fireFeedback.alignment = TextAnchor.MiddleLeft;
        fireFeedback.fontStyle = FontStyle.Bold;
        fireFeedback.raycastTarget = false;
        fireFeedback.gameObject.SetActive(false);

        RectTransform blood = OfficialLayout.FindChild(root, "blood");
        if (blood)
        {
            Transform bottom = OfficialLayout.FindChild(blood, "bottom");
            Transform top = OfficialLayout.FindChild(blood, "top");
            if (bottom) bottom.SetAsFirstSibling();
            if (top) top.SetSiblingIndex(1);
            foreach (string name in new[] { "value1", "value2", "value3" })
            {
                Transform value = OfficialLayout.FindChild(blood, name);
                if (value) value.gameObject.SetActive(false);
            }

            Text hp = OfficialUiKit.Text("RuntimeHeroHp", blood, font, "200/200",
                Vector2.one * .5f, new Vector2(1f, .5f), new Vector2(-70f, 0f),
                new Vector2(210f, 48f), 24, Color.white);
            hp.alignment = TextAnchor.MiddleRight;
            hp.fontStyle = FontStyle.Bold;
            hp.raycastTarget = false;
            heroHp = hp;
        }

        RectTransform energy = OfficialLayout.FindChild(root, "energy");
        if (energy)
        {
            Transform bottom = OfficialLayout.FindChild(energy, "bottom");
            Transform top = OfficialLayout.FindChild(energy, "top");
            if (bottom) bottom.SetAsFirstSibling();
            if (top) top.SetSiblingIndex(1);
            foreach (string name in new[] { "powerOutageCountdown", "powerOutageCountdownValue", "time", "Charging" })
            {
                Transform state = OfficialLayout.FindChild(energy, name);
                if (state) state.gameObject.SetActive(false);
            }
        }

        RectTransform robot = OfficialLayout.FindChild(root, "robotInfo");
        if (robot)
        {
            Transform avatar = OfficialLayout.FindChild(robot, "avatar");
            if (avatar)
            {
                avatar.gameObject.SetActive(true);
                avatar.SetAsFirstSibling();
                localRobotAvatar = avatar.GetComponent<Image>();
            }
            foreach (string name in new[] { "avatarborder", "idborder", "level_icon", "expbg", "exp", "id" })
            {
                Transform item = OfficialLayout.FindChild(robot, name);
                if (item) item.SetAsLastSibling();
            }
        }
    }

    void BindOutposts()
    {
        RectTransform red = layout.Find("RedOutpostItem");
        RectTransform blue = layout.Find("BlueOutpostItem");
        outpostHpRed = red ? OfficialLayout.FindText(red, "hpvalue") : null;
        outpostHpBlue = blue ? OfficialLayout.FindText(blue, "hpvalue") : null;
        outpostFillRed = red ? OfficialLayout.FindImage(red, "bloodslid") : null;
        outpostFillBlue = blue ? OfficialLayout.FindImage(blue, "bloodslid") : null;
        foreach (RectTransform root in new[] { red, blue })
        {
            if (!root) continue;
            Transform buff = OfficialLayout.FindChild(root, "buff");
            Transform rebuild = OfficialLayout.FindChild(root, "rebuild");
            if (buff) buff.gameObject.SetActive(false);
            if (rebuild) rebuild.gameObject.SetActive(false);
            root.gameObject.SetActive(false);
        }
    }

    void HideUnsupportedRoots()
    {
        // These serialized prefabs have no matching live rule/event feed. Their
        // default icons and numbers are artwork, not simulator state.
        foreach (string path in new[]
        {
            "TeammateBuffItem_fly", "TeammateBuffItem_Cool", "BuffItem_HP", "BuffItem_Cool",
            "MessageCenter", "NetworkTip", "ControlLink", "SignallampItem", "SundryItem",
            "SiloItem", "Deployment", "HelpItem", "DartState", "GainPoint", "ModuleItem"
        }) SetRootActive(path, false);
        if (roundLabel) roundLabel.gameObject.SetActive(false);
        if (redScore) redScore.gameObject.SetActive(false);
        if (blueScore) blueScore.gameObject.SetActive(false);
        RectTransform status = layout.Find("StatusbarItem");
        if (status)
        {
            Transform energy = OfficialLayout.FindChild(status, "energy");
            Transform info = OfficialLayout.FindChild(status, "robotInfo");
            if (energy) energy.gameObject.SetActive(false);
            if (info)
                // The portrait is real selected-robot identity, not demo rule
                // data. Keep it visible; only hide level/EXP artwork because
                // the original simulator exposes no authoritative values for it.
                foreach (string name in new[] { "level_icon", "expbg", "exp" })
                {
                    Transform part = OfficialLayout.FindChild(info, name);
                    if (part) part.gameObject.SetActive(false);
                }
        }
    }

    void SetRootActive(string path, bool active)
    {
        RectTransform root = layout.Find(path);
        if (root) root.gameObject.SetActive(active);
    }

    /// <summary>Bring the official system/robot message tabs back from their prefab staging offset.</summary>
    void PlaceBattleMessages()
    {
        RectTransform messages = layout.Find("BattleMessage");
        if (!messages) return;
        // Do not show empty system/robot message frames during normal play.
        // They are event overlays and are enabled by the message controller
        // only when an actual event arrives.
        messages.gameObject.SetActive(false);
        messages.anchorMin = messages.anchorMax = new Vector2(0f, 1f);
        messages.pivot = new Vector2(0f, 1f);
        messages.anchoredPosition = new Vector2(18f, -555f);
        messages.sizeDelta = new Vector2(475f, 270f);
        foreach (Transform child in messages.GetComponentsInChildren<Transform>(true))
            if (child.name == "TemplateTextBattleMessageItem") child.gameObject.SetActive(false);
    }

    /// <summary>Normalize the zero-sized official module row so both icons and labels render.</summary>
    // ---------------------------------------------------- original demo centre

    /// <summary>
    /// Recreate the centre overlay from the real-video custom UI verbatim. It intentionally
    /// remains independent of the imported official chrome, which is only used around it.
    /// </summary>
    void BuildLegacyCenterOverlay()
    {
        RectTransform legacy = OfficialUiKit.Rect("Demo Center Compatibility Root", layout.Root,
            Vector2.one * .5f, Vector2.one * .5f, Vector2.zero, new Vector2(1280f, 720f));
        legacy.localScale = new Vector3(1.5f, 1.5f, 1f);

        RectTransform root = new GameObject("Shooting Frame").AddComponent<RectTransform>();
        root.SetParent(legacy, false);
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.pivot = Vector2.one * .5f;
        root.offsetMin = root.offsetMax = Vector2.zero;
        centerOverlay = root.gameObject;

        Color green = new Color(.62f, 1f, .16f, .92f);
        var lines = new List<Image>
        {
            UiLine(root, new Vector2(-145f, 125f), new Vector2(145f, 125f), 3f, green),
            UiLine(root, new Vector2(-145f, -125f), new Vector2(145f, -125f), 3f, green),
            UiLine(root, new Vector2(-145f, -125f), new Vector2(-145f, 125f), 3f, green),
            UiLine(root, new Vector2(145f, -125f), new Vector2(145f, 125f), 3f, green),
            UiLine(root, new Vector2(-145f, -125f), new Vector2(-360f, -285f), 3f, green),
            UiLine(root, new Vector2(145f, -125f), new Vector2(360f, -285f), 3f, green),
            UiLine(root, new Vector2(-38f, -150f), new Vector2(-16f, -150f), 2f, green),
            UiLine(root, new Vector2(-8f, -150f), new Vector2(8f, -150f), 2f, green),
            UiLine(root, new Vector2(16f, -150f), new Vector2(38f, -150f), 2f, green),
        };
        BuildDemoCrosshair(root);
        BuildPitchScale(root);

        var damage = gameObject.AddComponent<DamageDirectionController>();
        damage.indicators = BuildDemoDamageDirections(root);
        damage.Bind(client);

        Color blue = new Color(.18f, .82f, 1f, .88f);
        RectTransform horizonRoot = OfficialUiKit.Rect("Horizon Indicator", root,
            new Vector2(.92f, .5f), new Vector2(.92f, .5f), Vector2.zero, new Vector2(40f, 90f));
        UiLine(horizonRoot, new Vector2(0f, -32f), new Vector2(0f, 32f), 3f, blue);
        UiLine(horizonRoot, new Vector2(0f, 32f), new Vector2(-11f, 17f), 3f, blue);
        UiLine(horizonRoot, new Vector2(0f, 32f), new Vector2(11f, 17f), 3f, blue);
        var horizon = gameObject.AddComponent<HorizonIndicatorController>();
        horizon.sourceCamera = operatorCamera;
        horizon.indicator = horizonRoot;

        var shooting = gameObject.AddComponent<ShootingFrameController>();
        shooting.aimLines = lines.ToArray();
        shooting.Bind(client);

        // On the frame itself, so it stops drawing (and stops projecting) whenever the
        // centre overlay is hidden by a camera change. Added last, so its band lands on
        // top of the green frame rather than under it.
        var steer = root.gameObject.AddComponent<SteeringTrajectoryController>();
        steer.client = client;
        steer.frameRoot = root;
        steer.Bind(client);

        // Same deal for the ballistic funnel: the backend predicts the round's path from
        // the gun's own pose, this only projects it into the sight. Added after the steer
        // arc so the funnel strokes land on top of it.
        var funnel = root.gameObject.AddComponent<FunnelLineController>();
        funnel.client = client;
        funnel.operatorCamera = operatorCamera;
        funnel.worldToScene = worldToScene;
        funnel.frameRoot = root;
        // The funnel's mouth is specified in metres (as in the MuJoCo
        // reference), so it needs the scene's own units-per-metre to stay the
        // same width on screen.
        funnel.unitsPerMetre = unitsPerMetre;
        funnel.Bind(client);

        // The group box carries no readout: the size is the whole message, and the
        // number that described it was clutter the user asked to be rid of.
        var dispersion = root.gameObject.AddComponent<DispersionBoxController>();
        dispersion.client = client;
        dispersion.operatorCamera = operatorCamera;
        dispersion.frameRoot = root;
        dispersion.Bind(client);

        // The holdover ruler. Fixed, so it is added last and never touched again; it
        // sits on top of the funnel and the box, which are what it is read against.
        var ladder = root.gameObject.AddComponent<RangeScaleController>();
        ladder.client = client;
        ladder.operatorCamera = operatorCamera;
        ladder.frameRoot = root;
        ladder.Bind(client);
    }

    void BuildPitchScale(RectTransform root)
    {
        // Transparent overlay aligned to the firing-frame edge; the sketch's white is not UI.
        RectTransform scale = OfficialUiKit.Rect("俯仰角刻度", root, Vector2.one*.5f, Vector2.one*.5f,
            new Vector2(-188f, 0f), new Vector2(76f, 286f));
        Color ink = new Color(.90f,.96f,1f,.96f);
        Color minor = new Color(.72f,.84f,.90f,.82f);
        UiLine(scale,new Vector2(0,-118),new Vector2(0,118),2f,ink);
        for(int degree=-10;degree<=70;degree+=5)
        {
            float y=Mathf.Lerp(-118f,118f,Mathf.InverseLerp(-10f,70f,degree));
            bool major=degree%10==0;
            UiLine(scale,new Vector2(0,y),new Vector2(major?17f:9f,y),major?2f:1.5f,major?ink:minor);
            if(major) OfficialUiKit.Text("刻度 "+degree,scale,font,degree+"°",Vector2.one*.5f,Vector2.one*.5f,
                new Vector2(-27f,y),new Vector2(48f,18f),10,ink,TextAnchor.MiddleRight);
        }
        Image slider=OfficialUiKit.Image("当前俯仰",scale,Vector2.one*.5f,Vector2.one*.5f,
            new Vector2(9f,0f),new Vector2(25f,13f),null,new Color(1f,.84f,.10f,.98f));
        Text value=OfficialUiKit.Text("俯仰读数",scale,font,"0.0°",Vector2.one*.5f,Vector2.one*.5f,
            new Vector2(43f,0f),new Vector2(62f,22f),13,new Color(1f,.88f,.18f,1),TextAnchor.MiddleLeft);
        var controller=gameObject.AddComponent<PitchScaleController>();
        controller.slider=slider.rectTransform;controller.valueText=value;controller.Bind(client);
    }

    void BuildDemoCrosshair(RectTransform root)
    {
        Color white = new Color(1f, 1f, 1f, .96f);
        UiLine(root, new Vector2(-22f, 0f), new Vector2(-7f, 0f), 2f, white).name = "Crosshair Left";
        UiLine(root, new Vector2(7f, 0f), new Vector2(22f, 0f), 2f, white).name = "Crosshair Right";
        UiLine(root, new Vector2(0f, 7f), new Vector2(0f, 22f), 2f, white).name = "Crosshair Top";
        UiLine(root, new Vector2(0f, -22f), new Vector2(0f, -7f), 2f, white).name = "Crosshair Bottom";
        Image dot = OfficialUiKit.Image("Crosshair Dot", root, Vector2.one * .5f, Vector2.one * .5f,
            Vector2.zero, new Vector2(3f, 3f), null, white);
        dot.raycastTarget = false;
    }

    Image[] BuildDemoDamageDirections(RectTransform root)
    {
        Color inactive = new Color(.72f, .84f, 1f, .16f);
        var result = new Image[4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = OfficialUiKit.Image("Damage " + i, root, Vector2.one * .5f, Vector2.one * .5f,
                Vector2.zero, new Vector2(170f, 170f), null, inactive);
            result[i].sprite = QuarterArcSprite(128, i);
            result[i].raycastTarget = false;
        }
        return result;
    }

    static Sprite QuarterArcSprite(int size, int quadrant)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = (size - 1) * .5f;
        float radius = center - 5f;
        float centerAngle = 90f - quadrant * 90f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center, dy = y - center;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float delta = Mathf.Abs(Mathf.DeltaAngle(centerAngle, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg));
            texture.SetPixel(x, y, Mathf.Abs(distance - radius) <= 3f && delta <= 42f ? Color.white : Color.clear);
        }
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), Vector2.one * .5f);
    }

    static Image UiLine(RectTransform parent, Vector2 from, Vector2 to, float width, Color color)
    {
        Image image = new GameObject("Frame Line").AddComponent<Image>();
        image.transform.SetParent(parent, false);
        Vector2 delta = to - from;
        image.rectTransform.anchorMin = image.rectTransform.anchorMax = Vector2.one * .5f;
        image.rectTransform.anchoredPosition = (from + to) * .5f;
        image.rectTransform.sizeDelta = new Vector2(delta.magnitude, width);
        image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    void HidePathSuffix(string suffix)
    {
        foreach (var pair in layout.ByPath)
        {
            if (!pair.Key.EndsWith(suffix, System.StringComparison.Ordinal)) continue;
            foreach (RectTransform rect in pair.Value) rect.gameObject.SetActive(false);
        }
    }

    // ------------------------------------------------------------ core binding

    void BindCoreNodes()
    {
        matchTime = FindTextUnder("TopRoundItem", "time");
        roundLabel = FindTextUnder("TopRoundItem", "round");
        blueScore = FindTextUnder("TopRoundItem", "score_left (1)");
        redScore = FindTextUnder("TopRoundItem", "score_right (1)");

        // Blue is our side (the manual and the earlier client both take blue as own team).
        economyBlue = FindTextUnder("CoinItem", "RightCoin");
        economyRed = FindTextUnder("CoinItem", "LeftCoin");
        if (!heroHp) heroHp = FindTextUnder("StatusbarItem", "value1");
        ammoCount = FindTextUnder("StatusbarItem", "bulletnumber");
        bulletAllow = FindTextUnder("CrosshairItem", "bulletNum");
        robotId = FindTextUnder("StatusbarItem", "id");

        RectTransform statusBar = layout.Find("StatusbarItem");
        if (statusBar) heroHpFill = OfficialLayout.FindImage(statusBar, "top");

        if (!baseHpBlue) baseHpBlue = FindTextUnder("BlueBaseItem", "value");
        if (!baseHpRed) baseHpRed = FindTextUnder("RedBaseItem", "value");
        baseFillBlue = FindBarFill("BlueBaseItem");
        baseFillRed = FindBarFill("RedBaseItem");
        // The fill layer is authored twice as wide as the visible bar and then
        // scaled down by its local scale. The old code captured the scaled
        // sizeDelta, so at full HP the fill covered only half the bar.
        if (baseFillBlue) baseFillWidthBlue = baseFillBlue.sizeDelta.x /
            Mathf.Max(0.01f, Mathf.Abs(baseFillBlue.localScale.x));
        if (baseFillRed) baseFillWidthRed = baseFillRed.sizeDelta.x /
            Mathf.Max(0.01f, Mathf.Abs(baseFillRed.localScale.x));
        // The imported bar stacks a dark "middle" sheen and a right-end "cover"
        // on top of the fill. At full HP they read as an empty tail, so keep
        // only the bottom background and the driven top layer.
        foreach (string basePath in new[] { "RedBaseItem", "BlueBaseItem" })
        {
            RectTransform root = layout.Find(basePath);
            RectTransform slip = root ? OfficialLayout.FindChild(root, "bloodSlipItem") : null;
            if (!slip) continue;
            foreach (string layer in new[] { "middle", "cover", "light" })
            {
                RectTransform decorative = OfficialLayout.FindChild(slip, layer);
                if (decorative) decorative.gameObject.SetActive(false);
            }
        }

    }

    /// <summary>The base HP bar is a mask-free fill layer with pivot (0, .5), shrinking from the right.</summary>
    RectTransform FindBarFill(string basePath)
    {
        RectTransform root = layout.Find(basePath);
        if (!root) return null;
        RectTransform slip = OfficialLayout.FindChild(root, "bloodSlipItem");
        if (!slip) return null;
        RectTransform top = OfficialLayout.FindChild(slip, "top");
        return top;
    }

    Text FindTextUnder(string path, string childName)
    {
        RectTransform root = layout.Find(path);
        return root ? OfficialLayout.FindText(root, childName) : null;
    }

    // ------------------------------------------------------------------- apply

    void Apply(MujocoState state)
    {
        if (state == null) return;

        // Demo snapshots are useful for layout previews, but are not match rules.
        // Do not render their placeholder robot HP/economy as if it were live.
        bool authoritativeRobots = state.robotMaster != null &&
            !state.robotMaster.simulated && state.robotMaster.robots != null;

        // Aim/ballistic widgets are keyed to the active MuJoCo robot. Refuse a
        // different vehicle's data instead of silently showing the last model.
        if (PacificSelectionState.TryGetSelection(out _, out _) && state.hero != null &&
            state.hero.id != PacificSelectionState.SelectedActualRobotId)
        {
            // Remember the rejected snapshot too. Update() repaints whenever
            // the state object differs; without this, a rejected snapshot was
            // reprocessed on every rendered frame until the next packet.
            lastAppliedState = state;
            return;
        }
        lastAppliedState = state;

        if (authoritativeRobots)
        {
            foreach (CardRefs card in redCards) if (card.root) card.root.SetActive(false);
            foreach (CardRefs card in blueCards) if (card.root) card.root.SetActive(false);
            foreach (Text row in liveTabRows.Values) if (row) row.gameObject.SetActive(false);
            foreach (RobotMasterRobot robot in state.robotMaster.robots)
            {
                    int displayRobotId = robot.id >= 20 ? robot.id - 20 : robot.id;
                    ApplyCard(robot.faction == 1 ? blueCards : redCards, displayRobotId, robot);
                    if (liveTabRows.TryGetValue(robot.id, out Text tabRow) && tabRow)
                    {
                        tabRow.text = $"{displayRobotId}  {RoleName(displayRobotId)}     " +
                            $"{Mathf.Max(0, robot.hp)} / {Mathf.Max(0, robot.maxHp)}     " +
                            $"弹 {AmmoText(robot.ammo)}";
                        tabRow.gameObject.SetActive(true);
                    }

                bool local = state.rules != null
                    ? robot.id == state.rules.localRobotId
                    : (robot.id >= 20 ? robot.id - 20 : robot.id) == 1;
                if (local)
                {
                    if (localRobotAvatar)
                    {
                        string side = robot.faction == 1 ? "blue" : "red";
                        SetSprite(localRobotAvatar, $"{side}_teammate_avatar_{RobotRole(displayRobotId)}");
                        localRobotAvatar.gameObject.SetActive(true);
                    }
                    if (heroHp) heroHp.text = robot.hp.ToString();
                    if (heroHpFill && robot.maxHp > 0)
                    {
                        // The official bar art is a "slid" strip; drive it as a horizontal fill
                        // so it drains from the right the way the manual shows.
                        heroHpFill.type = Image.Type.Filled;
                        heroHpFill.fillMethod = Image.FillMethod.Horizontal;
                        heroHpFill.fillOrigin = 0;
                        heroHpFill.fillAmount = Mathf.Clamp01((float)robot.hp / robot.maxHp);
                    }
                    if (ammoCount) ammoCount.text = AmmoText(robot.ammo);
                    if (bulletAllow) bulletAllow.text = AmmoText(robot.ammo);
                    if (robotId) robotId.text = displayRobotId.ToString();
                }
            }
        }
        else
        {
            foreach (CardRefs card in redCards) if (card.root) card.root.SetActive(false);
            foreach (CardRefs card in blueCards) if (card.root) card.root.SetActive(false);
            foreach (Text row in liveTabRows.Values) if (row) row.gameObject.SetActive(false);
        }

        if (state.rules != null)
        {
            MatchRuleState rules = state.rules;
            if (economyRed) economyRed.text = rules.redCoins.ToString();
            if (economyBlue) economyBlue.text = rules.blueCoins.ToString();
            ApplyBase(baseHpRed, baseFillRed, baseFillWidthRed, rules.redBaseHp, rules.redBaseMaxHp);
            ApplyBase(baseHpBlue, baseFillBlue, baseFillWidthBlue, rules.blueBaseHp, rules.blueBaseMaxHp);
            ApplyOutpost("RedOutpostItem", outpostHpRed, outpostFillRed, rules.redOutpostHp, rules.redOutpostMaxHp);
            ApplyOutpost("BlueOutpostItem", outpostHpBlue, outpostFillBlue, rules.blueOutpostHp, rules.blueOutpostMaxHp);
            int total = Mathf.Max(0, Mathf.CeilToInt(rules.timeLeftS));
            if (matchTime && total != lastMatchSeconds)
            {
                lastMatchSeconds = total;
                matchTime.text = $"{total / 60}:{total % 60:00}";
            }
        }
        else
        {
            // A missing rules feed is unknown, not a valid zero score/zero HP.
            if (baseHpRed) baseHpRed.text = "—";
            if (baseHpBlue) baseHpBlue.text = "—";
            if (economyRed) economyRed.text = "—";
            if (economyBlue) economyBlue.text = "—";
            if (redScore) redScore.text = "—";
            if (blueScore) blueScore.text = "—";
            if (heroHp) heroHp.text = "—";
            if (heroHpFill) heroHpFill.fillAmount = 0f;
            SetRootActive("RedOutpostItem", false);
            SetRootActive("BlueOutpostItem", false);
            foreach (CardRefs card in redCards) if (card.root) card.root.SetActive(false);
            foreach (CardRefs card in blueCards) if (card.root) card.root.SetActive(false);
            foreach (Text row in liveTabRows.Values) if (row) row.gameObject.SetActive(false);
        }

        // The gimbal-pose widget drives itself through RobotPoseWidgetController.
        if (state.rules == null && matchTime && state.simulationTimeS > 0f)
        {
            int total = Mathf.Max(0, 420 - Mathf.FloorToInt(state.simulationTimeS));
            // Changes once a second but arrives 30 times a second; the interpolation was
            // allocating a string on every state frame.
            if (total != lastMatchSeconds)
            {
                lastMatchSeconds = total;
                matchTime.text = $"{total / 60}:{total % 60:00}";
            }
        }
    }

    void ApplyOutpost(string path, Text label, Image fill, int hp, int maxHp)
    {
        RectTransform root = layout.Find(path);
        if (!root) return;
        root.gameObject.SetActive(maxHp > 0);
        if (maxHp <= 0) return;
        if (label) label.text = Mathf.Max(0, hp).ToString();
        if (fill)
        {
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = Mathf.Clamp01((float)hp / maxHp);
        }
    }

    static void ApplyBase(Text label, RectTransform fill, float fullWidth, int hp, int maxHp)
    {
        // Training Ground has no initialized match-base HP. An unavailable
        // referee value is not a destroyed base or a team's aggregate HP.
        if (label) label.text = maxHp > 0 ? Mathf.Max(0, hp).ToString() : "—";
        if (!fill) return;
        Image image = fill.GetComponent<Image>();
        if (image != null)
        {
            // The imported prefab ships the top layer with a demo fillAmount
            // (0.591). Resizing the rect alone kept that fraction permanently
            // dark, so a base at 5000/5000 still looked 59% full. Drive the
            // fill amount instead, exactly like the outpost bars.
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = 0;
            image.fillAmount = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
            Vector2 full = fill.sizeDelta;
            if (Mathf.Abs(full.x - fullWidth) > 0.01f) { full.x = fullWidth; fill.sizeDelta = full; }
            return;
        }
        Vector2 size = fill.sizeDelta;
        size.x = maxHp > 0 ? fullWidth * Mathf.Clamp01((float)hp / maxHp) : 0f;
        fill.sizeDelta = size;
    }

    void ApplyCard(List<CardRefs> cards, int robotId, RobotMasterRobot robot)
    {
        foreach (CardRefs card in cards)
        {
            if (card.robotId != robotId) continue;
            if (card.root) card.root.SetActive(true);
            if (card.healthMask && robot.maxHp > 0)
            {
                float ratio = Mathf.Clamp01((float)robot.hp / robot.maxHp);
                // The official bar is masked from the right, exactly like the base bars.
                card.healthMask.anchorMin = new Vector2(0f, card.healthMask.anchorMin.y);
                card.healthMask.anchorMax = new Vector2(ratio, card.healthMask.anchorMax.y);
            }
            if (card.bulletValue) card.bulletValue.text = AmmoText(robot.ammo);
            return;
        }
    }

    private static string AmmoText(int ammo) => ammo < 0 ? "∞" : Mathf.Max(0, ammo).ToString();

    public OfficialLayout Layout => layout;
}
