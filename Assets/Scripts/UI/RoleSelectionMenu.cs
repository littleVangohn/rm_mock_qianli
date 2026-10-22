using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

public class RoleSelectionMenu : MonoBehaviour
{
    private readonly Dictionary<int, NetworkObject> mujocoRoster = new Dictionary<int, NetworkObject>();
    public IEnumerable<int> MujocoRosterIds => mujocoRoster.Keys;

    // The legacy cursor policy reads this so the spawn screen keeps a free,
    // visible pointer even while the server-owned roster cars exist.
    public static int OpenMenus { get; private set; }

    void OnEnable() => OpenMenus++;
    void OnDisable() => OpenMenus = Mathf.Max(0, OpenMenus - 1);

    public bool SelectMujocoRobot(int actualId)
    {
        if (!PacificSelectionState.MujocoMode || !mujocoRoster.TryGetValue(actualId, out NetworkObject vehicle) ||
            vehicle == null || !vehicle.IsSpawned || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return false;
        ulong clientId = NetworkManager.Singleton.LocalClientId;
        if (vehicle.OwnerClientId != clientId) vehicle.ChangeOwnership(clientId);
        PacificSelectionState.CompleteActual(actualId, actualId >= 20 ? 1 : 0);
        Debug.Log($"[RoleSelection] selected existing Training Ground robot {actualId}");
        return true;
    }
    public TMP_Dropdown teamDropdown;
    public TMP_Dropdown roleDropdown;

    public List<NetworkPrefabsList> teamList;

    public Transform spawnPoint;

    public void Start()
    {
        UpdateTeamList();
        UpdateRoleList();
    }

    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            gameObject.SetActive(gameObject.activeSelf ? false : true);
        }
    }

    public void UpdateTeamList()
    {
        teamDropdown.ClearOptions();
        List<string> teamNames = new List<string>();
        foreach (NetworkPrefabsList team in teamList)
        {
            teamNames.Add(team.name);
        }
        teamDropdown.AddOptions(teamNames);
    }

    public void UpdateRoleList()
    {
        roleDropdown.ClearOptions();
        List<string> roleNames = new List<string>();
        foreach (NetworkPrefabsList team in teamList)
        {
            if (team.name == teamDropdown.options[teamDropdown.value].text)
            {
                foreach (NetworkPrefab role in team.PrefabList)
                {
                    roleNames.Add(role.Prefab.name);
                }
            }
        }
        roleDropdown.AddOptions(roleNames);
    }

    public void SpawnPlayer()
    {
        int teamIndex = teamDropdown.value;
        int roleIndex = roleDropdown.value;
        if (teamIndex < 0 || teamIndex >= teamList.Count || roleIndex < 0 || roleIndex >= teamList[teamIndex].PrefabList.Count)
        {
            Debug.LogError($"[RoleSelection] Invalid team/role selection: team={teamIndex}, role={roleIndex}");
            return;
        }

        NetworkManager network = NetworkManager.Singleton;
        if (PacificSelectionState.MujocoMode && (network == null || !network.IsListening))
        {
            if (network == null || !network.StartHost())
            {
                Debug.LogError("[RoleSelection] Could not start local Netcode host; selected robot was not spawned.");
                return;
            }
        }
        if (network != null && network.IsListening && !network.IsServer)
        {
            Debug.LogError("[RoleSelection] Connected client must request its robot spawn from the server; local-only spawn is not controllable.");
            return;
        }

        if (PacificSelectionState.MujocoMode)
        {
            BuildMujocoRoster(teamIndex);
            int chosenId = RoleRobotId(roleIndex, teamList[teamIndex].PrefabList[roleIndex].Prefab.name,
                teamList[teamIndex].name.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!SelectMujocoRobot(chosenId))
            {
                Debug.LogError($"[RoleSelection] Training Ground robot {chosenId} is not available.");
                return;
            }
            gameObject.SetActive(false);
            return;
        }

        // Training is a driving range: spawn only the vehicle the operator
        // picked. Other roster slots are created on demand when the official
        // P panel switches to them (EnsureLegacyVehicle).
        ResolveLegacySpawn(out Vector3 spawnPosition, out Quaternion spawnRotation);
        GameObject player = Instantiate(teamList[teamIndex].PrefabList[roleIndex].Prefab, spawnPosition, spawnRotation);
        RefereeController referee = player.GetComponent<RefereeController>();
        if (referee == null)
        {
            Debug.LogError("[RoleSelection] Selected prefab has no RefereeController; it cannot be controlled as a robot.");
            Destroy(player);
            return;
        }

        // Training-ground team lists are asset-configured. Derive allegiance
        // from the selected prefab team name rather than assuming team index 1.
        string teamName = teamList[teamIndex].name;
        bool blue = teamName.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0;
        bool red = teamName.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0;
        // The checked-in Training Ground scene has one ARTINX2023 prefab list,
        // not separate faction lists. Treat that explicit single-list setup as
        // Red; if faction lists are added later, their names select the side.
        if (!blue && !red && teamList.Count == 1) red = true;
        if (!red && !blue)
        {
            Debug.LogError($"[RoleSelection] Team '{teamName}' is not identified as Red or Blue; refusing to bind a robot.");
            Destroy(player);
            return;
        }
        int actualRobotId = (blue ? 21 : 1) + roleIndex;
        PacificSelectionState.CompleteActual(actualRobotId, blue ? 1 : 0);

        referee.spawnPoint = spawnPoint;
        referee.ConfigureSpawnIdentity(actualRobotId, blue ? Faction.Blue : Faction.Red);
        NetworkObject playerNetworkObject = player.GetComponent<NetworkObject>();
        if (playerNetworkObject == null)
        {
            Debug.LogError("[RoleSelection] Selected robot prefab has no root NetworkObject; cannot bind control or referee state.");
            PacificSelectionState.Reset();
            Destroy(player);
            return;
        }

        // Child shooter objects must be declared separate roots before NGO's
        // initial scan of the dynamic robot prefab.
        // Publish first so NetworkVariables have their NetworkBehaviour and
        // server write permissions before assigning the chosen role identity.
        if (network != null && network.IsListening && network.IsServer)
            referee.SpawnMujocoVehicle(playerNetworkObject, network.LocalClientId);

        // Training builds its roster with no car selected, then spawns only the
        // picked one. Bind it now so LegacyRoster keeps this car's shooter card
        // visible and every parked car's card hidden, matching the range HUD.
        LegacyRoster.Select(actualRobotId);

        gameObject.SetActive(false);
    }

    /// <summary>
    /// Pose used for every Training Ground vehicle, whether it comes from the
    /// spawn screen or from an on-demand P-panel switch. The authored marker
    /// sits in a corner facing a wall and the field centre overlaps the scene
    /// sentry, so probe a few open spots and pick the first clear one.
    /// </summary>
    public bool ResolveLegacySpawn(out Vector3 spawnPosition, out Quaternion spawnRotation,
        RefereeController ignore = null)
    {
        if (spawnPoint == null)
        {
            spawnPosition = Vector3.zero;
            spawnRotation = Quaternion.identity;
            return false;
        }
        spawnPosition = spawnPoint.position;
        spawnRotation = spawnPoint.rotation;
        if (SceneManager.GetActiveScene().name != "Training Ground") return true;

        GameObject field = GameObject.Find("Field");
        if (field == null) return true;
        // WheelController only applies power while it is touching a collider
        // tagged Ground. The training field never got that tag, so the wheels
        // spun with zero traction and WASD looked dead. Tag the whole field
        // (floor and walls) as ground.
        foreach (Transform part in field.GetComponentsInChildren<Transform>(true))
            part.gameObject.tag = "Ground";
        float pivotOffset = LegacyRoster.PivotOffsetAt(spawnPoint);
        Vector3[] candidates =
        {
            new Vector3(30f, spawnPoint.position.y, -25f),
            new Vector3(60f, spawnPoint.position.y, -20f),
            new Vector3(20f, spawnPoint.position.y, 20f),
            new Vector3(60f, spawnPoint.position.y, 20f),
            spawnPoint.position,
        };
        spawnRotation = Quaternion.Euler(0f, 90f, 0f);
        spawnPosition = LegacyRoster.ResolveGroundSpawn(candidates[0], spawnRotation, pivotOffset);
        // A vehicle being moved to the spawn is slid sideways off a parked car
        // by its caller; searching the fallback candidates here would send the
        // second car to a distant corner instead of the neighbouring slot.
        if (ignore == null)
        {
            foreach (Vector3 candidate in candidates)
            {
                Vector3 placed = LegacyRoster.ResolveGroundSpawn(candidate, spawnRotation, pivotOffset);
                if (SpawnAreaClear(placed, spawnRotation, ignore))
                {
                    spawnPosition = placed;
                    break;
                }
            }
        }
        // Face the arena centre instead of the authored corner wall.
        // The chassis forward is local +X (the same axis W drives on).
        Vector3 toCentre = new Vector3(-spawnPosition.x, 0f, -spawnPosition.z);
        if (toCentre.sqrMagnitude > 1f)
        {
            float centreYaw = Mathf.Atan2(toCentre.x, toCentre.z) * Mathf.Rad2Deg;
            spawnRotation = Quaternion.Euler(0f, centreYaw - 90f, 0f);
        }
        Debug.Log($"[RoleSelection] Training spawn {spawnPosition} yaw={spawnRotation.eulerAngles.y:F1}");
        return true;
    }

    /// <summary>
    /// Creates a Training Ground vehicle on demand. The spawn screen only
    /// instantiates the single picked prefab, so the official P panel used to
    /// list just the cars already in the scene. Switching to any other roster
    /// slot now builds that vehicle at the shared spawn pose; it stays hidden
    /// until LegacyRoster.Select enables the selected slot.
    /// </summary>
    public bool EnsureLegacyVehicle(int actualId)
    {
        if (PacificSelectionState.MujocoMode || actualId <= 0) return false;
        int displayId = actualId >= 20 ? actualId - 20 : actualId;
        if (displayId < 1 || displayId > 7) return false;
        bool blue = actualId >= 20;
        Faction faction = blue ? Faction.Blue : Faction.Red;

        foreach (RefereeController existing in FindObjectsByType<RefereeController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (existing == null || !existing.IsSpawned || existing.robotTags.Contains(RobotTag.Building)) continue;
            if (existing.RobotID.Value != actualId || existing.faction.Value != faction) continue;
            // Every training switch brings the chosen vehicle to the shared
            // spawn. Without this the scene-placed sentry kept its authored
            // pose at the arena edge (facing a wall), and a car left elsewhere
            // would reappear out of reach.
            if (PacificSelectionState.SelectedActualRobotId != actualId)
                MoveLegacyVehicleToSpawn(existing);
            return true;
        }

        NetworkManager network = NetworkManager.Singleton;
        if (network == null || !network.IsListening || !network.IsServer)
        {
            Debug.LogWarning($"[RoleSelection] Cannot create vehicle {actualId}: no local Netcode host.");
            return false;
        }
        GameObject prefab = FindLegacyVehiclePrefab(displayId, blue);
        if (prefab == null)
        {
            Debug.LogWarning($"[RoleSelection] No prefab for training robot {actualId}.");
            return false;
        }
        if (!ResolveLegacySpawn(out Vector3 spawnPosition, out Quaternion spawnRotation)) return false;
        // Parked roster cars keep their colliders active, so slide along the
        // row until the new car has a clear footprint.
        for (int attempt = 0; attempt < 6 && LegacyRoster.OverlapsVehicle(spawnPosition, spawnRotation); attempt++)
            spawnPosition += spawnRotation * Vector3.forward * 35f;

        GameObject player = Instantiate(prefab, spawnPosition, spawnRotation);
        RefereeController referee = player.GetComponent<RefereeController>();
        NetworkObject vehicle = player.GetComponent<NetworkObject>();
        if (referee == null || vehicle == null)
        {
            Debug.LogError("[RoleSelection] Training vehicle prefab has no RefereeController or NetworkObject.");
            Destroy(player);
            return false;
        }
        referee.spawnPoint = spawnPoint;
        referee.ConfigureSpawnIdentity(actualId, faction);
        referee.SpawnMujocoVehicle(vehicle, network.LocalClientId);
        LegacyRoster.InitializeAmmo(referee);
        LegacyRoster.Prepare(referee);
        Debug.Log($"[RoleSelection] Created training vehicle {actualId} at {spawnPosition}.");
        return true;
    }

    /// <summary>
    /// True when the spawn footprint is free. A vehicle being moved to the
    /// spawn must not count its own colliders; otherwise a car parked on the
    /// spawn made the search skip to a far corner candidate.
    /// </summary>
    private static bool SpawnAreaClear(Vector3 position, Quaternion rotation, RefereeController ignore)
    {
        Collider[] overlaps = Physics.OverlapBox(position + Vector3.up * 3f,
            new Vector3(3f, 2.5f, 3f), rotation, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
            if (overlap != null && overlap.GetComponentInParent<RefereeController>() != ignore)
                return false;
        return true;
    }

    /// <summary>
    /// Brings an existing training vehicle back to the shared spawn pose. The
    /// scene sentry otherwise stares into the arena wall from its authored
    /// corner, and a car left on the field would reappear where it stopped.
    /// </summary>
    private void MoveLegacyVehicleToSpawn(RefereeController referee)
    {
        if (referee == null) return;
        if (!ResolveLegacySpawn(out Vector3 spawnPosition, out Quaternion spawnRotation, referee)) return;
        // Parked roster cars keep their colliders active; slide along the row
        // until the moved car itself fits without landing on another one.
        for (int attempt = 0;
             attempt < 6 && LegacyRoster.OverlapsVehicle(spawnPosition, spawnRotation, referee);
             attempt++)
            spawnPosition += spawnRotation * Vector3.forward * 35f;

        referee.transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        Physics.SyncTransforms();
        // Child bodies (the chassis Base) are jointed to the root, so sync
        // every body to the teleported hierarchy and drop the old momentum.
        foreach (Rigidbody body in referee.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body == null || body.isKinematic) continue;
            body.position = body.transform.position;
            body.rotation = body.transform.rotation;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
        // The legacy turret is aimed in world space, so a chassis move alone
        // leaves the bore pointing the old way. Face it like the spawn does.
        RobotController controller = referee.GetComponent<RobotController>();
        if (controller != null && controller.IsOwner)
            controller.yawTargetAngle.Value = spawnRotation.eulerAngles.y;
        Debug.Log($"[RoleSelection] Training vehicle {referee.RobotID.Value} moved to spawn {spawnPosition}.");
    }

    private GameObject FindLegacyVehiclePrefab(int displayId, bool blue)
    {
        string role = displayId == 1 ? "hero" :
            displayId == 2 ? "engineer" :
            displayId == 7 ? "sentry" : "infantry";
        foreach (NetworkPrefabsList team in teamList)
        {
            if (team == null) continue;
            // Scenes with separate faction lists must not lend a red prefab
            // to a blue slot; the single ARTINX2023 training list serves both.
            bool blueTeam = team.name.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0;
            bool redTeam = team.name.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0;
            if (blue ? redTeam : blueTeam) continue;
            foreach (NetworkPrefab rolePrefab in team.PrefabList)
            {
                if (rolePrefab.Prefab == null) continue;
                if (rolePrefab.Prefab.name.IndexOf(role, StringComparison.OrdinalIgnoreCase) >= 0)
                    return rolePrefab.Prefab;
            }
        }
        return null;
    }

    private void BuildMujocoRoster(int teamIndex)
    {
        if (mujocoRoster.Count > 0) return;
        bool blue = teamList[teamIndex].name.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0;
        for (int i = 0; i < teamList[teamIndex].PrefabList.Count; i++)
        {
            GameObject prefab = teamList[teamIndex].PrefabList[i].Prefab;
            if (prefab == null) continue;
            int actualId = RoleRobotId(i, prefab.name, blue);
            if (mujocoRoster.ContainsKey(actualId)) continue;
            // MuJoCo supplies final poses on the first state frame. Keep the
            // prefabs apart during the short interval before that frame.
            GameObject player = Instantiate(prefab, spawnPoint.position + Vector3.right * (i * 3f), spawnPoint.rotation);
            RefereeController referee = player.GetComponent<RefereeController>();
            NetworkObject vehicle = player.GetComponent<NetworkObject>();
            if (referee == null || vehicle == null) { Destroy(player); continue; }
            referee.spawnPoint = spawnPoint;
            referee.ConfigureSpawnIdentity(actualId, blue ? Faction.Blue : Faction.Red);
            referee.SpawnMujocoVehicle(vehicle);
            InitializeTrainingAmmo(referee);
            mujocoRoster.Add(actualId, vehicle);
            RobotController controller = player.GetComponent<RobotController>();
            if (controller != null) controller.enabled = false;
            foreach (WheelController wheel in player.GetComponentsInChildren<WheelController>(true)) wheel.enabled = false;
            foreach (Rigidbody body in player.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }
            FPVController fpv = player.GetComponentInChildren<FPVController>(true);
            if (fpv != null) fpv.enabled = false;
            foreach (AudioListener listener in player.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
        }
        Debug.Log($"[RoleSelection] Training Ground MuJoCo roster ready: {mujocoRoster.Count} configured vehicles.");
    }

    private static void InitializeTrainingAmmo(RefereeController referee)
    {
        if (referee == null || !referee.IsServer) return;
        // Training Ground has no RMUC supply/economy loop. Give only vehicles
        // with a real shooter a practical training load; RMUC scenes continue
        // to start at zero and use their original purchase/supply rules.
        if (referee.has17mmShooter())
            referee.Ammo0.Value = Mathf.Min(50, referee.RealAmmo0.Value);
        if (referee.has42mmShooter())
            referee.Ammo1.Value = Mathf.Min(10, referee.RealAmmo1.Value);
        if (referee.has17mmShooter() || referee.has42mmShooter())
            Debug.Log($"[RoleSelection] Training ammunition initialized for robot {referee.RobotID.Value}: " +
                $"17mm={referee.Ammo0.Value}, 42mm={referee.Ammo1.Value}");
    }

    private static int RoleRobotId(int index, string prefabName, bool blue)
    {
        int id = prefabName.IndexOf("sentry", StringComparison.OrdinalIgnoreCase) >= 0 ? 7 :
            prefabName.IndexOf("engineer", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
            prefabName.IndexOf("hero", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 :
            Mathf.Clamp(index + 1, 3, 5);
        return blue ? id + 20 : id;
    }
}
