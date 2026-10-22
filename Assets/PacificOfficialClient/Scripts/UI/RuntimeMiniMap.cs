using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.UI;

public class RuntimeMiniMap : MonoBehaviour
{
    public MujocoClient client; public RectTransform mapRect;
    public Func<Vector3, Vector2> worldToMap;
    public Func<Vector2, Vector2> fieldToMap;
    // In the open-source match, pre-placed robots are referee-owned scene
    // objects. Their map pose comes from those objects, not demo MuJoCo slots.
    public Func<int, Vector2?> ruleRobotMapPosition;
    public Func<int, float?> ruleRobotHeadingDeg;
    // When hosted in the source simulator, only a MuJoCo body with a live
    // referee object and initialized HP is a real match vehicle marker.
    public Func<int, RobotMasterRobot> ruleRobotData;
    public Sprite sharedMarkerSprite;
    public float markerSize = 9f;
    readonly Dictionary<int, RectTransform> markers = new Dictionary<int, RectTransform>();
    // The health ring is looked up on every state update; caching it here avoids a
    // string-keyed subtree search plus GetComponent per robot, 30 times a second.
    readonly Dictionary<int, Image> healthBadges = new Dictionary<int, Image>();
    readonly Dictionary<int, Image> factionBadges = new Dictionary<int, Image>();
    // Cached once at marker creation: this runs 30 times a second and the old
    // string-keyed subtree search plus GetComponent was measurable garbage.
    readonly Dictionary<int, Text> numberLabels = new Dictionary<int, Text>();
    readonly Dictionary<int, Text> headingLabels = new Dictionary<int, Text>();
    readonly Dictionary<int, string> lastNumberText = new Dictionary<int, string>();
    readonly List<Image> trajectorySegments = new List<Image>();
    readonly List<Image> trajectoryDots = new List<Image>();
    RectTransform trajectoryRoot;
    // Reordering children rebuilds the canvas, so it is deferred to the updates where the
    // set of children actually changed rather than done on every state frame.
    bool orderDirty;
    RectTransform sharedMarker;
    Image sharedMarkerImage;
    Sprite circleSprite;
    Sprite commandMarkerSprite;
    readonly Color trajectoryColor = new Color(0.94f, 1f, 0.12f, 0.95f);
    MujocoClient subscribedClient;
    void OnEnable(){ Subscribe(); }
    void OnDisable(){ Unsubscribe(); }
    public void Bind(MujocoClient value)
    {
        if(client != value)
        {
            Unsubscribe();
            client=value;
        }
        Subscribe();
    }
    void Subscribe()
    {
        if(!isActiveAndEnabled || !client || subscribedClient == client) return;
        Unsubscribe();
        subscribedClient=client;
        subscribedClient.StateUpdated += Apply;
        // The fullscreen map is hidden during startup. Repaint from the latest state
        // immediately when it is shown instead of waiting for the next network packet.
        if(client.State != null) Apply(client.State);
    }
    void Unsubscribe()
    {
        if(subscribedClient) subscribedClient.StateUpdated -= Apply;
        subscribedClient=null;
    }
    void Apply(MujocoState state)
    {
        if(state == null || state.frame == null || state.frame.bounds == null) return;
        ApplyTrajectory(state.aim, state.frame.bounds);
        if(state.robots != null) foreach(var robot in state.robots)
        {
            if(robot == null || !string.Equals(robot.source, "mujoco", System.StringComparison.OrdinalIgnoreCase))
                continue;
            RobotMasterRobot authoritative = ruleRobotData?.Invoke(robot.id);
            if(ruleRobotData != null && authoritative == null)
            {
                if(markers.TryGetValue(robot.id, out var stale)) stale.gameObject.SetActive(false);
                continue;
            }
            if(!markers.TryGetValue(robot.id, out var marker)) marker = CreateMarker(robot.id);
            marker.gameObject.SetActive(true);
            // RobotState.x/y are already the MuJoCo field-plane coordinates;
            // only full 3-D payload points need CoordinateMapper.World(x,y,z).
            // RobotState stores the map-plane axes as x/y. The bridge callback
            // accepts Unity world coordinates (x/z), so preserve the source y
            // as z rather than pinning every marker to the field centre line.
            Vector2 normalized = fieldToMap != null
                ? fieldToMap(new Vector2(robot.x, robot.y))
                : worldToMap != null
                    ? worldToMap(new Vector3(robot.x, 0f, robot.y))
                    : CoordinateMapper.Map(robot.x, robot.y, state.frame.bounds);
            Vector2? rulePosition = ruleRobotMapPosition?.Invoke(robot.id);
            if(rulePosition.HasValue) normalized = rulePosition.Value;
            marker.anchorMin = marker.anchorMax = normalized; marker.anchoredPosition = Vector2.zero; marker.localRotation = Quaternion.identity;
            healthBadges.TryGetValue(robot.id, out Image badge);
            factionBadges.TryGetValue(robot.id, out Image factionBadge);
            int expectedFaction = robot.id >= 20 ? 1 : 0;
            int displayId = robot.id >= 20 ? robot.id - 20 : robot.id;
            // Rules use actual referee IDs (blue robots are 21-27). The
            // display ID is only a compact label, never a data key.
            RobotMasterRobot data = authoritative ?? FindRobot(state.robotMaster, robot.id, expectedFaction);
            if(badge)
            {
                badge.fillAmount = HealthFraction(data);
                badge.color = data == null ? new Color(.45f, .48f, .5f, .9f) :
                    data.faction == 1 ? new Color(.08f, .62f, 1f, .98f) : new Color(.98f, .16f, .12f, .98f);
            }
            if(factionBadge) factionBadge.color = data == null
                ? new Color(.52f,.56f,.60f,.9f)
                : data.faction == 1 ? new Color(.12f,.66f,1f,1f) : new Color(1f,.22f,.16f,1f);
            numberLabels.TryGetValue(robot.id, out Text label);
            if(label)
            {
                string text = data != null && data.maxHp > 0 ? $"{displayId}\n{data.hp}" : displayId.ToString();
                if(!lastNumberText.TryGetValue(robot.id, out string previous) || previous != text)
                {
                    lastNumberText[robot.id] = text;
                    label.text = text;
                }
            }
            headingLabels.TryGetValue(robot.id, out Text heading);
            if(heading)
            {
                heading.gameObject.SetActive(true);
                float yaw = ruleRobotHeadingDeg?.Invoke(robot.id) ?? robot.yawDeg;
                heading.rectTransform.localRotation = Quaternion.Euler(0f,0f,-yaw);
            }
        }
        ApplySharedMarker(state.sharedMarker, state.frame.bounds);
        if(orderDirty){ orderDirty=false; if(trajectoryRoot) trajectoryRoot.SetAsLastSibling(); }
    }
    static RobotMasterRobot FindRobot(RobotMasterState state, int id, int faction)
    {
        if(state?.robots == null) return null;
        for(int i=0;i<state.robots.Length;i++)
            if(state.robots[i] != null && state.robots[i].id == id && state.robots[i].faction == faction) return state.robots[i];
        return null;
    }
    public static float HealthFraction(RobotMasterRobot robot)
        => robot != null && robot.maxHp > 0 ? Mathf.Clamp01((float)robot.hp / robot.maxHp) : 0f;
    void ApplySharedMarker(SharedMarkerState state, FrameBounds bounds)
    {
        bool visible = state != null && state.active && state.positionM != null && state.positionM.Length >= 2;
        if (!visible)
        {
            if (sharedMarker) sharedMarker.gameObject.SetActive(false);
            return;
        }
        if (!sharedMarker)
        {
            sharedMarker = new GameObject("Shared Marker").AddComponent<RectTransform>();
            sharedMarker.SetParent(mapRect, false);
            sharedMarker.anchorMin = sharedMarker.anchorMax = new Vector2(.5f, .5f);
            sharedMarkerImage = sharedMarker.gameObject.AddComponent<Image>();
            sharedMarkerImage.raycastTarget = false;
            // Created last, so it sits above the robot markers for good; re-activating it
            // later does not change the sibling index, so this only has to happen once.
            sharedMarker.SetAsLastSibling();
        }
        sharedMarkerImage.sprite = CommandMarkerSprite();
        sharedMarkerImage.color = new Color(.949f, .357f, .416f, 1f);
        sharedMarkerImage.preserveAspect = true;
        // MiniMap.qml draws an 18 px ring on its 480 px map.
        sharedMarker.sizeDelta = Vector2.one * (mapRect.rect.width * 18f / 480f);
        Vector2 point;
        if (fieldToMap != null)
            point = fieldToMap(new Vector2(state.positionM[0], state.positionM[1]));
        else if (worldToMap != null)
            point = worldToMap(new Vector3(state.positionM[0], state.positionM[1], state.positionM[2]));
        else
            point = CoordinateMapper.Map(state.positionM[0], state.positionM[1], bounds);
        sharedMarker.anchorMin = sharedMarker.anchorMax = point;
        sharedMarker.anchoredPosition = Vector2.zero;
        sharedMarker.gameObject.SetActive(true);
    }
    Vector2 MapTrajectoryPoint(TrajectoryPoint point, FrameBounds bounds)
    {
        // Trajectory points are MuJoCo field metres. fieldToMap is the bridge's
        // calibrated backend->map transform; never feed it a Unity-world point.
        if (fieldToMap != null)
            return fieldToMap(new Vector2(point.x, point.y));
        if (worldToMap != null)
            return worldToMap(new Vector3(point.x, point.y, point.z));
        return CoordinateMapper.Map(point.x, point.y, bounds);
    }
    void ApplyTrajectory(AimState aim, FrameBounds bounds)
    {
        EnsureTrajectoryRoot();
        TrajectoryPoint[] points = aim?.trajectory;
        int pointCount = points == null ? 0 : points.Length;
        EnsureTrajectoryPool(pointCount);
        // Guarded, because this runs on every state frame for up to 55 images and the
        // count rarely changes.
        for(int i=0;i<trajectoryDots.Count;i++){bool on=i<pointCount;if(trajectoryDots[i].gameObject.activeSelf!=on)trajectoryDots[i].gameObject.SetActive(on);}
        for(int i=0;i<trajectorySegments.Count;i++){bool on=i<pointCount-1;if(trajectorySegments[i].gameObject.activeSelf!=on)trajectorySegments[i].gameObject.SetActive(on);}
        if(pointCount>0)
        {
            float width=trajectoryRoot.rect.width, height=trajectoryRoot.rect.height;
            for(int i=0;i<pointCount;i++)
            {
                Vector2 normalized=MapTrajectoryPoint(points[i],bounds);
                RectTransform dot=trajectoryDots[i].rectTransform;
                dot.anchorMin=dot.anchorMax=normalized;
                dot.anchoredPosition=Vector2.zero;
                if(i<pointCount-1)
                {
                    Vector2 next=MapTrajectoryPoint(points[i+1],bounds);
                    Vector2 from=new Vector2((normalized.x-.5f)*width,(normalized.y-.5f)*height);
                    Vector2 to=new Vector2((next.x-.5f)*width,(next.y-.5f)*height);
                    Vector2 delta=to-from;
                    float visibleLength=delta.magnitude*.62f;
                    RectTransform segment=trajectorySegments[i].rectTransform;
                    segment.anchorMin=segment.anchorMax=new Vector2(.5f,.5f);
                    segment.anchoredPosition=from+(delta.normalized*visibleLength*.5f);
                    segment.sizeDelta=new Vector2(visibleLength,1f);
                    segment.localRotation=Quaternion.Euler(0f,0f,Mathf.Atan2(delta.y,delta.x)*Mathf.Rad2Deg);
                }
            }
        }
    }
    void EnsureTrajectoryRoot()
    {
        if(trajectoryRoot) return;
        trajectoryRoot=new GameObject("Predicted Trajectory").AddComponent<RectTransform>();
        trajectoryRoot.SetParent(mapRect,false);
        trajectoryRoot.anchorMin=Vector2.zero; trajectoryRoot.anchorMax=Vector2.one;
        trajectoryRoot.offsetMin=trajectoryRoot.offsetMax=Vector2.zero;
        trajectoryRoot.SetAsFirstSibling();
    }
    void EnsureTrajectoryPool(int pointCount)
    {
        while(trajectoryDots.Count<pointCount) trajectoryDots.Add(CreateTrajectoryImage("Trajectory Point",new Vector2(1.5f,1.5f),true));
        while(trajectorySegments.Count<Mathf.Max(0,pointCount-1)) trajectorySegments.Add(CreateTrajectoryImage("Trajectory Dash",new Vector2(3,1f),false));
    }
    Image CreateTrajectoryImage(string name,Vector2 size,bool circle)
    {
        var image=new GameObject(name).AddComponent<Image>();
        image.transform.SetParent(trajectoryRoot,false);
        image.rectTransform.sizeDelta=size;
        image.color=trajectoryColor;
        image.raycastTarget=false;
        if(circle) image.sprite=CircleSprite();
        return image;
    }
    RectTransform CreateMarker(int id)
    {
        float size = Mathf.Max(9f, markerSize);
        var root = new GameObject("Robot " + id).AddComponent<RectTransform>(); root.SetParent(mapRect,false); root.sizeDelta = Vector2.one * size; markers[id]=root;
        healthBadges.Remove(id); numberLabels.Remove(id); headingLabels.Remove(id); lastNumberText.Remove(id); orderDirty=true;
        var factionRing = new GameObject("Faction").AddComponent<Image>(); factionRing.transform.SetParent(root,false); factionRing.rectTransform.anchorMin=factionRing.rectTransform.anchorMax=new Vector2(.5f,.5f); factionRing.rectTransform.sizeDelta=Vector2.one*(size+2f); factionRing.sprite=CircleSprite(); factionRing.color=new Color(.52f,.56f,.60f,.9f); factionRing.raycastTarget=false; factionBadges[id]=factionRing;
        var outline = new GameObject("Circle Outline").AddComponent<Image>(); outline.transform.SetParent(root,false); outline.rectTransform.anchorMin=outline.rectTransform.anchorMax=new Vector2(.5f,.5f); outline.rectTransform.sizeDelta=Vector2.one*size; outline.sprite=CircleSprite(); outline.color=new Color(.94f,.98f,1f,.98f); outline.raycastTarget=false;
        var baseCircle = new GameObject("Circle Base").AddComponent<Image>(); baseCircle.transform.SetParent(root,false); baseCircle.rectTransform.anchorMin=baseCircle.rectTransform.anchorMax=new Vector2(.5f,.5f); baseCircle.rectTransform.sizeDelta=Vector2.one*(size-2.5f); baseCircle.sprite=CircleSprite(); baseCircle.color=new Color(.055f,.075f,.085f,.94f); baseCircle.raycastTarget=false;
        var ring = new GameObject("Health").AddComponent<Image>(); ring.transform.SetParent(root,false); ring.rectTransform.anchorMin=ring.rectTransform.anchorMax=new Vector2(.5f,.5f); ring.rectTransform.sizeDelta=Vector2.one*(size-2.5f); ring.type=Image.Type.Filled; ring.fillMethod=Image.FillMethod.Radial360; ring.fillOrigin=(int)Image.Origin360.Top; ring.fillClockwise=true; ring.fillAmount=0f; ring.color=new Color(.92f,.10f,.12f,.98f); ring.sprite=CircleSprite(); ring.preserveAspect=true; ring.raycastTarget=false; healthBadges[id]=ring;
        var label = new GameObject("Number").AddComponent<Text>(); label.transform.SetParent(root,false); label.rectTransform.anchorMin=label.rectTransform.anchorMax=new Vector2(.5f,.5f); label.rectTransform.sizeDelta=Vector2.one * (size*1.5f); label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); label.fontSize=Mathf.Max(8,Mathf.RoundToInt(size*.42f)); label.fontStyle=FontStyle.Bold; label.alignment=TextAnchor.MiddleCenter; label.color=Color.white; label.text=id.ToString(); label.raycastTarget=false; numberLabels[id]=label; lastNumberText[id]=label.text;
        var arrow = new GameObject("Heading").AddComponent<Text>(); arrow.transform.SetParent(root,false); arrow.rectTransform.anchorMin=arrow.rectTransform.anchorMax=new Vector2(.5f,.5f); arrow.rectTransform.anchoredPosition=new Vector2(0,size*.5f); arrow.rectTransform.sizeDelta=Vector2.one * (size*.65f); arrow.font=label.font; arrow.fontSize=Mathf.Max(6,Mathf.RoundToInt(size*.65f)); arrow.alignment=TextAnchor.MiddleCenter; arrow.color=Color.cyan; arrow.text="▲"; headingLabels[id]=arrow;
        arrow.gameObject.SetActive(false);
        var firing = new GameObject("Firing").AddComponent<Image>(); firing.transform.SetParent(root,false); firing.rectTransform.anchorMin=firing.rectTransform.anchorMax=new Vector2(.5f,.5f); firing.rectTransform.sizeDelta=Vector2.one * (size*1.35f); firing.color=new Color(1,.7f,0,.25f); firing.gameObject.SetActive(false);
        return root;
    }
    Sprite CircleSprite(){if(circleSprite)return circleSprite;var t=new Texture2D(32,32,TextureFormat.RGBA32,false);for(int y=0;y<32;y++)for(int x=0;x<32;x++){float dx=x-15.5f,dy=y-15.5f;t.SetPixel(x,y,dx*dx+dy*dy<240?Color.white:Color.clear);}t.Apply(false,true);circleSprite=Sprite.Create(t,new Rect(0,0,32,32),new Vector2(.5f,.5f));return circleSprite;}
    Sprite CommandMarkerSprite()
    {
        if (commandMarkerSprite) return commandMarkerSprite;
        var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            float distance = Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f));
            texture.SetPixel(x, y, distance >= 24f && distance <= 31f ? Color.white : Color.clear);
        }
        texture.Apply(false, true);
        commandMarkerSprite = Sprite.Create(texture, new Rect(0, 0, 64, 64), Vector2.one * .5f);
        return commandMarkerSprite;
    }
}
