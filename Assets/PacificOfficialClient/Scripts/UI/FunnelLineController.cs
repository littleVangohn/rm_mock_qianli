using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the backend's predicted round path as a sight funnel.
///
/// MuJoCo owns the prediction: the server integrates the projectile forward from the
/// muzzle site's world pose with the gimbal's own yaw and pitch. That is the funnel's
/// only input -- no target, no ray cast, no mouse position -- so the curve is a rollout
/// of where this robot's gun actually points.
///
/// It is projected and never re-centred: the operator camera rides the barrel's pivot
/// 0.14 m above the bore line while the round leaves from the muzzle below it, and the
/// whole point of the line is to show where the shot really goes, not where the crosshair
/// looks. Expect the cone to start below the crosshair, and to swing out sideways when
/// the chassis is rolled up on terrain.
/// </summary>
public class FunnelLineController : MonoBehaviour
{
    public MujocoClient client;
    public Camera operatorCamera;
    public RectTransform frameRoot;
    public System.Func<Vector3, Vector3> worldToScene;

    // Samples at or behind the near plane have no usable projection: the camera rides the
    // barrel, so the first samples of the path sit on the camera itself.
    [Header("投影")]
    [Range(0.01f, 0.5f)] public float minProjectedDepth = 0.08f;
    public float maxLocalExtent = 20000f;

    [Header("漏斗")]
    // The mouth opens with how far the round has departed from the bore line, which is
    // what makes it a funnel instead of a ribbon: a fixed angle would draw parallel edges,
    // while the drop grows faster than the range does. The ratio is a display convention,
    // the departure it scales is MuJoCo's own trajectory.
    public float mouthRatio = .5f;
    public float minHalfWidthM = .03f;
    public float maxHalfWidthM = 4f;
    public float tickIntervalS = .1f;
    // The reference client runs at 1 scene unit = 1 m (MuJoCo coordinates are
    // imported directly). The OSS simulator scene is 10 units = 1 m, and the
    // trajectory points arrive in scene units -- so the departure the mouth is
    // measured from has to be divided by this before the metre clamps apply,
    // or the cone opens ten times too wide.
    public float unitsPerMetre = 1f;

    [Header("外观")]
    // Faint on purpose: the funnel is a reference under the sight picture, not a thing to
    // read. It shares the crosshair area with the frame, the steer arc and the range
    // ladder, and the sight stays legible only while every overlay stays quiet.
    public float centerWidth = 2f;
    public float edgeWidth = 2f;
    public float tickWidth = 1.5f;
    public Color centerColor = new Color(.88f, .96f, 1f, .45f);
    public Color edgeColor = new Color(.60f, .90f, 1f, .28f);
    public Color tickColor = new Color(.60f, .90f, 1f, .16f);

    MujocoClient subscribed;
    FunnelPoint[] points;
    readonly List<Vector2> visible = new List<Vector2>();
    readonly List<Vector2> perMeter = new List<Vector2>();
    readonly List<float> deviation = new List<float>();
    readonly List<int> tickBucket = new List<int>();
    readonly List<Vector2> leftEdge = new List<Vector2>();
    readonly List<Vector2> rightEdge = new List<Vector2>();
    readonly List<Image> centerPool = new List<Image>();
    readonly List<Image> leftPool = new List<Image>();
    readonly List<Image> rightPool = new List<Image>();
    readonly List<Image> tickPool = new List<Image>();

    void OnEnable() => Bind(client);
    void OnDisable() => Bind(null);

    /// <summary>This component lives on the shooting frame, which is toggled with the
    /// camera mode, so it is disabled and re-enabled routinely -- Bind must never drop
    /// the client reference, only the subscription.</summary>
    public void Bind(MujocoClient value)
    {
        if (subscribed) subscribed.StateUpdated -= Apply;
        subscribed = value;
        if (subscribed) subscribed.StateUpdated += Apply;
        else SetVisible(false);
    }

    void Apply(MujocoState state)
    {
        FunnelPoint[] incoming = state?.aim?.funnel?.points;
        points = incoming != null && incoming.Length >= 2 ? incoming : null;
        if (points == null) SetVisible(false);
    }

    // Re-projected every frame rather than on state arrival: the curve is glued to the
    // world, so redrawing only at the network rate would make it crawl across the sight.
    void LateUpdate()
    {
        if (points == null || !operatorCamera || !frameRoot) return;
        CollectVisible();
        if (visible.Count < 2)
        {
            SetVisible(false);
            return;
        }
        DrawCurve();
    }

