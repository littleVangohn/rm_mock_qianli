using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// F10 leaves the current match and returns to the start screen. The session
/// is stopped first so the welcome scene owns an idle NetworkManager again.
/// </summary>
public sealed class MatchExitHotkey : MonoBehaviour
{
    const KeyCode ReturnKey = KeyCode.F10;
    static bool leaving;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var root = new GameObject("Match Exit Hotkey");
        DontDestroyOnLoad(root);
        root.AddComponent<MatchExitHotkey>();
    }

    private void Update()
    {
        if (leaving || !Input.GetKeyDown(ReturnKey)) return;
        if (SceneManager.GetActiveScene().name == "Welcome") return;
        leaving = true;
        StartCoroutine(ReturnToWelcome());
    }

    private IEnumerator ReturnToWelcome()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network != null && (network.IsClient || network.IsServer)) network.Shutdown();
        // MenuManager is marked DontDestroyOnLoad, so without this the reloaded
        // welcome scene would stack a second menu canvas on top of the old one.
        foreach (MenuManager menu in FindObjectsByType<MenuManager>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (menu != null) Destroy(menu.gameObject);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        // Let NGO finish despawning before the match scene goes away.
        yield return null;
        SceneManager.LoadScene("Welcome", LoadSceneMode.Single);
        leaving = false;
    }
}
