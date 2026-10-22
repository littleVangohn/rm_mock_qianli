using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A fixed holdover ruler under the crosshair: one rung per range, five metres apart.
///
/// The rungs are where a round crosses each range, so putting the rung for 20 m on a
/// target 20 m away is the same as holding over by the right amount.  Nothing moves it:
/// the server solves it once in the muzzle's own frame at a level barrel, so the ruler
/// stays put while the gun is elevated, exactly as an etched reticle does.  The funnel
/// line is the live truth and the ruler is the reference it is read against; the two
/// drift apart by a few degrees during a steep lob, which is the price of a ruler that
/// does not crawl.
///
/// No numbers are drawn on it.  The spacing is fixed at RANGE_SCALE_MARKS_M in the
/// server, and a scale that is counted rather than read is the point.
/// </summary>
public class RangeScaleController : MonoBehaviour
{
    public MujocoClient client;
    public Camera operatorCamera;
    public RectTransform frameRoot;

    [Header("外观")]
    public float tickHalfWidth = 13f;
    public float tickWidth = 1.2f;
    public Color tickColor = new Color(1f, 1f, 1f, .55f);

    readonly List<Image> ticks = new List<Image>();
    MujocoClient subscribed;
    RangeMarkState[] marks;
    string lastSignature;

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
        RangeScaleState scale = state?.aim?.rangeScale;
        RangeMarkState[] incoming = scale?.marks;
        if (incoming == null || incoming.Length == 0)
        {
            marks = null;
            // Forget the signature too, so the next state that does carry a ladder is
            // drawn even if it looks exactly like the one that was up before.
            lastSignature = null;
            SetVisible(false);
            return;
        }
        // Constants on the wire, so this only ever redraws when the server's ballistics
        // change -- a new build, or a different muzzle speed.
        string signature = $"{incoming.Length}|{incoming[0].sightDeg:0.####}|{incoming[incoming.Length - 1].sightDeg:0.####}";
        if (signature == lastSignature) return;
        lastSignature = signature;
        marks = incoming;
        Rebuild();
    }

    void Rebuild()
    {
        if (marks == null || !frameRoot || !operatorCamera)
        {
            SetVisible(false);
            return;
        }
        float halfHeight = frameRoot.rect.height * .5f /
                           Mathf.Tan(operatorCamera.fieldOfView * .5f * Mathf.Deg2Rad);
        EnsurePool(marks.Length);
        SetVisible(true);
        for (int i = 0; i < marks.Length; i++)
        {
            // Every rung is drawn at the angle the round really appears at, measured
            // from the crosshair -- not shifted up so that the shortest one sits on the
            // centre.  The camera is parallel to the bore, so the round never crosses the
            // sight line; a ladder zeroed to the centre would be a ladder that tells the
            // shooter to aim 5 degrees low.  This way the ray from the eye through a rung
            // passes exactly through the round at that range, which is what makes putting
            // a rung on a target the same as hitting it.
            float angle = Mathf.Deg2Rad * marks[i].sightDeg;
            float y = -Mathf.Tan(angle) * halfHeight;
            Image tick = ticks[i];
            if (!tick.gameObject.activeSelf) tick.gameObject.SetActive(true);
            tick.rectTransform.anchoredPosition = new Vector2(0f, y);
            tick.rectTransform.sizeDelta = new Vector2(tickHalfWidth * 2f, tickWidth);
        }
        for (int i = marks.Length; i < ticks.Count; i++)
            if (ticks[i].gameObject.activeSelf) ticks[i].gameObject.SetActive(false);
    }

    void EnsurePool(int count)
    {
        while (ticks.Count < count)
        {
            var tick = new GameObject("表尺刻度").AddComponent<Image>();
            tick.transform.SetParent(frameRoot, false);
            tick.rectTransform.anchorMin = tick.rectTransform.anchorMax = Vector2.one * .5f;
            tick.rectTransform.pivot = Vector2.one * .5f;
            tick.color = tickColor;
            tick.raycastTarget = false;
            ticks.Add(tick);
        }
    }

    void SetVisible(bool value)
    {
        for (int i = 0; i < ticks.Count; i++)
            if (ticks[i].gameObject.activeSelf != value) ticks[i].gameObject.SetActive(value);
    }
}
