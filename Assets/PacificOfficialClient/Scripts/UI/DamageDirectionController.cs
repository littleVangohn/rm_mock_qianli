using UnityEngine;
using UnityEngine.UI;

public class DamageDirectionController : MonoBehaviour
{
    public MujocoClient client;
    public Image[] indicators;
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
        RobotMasterRobot robot = state?.robotMaster?.robots == null
            ? null
            : System.Array.Find(state.robotMaster.robots, value => value.id == 1);
        int active = -1;
        if (robot != null && robot.hasRecentHit)
            active = Mathf.RoundToInt(Mathf.Repeat(robot.hitDirectionDeg, 360f) / 90f) % 4;
        for (int i = 0; indicators != null && i < indicators.Length; i++)
            SetIndicator(indicators[i], i == active);
    }

    static void SetIndicator(Image image, bool active)
    {
        if (!image) return;
        image.color = active
            ? new Color(1f, 0.08f, 0.04f, 0.96f)
            : new Color(0.72f, 0.84f, 1f, 0.16f);
    }
}
