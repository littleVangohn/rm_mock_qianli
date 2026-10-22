using UnityEngine;

public class CameraModeController : MonoBehaviour
{
    public Camera operatorCamera;
    public Camera overviewCamera;
    public Camera ballisticsCamera;
    public int ActiveMode { get; private set; } = 1;
    public Camera ActiveCamera => ActiveMode == 2 ? overviewCamera : ActiveMode == 3 ? ballisticsCamera : operatorCamera;

    void Start() => Select(1);

    public void Select(int mode)
    {
        ActiveMode = Mathf.Clamp(mode, 1, 3);
        if (operatorCamera) operatorCamera.enabled = ActiveMode == 1;
        if (overviewCamera) overviewCamera.enabled = ActiveMode == 2;
        if (ballisticsCamera) ballisticsCamera.enabled = ActiveMode == 3;
    }
}
