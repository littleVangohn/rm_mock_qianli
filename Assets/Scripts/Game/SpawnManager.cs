using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using Unity.Netcode.Transports.UTP;

public class SpawnManager : NetworkBehaviour
{
    [SerializeField] private GameManager gameManager;
    
    public GameObject[] prefabList;

    [SerializeField] private Transform[] redSpawnPoints;
    [SerializeField] private Transform[] blueSpawnPoints;
    private readonly Dictionary<int, NetworkObject> mujocoRoster = new Dictionary<int, NetworkObject>();

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsServer) return;
        if (PacificSelectionState.MujocoMode) BuildMujocoRoster();
        else if (PacificSelectionState.LegacyRoster) BuildLegacyRoster();
    }

    /// <summary>
    /// Legacy build: instantiate both teams' full seven-vehicle roster from the
    /// scene's own prefabs and spawn points. Unlike the MuJoCo roster these keep
    /// their rigidbodies and collisions; only the selected vehicle's drive/FPV
    /// scripts are enabled, so pressing P can take over another car without
    /// spawning a duplicate.
    /// </summary>
    private void BuildLegacyRoster()
    {
        int count = Mathf.Min(7, prefabList.Length - 1, redSpawnPoints.Length, blueSpawnPoints.Length);
        int robotCount = Mathf.Min(6, count);   // slot 6 is the sentry marker
        // RMUC2024's serialized list repeats SpawnPoint-R3 for two slots. Using
        // the markers as-is therefore spawned two cars on top of each other and
        // shifted the rest off their authored spots (one row even ran through
        // the base wall). Keep the markers unique and use them verbatim.
        Transform[] redSlots = LegacySpawnSlots(redSpawnPoints, robotCount);
        Transform[] blueSlots = LegacySpawnSlots(blueSpawnPoints, robotCount);
        for (int index = 0; index < robotCount; index++)
        {
            AddLegacyRosterRobot(1, index, index < redSlots.Length ? redSlots[index] : null);
            AddLegacyRosterRobot(2, index, index < blueSlots.Length ? blueSlots[index] : null);
        }
        if (count > 6)
        {
            AddLegacyRosterRobot(1, 6, redSpawnPoints[6]);
            AddLegacyRosterRobot(2, 6, blueSpawnPoints[6]);
        }
        Debug.Log($"[LegacyRoster] Spawned {mujocoRoster.Count} vehicles (7v7) on the authored spawn points.");
        // Nothing is user-controlled yet; silence every barrel, including the
        // scene-set sentries that otherwise keep firing on their own.
        LegacyRoster.Select(0);
    }

    /// <summary>
    /// Authored markers, without the duplicate entries a scene may serialize
    /// for multi-client spawns. The roster places every car at once, so two
    /// cars sharing a marker would start inside each other.
    /// </summary>
    private static Transform[] LegacySpawnSlots(Transform[] points, int count)
    {
        var slots = new List<Transform>(count);
        for (int i = 0; i < points.Length && slots.Count < count; i++)
        {
            Transform point = points[i];
            if (point == null) continue;
            bool duplicate = false;
            foreach (Transform slot in slots)
                if ((slot.position - point.position).sqrMagnitude < 25f) { duplicate = true; break; }
            if (!duplicate) slots.Add(point);
        }
        return slots.ToArray();
    }

    private void AddLegacyRosterRobot(int role, int index, Transform point)
    {
        int actualId = index + (role == 2 ? 21 : 1);
        if (mujocoRoster.ContainsKey(actualId) || point == null) return;
        // RMUC scenes already contain rule-managed scene sentries. Reuse them
        // instead of spawning a second sentry at the roster point. They can be
        // spawned by NGO a moment after the SpawnManager itself, so adopt them
        // asynchronously rather than creating a moving duplicate here.
        if (index == 6)
        {
            int sentryPrefab = role == 2 ? actualId - 20 : actualId;
            if (!TryAdoptSceneSentry(actualId))
                StartCoroutine(AdoptSceneSentryWhenReady(actualId, point, sentryPrefab));
            return;
        }
        int prefabIndex = role == 2 ? actualId - 20 : actualId;
        if (prefabIndex < 1 || prefabIndex >= prefabList.Length || prefabList[prefabIndex] == null) return;
        Vector3 placed = point.position;
        for (int attempt = 0; attempt < 6 && LegacyRoster.OverlapsVehicle(placed, point.rotation); attempt++)
            placed += Vector3.forward * 22f;
        GameObject player = Instantiate(prefabList[prefabIndex], placed, point.rotation);
        RefereeController referee = player.GetComponent<RefereeController>();
        NetworkObject vehicle = player.GetComponent<NetworkObject>();
        if (referee == null || vehicle == null) { Destroy(player); return; }
        referee.spawnPoint = point;
        referee.ConfigureSpawnIdentity(actualId, role == 2 ? Faction.Blue : Faction.Red);
        referee.SpawnMujocoVehicle(vehicle);   // legacy branch: plain Spawn()
        LegacyRoster.InitializeAmmo(referee);
        mujocoRoster.Add(actualId, vehicle);
        LegacyRoster.Prepare(referee);
    }

    private bool TryAdoptSceneSentry(int actualId)
    {
        if (mujocoRoster.ContainsKey(actualId)) return true;
        foreach (RefereeController candidate in FindObjectsByType<RefereeController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            NetworkObject sceneObject = candidate != null ? candidate.GetComponent<NetworkObject>() : null;
            if (sceneObject == null || sceneObject.IsSceneObject != true || !sceneObject.IsSpawned) continue;
            if (candidate.RobotID.Value != actualId) continue;
            mujocoRoster.Add(actualId, sceneObject);
            LegacyRoster.Prepare(candidate);
            Debug.Log($"[LegacyRoster] Adopted scene sentry {actualId}.");
            return true;
        }
        return false;
    }

    private IEnumerator AdoptSceneSentryWhenReady(int actualId, Transform point, int prefabIndex)
    {
        float deadline = Time.unscaledTime + 5f;
        while (Time.unscaledTime < deadline)
        {
            if (TryAdoptSceneSentry(actualId)) yield break;
            yield return null;
        }
        // RMUC2023 has no scene-placed sentry bodies; fall back to the roster
        // prefab so that mode also starts with a full seven per side.
        if (point == null || prefabIndex < 1 || prefabIndex >= prefabList.Length ||
            prefabList[prefabIndex] == null)
        {
            Debug.LogWarning($"[LegacyRoster] No sentry available for slot {actualId}.");
            yield break;
        }
        GameObject player = Instantiate(prefabList[prefabIndex], point.position, point.rotation);
        RefereeController referee = player.GetComponent<RefereeController>();
        NetworkObject vehicle = player.GetComponent<NetworkObject>();
        if (referee == null || vehicle == null) { Destroy(player); yield break; }
        referee.spawnPoint = point;
        referee.ConfigureSpawnIdentity(actualId, actualId >= 21 ? Faction.Blue : Faction.Red);
        referee.SpawnMujocoVehicle(vehicle);
        mujocoRoster[actualId] = vehicle;
        LegacyRoster.Prepare(referee);
        Debug.Log($"[LegacyRoster] Spawned roster sentry {actualId} (no scene sentry in this mode).");
    }

    private void BuildMujocoRoster()
    {
        int count = Mathf.Min(7, prefabList.Length - 1, redSpawnPoints.Length, blueSpawnPoints.Length);
        for (int id = 0; id < count; id++)
        {
            AddRosterRobot(1, id, redSpawnPoints[id]);
            AddRosterRobot(2, id, blueSpawnPoints[id]);
        }
        Debug.Log($"[SpawnManager] MuJoCo roster ready: {mujocoRoster.Count} existing vehicles; Spawn/P select rather than instantiate.");
    }

    private void AddRosterRobot(int role, int index, Transform point)
    {
        int actualId = index + (role == 2 ? 21 : 1);
        if (mujocoRoster.ContainsKey(actualId) || point == null) return;
        // RMUC2024 contains rule-managed scene sentries; RMUC2023 only has
        // similarly named UI labels. Reuse real scene NetworkObjects when
        // present, otherwise create the one missing roster model below.
        if (index == 6)
        {
            RefereeController existing = null;
            foreach (RefereeController candidate in FindObjectsByType<RefereeController>(FindObjectsInactive.Include,
                FindObjectsSortMode.None))
            {
                NetworkObject sceneObject = candidate.GetComponent<NetworkObject>();
                if (candidate.RobotID.Value == actualId && sceneObject != null && sceneObject.IsSceneObject == true)
                {
                    existing = candidate;
                    break;
                }
            }
            NetworkObject sceneVehicle = existing != null ? existing.GetComponent<NetworkObject>() : null;
            if (sceneVehicle != null)
            {
                mujocoRoster.Add(actualId, sceneVehicle);
                DisableLegacyDrive(existing.gameObject);
                return;
            }
        }
        if (prefabList[index + 1] == null) return;
        GameObject player = Instantiate(prefabList[index + 1], point.position, point.rotation);
        RefereeController referee = player.GetComponent<RefereeController>();
        NetworkObject vehicle = player.GetComponent<NetworkObject>();
        if (referee == null || vehicle == null)
        {
            Destroy(player);
            return;
        }
        referee.spawnPoint = point;
        // NetworkVariables are assigned from RefereeController.OnNetworkSpawn,
        // after NGO has associated them with this vehicle's NetworkBehaviour.
        referee.ConfigureSpawnIdentity(actualId, role == 2 ? Faction.Blue : Faction.Red);
        referee.SpawnMujocoVehicle(vehicle);
        mujocoRoster.Add(actualId, vehicle);
        DisableLegacyDrive(player);
    }

    private static void DisableLegacyDrive(GameObject player)
    {
        RobotController controller = player.GetComponent<RobotController>();
        if (controller != null) controller.enabled = false;
        foreach (WheelController wheel in player.GetComponentsInChildren<WheelController>(true)) wheel.enabled = false;
        foreach (Rigidbody body in player.GetComponentsInChildren<Rigidbody>(true))
        {
            body.isKinematic = true;
            body.detectCollisions = false;
        }
        FPVController fpv = player.GetComponentInChildren<FPVController>(true);
        if (fpv != null)
        {
            fpv.enabled = false;
            // Roster cars still carry the source prefab's local FPV canvas.
            // It must not appear as stray world-space labels over our HUD.
            GameObject legacyUi = typeof(FPVController).GetField("PlayerUI",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fpv) as GameObject;
            if (legacyUi != null) legacyUi.SetActive(false);
        }
        foreach (AudioListener listener in player.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
    }

    void Start()
    {

    }

    [ServerRpc(RequireOwnership = false)]
    public void SpawnServerRpc(int role, int id, ServerRpcParams serverRpcParams = default)
    {
        var clientId = serverRpcParams.Receive.SenderClientId;
        if ((PacificSelectionState.MujocoMode || PacificSelectionState.LegacyRoster) && (role == 1 || role == 2))
        {
            if (SelectRosterRobotForClient(role, id, clientId)) return;
            if (PacificSelectionState.MujocoMode)
            {
                Debug.LogError($"[SpawnManager] MuJoCo roster slot {role}/{id} is unavailable; refusing to instantiate a duplicate vehicle.");
                return;
            }
        }
        SpawnForClient(role, id, clientId);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SelectRosterRobotServerRpc(int role, int id, ServerRpcParams serverRpcParams = default)
    {
        if (!PacificSelectionState.MujocoMode && !PacificSelectionState.LegacyRoster) return;
        SelectRosterRobotForClient(role, id, serverRpcParams.Receive.SenderClientId);
    }

    private bool SelectRosterRobotForClient(int role, int id, ulong clientId)
    {
        if ((role != 1 && role != 2) || id < 0 || id >= 7) return false;
        int actualId = id + (role == 2 ? 21 : 1);
        if (!mujocoRoster.TryGetValue(actualId, out NetworkObject vehicle) || vehicle == null || !vehicle.IsSpawned)
            return false;
        foreach (NetworkObject member in mujocoRoster.Values)
            if (member != null && member.IsSpawned && member.OwnerClientId == clientId && member != vehicle && clientId != NetworkManager.ServerClientId)
                member.RemoveOwnership();
        if (vehicle.OwnerClientId != clientId) vehicle.ChangeOwnership(clientId);
        if (PacificSelectionState.LegacyRoster) LegacyRoster.Select(actualId);
        Debug.Log($"[SpawnManager] selected existing roster robot {actualId} for client {clientId}");
        return true;
    }

    private void SpawnForClient(int role, int id, ulong clientId)
    {
        GameObject player;
        RefereeController referee;

        Debug.Log($"[SpawnManager] clientID: {clientId}");

        // role: 0-observer 1-red 2-blue 3-referee
        switch(role)
        {
            case 1:
                player = Instantiate(prefabList[id+1], redSpawnPoints[id].position, redSpawnPoints[id].rotation);
                referee = player.GetComponent<RefereeController>();
                if (referee != null) referee.spawnPoint = redSpawnPoints[id];
                referee.RobotID.Value = id + 1;
                referee.faction.Value = Faction.Red;
                break;
            case 2:
                player = Instantiate(prefabList[id+1], blueSpawnPoints[id].position, blueSpawnPoints[id].rotation);
                referee = player.GetComponent<RefereeController>();
                if (referee != null) referee.spawnPoint = blueSpawnPoints[id];
                referee.RobotID.Value = id + 21;
                referee.faction.Value = Faction.Blue;
                break;
            case 3:
            case 0:
            default:
                player = Instantiate(prefabList[0], Vector3.right * -110 + Vector3.up * 5, Quaternion.identity);
                break;
        }

        player.GetComponent<NetworkObject>().SpawnWithOwnership(clientId);

    }
}
