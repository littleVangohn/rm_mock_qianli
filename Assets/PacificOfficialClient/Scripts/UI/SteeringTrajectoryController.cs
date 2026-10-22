using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the omnidirectional chassis translation vector, anchored to the shooting frame.
///
/// The arrow starts at the middle of the frame's bottom edge, where the ground meets the
/// chassis in the operator view. Its screen direction is the predicted W/A/S/D translation
/// expressed in chassis coordinates; it is not a car steering or reversing-camera curve.
///
/// MuJoCo still owns the prediction; the fan's reach is driven by the distance the server
/// says the chassis will actually cover, so the display stays tied to the physics.
/// </summary>
public class SteeringTrajectoryController : MonoBehaviour
{
    public MujocoClient client;
    public RectTransform frameRoot;

    [Header("位置")]
    // Midpoints of the two green ground diagonals below the aiming frame. A-containing
    // movement uses the left anchor; D-containing movement uses the right anchor.
    public Vector2 leftAnchor = new Vector2(-235f, -205f);
    public Vector2 rightAnchor = new Vector2(235f, -205f);
    // How far each guide bends as it travels.
    [Range(0f, 60f)] public float combinationCurve = 22f;
    // The green frame's bottom edge is y=-125. Keep the complete guide visibly below it.
    public float guideCeilingY = -150f;

    [Header("外观")]
    public float minArcLength = 32f;
    public float maxArcLength = 95f;
    public float bandWidth = 3f;
    public float jointDiameter = 3f;
    // Reversing guides have no arrowhead; kept here so it can be turned back on.
    public bool showArrow = true;
    public float arrowLength = 16f;
    public float arrowWidth = 3f;
    [Range(10f, 60f)] public float arrowSpreadDeg = 30f;
    // World metres of predicted travel that saturates the fan's reach.
    public float saturatingDistanceM = 2.0f;
    public Color bandColor = new Color(1f, .84f, .10f, .62f);

    const int ArcSamples = 16;

    MujocoClient subscribed;
    readonly List<Image> segmentPool = new List<Image>();
    readonly List<Image> arrowPool = new List<Image>();
    readonly List<Vector2> arc = new List<Vector2>();
    SteerTrajectoryState steer;
    bool active;

    static Sprite circleSprite;

    void OnEnable() => Bind(client);
    void OnDisable() => Bind(null);

    /// <summary>Point the controller at a client. This component lives on the shooting
    /// frame, which is toggled with the camera mode, so it is disabled and re-enabled
    /// routinely -- Bind must never drop the client reference, only the subscription.</summary>
    public void Bind(MujocoClient value)
    {
        if (subscribed) subscribed.StateUpdated -= Apply;
        subscribed = value;
        if (subscribed) subscribed.StateUpdated += Apply;
        else SetVisible(false);
    }

    void Apply(MujocoState state)
    {
        steer = state?.hero?.steerTrajectory;
        active = steer != null && steer.active && steer.points != null && steer.points.Length >= 2;
        if (!active)
        {
            SetVisible(false);
            return;
        }
        DrawGuides();
    }

    void DrawGuides()
    {
        if (!frameRoot) return;
        float travelled = PathLength();
        float t = Mathf.Clamp01(travelled / Mathf.Max(0.01f, saturatingDistanceM));
        float length = Mathf.Lerp(minArcLength, maxArcLength, t);
        Vector2 localTravel = LocalTravel();
        if (localTravel.sqrMagnitude < .001f)
        {
            SetVisible(false);
            return;
        }

        EnsurePool(segmentPool, 2 * ArcSamples, bandWidth, false);
        int segmentIndex = 0;
        Vector2 leftTip = BuildOmniGuide(leftAnchor, localTravel, length);
        segmentIndex = DrawSegments(segmentIndex, arc);
        Vector2 leftHeading = (arc[arc.Count - 1] - arc[arc.Count - 2]).normalized;
        Vector2 rightTip = BuildOmniGuide(rightAnchor, localTravel, length);
        segmentIndex = DrawSegments(segmentIndex, arc);
        Vector2 rightHeading = (arc[arc.Count - 1] - arc[arc.Count - 2]).normalized;
        DeactivateFrom(segmentPool, segmentIndex);

        if (showArrow) DrawPairedArrows(leftTip, leftHeading, rightTip, rightHeading);
        else Hide(arrowPool, 0, arrowPool.Count);
    }

    Vector2 BuildOmniGuide(Vector2 anchor, Vector2 localTravel, float requestedLength)
    {
        Vector2 direction = new Vector2(-localTravel.y, localTravel.x).normalized;
        bool combined = Mathf.Abs(localTravel.x) > .02f && Mathf.Abs(localTravel.y) > .02f;

        // Leave the whole arrow under the green bottom edge without flattening its final
        // segment; shortening preserves an upper-left/upper-right arrowhead direction.
        float length = requestedLength;
        if (direction.y > .001f)
            length = Mathf.Min(length, Mathf.Max(12f, (guideCeilingY - anchor.y) / direction.y));

        Vector2 end = anchor + direction * length;
        arc.Clear();
        if (!combined)
        {
            // A/D alone is deliberately just a clean horizontal arrow.
            for (int i = 0; i <= ArcSamples; i++)
                arc.Add(Vector2.Lerp(anchor, end, (float)i / ArcSamples));
            return end;
        }

        // Concavity mirrors the lateral command while retaining its forward/backward
        // half-plane: WA -> upper-right, WD -> upper-left, SA -> lower-right,
        // SD -> lower-left.
        Vector2 curveSide = new Vector2(Mathf.Sign(localTravel.y),
            Mathf.Sign(localTravel.x)).normalized;
        Vector2 control1 = anchor + direction * length * .24f
            + curveSide * combinationCurve * 2f;
        Vector2 control2 = end - direction * length * .30f;
        for (int i = 0; i <= ArcSamples; i++)
        {
            float u = (float)i / ArcSamples;
            float v = 1f - u;
            Vector2 point = v * v * v * anchor + 3f * v * v * u * control1
                + 3f * v * u * u * control2 + u * u * u * end;
            point.y = Mathf.Min(point.y, guideCeilingY);
            arc.Add(point);
        }
        return end;
    }

