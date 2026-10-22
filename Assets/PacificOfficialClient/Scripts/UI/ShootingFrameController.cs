using UnityEngine;
using UnityEngine.UI;

public class ShootingFrameController : MonoBehaviour
{
    public MujocoClient client;
    public Image[] aimLines;
    public Image[] armorSegments;
    public Image healthFill;
    public Text healthText;
    public Text statusText;
    public Text poseText;
    readonly Color ready = new Color(0.62f, 1f, 0.16f, 0.95f);
    readonly Color waiting = new Color(0.62f, 1f, 0.16f, 0.95f);
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

    void Apply(MujocoState state)
    {
        if (state == null) return;
        Color frameColor = state.aim != null && state.aim.ready ? ready : waiting;
        if (aimLines != null) foreach (Image line in aimLines) if (line) line.color = frameColor;

        if (poseText && state.hero != null)
        {
            float x = state.hero.positionM != null && state.hero.positionM.Length > 0 ? state.hero.positionM[0] : 0f;
            float y = state.hero.positionM != null && state.hero.positionM.Length > 1 ? state.hero.positionM[1] : 0f;
            poseText.text = $"BODY {state.hero.yawDeg:000.0}  TURRET {state.hero.turretWorldYawDeg:000.0}\nX {x:+00.000;-00.000}  Y {y:+00.000;-00.000}";
        }

        RobotMasterRobot robot = state.robotMaster?.robots == null
            ? null
            : System.Array.Find(state.robotMaster.robots, value => value.id == 1);
        float health = robot != null && robot.maxHp > 0 ? Mathf.Clamp01((float)robot.hp / robot.maxHp) : 0f;
        if (healthFill) healthFill.fillAmount = health;
        if (healthText) healthText.text = robot == null ? "HP -- / --" : $"HP {robot.hp} / {robot.maxHp}";
        if (statusText) statusText.text = robot == null
            ? "AMMO --   ENERGY --\nBUFF --"
            : $"AMMO {robot.ammo}   ENERGY {robot.energy}\nBUFF {BuffText(robot.buffs)}";
        if (armorSegments != null)
        {
            foreach (Image segment in armorSegments)
            {
                if (!segment) continue;
                segment.color = robot == null
                    ? new Color(0.35f, 0.42f, 0.46f, 0.75f)
                    : Color.Lerp(new Color(1f, 0.08f, 0.05f, 0.95f), new Color(0.15f, 0.85f, 1f, 0.95f), health);
            }
        }
    }

    static string BuffText(string[] buffs) => buffs == null || buffs.Length == 0 ? "NONE" : string.Join(" ", buffs).ToUpperInvariant();
}
