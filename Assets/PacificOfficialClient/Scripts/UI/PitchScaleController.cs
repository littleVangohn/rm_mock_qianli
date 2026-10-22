using UnityEngine;
using UnityEngine.UI;

/// <summary>Moves the transparent pitch-scale slider from live MuJoCo barrel pitch.</summary>
public sealed class PitchScaleController : MonoBehaviour
{
    public MujocoClient client;
    public RectTransform slider;
    public Text valueText;
    public float minimumDeg = -10f;
    public float maximumDeg = 70f;
    public float travel = 236f;
    MujocoClient subscribed;

    void OnEnable() => Bind(client);
    void OnDisable() => Bind(null);
    public void Bind(MujocoClient value)
    {
        if (subscribed) subscribed.StateUpdated -= Apply;
        client=value; subscribed=value;
        if (subscribed) subscribed.StateUpdated += Apply;
    }
    void Apply(MujocoState state)
    {
        if (state?.hero == null) return;
        float pitch=Mathf.Clamp(state.hero.barrelPitchDeg,minimumDeg,maximumDeg);
        float y=Mathf.Lerp(-travel*.5f,travel*.5f,Mathf.InverseLerp(minimumDeg,maximumDeg,pitch));
        if (slider) slider.anchoredPosition=new Vector2(slider.anchoredPosition.x,y);
        if (valueText) valueText.text=$"{pitch:+0.0;-0.0;0.0}°";
    }
}
