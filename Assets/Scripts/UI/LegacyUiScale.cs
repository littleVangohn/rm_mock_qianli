using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Enlarges the original simulator's menu/selection canvases. They were
/// authored for a small window and keep a fixed pixel scale, so on a 1080p or
/// larger display they read as tiny; the official HUD has its own adaptive
/// canvas and is skipped here.
/// </summary>
public sealed class LegacyUiScale : MonoBehaviour
{
    private static readonly Vector2 Reference = new Vector2(960f, 540f);

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void Start() => StartCoroutine(Apply());

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => StartCoroutine(Apply());

    private IEnumerator Apply()
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        Pacific.Mujoco.PacificOfficialUiBridge bridge =
            FindFirstObjectByType<Pacific.Mujoco.PacificOfficialUiBridge>(FindObjectsInactive.Include);
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (canvas == null) continue;
            if (bridge != null && canvas.transform.IsChildOf(bridge.transform)) continue;
            // The custom loading screen is already a full-screen adaptive
            // canvas; rescaling it made it draw stuck at the bottom.
            if (canvas.GetComponentInParent<MujocoLoadingScreen>() != null) continue;
            if (canvas.name.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Reference;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.matchWidthOrHeight = 0.5f;
        }
    }
}
