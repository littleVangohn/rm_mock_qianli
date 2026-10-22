using System.Reflection;
using Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Legacy-build roster control. The OSS physics stays authoritative, so roster
/// vehicles keep their rigidbodies and collisions; only the drive/FPV scripts
/// of the currently selected vehicle are enabled. Pressing P in the official
/// HUD transfers that selection without respawning anything.
/// </summary>
public static class LegacyRoster
{
    /// <summary>RobotID of the vehicle the local operator currently drives.</summary>
    public static int SelectedId { get; private set; } = -1;

    /// <summary>
    /// Probes the real arena colliders for a flat, building-free spot near the
    /// requested position. The original spawn markers were authored for the
    /// old scenes; a straight roster line can land over a ramp, a hole or a
    /// base, so every car is placed on a downward ray hit and shifted toward
    /// the field centre until the spot is flat and clear.
    /// </summary>
    public static Vector3 ResolveGroundSpawn(Vector3 desired, Quaternion rotation, float pivotOffset)
        => ResolveGroundSpawn(desired, rotation, pivotOffset, desired.z);

    public static Vector3 ResolveGroundSpawn(Vector3 desired, Quaternion rotation, float pivotOffset, float lineCentreZ)
    {
        Physics.SyncTransforms();
        float towardCentre = desired.x < 0f ? 1f : -1f;
        float expectedGround = desired.y - pivotOffset;
        Vector3 candidate = desired;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (TryFindGround(candidate, out Vector3 ground, out Vector3 normal) &&
                normal.y > 0.8f && Mathf.Abs(ground.y - expectedGround) < 20f)
            {
                Vector3 placed = new Vector3(candidate.x, ground.y + pivotOffset, candidate.z);
                if (!OverlapsBuilding(placed, rotation)) return placed;
            }
            if (attempt < 4) candidate.x += towardCentre * 20f;   // two metres toward the centre
            else candidate.z += Mathf.Sign(lineCentreZ - candidate.z) * 20f; // slide back onto the row
        }
        Debug.LogWarning($"[LegacyRoster] No clear ground near {desired}; using the authored height.");
        return desired;
    }

    public static float PivotOffsetAt(Transform marker)
    {
        if (marker == null) return 0.5f;
        if (!TryFindGround(marker.position, out Vector3 ground, out _)) return 0.5f;
        return Mathf.Clamp(marker.position.y - ground.y, 0f, 3f);
    }

    public static bool TryGroundHeight(Vector3 position, out float height)
    {
        if (TryFindGround(position, out Vector3 ground, out _))
        {
            height = ground.y;
            return true;
        }
        height = position.y;
        return false;
    }

    private static bool TryFindGround(Vector3 desired, out Vector3 ground, out Vector3 normal)
    {
        ground = desired;
        normal = Vector3.up;
        RaycastHit[] hits = Physics.RaycastAll(new Ray(desired + Vector3.up * 300f, Vector3.down),
            900f, ~0, QueryTriggerInteraction.Ignore);
        bool found = false;
        float bestY = float.NegativeInfinity;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.isTrigger) continue;
            if (hit.collider.GetComponentInParent<RefereeController>() != null) continue;
            if (hit.point.y <= bestY) continue;
            bestY = hit.point.y;
            ground = hit.point;
            normal = hit.normal;
            found = true;
        }
        return found;
    }

    private static bool OverlapsBuilding(Vector3 position, Quaternion rotation)
    {
        Collider[] overlaps = Physics.OverlapBox(position + Vector3.up * 1.5f,
            new Vector3(2.2f, 1.5f, 2.2f), rotation, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
        {
            RefereeController owner = overlap != null ? overlap.GetComponentInParent<RefereeController>() : null;
            if (owner != null && owner.robotTags.Contains(RobotTag.Building)) return true;
        }
        return false;
    }

    /// <summary>True when another roster vehicle already occupies this spot.</summary>
    public static bool OverlapsVehicle(Vector3 position, Quaternion rotation, RefereeController ignore = null)
    {
        Collider[] overlaps = Physics.OverlapBox(position + Vector3.up * 1.5f,
            new Vector3(2.2f, 1.5f, 2.2f), rotation, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
        {
            RefereeController owner = overlap != null ? overlap.GetComponentInParent<RefereeController>() : null;
            if (owner != null && owner != ignore && !owner.robotTags.Contains(RobotTag.Building)) return true;
        }
        return false;
    }

    /// <summary>
    /// The legacy roster bypasses the original purchase flow, so give every
    /// shooter a practical starting load; otherwise RMUC modes fire nothing
    /// (their retail ammo starts at zero) and the player sees no bullets.
    /// </summary>
    public static void InitializeAmmo(RefereeController referee)
    {
        if (referee == null || !referee.IsServer) return;
        bool training = SceneManager.GetActiveScene().name == "Training Ground";
        if (training)
        {
            // The original training scene never implemented the MuJoCo build's
            // infinite-ammo rule, so refill the ledger high enough for a session.
            if (referee.has17mmShooter())
            {
                referee.Ammo0.Value = 9999;
                referee.RealAmmo0.Value = 9999;
            }
            if (referee.has42mmShooter())
            {
                referee.Ammo1.Value = 9999;
                referee.RealAmmo1.Value = 9999;
            }
            return;
        }
        if (referee.has17mmShooter())
            referee.Ammo0.Value = Mathf.Min(50, referee.RealAmmo0.Value);
        if (referee.has42mmShooter())
            referee.Ammo1.Value = Mathf.Min(10, referee.RealAmmo1.Value);
    }

    public static void Prepare(RefereeController referee)
    {
        if (referee == null) return;
        SetControl(referee, false);
        HidePlayerUi(referee);
        SetShooterCard(referee, false);
    }

    public static void Select(int actualId)
    {
        SelectedId = actualId;
        bool training = SceneManager.GetActiveScene().name == "Training Ground";
        foreach (RefereeController referee in Object.FindObjectsByType<RefereeController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (referee == null || !referee.IsSpawned || referee.robotTags.Contains(RobotTag.Building)) continue;
            bool selected = referee.RobotID.Value == actualId;
            // Training is a driving range: keep only the vehicle the operator
            // is holding visible in the scene. Hide geometry and colliders
            // instead of deactivating the NetworkObject, which made NGO and
            // RefereeController tick objects that no longer existed.
            if (training) SetVehicleVisible(referee, selected);
            SetControl(referee, selected);
            if (selected) HidePlayerUi(referee);
            SetShooterCard(referee, selected);
        }
    }

    /// <summary>
    /// The shooter prefab carries its own screen-space card. Only the selected
    /// vehicle may show it; otherwise every roster car's card lands on the
    /// same pixels and the ammo line turns into overlapping text.
    /// </summary>
    private static void SetShooterCard(RefereeController referee, bool visible)
    {
        foreach (ShooterController shooter in referee.GetComponentsInChildren<ShooterController>(true))
            if (shooter != null) shooter.SetLegacyCardVisible(visible);
    }

    private static void SetVehicleVisible(RefereeController referee, bool visible)
    {
        // Hide the mesh only. Disabling colliders/rigidbodies on the parked
        // cars made them fall through the floor and could destabilise the
        // selected vehicle's physics, which broke WASD.
        foreach (Renderer renderer in referee.GetComponentsInChildren<Renderer>(true))
            if (renderer != null) renderer.enabled = visible;
    }

    private static void SetControl(RefereeController referee, bool enabled)
    {
        RobotController controller = referee.GetComponent<RobotController>();
        if (controller != null && controller.enabled != enabled) controller.enabled = enabled;
        // Only the vehicle currently being driven may fire. Without this the
        // Training Ground sentry keeps shooting on its own.
        if (referee.IsServer)
        {
            if (referee.ShooterEnabled.Value != enabled) referee.ShooterEnabled.Value = enabled;
            foreach (ShooterController shooter in referee.GetComponentsInChildren<ShooterController>(true))
                if (shooter != null && shooter.Enabled.Value != enabled) shooter.Enabled.Value = enabled;
        }
        FPVController fpv = referee.GetComponentInChildren<FPVController>(true);
        if (fpv == null) return;
        if (enabled)
        {
            fpv.TurnOnCamera();
            fpv.enabled = true;
            CinemachineVirtualCamera vcam = null;
            foreach (CinemachineVirtualCamera candidate in referee.GetComponentsInChildren<CinemachineVirtualCamera>(true))
                if (candidate != null && candidate.name.IndexOf("FPV", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    vcam = candidate;
                    break;
                }
            if (vcam == null)
                vcam = typeof(FPVController)
                    .GetField("FPVCamera", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fpv)
                    as CinemachineVirtualCamera;
            if (vcam != null)
            {
                vcam.enabled = true;
                vcam.gameObject.SetActive(true);
                vcam.Priority = 10000;
                vcam.m_Follow = null;
                vcam.m_LookAt = null;
            }
            foreach (CinemachineVirtualCamera other in referee.GetComponentsInChildren<CinemachineVirtualCamera>(true))
                if (other != null && other != vcam) other.Priority = 0;
        }
        else
        {
            if (fpv.enabled) fpv.TurnOffCamera();
            fpv.enabled = false;
        }
    }

    private static void HidePlayerUi(RefereeController referee)
    {
        FPVController fpv = referee.GetComponentInChildren<FPVController>(true);
        if (fpv == null) return;
        GameObject ui = typeof(FPVController)
            .GetField("PlayerUI", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fpv) as GameObject;
        if (ui != null) ui.SetActive(false);
    }
}