    void CollectVisible()
    {
        visible.Clear();
        perMeter.Clear();
        deviation.Clear();
        tickBucket.Clear();
        // The bore line, taken from the first two samples: the round leaves along it, so
        // the distance from it is the departure the mouth widens with. Sampling it from
        // the path keeps the client out of the business of re-deriving gimbal angles.
        Vector3 boreOrigin = ScenePoint(points[0].x, points[0].y, points[0].z);
        Vector3 bore = ScenePoint(points[1].x, points[1].y, points[1].z) - boreOrigin;
        bore = bore.sqrMagnitude > 1e-8f ? bore.normalized : Vector3.zero;
        float interval = Mathf.Max(.01f, tickIntervalS);
        int bucket = int.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            if (!Project(points[i].x, points[i].y, points[i].z, out Vector2 local)) continue;
            if (!ProjectRight(points[i].x, points[i].y, points[i].z, local, out Vector2 reach)) continue;
            Vector3 arm = ScenePoint(points[i].x, points[i].y, points[i].z) - boreOrigin;
            int sample = Mathf.FloorToInt(points[i].t / interval + .5f);
            visible.Add(local);
            perMeter.Add(reach);
            deviation.Add(bore == Vector3.zero ? 0f : (arm - Vector3.Dot(arm, bore) * bore).magnitude);
            // One tick per interval, on the first sample that reaches it: sampling is the
            // backend's business, so a coarser ladder still lands on the same clock.
            tickBucket.Add(sample == bucket ? -1 : sample);
            bucket = sample;
        }
    }

    bool Project(float x, float y, float z, out Vector2 local)
    {
        local = Vector2.zero;
        Vector3 viewport = operatorCamera.WorldToViewportPoint(ScenePoint(x, y, z));
        if (viewport.z <= minProjectedDepth) return false;
        Vector3 screen = operatorCamera.ViewportToScreenPoint(viewport);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(frameRoot, screen, null, out local)) return false;
        // A grazing sample can land absurdly far out; the stroke maths stays finite but the
        // rects would not, so drop anything past a sane multiple of the frame.
        return Mathf.Abs(local.x) < maxLocalExtent && Mathf.Abs(local.y) < maxLocalExtent;
    }

    /// <summary>Frame-space vector spanning one scene unit sideways at that sample's depth.
    /// Measuring it by projection keeps the mouth honest under perspective and through
    /// the centre overlay's own scale, instead of assuming a fixed pixels-per-unit factor.</summary>
    bool ProjectRight(float x, float y, float z, Vector2 local, out Vector2 reach)
    {
        reach = Vector2.zero;
        Vector3 probeWorld = ScenePoint(x, y, z) + operatorCamera.transform.right;
        Vector3 viewport = operatorCamera.WorldToViewportPoint(probeWorld);
        if (viewport.z <= minProjectedDepth) return false;
        Vector3 screen = operatorCamera.ViewportToScreenPoint(viewport);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(frameRoot, screen, null, out Vector2 probe)) return false;
        reach = probe - local;
        return reach.sqrMagnitude > 1e-6f;
    }

    Vector3 ScenePoint(float x, float y, float z)
    {
        Vector3 world = CoordinateMapper.World(x, y, z);
        return worldToScene != null ? worldToScene(world) : world;
    }

    void DrawCurve()
    {
        // Same construction as the MuJoCo reference: centre path plus the
        // widening mouth and its rungs. The mouth is solved in metres (the
        // reference's units) and only then converted back to scene units, so
        // the 10 units/m OSS scene keeps the reference's proportions.
        float scale = Mathf.Max(1e-4f, unitsPerMetre);
        int count = visible.Count;
        leftEdge.Clear();
        rightEdge.Clear();
        for (int i = 0; i < count; i++)
        {
            float halfM = Mathf.Clamp(mouthRatio * deviation[i] / scale, minHalfWidthM, maxHalfWidthM);
            Vector2 reach = perMeter[i] * (halfM * scale);
            leftEdge.Add(visible[i] + reach);
            rightEdge.Add(visible[i] - reach);
        }
        EnsurePool(centerPool, count - 1, centerWidth, centerColor);
        EnsurePool(leftPool, count - 1, edgeWidth, edgeColor);
        EnsurePool(rightPool, count - 1, edgeWidth, edgeColor);
        for (int i = 0; i < count - 1; i++)
        {
            Stroke(centerPool[i], visible[i], visible[i + 1]);
            Stroke(leftPool[i], leftEdge[i], leftEdge[i + 1]);
            Stroke(rightPool[i], rightEdge[i], rightEdge[i + 1]);
        }
        EnsurePool(tickPool, count, tickWidth, tickColor);
        for (int i = 0; i < count; i++)
        {
            if (tickBucket[i] < 0) Hide(tickPool, i);
            else Stroke(tickPool[i], leftEdge[i], rightEdge[i]);
        }
        // The pools only grow, and the visible sample count changes every frame
        // (the rollout ends wherever the terrain is). Strokes left over from a
        // longer path would stay frozen at their old screen positions and show
        // through as a ghost copy of the funnel, so the unused tail is hidden.
        HideFrom(centerPool, count - 1);
        HideFrom(leftPool, count - 1);
        HideFrom(rightPool, count - 1);
        HideFrom(tickPool, count);
    }

    static void HideFrom(List<Image> pool, int start)
    {
        for (int i = Mathf.Max(0, start); i < pool.Count; i++)
            if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    void Stroke(Image image, Vector2 from, Vector2 to)
    {
        if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
        Vector2 delta = to - from;
        image.rectTransform.anchoredPosition = (from + to) * .5f;
        image.rectTransform.sizeDelta = new Vector2(delta.magnitude, image.rectTransform.sizeDelta.y);
        image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    void Hide(List<Image> pool, int index)
    {
        if (index < pool.Count && pool[index].gameObject.activeSelf) pool[index].gameObject.SetActive(false);
    }

    void EnsurePool(List<Image> pool, int count, float width, Color color)
    {
        while (pool.Count < count)
        {
            var image = new GameObject("Funnel Stroke").AddComponent<Image>();
            image.transform.SetParent(frameRoot, false);
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = Vector2.one * .5f;
            image.rectTransform.pivot = Vector2.one * .5f;
            image.rectTransform.sizeDelta = new Vector2(0f, width);
            image.raycastTarget = false;
            image.color = color;
            image.gameObject.SetActive(false);
            pool.Add(image);
        }
    }

    void SetVisible(bool value)
    {
        if (value) return;
        HideRange(centerPool);
        HideRange(leftPool);
        HideRange(rightPool);
        HideRange(tickPool);
    }

    static void HideRange(List<Image> pool)
    {
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }
}
