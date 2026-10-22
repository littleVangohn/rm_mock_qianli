using UnityEngine;

public class HorizonIndicatorController : MonoBehaviour
{
    public Camera sourceCamera;
    public RectTransform indicator;

    void LateUpdate()
    {
        if (!sourceCamera || !indicator) return;
        Vector3 horizontalForward = Vector3.ProjectOnPlane(sourceCamera.transform.forward, Vector3.up);
        if (horizontalForward.sqrMagnitude < 0.0001f)
        {
            indicator.gameObject.SetActive(false);
            return;
        }

        indicator.gameObject.SetActive(true);
        Vector3 horizonViewport = sourceCamera.WorldToViewportPoint(
            sourceCamera.transform.position + horizontalForward.normalized * 100f
        );
        float horizonY = horizonViewport.z > 0f ? Mathf.Clamp(horizonViewport.y, 0.08f, 0.92f) : 0.5f;
        indicator.anchorMin = indicator.anchorMax = new Vector2(0.92f, horizonY);
        indicator.anchoredPosition = Vector2.zero;

        Vector3 screenUp = sourceCamera.transform.InverseTransformDirection(Vector3.up);
        float roll = -Mathf.Atan2(screenUp.x, screenUp.y) * Mathf.Rad2Deg;
        indicator.localRotation = Quaternion.Euler(0f, 0f, roll);
    }
}
