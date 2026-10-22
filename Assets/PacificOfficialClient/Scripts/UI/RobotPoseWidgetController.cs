using UnityEngine;
using UnityEngine.UI;

public class RobotPoseWidgetController : MonoBehaviour
{
    public MujocoClient client;
    public RectTransform chassis;
    public RectTransform turret;
    public Text modeText;
    MujocoClient subscribedClient;

    void OnEnable() => Bind(client);
    void OnDisable() => Bind(null);

    public void Bind(MujocoClient value)
    {
        if (subscribedClient) subscribedClient.StateUpdated -= Apply;
        client = value;
        subscribedClient = value;
        if (subscribedClient) subscribedClient.StateUpdated += Apply;
    }

    /// <summary>
    /// Marker rotation for the chassis outline, drawn in the gimbal's frame.
    ///
    /// The operator sits in the gimbal, so the gimbal frame is the fixed one: the turret
    /// marker never turns and the chassis is drawn at its heading measured from the
    /// gimbal. Aim the gimbal 90 deg left and the chassis is still pointing where it was,
    /// which from the gimbal is 90 deg to the right -- so the marker swings right and
    /// eases back to centre as the chassis follows the gimbal round.
    /// 小陀螺 needs no special case -- the gimbal holds its world heading there, so the
    /// same subtraction leaves the chassis marker doing all of the turning.
    /// </summary>
    public static float ChassisMarkerAngle(float heroYawDeg, float turretWorldYawDeg)
        => heroYawDeg - turretWorldYawDeg;

    void Apply(MujocoState state)
    {
        if (state?.hero == null) return;
        if (chassis) chassis.localRotation = Quaternion.Euler(
            0f, 0f, ChassisMarkerAngle(state.hero.yawDeg, state.hero.turretWorldYawDeg));
        if (turret) turret.localRotation = Quaternion.identity;
        // The one thing the widget has to say in words: which of the two modes is live,
        // so the G toggle is visible without having to test whether C/V do anything.
        if (modeText)
        {
            bool gyro = state.controlMode == "gyro";
            modeText.text = gyro ? "小陀螺模式" : "跟随模式";
            modeText.color = gyro ? new Color(1f, .78f, .25f) : new Color(.55f, .85f, 1f);
        }
    }
}