    int DrawSegments(int index, List<Vector2> points)
    {
        for (int i = 0; i < points.Count - 1; i++)
        {
            Image segment = segmentPool[index++];
            if (!segment.gameObject.activeSelf) segment.gameObject.SetActive(true);
            Stroke(segment, points[i], points[i + 1], bandWidth);
            segment.color = bandColor;
        }
        return index;
    }

    /// <summary>Predicted displacement in chassis coordinates: x is forward, y is left.</summary>
    Vector2 LocalTravel()
    {
        TrajectoryPoint first = steer.points[0], last = steer.points[steer.points.Length - 1];
        float dx = last.x - first.x, dy = last.y - first.y;
        float forwardX = 1f, forwardY = 0f;
        float leftX = 0f, leftY = 1f;
        if (steer.yawDeg != null && steer.yawDeg.Length > 0)
        {
            float radians = steer.yawDeg[0] * Mathf.Deg2Rad;
            forwardX = Mathf.Cos(radians);
            forwardY = Mathf.Sin(radians);
            leftX = -Mathf.Sin(radians);
            leftY = Mathf.Cos(radians);
        }
        return new Vector2(dx * forwardX + dy * forwardY, dx * leftX + dy * leftY);
    }

    float PathLength()
    {
        // Straight-line start-to-end: the fan only needs a magnitude, and summing 24
        // segments to get a slightly longer number buys nothing visible.
        TrajectoryPoint first = steer.points[0], last = steer.points[steer.points.Length - 1];
        return Mathf.Sqrt((last.x - first.x) * (last.x - first.x) + (last.y - first.y) * (last.y - first.y));
    }

    void DrawPairedArrows(Vector2 leftTip, Vector2 leftHeading,
        Vector2 rightTip, Vector2 rightHeading)
    {
        EnsurePool(arrowPool, 4, arrowWidth, false);
        for (int rail = 0; rail < 2; rail++)
        {
            Vector2 tip = rail == 0 ? leftTip : rightTip;
            Vector2 heading = rail == 0 ? leftHeading : rightHeading;
            Vector2 barb = heading * arrowLength;
            for (int side = 0; side < 2; side++)
            {
                Image image = arrowPool[rail * 2 + side];
                if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
                Stroke(image, tip,
                    tip - Rotate(barb, arrowSpreadDeg * (side == 0 ? 1f : -1f)), arrowWidth);
                image.color = bandColor;
            }
        }
        DeactivateFrom(arrowPool, 4);
    }

    void Stroke(Image image, Vector2 from, Vector2 to, float width)
    {
        Vector2 delta = to - from;
        image.rectTransform.anchoredPosition = (from + to) * .5f;
        image.rectTransform.sizeDelta = new Vector2(delta.magnitude, width);
        image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    static Vector2 Rotate(Vector2 vector, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
        return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
    }

    void EnsurePool(List<Image> pool, int count, float width, bool circle)
    {
        while (pool.Count < count)
        {
            var image = new GameObject(circle ? "Swing Joint" : "Swing Stroke").AddComponent<Image>();
            image.transform.SetParent(frameRoot, false);
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = Vector2.one * .5f;
            image.rectTransform.pivot = Vector2.one * .5f;
            image.raycastTarget = false;
            image.sprite = circle ? Circle() : null;
            image.color = bandColor;
            if (circle) image.rectTransform.sizeDelta = Vector2.one * jointDiameter;
            pool.Add(image);
        }
    }

    void SetVisible(bool value)
    {
        if (value) return;
        Hide(segmentPool, 0, segmentPool.Count);
        Hide(arrowPool, 0, arrowPool.Count);
    }

    /// <summary>Hands back the pooled images a shorter draw did not use, so they do not
    /// linger at a stale position from the previous frame.</summary>
    static void DeactivateFrom(List<Image> pool, int index)
    {
        for (int i = index; i < pool.Count; i++)
            if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    static void Hide(List<Image> pool, int index, int count)
    {
        for (int i = index; i < index + count && i < pool.Count; i++)
            if (pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    static Sprite Circle()
    {
        if (circleSprite) return circleSprite;
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float center = (size - 1) * .5f, radius = center;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
            pixels[y * size + x] = new Color32(255, 255, 255,
                (byte)Mathf.RoundToInt(Mathf.Clamp01(radius - distance + .5f) * 255f));
        }
        texture.SetPixels32(pixels);
        texture.Apply();
        circleSprite = Sprite.Create(texture, new Rect(0, 0, size, size), Vector2.one * .5f, 100f);
        return circleSprite;
    }
}
