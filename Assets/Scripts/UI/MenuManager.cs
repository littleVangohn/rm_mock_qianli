using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Unity.Netcode.Transports.UTP;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using TMPro;

public class MenuManager : MonoBehaviour {
    
    [SerializeField] private TMP_Dropdown SceneSelection;

    [SerializeField] private GameObject MainMenu;
    [SerializeField] private GameObject[] MenuList;

    // [SerializeField] private GameObject FirstLevelMenu;
    [SerializeField] private Button MultiplayerBtn;
    [SerializeField] private Button SettingBtn;
    [SerializeField] private Button ExitBtn;
    
    // [SerializeField] private GameObject MultiplayerMenu;
    [SerializeField] private Button HostBtn;
    [SerializeField] private Button ServerBtn;
    [SerializeField] private TMP_InputField ServerInfo;
    [SerializeField] private Button ClientBtn;
    [SerializeField] private Button ExitMultiplayerBtn;

    // Multiplayer Info
    [SerializeField] private GameObject MultiplayerInfo;
    [SerializeField] private TMP_Text ClientInfo;
    [SerializeField] private TMP_Text NetworkInfo;

    [SerializeField] private TMP_Text VersionInfo;

    void Awake()
    {
        // Application.targetFrameRate = 60;
    }

    void Start()
    {
        DontDestroyOnLoad(gameObject);
        
        VersionInfo.text = "Robomaster Simulater @ " + Application.version;

        // FirstLevelMenu
        MultiplayerBtn.onClick.AddListener(() => EnterMenu(1));

        // MultiplayerMenu
        ExitMultiplayerBtn.onClick.AddListener(() => EnterMenu(0));
        HostBtn.onClick.AddListener(() => StartMultiplayer(0));
        ServerBtn.onClick.AddListener(() => StartMultiplayer(1));
        ClientBtn.onClick.AddListener(() => StartMultiplayer(2));
        // MainMenu
        ExitBtn.onClick.AddListener(() => Application.Quit());
    }

    void FixedUpdate()
    {
        if(NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            ClientInfo.text = "ClientID: " + NetworkManager.Singleton.LocalClientId;    
            // NetworkInfo.text = "Ping: " + NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.Singleton.LocalClientId) + "ms";
        }
    }

    void EnterMenu(int index = 0)
    {
        for(int i = 0; i < MenuList.Length; i++)
        {
            if(i == index)
            {
                MenuList[i].SetActive(true);
            } else {
                MenuList[i].SetActive(false);
            }
        }
    }

    void StartMultiplayer(int mode = 0)
    {
        RegisterNestedNetworkPrefabs();

        switch(mode){
            case 0:
                NetworkManager.Singleton.StartHost();
                break;
            case 1:
                NetworkManager.Singleton.StartServer();
                break;
            case 2:
                ApplyClientAddress(ServerInfo != null ? ServerInfo.text : null);
                NetworkManager.Singleton.StartClient();
                break;
            default:
                Debug.LogError($"Unknown multiplayer mode {mode}");
                break;
        }

        if (NetworkManager.Singleton.IsServer)
        {
            var status = NetworkManager.Singleton.SceneManager.LoadScene(SceneSelection.captionText.text, LoadSceneMode.Single);
            
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogWarning($"Failed to load {SceneSelection.captionText.text} " +
                        $"with a {nameof(SceneEventProgressStatus)}: {status}");
            }
            
            hide();
        }

