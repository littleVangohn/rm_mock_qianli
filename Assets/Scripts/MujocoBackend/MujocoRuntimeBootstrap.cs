using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using IngameDebugConsole;

namespace Pacific.Mujoco
{
    /// <summary>
    /// Legacy-physics build of the simulator: the original arena, drive model,
    /// shooting, referee rules and Netcode multiplayer run unchanged. Only the
    /// first-person operator UI is replaced by the official-client HUD. No
    /// MuJoCo backend is contacted, so nothing here waits for a TCP endpoint.
    /// </summary>
    public sealed class MujocoRuntimeBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SetCursorModeBeforeSceneStarts()
        {
            // Original gameplay is gated on this flag (FPV camera, drive,
            // shooter, spawn flow). It must stay false so the OSS simulator
            // keeps owning physics and controls.
            PacificSelectionState.SetMujocoMode(false);
            // Start with a normal desktop pointer. The bridge only captures it
            // after a vehicle has been spawned and the first-person view owns
            // the screen; menus and the welcome page always keep it visible.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallAlways()
        {
            var root = new GameObject("Pacific Legacy HUD");
            root.SetActive(false);
            root.AddComponent<MujocoRuntimeBootstrap>();
            root.SetActive(true);
            DontDestroyOnLoad(root);
        }

        private void Awake()
        {
            PacificSelectionState.SetMujocoMode(false);
            PacificSelectionState.Reset();
            SceneManager.sceneLoaded += OnModeSceneLoaded;
            // The official HUD path owns robot binding and the HUD; the original
            // selection UI still owns faction, role and spawn timing.
            gameObject.AddComponent<PacificOfficialUiBridge>();
            // Original menu/selection canvases are fixed-pixel; enlarge them
            // for fullscreen play while leaving the official HUD untouched.
            gameObject.AddComponent<LegacyUiScale>();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnModeSceneLoaded;
        }

        private void OnModeSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(ShowModeRulesWhenReady(scene));
        }

        private IEnumerator ShowModeRulesWhenReady(Scene scene)
        {
            yield return null;
            if (scene != SceneManager.GetActiveScene()) yield break;
            // Same rules pages as the MuJoCo build.
            MujocoModeRulesPanel.Show(scene);
            StartCoroutine(HideDeveloperConsole());
        }

        private IEnumerator HideDeveloperConsole()
        {
            // The OSS in-game log popup is a developer overlay, not part of
            // either the selection screen or the custom first-person HUD. It
            // can be created after the scene finishes loading, so keep hiding
            // it for a few seconds instead of only once.
            float until = Time.unscaledTime + 5f;
            do
            {
                foreach (DebugLogManager console in FindObjectsByType<DebugLogManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (console != null) console.gameObject.SetActive(false);
                yield return null;
            } while (Time.unscaledTime < until);
        }
    }
}
