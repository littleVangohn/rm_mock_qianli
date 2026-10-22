using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the group box: how wide this gun's rounds scatter at the reference range.
///
/// The number is MuJoCo's, not the client's.  The server fires every round with its own
/// draw on the two aim angles and on the muzzle speed, then measures the spread of those
/// same draws at the reference range -- so the box is a statistic of the shots actually
/// leaving the barrel, not a decoration that happens to sit near the crosshair.
///
/// It is drawn around the centre of the sight and does not follow the range: the group is
/// an angle, and an angle is a fixed size on screen however the gun is pointed.  How many
/// sigmas the box spans is the server's call (DISPERSION_SIGMAS); one sigma is about two
/// thirds of the rounds, two sigma about ninety five percent.
/// </summary>
public class DispersionBoxController : MonoBehaviour
{
    public MujocoClient client;
    public Camera operatorCamera;
    public RectTransform frameRoot;

    [Header("显示")]
    // Two pixels, not one: a one-pixel line is at the mercy of where it lands on the
    // pixel grid and of whatever scaling the display applies, and a box whose two long
    // edges survive while the two short ones wash out is worse than no box at all.
    public float lineWidth = 2f;
    // White, and no tint: the box is a size reference the eye measures against the
    // crosshair, so it has to read at a glance. An earlier cyan tint at 38% alpha was
    // invisible over the sky, which is worse than not drawing it at all.
    public Color boxColor = new Color(1f, 1f, 1f, .9f);

    Image top, bottom, left, right;
    MujocoClient subscribed;
    float lastHalfWidth = -1f, lastHalfHeight = -1f;

    void OnEnable() => Bind(client);
    void OnDisable() => Bind(null);

    /// <summary>Lives on the shooting frame, which the camera mode toggles, so binding
    /// must drop the subscription and never the reference.</summary>
    public void Bind(MujocoClient value)
    {
        if (subscribed) subscribed.StateUpdated -= Apply;
        subscribed = value;
        if (subscribed) subscribed.StateUpdated += Apply;
        else SetVisible(false);
    }

    void Apply(MujocoState state)
    {
        DispersionState dispersion = state?.aim?.dispersion;
        if (dispersion == null || dispersion.samples <= 0 || dispersion.sigmaWidthM <= 0f)
        {
            SetVisible(false);
            return;
        }
        float halfWidth = HalfExtent(dispersion.sigmaWidthM, dispersion.referenceDistanceM, dispersion.sigmas);
        float halfHeight = HalfExtent(dispersion.sigmaHeightM, dispersion.referenceDistanceM, dispersion.sigmas);
        if (halfWidth <= 0f || halfHeight <= 0f)
        {
            SetVisible(false);
            return;
        }
        SetVisible(true);
        Draw(halfWidth, halfHeight);
    }

    /// <summary>Half the box, in the frame's own pixels.
    ///
    /// A spread of w metres at range r subtends atan(w/r), and the frame is a rect whose
    /// half-height is exactly half the camera's vertical field of view -- so the angle
    /// maps to pixels through the same tan the projection uses.  No field-of-view figure
    /// is copied in here, and nothing assumes the window size.</summary>
    float HalfExtent(float sigmaM, float referenceM, float sigmas)
    {
        if (!frameRoot || !operatorCamera) return 0f;
        float halfAngle = Mathf.Atan(sigmas * sigmaM / Mathf.Max(referenceM, .01f));
        float halfHeight = frameRoot.rect.height * .5f / Mathf.Tan(operatorCamera.fieldOfView * .5f * Mathf.Deg2Rad);
        return Mathf.Tan(halfAngle) * halfHeight;
    }

    void Draw(float halfWidth, float halfHeight)
    {
        Ensure();
        if (Mathf.Approximately(halfWidth, lastHalfWidth) && Mathf.Approximately(halfHeight, lastHalfHeight)) return;
        lastHalfWidth = halfWidth;
        lastHalfHeight = halfHeight;
        Stroke(top, new Vector2(-halfWidth, halfHeight), new Vector2(halfWidth, halfHeight));
        Stroke(bottom, new Vector2(-halfWidth, -halfHeight), new Vector2(halfWidth, -halfHeight));
        Stroke(left, new Vector2(-halfWidth, -halfHeight), new Vector2(-halfWidth, halfHeight));
        Stroke(right, new Vector2(halfWidth, -halfHeight), new Vector2(halfWidth, halfHeight));
    }

    void Stroke(Image image, Vector2 from, Vector2 to)
    {
        // The rect only ever grows sideways, so a vertical edge has to be rotated onto
        // its own span. Without this the left and right edges come out as two more
        // horizontal bars lying across the centre of the sight.
        Vector2 delta = to - from;
        image.rectTransform.anchoredPosition = (from + to) * .5f;
        image.rectTransform.sizeDelta = new Vector2(delta.magnitude, lineWidth);
        image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    void Ensure()
    {
        if (top) return;
        top = Make("散布框 上");
        bottom = Make("散布框 下");
        left = Make("散布框 左");
        right = Make("散布框 右");
    }

    Image Make(string name)
    {
        var image = new GameObject(name).AddComponent<Image>();
        image.transform.SetParent(frameRoot, false);
        image.rectTransform.anchorMin = image.rectTransform.anchorMax = Vector2.one * .5f;
        image.rectTransform.pivot = Vector2.one * .5f;
        image.color = boxColor;
        image.raycastTarget = false;
        return image;
    }

    void SetVisible(bool value)
    {
        if (top) top.gameObject.SetActive(value);
        if (bottom) bottom.gameObject.SetActive(value);
        if (left) left.gameObject.SetActive(value);
        if (right) right.gameObject.SetActive(value);
    }
}