        if (NetworkManager.Singleton.IsClient)
        {
            MultiplayerInfo.SetActive(true);
            hide();
        }
    }

    // The roster robots keep their turrets as nested NetworkObjects. The server
    // spawns each turret as a separate network root (RefereeController does it
    // so the barrel can aim independently), but a nested prefab's hash is not
    // in the NetworkPrefabsList. Clients therefore cannot instantiate it: NGO
    // drops the spawn, every NetworkTransform message for that object throws,
    // and the client robot ends up without working weapons. Register every
    // nested NetworkObject as a top-level runtime prefab before Netcode starts.
    // Both host and client run this exact scan, so ForceSamePrefabs still sees
    // an identical set on both ends.
    private static void RegisterNestedNetworkPrefabs()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || manager.IsListening || manager.NetworkConfig == null) return;
        NetworkPrefabs prefabs = manager.NetworkConfig.Prefabs;
        if (prefabs == null) return;

        // Plain for loops keep this helper free of compiler-generated
        // try/finally blocks, which keeps it easy to transplant into the
        // shipped player assembly with Mono.Cecil.
        var additions = new List<GameObject>();
        var seen = new HashSet<uint>();
        for (int e = 0; e < prefabs.Prefabs.Count; e++)
        {
            NetworkPrefab entry = prefabs.Prefabs[e];
            if (entry == null || entry.Prefab == null) continue;
            NetworkObject[] children = entry.Prefab.GetComponentsInChildren<NetworkObject>(true);
            for (int c = 0; c < children.Length; c++)
            {
                NetworkObject child = children[c];
                if (child == null || child.PrefabIdHash == 0) continue;
                uint hash = child.PrefabIdHash;
                if (prefabs.NetworkPrefabOverrideLinks.ContainsKey(hash)) continue;
                if (!seen.Add(hash)) continue;
                additions.Add(child.gameObject);
            }
        }

        // Training fires from the Resources copies of the bullet prefabs; those
        // are distinct assets from the registered list entries. Registering the
        // hashes keeps them resolvable if the same rounds ever spawn in a match.
        string[] bulletNames = { "Bullet-17mm", "Bullet-42mm" };
        for (int b = 0; b < bulletNames.Length; b++)
        {
            GameObject bullet = Resources.Load<GameObject>(bulletNames[b]);
            if (bullet == null) continue;
            NetworkObject networkObject = bullet.GetComponent<NetworkObject>();
            if (networkObject == null || networkObject.PrefabIdHash == 0) continue;
            if (prefabs.NetworkPrefabOverrideLinks.ContainsKey(networkObject.PrefabIdHash)) continue;
            if (!seen.Add(networkObject.PrefabIdHash)) continue;
            additions.Add(bullet);
        }

        for (int p = 0; p < additions.Count; p++)
        {
            GameObject prefab = additions[p];
            manager.AddNetworkPrefab(prefab);
            Debug.Log($"[Multiplayer] Registered nested network prefab {prefab.name} " +
                      $"({prefab.GetComponent<NetworkObject>().PrefabIdHash})");
        }
    }

    // Accepts "host", "host:port" or "[ipv6]:port"; an empty field keeps the
    // 127.0.0.1 default baked into the Welcome scene.
    private static void ApplyClientAddress(string serverInfo)
    {
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null) return;

        string address = string.IsNullOrWhiteSpace(serverInfo) ? string.Empty : serverInfo.Trim();

        // Chinese IMEs turn ':' into the full-width '：' (and occasionally fold
        // digits and dots too). Normalize before parsing so a pasted or typed
        // "192.168.1.5：19231" still works.
        if (address.Length > 0)
        {
            char[] chars = address.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '：') chars[i] = ':';
                else if (c == '．') chars[i] = '.';
                else if (c >= '０' && c <= '９') chars[i] = (char)('0' + (c - '０'));
            }
            address = new string(chars);
        }

        ushort port = 0;
        if (address.Length > 0 && address[0] == '[')
        {
            int close = address.IndexOf(']');
            if (close > 0)
            {
                string tail = address.Substring(close + 1);
                if (tail.StartsWith(":") && ushort.TryParse(tail.Substring(1), out ushort bracketPort))
                    port = bracketPort;
                address = address.Substring(1, close - 1);
            }
        }
        else
        {
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon &&
                ushort.TryParse(address.Substring(colon + 1), out ushort parsedPort))
            {
                port = parsedPort;
                address = address.Substring(0, colon);
            }
        }

        if (address.Length == 0) address = transport.ConnectionData.Address;
        transport.ConnectionData.Address = address;
        if (port != 0) transport.ConnectionData.Port = port;
        Debug.Log($"[Multiplayer] Joining {address}:{transport.ConnectionData.Port}");
    }

    public void hide()
    {
        MainMenu.SetActive(false);
    }

    public void show()
    {
        MainMenu.SetActive(true);
    }

}
