using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Cinemachine;

namespace Pacific.Mujoco
{
    /// <summary>
    /// Hosts the already validated Pacific official-client HUD inside the open-source
    /// simulator. The original UI project remains untouched and MuJoCo keeps a single
    /// realtime TCP connection.
    /// </summary>
    public sealed class PacificOfficialUiBridge : MonoBehaviour
    {
        // Source robot and bullet prefabs are authored at ten Unity units per
        // metre (17 mm prefab scale 0.17, 42 mm prefab scale 0.42).  The 7.5
        // field calibration is positional only and must not size a projectile
        // relative to those robot meshes.
        private const float SourceSimulatorUnitsPerMetre = 10f;
        // One arena scale for every role and scene. Prefab FPV mount heights
        // differ by class and must never resize the entire MuJoCo field.
        private const float DefaultVisualScale = 7.5f;
        // This build keeps the OSS simulator as the physics/rule authority. The
        // bridge only supplies the official HUD: it never contacts a backend,
        // never moves a robot and never disables the original drive stack.
        private static readonly bool LegacyMode = true;
        // The original minimap image (Assets/Textures/Map.png) covers a
        // 290 x 155 world-unit rectangle centred on the arena origin: the
        // RMUC field (280 x 150 at 10 units per metre) plus a small margin.
        private const float LegacyMapWorldWidth = 290f;
        private const float LegacyMapWorldDepth = 155f;
        private readonly Dictionary<RobotController, BoundRobot> robots = new Dictionary<RobotController, BoundRobot>();
        private MujocoClient client;
        private ControlInput controls;
        private Canvas officialCanvas;
        private Camera operatorCamera;
        private Camera overviewCamera;
        private Camera ballisticsCamera;
        private MeshCollider visualFieldSurface;
        private CameraModeController cameraModes;
        private OfficialPanelController panels;
        private MujocoState latestState;
        private MujocoLoadingScreen loadingScreen;
        private float nextBackendSelectionCheck;
        private int confirmedBackendRobotId = -1;
        private int ammoConfiguredRobotId = -1;
        private bool matchResetRequested;
        private bool matchResetComplete;
        private readonly Queue<KeyValuePair<int, string>> pendingShots = new Queue<KeyValuePair<int, string>>();
        private int trackedShotId;
        private int impactedShotId;
        private Vector3 previousProjectileWorld;
        private bool hasPreviousProjectileWorld;
        private float pendingShotAt;
        private int pendingStartShotId;
        private int lastDebitedShotId;
        private GameObject projectileVisual;
        private GameObject sharedMarkerVisual;
        private TrailRenderer projectileTrail;
        private bool projectileVisualWasActive;
        private float projectileVisualUntil;
        private int lastVisibleProjectileShotId;
        private readonly Dictionary<AreaController, Bounds> ruleAreaBounds = new Dictionary<AreaController, Bounds>();
        private readonly Dictionary<ArmorController, Bounds> buildingArmorBounds = new Dictionary<ArmorController, Bounds>();
        private readonly Dictionary<RefereeController, ArmorController[]> armorCache = new Dictionary<RefereeController, ArmorController[]>();
        private readonly HashSet<Collider> seenArmorColliders = new HashSet<Collider>();

        // Scene lookups are cached and refreshed a few times per second. The
        // state callback runs at 30 Hz; walking 80k loaded objects there for
        // RefereeController/RobotController caused a visible hitch on every
        // state packet. Membership only changes on spawn, so 0.5 s is plenty.
        private const float SceneCacheSeconds = 0.5f;
        private RefereeController[] cachedReferees = Array.Empty<RefereeController>();
        private RobotController[] cachedRobots = Array.Empty<RobotController>();
        private readonly List<RobotController> staleRobots = new List<RobotController>();
        private readonly Dictionary<int, RefereeController> ruleRobots = new Dictionary<int, RefereeController>();
        private GameManager cachedManager;
        private float nextSceneCacheAt;

        // The merged HUD roster is rebuilt 30 times a second. Reusing the
        // sample objects and the output array keeps that path allocation-free
        // once the roster is stable; only a changed buff set allocates.
        private readonly Dictionary<int, RobotMasterRobot> mergedRobotCache = new Dictionary<int, RobotMasterRobot>();
        private readonly Dictionary<int, int> mergedBuffMask = new Dictionary<int, int>();
        private readonly Dictionary<int, RobotMasterRobot> mapRobotCache = new Dictionary<int, RobotMasterRobot>();
        private readonly List<RobotMasterRobot> mergedRobotBuffer = new List<RobotMasterRobot>();
        private RobotMasterRobot[] mergedRobotArray = Array.Empty<RobotMasterRobot>();
        private MatchRuleState rulesState;

        private void OnGUI()
        {
            if (LegacyMode || !PacificSelectionState.Completed || client == null || client.IsConnected || Time.unscaledTime < 3f)
                return;
            Rect area = new Rect(Screen.width * 0.19f, 16f, Screen.width * 0.62f, 46f);
            GUI.Box(area, $"MuJoCo 后端未连接：车辆和第一人称控制尚未就绪。请检查 WSL 后端（TCP {client.port}）。");
        }

        private sealed class BoundRobot
        {
            public int id;
            public Transform root;
            public Transform chassis;
            public Quaternion chassisLocalRotation;
            public Transform yaw;
            public Transform pitch;
            public Transform cameraMount;
            public CinemachineVirtualCamera fpvCamera;
            public bool syntheticFpvCamera;
            public CinemachineBrain brain;
            public Vector3 mappedCameraPosition;
            public Quaternion mappedCameraRotation;
            public bool hasMappedCameraPose;
            public Vector3 cameraLocalPosition;
            public Quaternion cameraLocalRotation;
            public Vector3 unityOriginPosition;
            public Quaternion unityOriginRotation;
            public Vector3 mujocoOriginPosition;
            public Quaternion mujocoOriginRotation;
            public float visualScale = DefaultVisualScale;
            public bool calibrated;
        }

        private void Awake()
        {
            client = gameObject.AddComponent<MujocoClient>();
            client.LocalMode = LegacyMode;
            ApplyEndpointArguments(client);
            client.UnityToBackendWorld = UnityWorldToBackend;
            client.RaycastField = RaycastFieldSurface;
            controls = gameObject.AddComponent<ControlInput>();
            controls.client = client;
            // Legacy build: use the exact key/mouse layer of the custom UI
            // client. It owns the bindings (WASD, mouse yaw/pitch, G, C/V,
            // boost, brake, fire); this bridge feeds its commands into the
            // selected OSS robot. Passive cursor keeps the pointer policy in
            // UpdateLegacyCursor() below, and also stops OnDisable() from
            // hiding the pointer before the first menu is drawn.
            if (LegacyMode)
            {
                controls.passiveCursor = true;
                RobotController.ExternalInput = true;
                client.LocalControl = ApplyLegacyControl;
                client.LocalControlMode = ApplyLegacyControlMode;
                client.LocalAmmo = ApplyLegacyAmmo;
            }
            client.FireAllowed = CanFireSelectedRobot;
            client.FireBlockReason = GetFireBlockReason;
            client.FireQueued += OnFireQueued;
            // Legacy mode publishes a locally assembled state; ApplyState must
            // not run, because it would try to drive robots and install the
            // MuJoCo field from a pose that does not exist here.
            if (!LegacyMode) client.StateUpdated += ApplyState;
            client.CommandCompleted += OnCommandCompleted;
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPreRender += OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(InstallWhenRobotExists());
        }

        private void OnDestroy()
        {
            if (client != null) client.StateUpdated -= ApplyState;
            if (client != null) client.CommandCompleted -= OnCommandCompleted;
            if (client != null) client.FireQueued -= OnFireQueued;
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPreRender -= OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            nextSceneCacheAt = 0f;
            nextRobotDiscoveryAt = 0f;
            // The start screen is not a match: do not let a completed Spawn
            // make the loading overlay (or the old HUD) reappear there.
            if (scene.name == "Welcome") PacificSelectionState.Reset();
            // Scene teardown normally despawns observers, but a stale count
            // would keep the pointer captured on the next welcome screen.
            ObserverController.ResetOwnedObservers();
            robots.Clear();
            pendingShots.Clear();
            pendingStartShotId = 0;
            lastDebitedShotId = 0;
            trackedShotId = 0;
            impactedShotId = 0;
            hasPreviousProjectileWorld = false;
            ruleAreaBounds.Clear();
            buildingArmorBounds.Clear();
            matchResetRequested = false;
            matchResetComplete = false;
            if (mujocoField != null) Destroy(mujocoField);
            if (sharedMarkerVisual != null) Destroy(sharedMarkerVisual);
            sharedMarkerVisual = null;
            mujocoField = null;
            visualFieldSurface = null;
            legacyOperatorReady = false;
            legacyControlModeGyro = false;
            legacyPointerOwnedByUi = false;
            if (controls != null) controls.SetOperatorReady(false);
            if (officialCanvas != null) Destroy(officialCanvas.gameObject);
            officialCanvas = null;
            if (overviewCamera != null) Destroy(overviewCamera.gameObject);
            if (ballisticsCamera != null) Destroy(ballisticsCamera.gameObject);
            if (projectileVisual != null) Destroy(projectileVisual);
            projectileVisual = null;
            projectileTrail = null;
            projectileVisualWasActive = false;
            if (loadingScreen != null) { Destroy(loadingScreen.gameObject); loadingScreen = null; }
            armorCache.Clear();
            seenArmorColliders.Clear();
            operatorCamera = null;
            overviewCamera = null;
            ballisticsCamera = null;
            if (cameraModes != null) Destroy(cameraModes);
            cameraModes = null;
            if (LegacyMode)
            {
                // These HUD components live on this persistent object, not on
                // the canvas that was just destroyed. Without this a second
                // match stacks a second binder on the same client events.
                foreach (OfficialHudBinder hud in GetComponents<OfficialHudBinder>()) Destroy(hud);
                foreach (OfficialFullscreenController overlay in GetComponents<OfficialFullscreenController>()) Destroy(overlay);
                foreach (OfficialPanelController panel in GetComponents<OfficialPanelController>()) Destroy(panel);
                foreach (OfficialRuleUiAdapter adapter in GetComponents<OfficialRuleUiAdapter>()) Destroy(adapter);
                foreach (SeasonMapController map in GetComponents<SeasonMapController>()) Destroy(map);
                foreach (TacticalCommandController tactical in GetComponents<TacticalCommandController>()) Destroy(tactical);
                panels = null;
            }
            latestState = null;
            StartCoroutine(InstallWhenRobotExists());
        }

        private void OnCommandCompleted(CommandFeedback feedback)
        {
            if (feedback != null && feedback.name == "fire")
            {
                if (pendingShots.Count == 0) return;
                if (feedback.shotId > 0 && feedback.shotId <= pendingStartShotId) return;
                KeyValuePair<int, string> shot = pendingShots.Dequeue();
                if (feedback.ok)
                {
                    RefereeController referee = FindRuleRobot(shot.Key);
                    if (referee == null || !referee.RecordMujocoShot(shot.Value))
                        Debug.LogError($"[MuJoCo] Projectile fired but referee could not debit {shot.Value} from robot {shot.Key}.");
                    else lastDebitedShotId = Mathf.Max(lastDebitedShotId, feedback.shotId);
                }
                else Debug.Log($"[MuJoCo] Fire rejected for robot {shot.Key}: {feedback.error}");
                return;
            }
            if (feedback == null || feedback.name != "reset" || !matchResetRequested) return;
            if (!feedback.ok)
            {
                Debug.LogError($"[MuJoCo] Match reset failed: {feedback.error}");
                return;
            }
            matchResetComplete = true;
            Debug.Log("[MuJoCo] New match reset acknowledged; arena calibration enabled.");
        }

        private bool CanFireSelectedRobot(int actualId, string roundName)
        {
            return string.IsNullOrEmpty(GetFireBlockReason(actualId, roundName));
        }

        private string GetFireBlockReason(int actualId, string roundName)
        {
            // The referee ledger is debited on backend acknowledgement. Keep
            // one unacknowledged request so two clicks cannot spend one round.
            if (pendingShots.Count > 0) return "FIRE_ACK_PENDING";
            RefereeController referee = FindRuleRobot(actualId);
            return referee == null ? "REFEREE_NOT_FOUND" : referee.GetMujocoFireBlockReason(roundName);
        }

        private void OnFireQueued(int actualId, string roundName)
        {
            pendingShots.Enqueue(new KeyValuePair<int, string>(actualId, roundName));
            pendingShotAt = Time.unscaledTime;
            pendingStartShotId = Mathf.Max(lastDebitedShotId, latestState?.projectile?.shotId ?? 0);
        }

        private IEnumerator InstallWhenRobotExists()
        {
            Scene launchScene = SceneManager.GetActiveScene();
            while (isActiveAndEnabled)
            {
                DiscoverRobots();
                if (PacificSelectionState.Completed && robots.Count > 0 && (LegacyMode || mujocoField != null))
                {
                    Camera camera = FindOperatorCamera();
                    if (camera != null && officialCanvas == null) InstallOfficialUi(camera);
                    if (officialCanvas != null) yield break;
                }
                yield return new WaitForSecondsRealtime(0.25f);
                // A previous scene's coroutine must not race the new scene's
                // install and build a second HUD canvas.
                if (SceneManager.GetActiveScene() != launchScene) yield break;
            }
        }

        private float nextRobotDiscoveryAt;

        private void DiscoverRobots(bool force = false)
        {
            // State arrives 30 times a second, but roster membership only
            // changes on spawn or a P-panel switch. Walking every loaded
            // object here was a per-packet hitch; the cache keeps it O(1).
            float now = Time.unscaledTime;
            if (!force && now < nextRobotDiscoveryAt) return;
            nextRobotDiscoveryAt = now + SceneCacheSeconds;
            // A P-panel switch or scene teardown can destroy the previously
            // selected controller before the next backend state arrives.
            // Unity's destroyed-object keys remain in the dictionary until
            // explicitly removed and must never be dereferenced.
            staleRobots.Clear();
            foreach (RobotController controller in robots.Keys)
                if (controller == null) staleRobots.Add(controller);
            foreach (RobotController stale in staleRobots) robots.Remove(stale);
            int cachedRobotCount = cachedRobots.Length;
            RefreshSceneCache(force);
            if (cachedRobots.Length != cachedRobotCount) EnforceSingleAudioListener();
            bool boundAny = false;
            foreach (RobotController controller in cachedRobots)
            {
                if (controller == null || robots.ContainsKey(controller)) continue;
                RefereeController referee = controller.GetComponent<RefereeController>();
                // Bind only the one explicit local player object.  Host mode
                // can mark several server-spawned referee objects IsOwner;
                // letting all of them write MuJoCo every frame makes the last
                // Update win and usually replaces W/S with a zero command.
                if (referee == null || !referee.IsSpawned || !referee.IsOwner || !IsSelectedLocalRobot(controller, referee)) continue;
                // Roster cars are enlarged for visibility in the scaled OBJ
                // arena. The locally driven prefab must retain its authored
                // scale so its FPV mount and camera calibration stay correct.
                controller.transform.localScale = Vector3.one;
                int id = referee != null ? referee.RobotID.Value : 1;
                Transform chassis = GetPrivateTransform(controller, "Base") ?? controller.transform;
                Transform yaw = GetPrivateTransform(controller, "Yaw");
                Transform pitch = GetPrivateTransform(controller, "Pitch");
                // Run the legacy turret setup before the camera is resolved:
                // it moves off-gimbal shooters onto the Pitch, and the Sentry
                // operator camera is mounted from the muzzle.
                if (LegacyMode)
                {
                    controller.UseExternalInput();
                    PrepareLegacyKinematicTurret(controller);
                }
                Transform cameraMount = FindFirstPersonCameraMount(controller);
                CinemachineVirtualCamera virtualCamera = GetPrivateComponent(
                    controller.GetComponentInChildren<FPVController>(true), "FPVCamera") as CinemachineVirtualCamera;
                bool syntheticFpvCamera = false;
                if (virtualCamera == null)
                {
                    // Scene-placed cars and the Sentry can lack the runtime
                    // prefab's FPV virtual camera. The reference funnel camera
                    // rides 0.14 m above the bore and looks along the barrel;
                    // borrowing a child camera instead pointed the view 90 deg
                    // away from the gun (the Sentry's cameras are AI sensors).
                    Transform parent = cameraMount;
                    Vector3 localPosition = Vector3.zero;
                    Quaternion localRotation = Quaternion.identity;
                    ShooterController muzzleShooter = PickLegacyShooter(referee);
                    if (muzzleShooter != null && muzzleShooter.ShootPoint != null)
                    {
                        parent = muzzleShooter.ShootPoint.transform;
                        localPosition = new Vector3(0f, LegacyBoreCameraOffset, 0f);
                        localRotation = Quaternion.Euler(0f, 90f, 0f);
                    }
                    else if (parent == null)
                    {
                        parent = pitch != null ? pitch : yaw != null ? yaw : chassis;
                    }
                    GameObject mount = FindSyntheticFpvMount(controller);
                    if (mount == null) mount = new GameObject("MuJoCo Vehicle FPV Mount");
                    mount.transform.SetParent(parent, false);
                    mount.transform.localPosition = localPosition;
                    mount.transform.localRotation = localRotation;
                    cameraMount = mount.transform;
                    virtualCamera = mount.GetComponent<CinemachineVirtualCamera>();
                    if (virtualCamera == null) virtualCamera = mount.AddComponent<CinemachineVirtualCamera>();
                    syntheticFpvCamera = true;
                }

                // Keep the referee/FPV component alive. It owns the original
                // Cinemachine virtual camera and simulator rule values.
                FPVController fpv = controller.GetComponentInChildren<FPVController>(true);
                if (fpv != null)
                {
                    if (!LegacyMode) fpv.TurnOnCamera();
                    // TurnOnCamera reactivates the legacy robot HUD subtree.
                    // That HUD is replaced by the official one, so hide only the
                    // obsolete visual HUD; keep the camera, audio listener and
                    // RefereeController untouched.
                    GameObject legacyPlayerUi = GetPrivateGameObject(fpv, "PlayerUI");
                    if (legacyPlayerUi != null) legacyPlayerUi.SetActive(false);
                    // In the original simulator this script also drives the
                    // camera and cursor, so only the MuJoCo build disables it.
                    if (!LegacyMode) fpv.enabled = false;
                }
                if (!LegacyMode)
                {
                    controller.enabled = false;
                    foreach (WheelController wheel in controller.GetComponentsInChildren<WheelController>(true)) wheel.enabled = false;
                    foreach (Rigidbody body in controller.GetComponentsInChildren<Rigidbody>(true))
                    {
                        body.isKinematic = true;
                        body.detectCollisions = false;
                    }
                }
                if (LegacyMode) FocusLegacyFpvCamera(controller, virtualCamera);
                robots.Add(controller, new BoundRobot
                {
                    id = id,
                    root = controller.transform,
                    chassis = chassis,
                    chassisLocalRotation = Quaternion.Inverse(controller.transform.rotation) * chassis.rotation,
                    yaw = yaw,
                    pitch = pitch,
                    cameraMount = cameraMount,
                    fpvCamera = virtualCamera,
                    syntheticFpvCamera = syntheticFpvCamera,
                    unityOriginPosition = chassis.position,
                    unityOriginRotation = chassis.rotation,
                });
                if (operatorCamera != null && robots.TryGetValue(controller, out BoundRobot bound))
                {
                    bound.brain = operatorCamera.GetComponent<CinemachineBrain>();
                    if (bound.cameraMount != null)
                    {
                        bound.cameraLocalPosition = bound.cameraMount.localPosition;
                        bound.cameraLocalRotation = bound.cameraMount.localRotation;
                    }
                }
                string robotClass = referee.robotClass.Value.ToString();
                if (!LegacyMode && client.IsConnected) client.SelectRobot(id, robotClass);
                if (panels != null)
                {
                    RoleSelectionMenu training = FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include);
                    panels.ConfigureRobotSwitch(SwitchSelectedRobot, id, PanelRobotIds(training));
                }
                Debug.Log($"[MuJoCo] Bound selected robot actualId={id} faction={referee.faction.Value} class={robotClass}; requested matching MuJoCo model.");
                boundAny = true;
            }
            if (boundAny) EnforceSingleAudioListener();
        }

        /// <summary>
        /// Reference-bridge pose path for the legacy physics build. The OSS
        /// prefab puts Yaw/Pitch next to the chassis under the root and drives
        /// them through HingeJoint motors; those motors stall against the
        /// prefab's spring damper/collisions, so the camera stays at the spawn
        /// pose while the chassis drives away. The reference MuJoCo bridge
        /// instead writes the turret rotations directly (ApplyHeroPose). Copy
        /// that here: drop the turret physics, parent it under the physics
        /// chassis, and rotate Yaw/Pitch from the aim targets every frame.
        /// </summary>
        private void PrepareLegacyKinematicTurret(RobotController controller)
        {
            RobotController.KinematicAim = true;
            Transform baseTransform = GetPrivateTransform(controller, "Base") ?? controller.transform;
            Transform yaw = GetPrivateTransform(controller, "Yaw");
            Transform pitch = GetPrivateTransform(controller, "Pitch");
            if (yaw != null)
            {
                DisableTurretPhysics(yaw);
                if (yaw.parent != baseTransform) yaw.SetParent(baseTransform, true);
            }
            if (pitch != null)
            {
                DisableTurretPhysics(pitch);
                Transform parent = yaw != null ? yaw : baseTransform;
                if (pitch.parent != parent) pitch.SetParent(parent, true);
            }
            // The automatic sentry ignores operator aim while AutoOperate is
            // on and its patrol kept spinning the view; the selected legacy
            // robot is always hand-driven.
            if (controller.IsServer && controller.AutoOperate.Value)
                controller.AutoOperate.Value = false;
            // ARTINX cars mount the barrel on the Pitch. The Sentry instead
            // authors Shooter-17mm under the hull and relies on the original
            // FixedJoint; this build removes that joint, which left the muzzle
            // (and the funnel) ignoring the turret. Ride the Pitch like the
            // other robots so aiming moves the gun and the camera together.
            Transform shooterMount = pitch != null ? pitch : yaw;
            if (shooterMount == null) return;
            foreach (ShooterController shooter in controller.GetComponentsInChildren<ShooterController>(true))
            {
                if (shooter == null || shooter.transform.IsChildOf(shooterMount)) continue;
                // Shooter-17mm carries its own NetworkObject. NGO refuses to
                // move a spawned NetworkObject under a plain transform and
                // reverts the parent, so stop syncing this host-only parent
                // change before reparenting it onto the Pitch.
                NetworkObject shooterObject = shooter.GetComponent<NetworkObject>();
                if (shooterObject != null) shooterObject.AutoObjectParentSync = false;
                shooter.transform.SetParent(shooterMount, true);
            }
        }

        private static void DisableTurretPhysics(Transform turret)
        {
            foreach (MonoBehaviour behaviour in turret.GetComponents<MonoBehaviour>())
                if (behaviour != null && behaviour.GetType().Name == "NetworkTransform")
                    behaviour.enabled = false;
            HingeJoint joint = turret.GetComponent<HingeJoint>();
            if (joint != null) Destroy(joint);
            Rigidbody body = turret.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.detectCollisions = false;
                Destroy(body);
            }
        }

        private void UpdateLegacyTurret()
        {
            RobotController robot = SelectedLegacyRobot();
            if (robot == null) return;
            if (operatorCamera != null)
            {
                Vector3 viewForward = operatorCamera.transform.forward;
                viewForward.y = 0f;
                if (viewForward.sqrMagnitude > 1e-4f)
                    RobotController.ViewForward = viewForward.normalized;
                Vector3 viewRight = operatorCamera.transform.right;
                viewRight.y = 0f;
                if (viewRight.sqrMagnitude > 1e-4f)
                    RobotController.ViewRight = viewRight.normalized;
            }
            Transform yaw = GetPrivateTransform(robot, "Yaw");
            Transform pitch = GetPrivateTransform(robot, "Pitch");
            if (yaw != null)
            {
                // World-space yaw only: the reference FPV is gimbal-stabilised
                // and must not inherit chassis roll/pitch. A local rotation
                // under the tilted Base made the whole battlefield spin.
                yaw.rotation = Quaternion.Euler(0f, robot.yawTargetAngle.Value, 0f);
            }
            if (pitch != null)
                pitch.localRotation = Quaternion.Euler(0f, 0f,
                    Mathf.Clamp(robot.pitchTargetAngle.Value, LegacyManualPitchMin, LegacyManualPitchMax));
        }

        /// <summary>
        /// The OSS scenes contain several robots with their own FPV virtual
        /// cameras. In the legacy build the roster/sentry cameras stay at the
        /// prefab priority (20), so the brain can keep rendering a stationary
        /// scene camera while the player's car drives away. Only the selected
        /// robot's FPV may be active during operator play.
        /// </summary>
        private void FocusLegacyFpvCamera(RobotController selected, CinemachineVirtualCamera selectedCamera)
        {
            if (selectedCamera != null)
            {
                selectedCamera.enabled = true;
                selectedCamera.gameObject.SetActive(true);
                selectedCamera.Priority = 10000;
            }
            foreach (RobotController other in cachedRobots)
            {
                if (other == null || other == selected) continue;
                FPVController fpv = other.GetComponentInChildren<FPVController>(true);
                if (fpv == null) continue;
                CinemachineVirtualCamera camera =
                    GetPrivateComponent(fpv, "FPVCamera") as CinemachineVirtualCamera;
                if (camera != null)
                {
                    camera.Priority = 0;
                    camera.enabled = false;
                }
                if (fpv.enabled) fpv.enabled = false;
            }
        }

        private void ApplyState(MujocoState state)
        {
            if (state == null) return;
            DiscoverRobots();
            if (!matchResetRequested && robots.Count > 0 && client.IsConnected &&
                PacificSelectionState.Completed)
            {
                // A new Spawn starts a new match. P-panel switches keep the
                // same bridge/scene and must not reset the running match.
                matchResetRequested = true;
                client.ResetSimulation();
            }
            // Do not calibrate a new arena from a pose left by the previous
            // match. The reset acknowledgement precedes the fresh state.
            if (robots.Count > 0 && !matchResetComplete) return;
            if (robots.Count > 0 && state.selectedRobotId == CurrentSelectedActualRobotId() &&
                string.Equals(state.selectedRobotClass, SelectedRobotClassName(), StringComparison.OrdinalIgnoreCase))
                confirmedBackendRobotId = state.selectedRobotId;
            ReconcilePendingShot(state.projectile);
            // Discover the owner before merging rules so blue-side/local IDs
            // are correct on the very first frame (otherwise the HUD briefly
            // binds to red robot 1 and appears to show placeholder HP/coins).
            MergeRuleState(state);
            bool hasSelectedIdentity = state.selectedRobotId > 0 && !string.IsNullOrWhiteSpace(state.selectedRobotClass);
            if (hasSelectedIdentity && PacificSelectionState.TryGetSelection(out _, out _) &&
                (state.selectedRobotId != PacificSelectionState.SelectedActualRobotId ||
                 !string.Equals(state.selectedRobotClass, SelectedRobotClassName(), StringComparison.OrdinalIgnoreCase)))
            {
                if (state.selectedRobotId == 0 && !warnedLegacyBackend)
                {
                    warnedLegacyBackend = true;
                    Debug.LogError("[MuJoCo] Backend does not report selectedRobotId/selectedRobotClass. Refusing to apply its generic hero pose to the selected vehicle; update the backend protocol before controlling this role.");
                }
            }
            latestState = state;
            EnsureSelectedFpvCamera();
            foreach (BoundRobot robot in robots.Values)
            {
                if (robot.chassis == null) continue;
                // Never apply a generic hero pose to whichever Unity robot was
                // selected. The backend must identify the matching vehicle.
                bool backendRobotMatches = state.selectedRobotId == robot.id && state.hero != null && state.hero.id == robot.id &&
                    string.Equals(state.selectedRobotClass, SelectedRobotClass(robot.id), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(state.hero.robotClass, SelectedRobotClass(robot.id), StringComparison.OrdinalIgnoreCase) &&
                    Valid(state.hero.positionM);
                if (backendRobotMatches)
                {
                    Vector3 mujocoPosition = CoordinateMapper.World(state.hero.positionM);
                    ApplyHeroPose(robot, mujocoPosition, state.hero.yawDeg,
                        state.hero.turretWorldYawDeg, state.hero.barrelPitchDeg);
                    EnsureMujocoField(robot);
                    AlignToVisualField(robot.root, robot.chassis, robot);
                    ApplyBackendCameraPose(robot, state.operatorCamera, mujocoPosition,
                        Quaternion.Euler(0f, -state.hero.yawDeg, 0f));
                    continue;
                }

                RobotState pose = FindRobot(state.robots, robot.id);
                if (pose == null || !string.Equals(pose.source, "mujoco", StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 current = robot.chassis.position;
                MoveWholeRobot(robot, new Vector3(pose.x, current.y, pose.y), Quaternion.Euler(0f, -pose.yawDeg, 0f));
                if (robot.cameraMount != null && robot.brain != null && robot.brain.OutputCamera != null)
                    robot.brain.OutputCamera.transform.SetPositionAndRotation(
                        robot.cameraMount.position,
                        robot.cameraMount.rotation);
            }
            UpdateRosterRobotPoses(state);
            ApplySharedMarkerVisual(state.sharedMarker);
            SyncRuleAreas();
            ApplyProjectileVisual(state.projectile, state.ammo);
            ApplyProjectileHit(state.projectile);
        }

        private void ApplyProjectileVisual(global::ProjectileState shot, AmmoState ammo)
        {
            if (projectileVisual == null || mujocoField == null) return;
            float roundScale = ammo != null && ammo.radius > 0f ? ammo.radius / 0.02125f : 1f;
            projectileVisual.transform.localScale = Vector3.one * (SourceSimulatorUnitsPerMetre * roundScale);
            if (projectileTrail != null && ammo != null && ammo.radius > 0f)
            {
                float worldDiameter = ammo.radius * 2f * SourceSimulatorUnitsPerMetre;
                projectileTrail.startWidth = worldDiameter;
                projectileTrail.endWidth = worldDiameter * 0.2f;
            }
            bool visible = shot != null && shot.visible && shot.active && Valid(shot.positionM);
            if (visible)
            {
                if (!projectileVisual.activeSelf)
                {
                    projectileVisual.SetActive(true);
                    if (projectileTrail != null) projectileTrail.Clear();
                }
                // LateUpdate points the visual from the interpolated samples;
                // writing the raw packet here as well would reintroduce the
                // one-packet jump this method used to have.
                if (projectileTrail != null) projectileTrail.emitting = true;
                projectileVisualWasActive = true;
                projectileVisualUntil = Time.unscaledTime + 0.10f;
                lastVisibleProjectileShotId = shot.shotId;
                return;
            }

            // Fast rounds can leave the muzzle and hit terrain between two
            // Unity state samples. In that case the first state carrying the
            // new shotId is already terminal and active was never observed.
            // Preserve the backend's real origin/impact endpoints as a short
            // tracer instead of silently dropping the entire shot.
            bool unseenTerminal = shot != null && shot.shotId > 0 &&
                                  shot.shotId != lastVisibleProjectileShotId &&
                                  !shot.active && Valid(shot.originM) &&
                                  (Valid(shot.impactM) || Valid(shot.positionM));
            if (unseenTerminal)
            {
                lastVisibleProjectileShotId = shot.shotId;
                projectileVisual.SetActive(true);
                Vector3 origin = MapMujocoPointToCameraFrame(CoordinateMapper.World(shot.originM));
                float[] terminalM = Valid(shot.impactM) ? shot.impactM : shot.positionM;
                Vector3 terminal = MapMujocoPointToCameraFrame(CoordinateMapper.World(terminalM));
                projectileVisual.transform.position = terminal;
                if (projectileTrail != null)
                {
                    projectileTrail.Clear();
                    projectileTrail.emitting = false;
                    projectileTrail.AddPosition(origin);
                    projectileTrail.AddPosition(terminal);
                }
                projectileVisualWasActive = false;
                projectileVisualUntil = Time.unscaledTime + 0.10f;
                return;
            }
            if (projectileVisualWasActive)
            {
                projectileVisualWasActive = false;
                projectileVisualUntil = Time.unscaledTime + 0.10f;
                if (shot != null && Valid(shot.impactM))
                    projectileVisual.transform.position = MapMujocoPointToCameraFrame(
                        CoordinateMapper.World(shot.impactM));
                if (projectileTrail != null) projectileTrail.emitting = false;
            }
        }

        private void SyncRuleAreas()
        {
            if (ruleAreaBounds.Count == 0) return;
            RefreshSceneCache();
            foreach (var entry in ruleAreaBounds)
            {
                AreaController area = entry.Key;
                if (area == null) continue;
                Bounds region = entry.Value;
                foreach (RefereeController robot in cachedReferees)
                {
                    if (robot == null || !robot.IsSpawned || robot.robotTags.Contains(RobotTag.Building)) continue;
                    Vector3 position = robot.transform.position;
                    // Occupancy is planar: MuJoCo's ground height must not be
                    // compared with the old trigger's vertical placement.
                    bool inside = position.x >= region.min.x && position.x <= region.max.x &&
                                  position.z >= region.min.z && position.z <= region.max.z;
                    area.SyncMujocoOccupant(robot, inside);
                }
            }
        }

        private void ReconcilePendingShot(global::ProjectileState projectile)
        {
            if (pendingShots.Count == 0 || projectile == null ||
                projectile.shotId <= pendingStartShotId || projectile.shotId <= lastDebitedShotId) return;
            KeyValuePair<int, string> pending = pendingShots.Peek();
            if (projectile.shooterId != pending.Key || projectile.roundName != pending.Value) return;
            pendingShots.Dequeue();
            RefereeController referee = FindRuleRobot(pending.Key);
            if (referee == null || !referee.RecordMujocoShot(pending.Value))
                Debug.LogError($"[MuJoCo] Shot {projectile.shotId} exists in physics but could not be debited by referee.");
            else
            {
                lastDebitedShotId = projectile.shotId;
            }
        }

        private void ApplyProjectileHit(global::ProjectileState shot)
        {
            if (mujocoField == null || shot == null || shot.shotId <= 0 ||
                !Valid(shot.positionM)) return;
            Vector3 current = mujocoField.transform.TransformPoint(new Vector3(
                shot.positionM[0], shot.positionM[1], shot.positionM[2]));
            if (trackedShotId != shot.shotId)
            {
                trackedShotId = shot.shotId;
                hasPreviousProjectileWorld = false;
                if (Valid(shot.originM))
                {
                    previousProjectileWorld = mujocoField.transform.TransformPoint(new Vector3(
                        shot.originM[0], shot.originM[1], shot.originM[2]));
                    hasPreviousProjectileWorld = true;
                }
            }
            if (impactedShotId != shot.shotId && hasPreviousProjectileWorld)
            {
                Vector3 delta = current - previousProjectileWorld;
                float distance = delta.magnitude;
                if (distance > 0.001f)
                {
                    Ray ray = new Ray(previousProjectileWorld, delta / distance);
                    ArmorController hitArmor = null;
                    RefereeController victim = null;
                    float nearest = distance;
                    seenArmorColliders.Clear();
                    RefreshSceneCache();
                    foreach (RefereeController candidate in cachedReferees)
                    {
                        if (candidate == null || !candidate.IsSpawned || candidate.RobotID.Value == shot.shooterId) continue;
                        bool building = candidate.robotTags.Contains(RobotTag.Building);
                        if (!armorCache.TryGetValue(candidate, out ArmorController[] armors))
                        {
                            armors = candidate.GetComponentsInChildren<ArmorController>(true);
                            armorCache[candidate] = armors;
                        }
                        foreach (ArmorController armor in armors)
                        {
                            Collider target = armor.ArmorCollider;
                            if (!armor.Enabled || target == null || !seenArmorColliders.Add(target)) continue;
                            Bounds bounds;
                            if (building)
                            {
                                if (!buildingArmorBounds.TryGetValue(armor, out bounds)) continue;
                            }
                            else
                            {
                                if (!target.enabled) continue;
                                bounds = target.bounds;
                            }
                            bounds.Expand(0.35f);
                            if (!bounds.IntersectRay(ray, out float hitAt) || hitAt < 0f || hitAt > nearest) continue;
                            nearest = hitAt;
                            hitArmor = armor;
                            victim = candidate;
                        }
                    }
                    if (victim != null && victim.RecordMujocoImpact(hitArmor, shot.roundName, shot.shooterId))
                        impactedShotId = shot.shotId;
                }
            }
            previousProjectileWorld = current;
            hasPreviousProjectileWorld = shot.active;
        }

        private void UpdateRosterRobotPoses(MujocoState state)
        {
            if (mujocoField == null || state.robots == null) return;
            Quaternion backendToUnity = mujocoField.transform.rotation *
                Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f));
            RefreshSceneCache();
            foreach (RobotController controller in cachedRobots)
            {
                if (robots.ContainsKey(controller)) continue;
                RefereeController referee = controller.GetComponent<RefereeController>();
                if (referee == null || !referee.IsSpawned || referee.robotTags.Contains(RobotTag.Building)) continue;
                RobotState pose = FindRobot(state.robots, referee.RobotID.Value);
                if (pose == null || !string.Equals(pose.source, "mujoco", StringComparison.OrdinalIgnoreCase)) continue;
                Transform chassis = GetPrivateTransform(controller, "Base") ?? controller.transform;
                // Match the body's actual MuJoCo height. A fixed 0.16 m put
                // the unselected roster at a different elevation from the
                // selected robot and the rendered field.
                Vector3 world = mujocoField.transform.TransformPoint(new Vector3(pose.x, pose.y,
                    pose.z > 0.01f ? pose.z : 0.16f));
                Quaternion rotation = backendToUnity * Quaternion.Euler(0f, -pose.yawDeg, 0f);
                controller.transform.rotation = rotation * Quaternion.Inverse(
                    Quaternion.Inverse(controller.transform.rotation) * chassis.rotation);
                controller.transform.position += world - chassis.position;
                AlignToVisualField(controller.transform, chassis, null);
            }
        }

        private void AlignToVisualField(Transform root, Transform chassis, BoundRobot selected)
        {
            if (visualFieldSurface == null || root == null || chassis == null) return;
            Vector3 probe = chassis.position + Vector3.up * 50f;
            if (!visualFieldSurface.Raycast(new Ray(probe, Vector3.down), out RaycastHit hit, 150f)) return;
            float lowest = float.PositiveInfinity;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                // The prefab contains exported HUD/world-space UI meshes with
                // huge bounds; only nearby mechanical geometry defines contact.
                Bounds bounds = renderer.bounds;
                if (Mathf.Abs(bounds.center.x - chassis.position.x) > 15f ||
                    Mathf.Abs(bounds.center.z - chassis.position.z) > 15f ||
                    bounds.extents.magnitude > 20f) continue;
                lowest = Mathf.Min(lowest, bounds.min.y);
            }
            if (float.IsInfinity(lowest)) return;
            float correction = hit.point.y + 0.04f - lowest;
            if (Mathf.Abs(correction) < 0.015f || Mathf.Abs(correction) > 20f) return;
            root.position += Vector3.up * correction;
            if (selected != null) selected.unityOriginPosition += Vector3.up * correction;
        }

        private void ApplyHeroPose(BoundRobot robot, Vector3 mujocoPosition,
            float chassisYawDeg, float turretWorldYawDeg, float barrelPitchDeg)
        {
            Quaternion mujocoRotation = Quaternion.Euler(0f, -chassisYawDeg, 0f);
            if (!robot.calibrated)
            {
                robot.mujocoOriginPosition = mujocoPosition;
                robot.mujocoOriginRotation = mujocoRotation;
                if (mujocoField != null)
                {
                    // A P-panel vehicle switch keeps the already-installed
                    // field frame. Place the new model in that same frame.
                    robot.visualScale = Mathf.Abs(mujocoField.transform.localScale.x);
                    robot.unityOriginPosition = mujocoField.transform.TransformPoint(
                        new Vector3(mujocoPosition.x, mujocoPosition.z, mujocoPosition.y));
                    robot.unityOriginRotation = mujocoField.transform.rotation *
                        Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f)) * mujocoRotation;
                }
                robot.calibrated = true;
            }
            Vector3 localDelta = Quaternion.Inverse(robot.mujocoOriginRotation) *
                (mujocoPosition - robot.mujocoOriginPosition);
            Vector3 unityPosition = robot.unityOriginPosition + robot.unityOriginRotation * (localDelta * robot.visualScale);
            Quaternion unityRotation = robot.unityOriginRotation *
                Quaternion.Inverse(robot.mujocoOriginRotation) * mujocoRotation;
            MoveWholeRobot(robot, unityPosition, unityRotation);
            if (robot.yaw != null)
            {
                float relativeYaw = Mathf.DeltaAngle(chassisYawDeg, turretWorldYawDeg);
                robot.yaw.localRotation = Quaternion.Euler(0f, -relativeYaw, 0f);
            }
            if (robot.pitch != null)
                robot.pitch.localRotation = Quaternion.Euler(0f, 0f, barrelPitchDeg);
        }

        /// <summary>
        /// Locates the original mode arena (RMUT Field / RMUC2023 / RMUC2024)
        /// and returns its world footprint. Call while the legacy renderers are
        /// still enabled: field installation happens before they are hidden.
        /// </summary>
        private static Bounds FindLegacyArenaBounds()
        {
            string[] roots = { "RMUC2024", "RMUC2024(Clone)", "RMUC2023", "RMUC2023(Clone)", "Field" };
            foreach (string rootName in roots)
            {
                foreach (Transform candidate in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (candidate == null || candidate.name != rootName) continue;
                    Bounds bounds = new Bounds(candidate.position, Vector3.zero);
                    bool found = false;
                    foreach (Renderer renderer in candidate.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null || renderer is ParticleSystemRenderer ||
                            renderer.GetComponentInParent<Canvas>() != null) continue;
                        if (!found) { bounds = renderer.bounds; found = true; }
                        else bounds.Encapsulate(renderer.bounds);
                    }
                    if (found && bounds.size.x > 1f && bounds.size.z > 1f) return bounds;
                }
            }
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Re-expresses a legacy-arena rectangle on the imported field's
        /// footprint. Both arenas describe the same physical field, so matching
        /// the footprints keeps a zone where it was authored relative to the
        /// field instead of leaving it at stale metres on the old arena.
        /// </summary>
        private static Bounds RemapArenaBounds(Bounds bounds, Bounds from, Bounds to, bool mirrorX, bool mirrorZ)
        {
            float x0 = NormalizeArenaAxis(from.min.x, from.max.x, bounds.min.x);
            float x1 = NormalizeArenaAxis(from.min.x, from.max.x, bounds.max.x);
            float z0 = NormalizeArenaAxis(from.min.z, from.max.z, bounds.min.z);
            float z1 = NormalizeArenaAxis(from.min.z, from.max.z, bounds.max.z);
            if (mirrorX) { float low = x0; x0 = 1f - x1; x1 = 1f - low; }
            if (mirrorZ) { float low = z0; z0 = 1f - z1; z1 = 1f - low; }
            Vector3 min = new Vector3(
                Mathf.Lerp(to.min.x, to.max.x, x0), bounds.min.y, Mathf.Lerp(to.min.z, to.max.z, z0));
            Vector3 max = new Vector3(
                Mathf.Lerp(to.min.x, to.max.x, x1), bounds.max.y, Mathf.Lerp(to.min.z, to.max.z, z1));
            return new Bounds((min + max) * 0.5f, max - min);
        }

        private static float NormalizeArenaAxis(float min, float max, float value)
        {
            return max - min > 0.0001f ? Mathf.Clamp01((value - min) / (max - min)) : 0.5f;
        }

        private void EnsureMujocoField(BoundRobot robot)
        {
            if (mujocoField != null || !robot.calibrated) return;
            GameObject source = Resources.Load<GameObject>("MuJoCo2026/rmuc2026_field");
            if (source == null)
            {
                Debug.LogError("[MuJoCo] RMUC2026 field OBJ is missing; cannot display a field that matches physics.");
                return;
            }
            mujocoField = Instantiate(source);
            mujocoField.name = "MuJoCo 2026 Visual Field";
            Quaternion backendToUnity = robot.unityOriginRotation * Quaternion.Inverse(robot.mujocoOriginRotation);
            mujocoField.transform.SetPositionAndRotation(
                robot.unityOriginPosition - backendToUnity * (robot.mujocoOriginPosition * robot.visualScale),
                backendToUnity * Quaternion.Euler(90f, 0f, 0f));
            // OBJ/MuJoCo is Z-up; the reflected axis preserves its x/y field layout.
            mujocoField.transform.localScale = new Vector3(robot.visualScale, robot.visualScale, -robot.visualScale);
            foreach (Collider collider in mujocoField.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            MeshFilter surfaceMesh = mujocoField.GetComponentInChildren<MeshFilter>();
            if (surfaceMesh != null && surfaceMesh.sharedMesh != null)
            {
                visualFieldSurface = surfaceMesh.gameObject.AddComponent<MeshCollider>();
                visualFieldSurface.sharedMesh = surfaceMesh.sharedMesh;
                visualFieldSurface.isTrigger = true;
            }
            Bounds fieldBounds = new Bounds(mujocoField.transform.position, Vector3.zero);
            int fieldRenderers = 0;
            foreach (Renderer renderer in mujocoField.GetComponentsInChildren<Renderer>(true))
            {
                if (fieldRenderers++ == 0) fieldBounds = renderer.bounds;
                else fieldBounds.Encapsulate(renderer.bounds);
            }
            Debug.Log($"[MuJoCo] imported field renderers={fieldRenderers} scale={robot.visualScale:F2} bounds={fieldBounds} robot={robot.chassis.position} mount={robot.cameraMount?.position}");
            // The legacy arenas are authored at ten Unity units per metre in
            // their own prefab instances, while the imported 2026 field is
            // drawn at visualScale around the calibrated robot. Rule triggers
            // still live in the legacy arena's world coordinates, so every
            // region is re-expressed on the imported field's footprint before
            // it can answer occupancy. Without this a training buff point sits
            // metres outside the drivable field and can never be stepped on.
            Bounds legacyArena = FindLegacyArenaBounds();
            bool remapRegions = legacyArena.size.x > 1f && legacyArena.size.z > 1f &&
                                fieldBounds.size.x > 1f && fieldBounds.size.z > 1f;
            bool mirrorX = false;
            bool mirrorZ = false;
            if (remapRegions)
            {
                // Both arenas are symmetric fields, so a mirrored axis would
                // silently swap red and blue. Decide each axis from the one
                // point that is known in both frames: the calibrated robot.
                Vector3 legacyOffset = robot.unityOriginPosition - legacyArena.center;
                Vector3 fieldOffset = robot.unityOriginPosition - fieldBounds.center;
                float marginX = legacyArena.size.x * 0.05f;
                float marginZ = legacyArena.size.z * 0.05f;
                mirrorX = Mathf.Abs(legacyOffset.x) > marginX && Mathf.Abs(fieldOffset.x) > marginX &&
                          Mathf.Sign(legacyOffset.x) != Mathf.Sign(fieldOffset.x);
                mirrorZ = Mathf.Abs(legacyOffset.z) > marginZ && Mathf.Abs(fieldOffset.z) > marginZ &&
                          Mathf.Sign(legacyOffset.z) != Mathf.Sign(fieldOffset.z);
            }
            // Cache each original rule region before its collider is disabled.
            // These bounds serve only as non-blocking rule queries.
            ruleAreaBounds.Clear();
            foreach (AreaController area in FindObjectsByType<AreaController>(FindObjectsSortMode.None))
            {
                Collider[] volumes = area.GetComponentsInChildren<Collider>(true);
                bool found = false;
                Bounds bounds = default;
                foreach (Collider volume in volumes)
                {
                    if (volume == null || !volume.isTrigger) continue;
                    if (!found) { bounds = volume.bounds; found = true; }
                    else bounds.Encapsulate(volume.bounds);
                }
                if (found)
                {
                    if (remapRegions) bounds = RemapArenaBounds(bounds, legacyArena, fieldBounds, mirrorX, mirrorZ);
                    ruleAreaBounds.Add(area, bounds);
                }
                else Debug.LogWarning($"[MuJoCo] Rule area {area.name} has no trigger volume; occupancy unavailable.");
            }
            Debug.Log(remapRegions
                ? $"[MuJoCo] Cached {ruleAreaBounds.Count} rule regions on the imported field footprint (mirrorX={mirrorX}, mirrorZ={mirrorZ})."
                : $"[MuJoCo] Cached {ruleAreaBounds.Count} referee regions without a legacy field footprint; they may sit off-field.");
            // MuJoCo owns every collision body and its scene contains no
            // building geometry at all, so a cached base/outpost armor volume
            // could only ever register a hit on empty air. Building damage is
            // therefore unavailable in MuJoCo mode rather than silently
            // misplaced; the dictionary stays empty and the hit ray skips it.
            buildingArmorBounds.Clear();
            Debug.Log("[MuJoCo] MuJoCo scene has no building bodies; legacy building armor volumes are not used for projectile damage.");
            // The old arena is spread over several scene roots, not just the
            // object named RMUC2024. Leave robot prefabs and UI alone, but
            // remove every legacy static prop from both the view and Unity's
            // collision/trigger world. MuJoCo remains the sole drive authority.
            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (renderer != null && !renderer.transform.IsChildOf(mujocoField.transform) &&
                    renderer.GetComponentInParent<RobotController>() == null &&
                    renderer.GetComponentInParent<Canvas>() == null)
                    renderer.enabled = false;
            foreach (Collider collider in FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (collider != null && !collider.transform.IsChildOf(mujocoField.transform) &&
                    collider.GetComponentInParent<RobotController>() == null)
                    collider.enabled = false;
            SimulatorMapCapture.Invalidate();
            Debug.Log("[MuJoCo] RMUC2026 visual field installed from the backend OBJ; RMUC2024 field hidden.");
        }

        private static void ApplyBackendCameraPose(BoundRobot robot, CameraState cameraState, Vector3 mujocoRobotPosition, Quaternion mujocoRobotRotation)
        {
            if (robot == null || cameraState == null || !Valid(cameraState.positionM) ||
                !Valid(cameraState.forwardM) || !Valid(cameraState.upM)) return;
            Vector3 position = CoordinateMapper.World(cameraState.positionM);
            Vector3 forward = CoordinateMapper.WorldVector(cameraState.forwardM).normalized;
            Vector3 up = CoordinateMapper.WorldVector(cameraState.upM).normalized;
            if (forward.sqrMagnitude < 0.5f || up.sqrMagnitude < 0.5f) return;
            Quaternion rotation = Quaternion.LookRotation(forward, up);
            Quaternion backendToUnityRotation = robot.unityOriginRotation * Quaternion.Inverse(robot.mujocoOriginRotation);
            Vector3 unityPosition = robot.chassis.position + backendToUnityRotation * ((position - mujocoRobotPosition) * robot.visualScale);
            Quaternion unityRotation = backendToUnityRotation * rotation;
            robot.mappedCameraPosition = unityPosition;
            robot.mappedCameraRotation = unityRotation;
            robot.hasMappedCameraPose = true;
            // Keep the backend pose for HUD calibration only. Its world
            // coordinates must not override the selected prefab's render view.
        }

        private bool warnedLegacyBackend;
        private int lastControlRobotId = -1;
        private float lastControlDiagnosticAt;
        private GameObject mujocoField;

        private string SelectedRobotClassName()
        {
            foreach (RobotController controller in robots.Keys)
            {
                RefereeController referee = controller != null ? controller.GetComponent<RefereeController>() : null;
                if (referee != null) return referee.robotClass.Value.ToString();
            }
            return string.Empty;
        }

        private string SelectedRobotClass(int id)
        {
            foreach (RobotController controller in robots.Keys)
            {
                RefereeController referee = controller != null ? controller.GetComponent<RefereeController>() : null;
                if (referee != null && referee.RobotID.Value == id) return referee.robotClass.Value.ToString();
            }
            return string.Empty;
        }

        private bool IsSelectedLocalRobot(RobotController controller, RefereeController referee)
        {
            // The OSS scenes pre-place a full lineup of scene robots. In the
            // legacy build the player's vehicle is a runtime-spawned prefab
            // with the same RobotID, so the scene-object equality rule below
            // would never match it. Ownership plus the selection state are the
            // authoritative identity here.
            if (LegacyMode)
            {
                NetworkObject playerObject = controller.GetComponent<NetworkObject>();
                if (playerObject == null) return false;
                bool runtimeSpawned = playerObject.IsSceneObject != true;
                if (!runtimeSpawned)
                {
                    // Prefer the spawned player prefab; a scene robot (for
                    // example the rule-managed sentry) is used only when the
                    // game did not create a runtime copy for that slot.
                    foreach (RefereeController other in cachedReferees)
                    {
                        if (other == null || other == referee || !other.IsSpawned || !other.IsOwner) continue;
                        if (other.RobotID.Value != referee.RobotID.Value ||
                            other.faction.Value != referee.faction.Value) continue;
                        NetworkObject otherObject = other.GetComponent<NetworkObject>();
                        if (otherObject != null && otherObject.IsSceneObject != true) return false;
                    }
                }
                return PacificSelectionState.TryGetSelection(out _, out _) &&
                       PacificSelectionState.Matches(referee.RobotID.Value,
                           referee.faction.Value == Faction.Blue ? 1 : 0);
            }
            // Exact actual simulator identity (including the blue 20-offset),
            // not merely a display ID. In MuJoCo mode Spawn selects one of the
            // numbered scene robots instead of instantiating a duplicate.
            NetworkObject networkObject = controller.GetComponent<NetworkObject>();
            bool hasSceneRobot = false;
            RefreshSceneCache();
            foreach (RefereeController candidate in cachedReferees)
            {
                NetworkObject objectInScene = candidate.GetComponent<NetworkObject>();
                if (objectInScene != null && objectInScene.IsSpawned && objectInScene.IsSceneObject == true &&
                    candidate.RobotID.Value == referee.RobotID.Value && candidate.faction.Value == referee.faction.Value)
                {
                    hasSceneRobot = true;
                    break;
                }
            }
            return PacificSelectionState.TryGetSelection(out _, out _) &&
                   networkObject != null && networkObject.IsSceneObject == hasSceneRobot &&
                   PacificSelectionState.Matches(referee.RobotID.Value,
                       referee.faction.Value == Faction.Blue ? 1 : 0);
        }

        private static void MoveWholeRobot(BoundRobot robot, Vector3 chassisPosition, Quaternion chassisRotation)
        {
            if (robot.root != null && robot.chassis.IsChildOf(robot.root))
            {
                // Base is not the parent of the Pitch/FPV branch in the source
                // prefab. Moving Base alone changes the minimap pose while the
                // render camera remains fixed. Move the common robot root.
                robot.root.rotation = chassisRotation * Quaternion.Inverse(robot.chassisLocalRotation);
                robot.root.position += chassisPosition - robot.chassis.position;
            }
            else
                robot.chassis.SetPositionAndRotation(chassisPosition, chassisRotation);
        }

        private static int DisplayRobotId(int actualId) => actualId >= 20 ? actualId - 20 : actualId;

        /// <summary>
        /// IDs the P panel may offer. RMUC scenes use the SpawnManager roster
        /// (1-7 / 21-27). Training Ground has no SpawnManager, and its
        /// RoleSelectionMenu only registers a roster in MuJoCo mode. In the
        /// legacy build any slot can be instantiated on demand, so offer the
        /// full 1-7 / 21-27 list instead of only the cars already in the scene.
        /// </summary>
        private static IEnumerable<int> PanelRobotIds(RoleSelectionMenu training)
        {
            if (FindFirstObjectByType<SpawnManager>() != null) return null;
            if (LegacyMode) return null;
            if (training != null)
            {
                foreach (int id in training.MujocoRosterIds) return training.MujocoRosterIds;
            }
            return null;
        }

        private void SwitchSelectedRobot(int actualId)
        {
            if (LegacyMode)
            {
                int legacyDisplayId = DisplayRobotId(actualId);
                int legacyFaction = actualId >= 20 ? 1 : 0;
                if (legacyDisplayId < 1 || legacyDisplayId > 7) return;
                SpawnManager legacySpawn = FindFirstObjectByType<SpawnManager>();
                // Training only instantiates the vehicle picked on the spawn
                // screen. Build any other roster slot on demand so the P panel
                // can switch to every car, not just the ones already present.
                RoleSelectionMenu legacyTraining = legacySpawn == null
                    ? FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include)
                    : null;
                if (legacyTraining != null && !legacyTraining.EnsureLegacyVehicle(actualId))
                {
                    Debug.LogWarning($"[LegacyHud] Robot {actualId} is not available in this scene; selection unchanged.");
                    return;
                }
                PacificSelectionState.CompleteActual(actualId, legacyFaction);
                // Drop the old binding first so the HUD cannot keep reporting
                // the previous chassis while the new one is being enabled.
                robots.Clear();
                if (legacySpawn != null)
                    legacySpawn.SelectRosterRobotServerRpc(legacyFaction == 1 ? 2 : 1, legacyDisplayId - 1);
                else
                    LegacyRoster.Select(actualId);
                nextRobotDiscoveryAt = 0f;
                DiscoverRobots(force: true);
                Debug.Log($"[LegacyHud] Switched local control to robot {actualId}.");
                return;
            }
            int displayId = DisplayRobotId(actualId);
            int faction = actualId >= 20 ? 1 : 0;
            if (displayId < 1 || displayId > 7 || !PacificSelectionState.Completed) return;
            if (actualId == CurrentSelectedActualRobotId()) return;
            SpawnManager spawn = FindFirstObjectByType<SpawnManager>();
            RoleSelectionMenu training = FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include);
            if (spawn == null && (training == null || !training.SelectMujocoRobot(actualId))) return;
            controls.SetOperatorReady(false);
            foreach (BoundRobot robot in robots.Values)
                if (robot.fpvCamera != null) robot.fpvCamera.enabled = false;
            confirmedBackendRobotId = -1;
            ammoConfiguredRobotId = -1;
            lastControlRobotId = -1;
            robots.Clear();
            // Bind the new vehicle on the very next state packet instead of
            // waiting for the discovery cache to expire.
            nextRobotDiscoveryAt = 0f;
            PacificSelectionState.Complete(displayId, faction);
            if (spawn != null) spawn.SelectRosterRobotServerRpc(faction == 1 ? 2 : 1, displayId - 1);
            Debug.Log($"[MuJoCo] P-panel selected existing roster robot {actualId}; waiting for ownership and backend acknowledgment.");
        }

        private void MergeRuleState(MujocoState state)
        {
            RefreshSceneCache();
            GameManager manager = cachedManager;
            mergedRobotBuffer.Clear();
            string sceneName = SceneManager.GetActiveScene().name;
            // Use the original simulator's authoritative RefereeController
            // NetworkVariables. RefereeControllerList is server-populated in
            // Start/RPC order and is transiently empty during Spawn; relying
            // on it alone caused official HUD cards to disappear or fall back
            // to MuJoCo demo values on clients.
            foreach (RefereeController referee in cachedReferees)
            {
                if (referee == null || !referee.IsSpawned || referee.robotTags.Contains(RobotTag.Building)) continue;
                int actualId = referee.RobotID.Value;
                int displayId = actualId >= 20 ? actualId - 20 : actualId;
                if (displayId < 1 || displayId > 7) continue;
                if (!ruleRobots.TryGetValue(actualId, out RefereeController ruleRobot) || ruleRobot != referee) continue;
                int faction = referee.faction.Value == Faction.Blue ? 1 : 0;
                int key = RobotSampleKey(actualId, faction);
                if (!mergedRobotCache.TryGetValue(key, out RobotMasterRobot sample))
                {
                    sample = new RobotMasterRobot();
                    mergedRobotCache[key] = sample;
                }
                // Backend robot IDs are actual referee IDs (red 1-7,
                // blue 21-27); retain actual ID so map/HUD bind unambiguously.
                sample.id = actualId;
                sample.faction = faction;
                sample.hp = Mathf.RoundToInt(referee.HP.Value);
                sample.maxHp = Mathf.RoundToInt(referee.HPLimit.Value);
                // Training is unlimited; match modes keep the finite
                // referee ledger. The HUD renders -1 as an infinity mark.
                sample.ammo = sceneName == "Training Ground"
                    ? -1
                    : Mathf.Max(0, referee.Ammo0.Value + referee.Ammo1.Value);
                sample.energy = Mathf.RoundToInt(referee.Energy.Value);
                sample.economy = manager != null ? SafeList(manager.Coins, faction) : 0;
                int buffMask = (referee.ATKBuff.Value > 0 ? 1 : 0) | (referee.DEFBuff.Value > 0 ? 2 : 0) |
                               (referee.HealBuff.Value > 0 ? 4 : 0) | (referee.CDBuff.Value > 0 ? 8 : 0);
                if (!mergedBuffMask.TryGetValue(key, out int previousMask) || previousMask != buffMask)
                {
                    mergedBuffMask[key] = buffMask;
                    sample.buffs = BuildBuffList(buffMask);
                }
                mergedRobotBuffer.Add(sample);
            }
            if (state.robotMaster == null) state.robotMaster = new global::RobotMasterState();
            state.robotMaster.connected = true;
            // A live RefereeController list is authoritative.  If the scene is
            // still between Host/Spawn and the list is not ready, retain the
            // backend snapshot instead of replacing it with an empty HUD.
            if (mergedRobotBuffer.Count > 0)
            {
                if (mergedRobotArray.Length != mergedRobotBuffer.Count)
                    mergedRobotArray = new RobotMasterRobot[mergedRobotBuffer.Count];
                for (int i = 0; i < mergedRobotBuffer.Count; i++) mergedRobotArray[i] = mergedRobotBuffer[i];
                state.robotMaster.simulated = false;
                state.robotMaster.connected = true;
                state.robotMaster.robots = mergedRobotArray;
            }

            BoundRobot local = null;
            foreach (BoundRobot candidate in robots.Values) { local = candidate; break; }
            RefereeController localReferee = null;
            if (local != null)
                foreach (RobotController controller in robots.Keys)
                    if (controller != null && robots[controller] == local)
                    { localReferee = controller.GetComponent<RefereeController>(); break; }
            int localActualId = localReferee != null ? localReferee.RobotID.Value : 1;
            int localFaction = localReferee != null && localReferee.faction.Value == Faction.Blue ? 1 : 0;
            if (manager != null && manager.IsSpawned)
            {
                if (rulesState == null) rulesState = new MatchRuleState();
                rulesState.timeLeftS = manager.TimeLeft.Value;
                rulesState.gameStatus = manager.GameStatus.Value;
                rulesState.redCoins = SafeList(manager.Coins, 0);
                rulesState.blueCoins = SafeList(manager.Coins, 1);
                rulesState.redBaseHp = Hp(manager.RedBase);
                rulesState.redBaseMaxHp = MaxHp(manager.RedBase);
                rulesState.blueBaseHp = Hp(manager.BlueBase);
                rulesState.blueBaseMaxHp = MaxHp(manager.BlueBase);
                rulesState.redOutpostHp = Hp(manager.RedOutpost);
                rulesState.redOutpostMaxHp = MaxHp(manager.RedOutpost);
                rulesState.blueOutpostHp = Hp(manager.BlueOutpost);
                rulesState.blueOutpostMaxHp = MaxHp(manager.BlueOutpost);
                rulesState.localRobotId = localActualId;
                rulesState.localFaction = localFaction;
                state.rules = rulesState;
            }
            else
            {
                // The bundled MuJoCo service marks its fallback RobotMaster
                // payload simulated=true. Never promote it to live match rules.
                state.rules = null;
                if (state.robotMaster != null && state.robotMaster.simulated)
                    state.robotMaster.connected = false;
            }
        }

        private static int SafeList(Unity.Netcode.NetworkList<int> values, int index) => values != null && index >= 0 && index < values.Count ? values[index] : 0;
        private static int Hp(RefereeController value) => value != null ? Mathf.RoundToInt(value.HP.Value) : 0;
        private static int MaxHp(RefereeController value) => value != null ? Mathf.RoundToInt(value.HPLimit.Value) : 0;
        private static readonly string[] NoBuffs = Array.Empty<string>();
        private static string[] BuildBuffList(int mask)
        {
            if (mask == 0) return NoBuffs;
            var buffs = new List<string>(4);
            if ((mask & 1) != 0) buffs.Add("attack");
            if ((mask & 2) != 0) buffs.Add("defense");
            if ((mask & 4) != 0) buffs.Add("heal");
            if ((mask & 8) != 0) buffs.Add("cooldown");
            return buffs.ToArray();
        }

        private void ConfigureSupportedAmmo(RefereeController referee, int actualId)
        {
            if (client == null || referee == null || ammoConfiguredRobotId == actualId) return;
            bool supports17 = referee.has17mmShooter();
            bool supports42 = referee.has42mmShooter();
            string loaded = client.State?.ammo?.name;
            if ((loaded == "17mm" && supports17) || (loaded == "42mm" && supports42))
            {
                ammoConfiguredRobotId = actualId;
                return;
            }

            string compatible = supports42 ? "42mm" : supports17 ? "17mm" : null;
            if (!string.IsNullOrEmpty(compatible))
            {
                client.SetAmmo(compatible);
                Debug.Log($"[MuJoCo] Selected robot {actualId} supports {compatible}; loading that round for left-click fire.");
            }
            ammoConfiguredRobotId = actualId;
        }

        // Legacy cursor policy. The OSS scripts each write Cursor from their
        // own Update (RobotController, FPVController); a host owns every
        // server-spawned car, so those writes used to capture the pointer
        // while the role/spawn screen was still up. This runs last and is the
        // single authority: interactive screens get a visible pointer (placed
        // in the centre when a new screen appears), and only the selected
        // first-person operator loses it.
        private int legacyCursorUiMask;
        private bool legacyCursorWasUi;
        private bool legacyCursorInitialised;
        private bool legacyCursorHadFocus = true;
        private bool legacyPointerOwnedByUi;
        private bool legacyOperatorReady;
        private bool legacyControlModeGyro;

        // Same rates the custom UI backend applies to its control packets:
        // 90 deg/s turret yaw and 30 deg/s barrel pitch at full mouse axis.
        private const float LegacyYawDegreesPerSecond = 90f;
        private const float LegacyPitchDegreesPerSecond = 30f;
        private const float LegacyManualPitchMin = -10f;
        private const float LegacyManualPitchMax = 45f;
        // The custom UI's funnel camera rides 0.14 m above the bore; the OSS
        // arena runs at 10 units per metre.
        private const float LegacyBoreCameraOffset = 1.4f;

        /// <summary>
        /// ControlInput's command packet, applied to the selected OSS robot
        /// instead of being sent over TCP. This is the custom UI client's
        /// WASD/mouse behaviour with the original simulator's physics.
        /// </summary>
        private void ApplyLegacyControl(float forward, float strafe, float chassisYaw,
            float turretYaw, float pitch, bool boost, bool brake)
        {
            RobotController robot = SelectedLegacyRobot();
            if (robot == null) return;

            bool uiOpen = legacyPointerOwnedByUi || FPVController.UiPointerRequested ||
                          MujocoModeRulesPanel.AnyOpen ||
                          RoleSelectionMenu.OpenMenus > 0 || ClassSelectionMenu.OpenMenus > 0 ||
                          (controls != null && controls.UiBlocked);
            bool firing = controls != null && controls.PrimaryFireHeld && !uiOpen;
            if (uiOpen)
            {
                forward = strafe = chassisYaw = turretYaw = pitch = 0f;
                boost = false;
            }
            if (brake) forward = strafe = 0f;

            robot.moveDirectionX.Value = Mathf.Clamp(strafe, -1f, 1f);
            robot.moveDirectionY.Value = Mathf.Clamp(forward, -1f, 1f);

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            // The backend's world yaw runs opposite to Unity's +Y euler.
            robot.yawTargetAngle.Value = Mathf.Repeat(
                robot.yawTargetAngle.Value - turretYaw * LegacyYawDegreesPerSecond * dt, 360f);
            robot.pitchTargetAngle.Value = Mathf.Clamp(
                robot.pitchTargetAngle.Value + pitch * LegacyPitchDegreesPerSecond * dt,
                LegacyManualPitchMin, LegacyManualPitchMax);

            robot.IsBoost.Value = boost;
            robot.IsShoot.Value = firing;
            bool spin = legacyControlModeGyro && Mathf.Abs(chassisYaw) > 0.01f;
            robot.IsSpin.Value = spin;
            if (spin) robot.SpinDirection.Value = Mathf.Sign(chassisYaw);
        }

        private void ApplyLegacyControlMode(bool gyro)
        {
            legacyControlModeGyro = gyro;
            RobotController robot = SelectedLegacyRobot();
            if (robot != null && !gyro) robot.IsSpin.Value = false;
        }

        private void ApplyLegacyAmmo(string roundName)
        {
            RobotController robot = SelectedLegacyRobot();
            if (robot != null) robot.SelectLegacyAmmo(roundName);
        }

        private RobotController SelectedLegacyRobot()
        {
            foreach (RobotController controller in robots.Keys)
                if (controller != null) return controller;
            return null;
        }

        private void UpdateLegacyCursor()
        {
            bool menus = RoleSelectionMenu.OpenMenus > 0 || ClassSelectionMenu.OpenMenus > 0;
            bool rules = MujocoModeRulesPanel.AnyOpen;
            bool panelsOpen = (panels != null && panels.IsModal) ||
                              (controls != null && controls.UiNeedsCursor) ||
                              FPVController.UiPointerRequested;
            int uiMask = (menus ? 1 : 0) | (rules ? 2 : 0) | (panelsOpen ? 4 : 0);

            bool gameplayOwnsPointer = ObserverController.OwnedObservers > 0 ||
                                       (PacificSelectionState.Completed && robots.Count > 0);
            bool uiOwnsPointer = uiMask != 0 || !gameplayOwnsPointer;

            if (uiOwnsPointer)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                // Recentre whenever a different interactive screen takes over
                // (rules → spawn menu, first-person → P panel, ...) and on
                // returning from Alt-Tab while a menu is open.
                bool refocused = Application.isFocused && !legacyCursorHadFocus;
                if (!legacyCursorInitialised || !legacyCursorWasUi ||
                    uiMask != legacyCursorUiMask || refocused)
                    StartCoroutine(CentreLegacyCursor());
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            legacyPointerOwnedByUi = uiOwnsPointer;
            legacyCursorWasUi = uiOwnsPointer;
            legacyCursorUiMask = uiOwnsPointer ? uiMask : 0;
            legacyCursorInitialised = true;
            legacyCursorHadFocus = Application.isFocused;

            // ControlInput reads mouse axes only while its own capture flag is
            // set. The bridge owns the real pointer, so mirror its decision
            // into that flag (SetCursorState is a no-op for Cursor in passive
            // mode but still records the state).
            if (controls != null && legacyOperatorReady)
            {
                if (uiOwnsPointer) controls.SetCursorCaptured(false);
                else if (Application.isFocused) controls.SetCursorCaptured(true);
            }
        }

        private IEnumerator CentreLegacyCursor()
        {
            // Wait for the panel's layout pass before resolving the screen
            // centre; Input System coordinates are relative to the game view.
            yield return new WaitForEndOfFrame();
            if (!Application.isFocused || Mouse.current == null) yield break;
            Mouse.current.WarpCursorPosition(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
        }

        private void LateUpdate()
        {
            if (LegacyMode)
            {
                // Refresh the bound robot before deciding whether the pointer
                // belongs to a menu or to the first-person operator.
                DiscoverRobots();
                UpdateLegacyTurret();
                if (controls != null)
                {
                    bool ready = PacificSelectionState.Completed && robots.Count > 0;
                    if (ready != legacyOperatorReady)
                    {
                        legacyOperatorReady = ready;
                        controls.SetOperatorReady(ready);
                        if (ready) controls.SetCursorCaptured(true);
                    }
                }
                UpdateLegacyCursor();
                // Same custom loading page as the MuJoCo build: it covers the
                // Spawn → HUD transition and disappears once the HUD exists.
                UpdateLoadingScreen();
                PublishLegacyHudState();
                return;
            }
            if (pendingShots.Count > 0 && client != null && client.IsConnected &&
                Time.unscaledTime - pendingShotAt > 2f)
            {
                Debug.LogWarning($"[MuJoCo] Fire request unresolved after reconnect (baseline shot {pendingStartShotId}, latest {latestState?.projectile?.shotId}); releasing input gate without referee debit.");
                pendingShots.Clear();
            }
            if (client != null && client.IsConnected && Time.unscaledTime >= nextBackendSelectionCheck)
            {
                nextBackendSelectionCheck = Time.unscaledTime + 0.25f;
                foreach (RobotController controller in robots.Keys)
                {
                    RefereeController referee = controller != null ? controller.GetComponent<RefereeController>() : null;
                    if (referee != null)
                    {
                        int actualId = referee.RobotID.Value;
                        client.SelectRobot(actualId, referee.robotClass.Value.ToString());
                        if (confirmedBackendRobotId != actualId && Time.unscaledTime - lastControlDiagnosticAt > 2f)
                        {
                            lastControlDiagnosticAt = Time.unscaledTime;
                            Debug.LogWarning($"[MuJoCo] Waiting for backend selection acknowledgment: Unity robot={actualId}/{referee.robotClass.Value}, backend={latestState?.selectedRobotId}/{latestState?.selectedRobotClass}. Input remains routed to robot {actualId}.");
                        }
                        else if (confirmedBackendRobotId == actualId && lastControlRobotId != actualId)
                        {
                            lastControlRobotId = actualId;
                            ConfigureSupportedAmmo(referee, actualId);
                            controls.SetOperatorReady(true);
                            Debug.Log($"[MuJoCo] Operator binding confirmed: actual robot={actualId}, role={referee.robotClass.Value}, owner={referee.IsOwner}. W/S move, A/D strafe, mouse aims.");
                        }
                    }
                    break;
                }
            }
            UpdateRenderedRobotPose();
            UpdateRenderedProjectile();
            if (projectileVisual != null && projectileVisual.activeSelf && !projectileVisualWasActive &&
                Time.unscaledTime >= projectileVisualUntil)
                projectileVisual.SetActive(false);
            PositionAuxiliaryCameras();
            ApplyOperatorCameraPose();
            if (officialCanvas != null && cameraModes != null)
                officialCanvas.enabled = cameraModes.ActiveMode == 1;
            UpdateLoadingScreen();
        }

        // Legacy build: the HUD subscribes to MujocoClient.StateUpdated just as
        // it does against the backend. This assembles the same snapshot from
        // the OSS RefereeController/GameManager objects at 30 Hz.
        // Each publish allocates a fresh state: the HUD compares state objects
        // by reference, so mutating one instance in place would freeze it after
        // the first frame.
        private float nextLegacyPublishAt;
        private bool loggedLegacyAim;

        private void PublishLegacyHudState()
        {
            if (!PacificSelectionState.Completed) return;
            if (Time.unscaledTime < nextLegacyPublishAt) return;
            // The official settings panel edits ControlInput's sensitivity;
            // route it into the original look path so the slider is real.
            if (controls != null)
            {
                RobotController.LegacyYawSensitivity =
                    Mathf.Clamp(controls.mouseYawSensitivity / 0.75f, 0.1f, 3f);
                RobotController.LegacyPitchSensitivity =
                    Mathf.Clamp(controls.mousePitchSensitivity / 0.55f, 0.1f, 3f);
            }
            // The install coroutine exits once the HUD is built, so this 30 Hz
            // path is also the one that re-binds after a P-panel switch.
            DiscoverRobots();
            if (robots.Count == 0) return;
            nextLegacyPublishAt = Time.unscaledTime + 1f / 30f;
            MujocoState state = new MujocoState();
            state.schema = "legacy.oss";
            state.connected = true;
            state.frame = new FrameState
            {
                bounds = new FrameBounds
                {
                    xMin = -LegacyMapWorldWidth * 0.5f,
                    xMax = LegacyMapWorldWidth * 0.5f,
                    yMin = -LegacyMapWorldDepth * 0.5f,
                    yMax = LegacyMapWorldDepth * 0.5f,
                },
            };
            RefreshSceneCache();
            var poses = new List<RobotState>();
            // ruleRobots prefers the runtime-spawned player prefab over the
            // scene robot with the same ID, so markers never jitter between
            // the two copies of a vehicle.
            foreach (RefereeController referee in ruleRobots.Values)
            {
                if (referee == null || !referee.IsSpawned || referee.robotTags.Contains(RobotTag.Building)) continue;
                RobotController controller = referee.GetComponent<RobotController>();
                Transform chassis = controller != null
                    ? (GetPrivateTransform(controller, "Base") ?? controller.transform)
                    : referee.transform;
                Vector3 position = chassis != null ? chassis.position : referee.transform.position;
                poses.Add(new RobotState
                {
                    id = referee.RobotID.Value,
                    source = "mujoco",
                    robotClass = referee.robotClass.Value.ToString(),
                    x = position.x,
                    y = position.z,
                    z = position.y,
                    yawDeg = -chassis.eulerAngles.y,
                });
            }
            state.robots = poses.ToArray();
            // Rules cards, HP, ammo and buffs come from the original referee
            // NetworkVariables; this call only fills the HUD model.
            MergeRuleState(state);
            foreach (KeyValuePair<RobotController, BoundRobot> pair in robots)
            {
                BoundRobot local = pair.Value;
                RefereeController referee = pair.Key != null ? pair.Key.GetComponent<RefereeController>() : null;
                if (referee == null) continue;
                state.selectedRobotId = local.id;
                state.selectedRobotClass = referee.robotClass.Value.ToString();
                RobotController controller = referee.GetComponent<RobotController>();
                // The custom UI client's mode indicator and right-Shift ammo
                // toggle read these fields from the state packet.
                state.controlMode = legacyControlModeGyro ? "gyro" : "follow";
                if (controller != null)
                    state.ammo = new AmmoState
                    {
                        name = controller.CurrentAmmoType() == 1 ? "42mm" : "17mm",
                    };
                // The original simulator exposes the gimbal pitch as the Pitch
                // hinge's Z angle; it is the same quantity the MuJoCo backend
                // reports as barrelPitchDeg, so the pitch scale can run here too.
                float pitchDeg = controller != null ? controller.currentPitchAngle.Value : 0f;
                state.hero = new HeroState
                {
                    id = local.id,
                    robotClass = state.selectedRobotClass,
                    yawDeg = local.root != null ? local.root.eulerAngles.y : 0f,
                    turretWorldYawDeg = local.yaw != null ? local.yaw.eulerAngles.y : 0f,
                    barrelPitchDeg = pitchDeg,
                };
                state.aim = BuildLegacyAim(referee, pitchDeg);
                // Shooters are separate network roots and can be spawned after
                // the chassis, re-reading a disabled prefab default. Keep the
                // selected vehicle's barrels enabled, and keep training ammo
                // topped up (the OSS training scene has no infinite-ammo rule).
                if (referee.IsServer)
                {
                    if (!referee.Enabled.Value) referee.Enabled.Value = true;
                    if (!referee.ShooterEnabled.Value) referee.ShooterEnabled.Value = true;
                    foreach (ShooterController shooter in referee.GetComponentsInChildren<ShooterController>(true))
                    {
                        if (shooter == null) continue;
                        // The ARTINX prefabs weld the shooter to the Pitch with
                        // a FixedJoint on a gravity-enabled body. The body falls
                        // away from the robot (the muzzle and its funnel end up
                        // underground). Making it kinematic stops the fall, but
                        // the joint then anchors the whole chassis to the world
                        // and WASD goes dead -- so drop the joint as well.
                        // A kinematic body still refuses to follow its parent
                        // and a FixedJoint to it pins the Pitch (and therefore
                        // the whole car) to the world -- WASD and the mouse
                        // both go dead. Remove the physics from the mount
                        // entirely; it only needs to ride the Pitch transform.
                        foreach (Joint joint in shooter.GetComponentsInChildren<Joint>(true))
                            Destroy(joint);
                        foreach (Collider collider in shooter.GetComponentsInChildren<Collider>(true))
                        {
                            collider.enabled = false;
                            Destroy(collider);
                        }
                        Rigidbody shooterBody = shooter.GetComponent<Rigidbody>();
                        if (shooterBody != null)
                        {
                            shooterBody.useGravity = false;
                            shooterBody.isKinematic = true;
                            Destroy(shooterBody);
                        }
                        if (!shooter.Enabled.Value)
                        {
                            shooter.Enabled.Value = true;
                            Debug.Log($"[LegacyHud] Enabled shooter {shooter.ID} on robot {referee.RobotID.Value} " +
                                      $"(server={referee.IsServer}, ammo0={referee.Ammo0.Value}, ammo1={referee.Ammo1.Value}).");
                        }
                        // Training's RMUT_GameManager never runs OnPerformanceChange,
                        // so SpeedLimit stays 0 and PullTrigger spawns a round with
                        // zero velocity -- it never leaves the muzzle. The funnel
                        // already falls back to the OSS speed; give the actual
                        // shooter the same value so the tracer (and the round)
                        // really moves.
                        if (shooter.SpeedLimit.Value <= 0)
                        {
                            int speed = LegacyShooterSpeed(shooter);
                            shooter.SpeedLimit.Value = speed;
                            Debug.Log($"[LegacyHud] Set shooter {shooter.ID} speed={speed} on robot {referee.RobotID.Value}.");
                        }
                    }
                    bool training = SceneManager.GetActiveScene().name == "Training Ground";
                    int ammo0 = training ? 9999 : 50;
                    int ammo1 = training ? 9999 : 10;
                    if (referee.Ammo0.Value < ammo0) referee.Ammo0.Value = ammo0;
                    if (referee.RealAmmo0.Value < ammo0) referee.RealAmmo0.Value = ammo0;
                    if (referee.Ammo1.Value < ammo1) referee.Ammo1.Value = ammo1;
                    if (referee.RealAmmo1.Value < ammo1) referee.RealAmmo1.Value = ammo1;
                    // The practice range never runs the RMUC performance/
                    // level-up loop, so the chassis stays at the level-0
                    // power (55) and the car barely creeps (0.4 units in
                    // two seconds). Give the range a usable power ceiling.
                    if (training && referee.PowerLimit.Value < 200f)
                        referee.PowerLimit.Value = 200f;
                }
                break;
            }
            latestState = state;
            client.PublishLocalState(state);
        }

        private static ShooterController PickLegacyShooter(RefereeController referee)
        {
            ShooterController fallback = null;
            // RefereeController registers its shooters by ID during network
            // spawn. They can be separate network roots, so this registry is
            // more reliable than a child-hierarchy walk.
            if (referee.ShooterControllerList != null)
            {
                foreach (ShooterController shooter in referee.ShooterControllerList.Values)
                {
                    if (shooter == null || shooter.ShootPoint == null) continue;
                    if (shooter.Type.Value == 0 && shooter.Enabled.Value) return shooter;
                    if (fallback == null) fallback = shooter;
                }
            }
            if (fallback != null) return fallback;
            foreach (ShooterController shooter in referee.GetComponentsInChildren<ShooterController>(true))
            {
                if (shooter == null || shooter.ShootPoint == null) continue;
                if (shooter.Type.Value == 0 && shooter.Enabled.Value) return shooter;
                if (fallback == null) fallback = shooter;
            }
            return fallback;
        }

        private static int LegacyShooterSpeed(ShooterController shooter)
        {
            int speed = shooter.SpeedLimit.Value;
            if (speed > 0) return speed;
            RobotPerformanceSO performance = shooter.GimbalPerformance;
            if (performance != null && performance.shootSpeed != null && performance.shootSpeed.Length > 0)
                return performance.shootSpeed[Mathf.Clamp(shooter.Level.Value, 0, performance.shootSpeed.Length - 1)];
            // Training's game manager never configures the shooter, so fall
            // back to the OSS round speeds (metres per second).
            return shooter.Type.Value == 1 ? 16 : 30;
        }

        /// <summary>
        /// Builds the same sight data the MuJoCo backend would send, using the
        /// OSS shooter itself: muzzle pose, SpeedLimit and Unity's rigidbody
        /// integration (gravity + the bullet prefab's linear drag). The funnel
        /// is the live rollout of where this gun points; the range ladder is
        /// solved once with a level barrel, as the official reticle expects.
        /// </summary>
        private AimState BuildLegacyAim(RefereeController referee, float pitchDeg)
        {
            ShooterController shooter = PickLegacyShooter(referee);
            if (shooter == null)
            {
                Debug.LogWarning($"[LegacyHud] Funnel unavailable: no ShooterController on robot {referee.RobotID.Value} " +
                                 $"(registry={referee.ShooterControllerList?.Count ?? -1}, " +
                                 $"children={referee.GetComponentsInChildren<ShooterController>(true).Length}).");
                return null;
            }
            if (shooter.ShootPoint == null)
            {
                Debug.LogWarning($"[LegacyHud] Funnel unavailable: shooter {shooter.ID} has no ShootPoint.");
                return null;
            }
            Transform muzzle = shooter.ShootPoint.transform;
            // Custom UI reference: the funnel is a rollout of the real round
            // from the muzzle site. The camera rides 0.14 m above the bore, so
            // the tip deliberately appears just BELOW the crosshair; it is not
            // a screen-space decoration anchored to the reticle.
            Vector3 origin = muzzle.position;
            Vector3 forward = muzzle.right;
            float speed = LegacyShooterSpeed(shooter) * SourceSimulatorUnitsPerMetre;
            if (speed <= 0.01f || forward.sqrMagnitude < 0.0001f)
            {
                Debug.LogWarning($"[LegacyHud] Funnel unavailable: speed={speed:F1} forward={forward}.");
                return null;
            }

            List<Vector3> live = SimulateLegacyRound(
                origin, forward.normalized * speed, LegacyShooterDrag(shooter),
                LegacyShooterRadius(shooter), out List<float> times, out float tof);
            var points = new FunnelPoint[live.Count];
            for (int i = 0; i < live.Count; i++)
            {
                Vector3 world = live[i];
                // FunnelPoint is MuJoCo-ordered (x, y, depth, z=up); the HUD
                // runs it back through CoordinateMapper.World before projecting.
                points[i] = new FunnelPoint { x = world.x, y = world.z, z = world.y, t = times[i] };
            }

            AimState aim = new AimState
            {
                available = true,
                actualBarrelPitchDeg = pitchDeg,
                actualTurretYawDeg = muzzle.eulerAngles.y,
                funnel = new FunnelState { points = points, tofS = tof },
            };
            aim.rangeScale = BuildLegacyRangeScale(origin, forward, speed,
                LegacyShooterDrag(shooter), LegacyShooterRadius(shooter));
            if (!loggedLegacyAim)
            {
                loggedLegacyAim = true;
                Debug.Log($"[LegacyHud] Funnel from shooter {shooter.ID}: " +
                          $"speed={speed / SourceSimulatorUnitsPerMetre:F1} m/s points={points.Length} " +
                          $"ladder={(aim.rangeScale != null && aim.rangeScale.marks != null ? aim.rangeScale.marks.Length : 0)}");
            }
            return aim;
        }

        // Reference funnel sampling: one point every 0.05 s, 64 points, and the
        // last point is the terrain impact. The integration runs finer than the
        // sample spacing -- the OSS round is a plain Unity rigidbody, so the
        // rollout uses exactly its gravity and linear drag.
        private const float LegacyShotStepS = 0.05f;
        private const float LegacyShotIntegrationS = 0.005f;
        private const int LegacyShotPointLimit = 64;

        /// <summary>The Bullet-17mm / Bullet-42mm prefab's linear drag. The funnel
        /// must ride the round the referee actually fires, or the tracer visibly
        /// leaves the drawn path.</summary>
        private static float LegacyShooterDrag(ShooterController shooter)
        {
            return shooter.Type.Value == 1 ? 0.05f : 1f;
        }

        /// <summary>Round radius in scene units (10 units per metre). The impact
        /// rests on the surface by its radius, like the backend's terrain test.</summary>
        private static float LegacyShooterRadius(ShooterController shooter)
        {
            return (shooter.Type.Value == 1 ? 0.02125f : 0.0085f) * SourceSimulatorUnitsPerMetre;
        }

        private static List<Vector3> SimulateLegacyRound(Vector3 origin, Vector3 velocity, float drag,
            float groundClearance, out List<float> times, out float tof, bool stopAtGround = true)
        {
            var samples = new List<Vector3>(LegacyShotPointLimit + 1) { origin };
            var sampleTimes = new List<float>(LegacyShotPointLimit + 1) { 0f };
            float startY = origin.y;
            float time = 0f;
            Vector3 position = origin;
            int emitEvery = Mathf.Max(1, Mathf.RoundToInt(LegacyShotStepS / LegacyShotIntegrationS));
            for (int step = 1; step <= (LegacyShotPointLimit - 1) * emitEvery; step++)
            {
                // Unity rigidbody integration: v += g*dt, then v *= 1/(1+drag*dt).
                Vector3 previous = position;
                velocity += Physics.gravity * LegacyShotIntegrationS;
                velocity /= 1f + drag * LegacyShotIntegrationS;
                position += velocity * LegacyShotIntegrationS;
                time += LegacyShotIntegrationS;
                // Stop on the actual arena surface, like the backend's terrain
                // impact, so the trail never smears below the field. The first
                // 40 ms are airborne by construction; the reference uses the
                // same grace period to keep the muzzle sample off the ground.
                if (stopAtGround && time > 0.04f &&
                    LegacyRoster.TryGroundHeight(position, out float groundY) &&
                    position.y <= groundY + groundClearance)
                {
                    float surface = groundY + groundClearance;
                    // Only snap onto a surface the round actually crossed from
                    // above. The probe returns the highest collider overhead,
                    // so a wall top or a prop several metres above the path
                    // also reports "below the surface"; snapping the impact up
                    // to it made the last funnel segment jump and fold over the
                    // sight. That is an obstacle the round strikes from the
                    // side, so its impact stays at the round's own height.
                    bool landed = previous.y >= surface - 1e-3f;
                    samples.Add(landed ? new Vector3(position.x, surface, position.z) : position);
                    sampleTimes.Add(time);
                    break;
                }
                if (step % emitEvery == 0)
                {
                    samples.Add(position);
                    sampleTimes.Add(time);
                }
                if (position.y < startY - 60f) break;   // six metres below the muzzle
            }
            times = sampleTimes;
            tof = time;
            return samples;
        }

        private RangeScaleState BuildLegacyRangeScale(Vector3 origin, Vector3 forward, float speed,
            float drag, float groundClearance)
        {
            if (operatorCamera == null) return null;
            Vector3 level = new Vector3(forward.x, 0f, forward.z);
            if (level.sqrMagnitude < 0.0001f) return null;
            // The ladder is a level sight reference, not a live round: keep it
            // integrating past the floor so the 5..30 m rungs still exist.
            List<Vector3> levelPath = SimulateLegacyRound(
                origin, level.normalized * speed, drag, groundClearance, out _, out _, false);
            float cameraHeight = operatorCamera.transform.position.y - origin.y;
            var marks = new List<RangeMarkState>(6);
            const float stepM = 5f;
            const int markCount = 6;
            for (int mark = 1; mark <= markCount; mark++)
            {
                float targetUnits = mark * stepM * SourceSimulatorUnitsPerMetre;
                Vector3 point = Vector3.zero;
                bool found = false;
                for (int i = 1; i < levelPath.Count; i++)
                {
                    Vector3 a = levelPath[i - 1];
                    Vector3 b = levelPath[i];
                    float da = new Vector2(a.x - origin.x, a.z - origin.z).magnitude;
                    float db = new Vector2(b.x - origin.x, b.z - origin.z).magnitude;
                    if (db < targetUnits) continue;
                    float t = db > da ? Mathf.Clamp01((targetUnits - da) / (db - da)) : 0f;
                    point = Vector3.Lerp(a, b, t);
                    found = true;
                    break;
                }
                if (!found) break;
                float dropUnits = origin.y - point.y;
                // The rung is where the eye looks through the round: the camera
                // rides above the bore, so its sight angle is the bore drop plus
                // that mount height over the range, not the drop alone.
                float sightDeg = Mathf.Atan2(cameraHeight + dropUnits, targetUnits) * Mathf.Rad2Deg;
                marks.Add(new RangeMarkState
                {
                    distanceM = mark * stepM,
                    sightDeg = sightDeg,
                    dropM = dropUnits / SourceSimulatorUnitsPerMetre,
                });
            }
            if (marks.Count == 0) return null;
            return new RangeScaleState
            {
                referenceDistanceM = marks[0].distanceM,
                computedAtPitchDeg = 0f,
                marks = marks.ToArray(),
            };
        }

        /// <summary>
        /// Moves the rendered round every frame. It used to be written only when
        /// a 30 Hz state arrived, so the ball sat still between packets and then
        /// jumped — the firing stutter. Interpolate between the two newest
        /// samples and extrapolate with the backend velocity.
        /// </summary>
        private void UpdateRenderedProjectile()
        {
            if (projectileVisual == null || !projectileVisualWasActive) return;
            if (client == null || !client.IsConnected) return;
            if (!client.TryGetSamples(out StateSample previous, out StateSample current, out double targetTime)) return;
            MujocoState state = current.state;
            global::ProjectileState newest = state?.projectile;
            if (newest == null || !newest.visible || !newest.active || !Valid(newest.positionM)) return;
            global::ProjectileState older = previous?.state?.projectile;
            float previousTime = state.simulationTimeS;
            if (older != null && older.shotId == newest.shotId && older.visible && Valid(older.positionM))
                previousTime = previous.state.simulationTimeS;
            else
                older = null;
            float span = Mathf.Max(0.0001f, state.simulationTimeS - previousTime);
            float alpha = Mathf.Clamp01((float)((targetTime - previousTime) / span));
            float extrapolation = Mathf.Clamp((float)(targetTime - state.simulationTimeS), 0f, 0.1f);
            Vector3 world = older != null
                ? Vector3.LerpUnclamped(CoordinateMapper.World(older.positionM),
                    CoordinateMapper.World(newest.positionM), alpha)
                : CoordinateMapper.World(newest.positionM);
            if (extrapolation > 0f && Valid(newest.velocityMps))
                world += CoordinateMapper.WorldVector(newest.velocityMps) * extrapolation;
            projectileVisual.transform.position = MapMujocoPointToCameraFrame(world);
        }

        private void UpdateLoadingScreen()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            bool modeScene = sceneName == "Training Ground" || sceneName == "RMUC2023" ||
                             sceneName == "RMUC2024";
            if (!modeScene || !PacificSelectionState.Completed)
            {
                // The loading page belongs to the Spawn → map transition only.
                if (loadingScreen != null) { Destroy(loadingScreen.gameObject); loadingScreen = null; }
                return;
            }
            if (officialCanvas != null)
            {
                if (loadingScreen != null)
                {
                    loadingScreen.SetProgress(1f, "就绪");
                    loadingScreen.HideAfter(0.15f);
                    loadingScreen = null;
                }
                return;
            }
            if (loadingScreen == null)
            {
                loadingScreen = MujocoLoadingScreen.Show();
                HideLegacyLoadingUi();
            }
            float progress = 0.08f;
            string status = LegacyMode ? "正在准备赛场…" : "正在连接 MuJoCo 后端…";
            if (client != null && client.IsConnected)
            {
                progress = 0.35f;
                status = LegacyMode ? "正在准备车辆…" : "正在同步车辆状态…";
            }
            if (client?.State?.hero != null) { progress = 0.5f; status = "正在载入场地…"; }
            if (robots.Count > 0) { progress = 0.75f; status = "正在绑定机器人…"; }
            if (mujocoField != null) { progress = 0.88f; status = "正在生成驾驶舱 HUD…"; }
            loadingScreen.SetProgress(progress, status);
        }

        /// <summary>Cover/remove the source simulator's own canvases during the load gap.</summary>
        private void HideLegacyLoadingUi()
        {
            foreach (MatchInfoController match in FindObjectsByType<MatchInfoController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (match != null) match.gameObject.SetActive(false);
            foreach (ClassSelectionMenu menu in FindObjectsByType<ClassSelectionMenu>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (menu != null) menu.gameObject.SetActive(false);
            foreach (RoleSelectionMenu menu in FindObjectsByType<RoleSelectionMenu>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (menu != null) menu.gameObject.SetActive(false);
        }

        private void UpdateRenderedRobotPose()
        {
            if (client == null || !client.IsConnected ||
                !client.TryGetSamples(out StateSample previous, out StateSample current, out double targetTime)) return;
            MujocoState state = current.state;
            global::HeroState newest = state?.hero;
            if (newest == null || !Valid(newest.positionM)) return;
            global::HeroState older = previous?.state?.hero;
            float previousTime = state.simulationTimeS;
            if (older != null)
            {
                previousTime = previous.state.simulationTimeS;
                float interval = state.simulationTimeS - previousTime;
                // A P-panel switch changes the hero id, and a stalled socket can
                // leave a sample far behind. Both would make interpolation slide
                // across unrelated poses.
                if (older.id != newest.id || interval <= 0.001f || interval > 0.2f)
                {
                    older = newest;
                    previousTime = state.simulationTimeS;
                }
            }
            if (older == null) older = newest;
            float span = Mathf.Max(0.0001f, state.simulationTimeS - previousTime);
            float alpha = Mathf.Min(1f, Mathf.Max(0f, (float)((targetTime - previousTime) / span)));
            // The target is one packet behind the newest sample, so a new state
            // never snaps the rendered pose forward; the remainder past the
            // newest sample is extrapolated with its velocity.
            float extrapolation = Mathf.Clamp((float)(targetTime - state.simulationTimeS), 0f, 0.1f);
            Vector3 position = Vector3.LerpUnclamped(
                CoordinateMapper.World(older.positionM), CoordinateMapper.World(newest.positionM), alpha);
            float chassisYaw = Mathf.LerpAngle(older.yawDeg, newest.yawDeg, alpha);
            float turretYaw = Mathf.LerpAngle(older.turretWorldYawDeg, newest.turretWorldYawDeg, alpha);
            float barrelPitch = Mathf.LerpAngle(older.barrelPitchDeg, newest.barrelPitchDeg, alpha);
            if (extrapolation > 0f)
            {
                if (Valid(newest.linearVelocityMps))
                    position += CoordinateMapper.WorldVector(newest.linearVelocityMps) * extrapolation;
                if (Valid(newest.angularVelocityDps))
                    chassisYaw += newest.angularVelocityDps[2] * extrapolation;
            }
            foreach (BoundRobot robot in robots.Values)
            {
                if (!robot.calibrated || state.selectedRobotId != robot.id || newest.id != robot.id ||
                    !string.Equals(state.selectedRobotClass, SelectedRobotClass(robot.id), StringComparison.OrdinalIgnoreCase))
                    continue;
                ApplyHeroPose(robot, position, chassisYaw, turretYaw, barrelPitch);
                break;
            }
        }

        private int CurrentSelectedActualRobotId()
        {
            foreach (RobotController controller in robots.Keys)
            {
                RefereeController referee = controller != null ? controller.GetComponent<RefereeController>() : null;
                if (referee != null) return referee.RobotID.Value;
            }
            return -1;
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (camera == operatorCamera) ApplyOperatorCameraPose();
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == operatorCamera) ApplyOperatorCameraPose();
        }

        private void ApplyOperatorCameraPose()
        {
            if (operatorCamera == null) return;
            // Force the operator view to remain attached to the selected robot.
            // Cinemachine Brain otherwise re-applies stale spectator/virtual-camera
            // state after the MuJoCo state callback moved the chassis.
            foreach (BoundRobot robot in robots.Values)
            {
                if (robot.cameraMount == null) continue;
                // The class-specific FPV is a child of Pitch, which in turn
                // follows the MuJoCo-driven chassis. The backend camera pose
                // belongs to a differently scaled arena and can put this
                // render camera inside arena geometry. Use the live prefab
                // mount. Never apply the backend's unrelated camera position.
                operatorCamera.transform.SetPositionAndRotation(
                    robot.cameraMount.position,
                    robot.cameraMount.rotation);
                return;
            }
        }

        private void EnsureSelectedFpvCamera()
        {
            foreach (BoundRobot robot in robots.Values)
            {
                CinemachineVirtualCamera vcam = robot.fpvCamera;
                if (vcam != null)
                {
                    vcam.enabled = true;
                    vcam.gameObject.SetActive(true);
                    vcam.Priority = 10000;
                    // The prefab's Cinemachine composer targets the turret's
                    // yaw object and shifts the rendered lens vertically in
                    // Training Ground. MuJoCo already drives this mounted
                    // camera, so no secondary follow/aim solve is needed.
                    vcam.m_Follow = null;
                    vcam.m_LookAt = null;
                }
                if (operatorCamera != null && robot.brain == null)
                    robot.brain = operatorCamera.GetComponent<CinemachineBrain>();
                return;
            }
        }

        private void EnforceSingleAudioListener()
        {
            if (operatorCamera == null) return;
            foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                listener.enabled = listener.gameObject == operatorCamera.gameObject;
        }

        private void InstallOfficialUi(Camera camera)
        {
            // Only the selected robot's legacy PlayerUI is hidden in DiscoverRobots.
            // Preserve the source simulator's selection, match-rule and spectator UI.
            Font font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, 18);
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            operatorCamera = camera;
            // Keep the original Brain -> selected robot FPV virtual camera
            // pipeline. The UI bridge must never reparent the output camera.
            CinemachineBrain brain = operatorCamera.GetComponent<CinemachineBrain>();
            if (brain != null)
            {
                brain.m_CameraActivatedEvent.RemoveListener(OnCinemachineCameraActivated);
                brain.enabled = true;
            }
            // The OSS FPV virtual camera still follows its turret rig; clearing
            // Follow/LookAt is only valid when MuJoCo drives the mount.
            if (!LegacyMode) EnsureSelectedFpvCamera();
            operatorCamera.enabled = true;
            operatorCamera.nearClipPlane = 0.05f;
            // Preserve the original canvases, but prevent spectator/scene
            // cameras from rendering over the selected robot's FPV output.
            foreach (Camera other in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (other != operatorCamera && other.targetTexture == null) other.enabled = false;
            EnforceSingleAudioListener();
            overviewCamera = CreateAuxiliaryCamera("MuJoCo Overview Camera", camera, true);
            ballisticsCamera = CreateAuxiliaryCamera("MuJoCo Ballistics Camera", camera, false);
            cameraModes = gameObject.AddComponent<CameraModeController>();
            cameraModes.operatorCamera = operatorCamera;
            cameraModes.overviewCamera = overviewCamera;
            cameraModes.ballisticsCamera = ballisticsCamera;
            controls.cameras = cameraModes;
            cameraModes.operatorCamera = operatorCamera;
            cameraModes.Select(1);
            // Camera and field now share MuJoCo's world coordinates.
            officialCanvas = BuildCanvas();
            projectileVisual = NativeSceneBuilder.BuildFriendlyProjectile(transform);
            projectileTrail = projectileVisual.GetComponent<TrailRenderer>();
            // Match rules keep running in GameManager/RefereeController; only
            // the source simulator's old in-game status canvas is replaced.
            // It otherwise draws a second scoreboard and minimap over the
            // custom first-person HUD in every RMUC mode.
            foreach (MatchInfoController legacyStatus in
                FindObjectsByType<MatchInfoController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (legacyStatus != null) legacyStatus.gameObject.SetActive(false);
            if (LegacyMode)
            {
                // The OSS split its old scoreboard and world-space minimap into
                // separate prefab instances; hide both so the official HUD is
                // the only status surface.
                foreach (Transform legacyUi in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (legacyUi != null && (legacyUi.name == "MapUI" || legacyUi.name == "GameStatusMenu"))
                        legacyUi.gameObject.SetActive(false);
                // Training Ground ships no BulletManager; without it the
                // referee raises OnShoot and no round ever appears.
                if (FindFirstObjectByType<BulletManager>() == null &&
                    GetComponent<LegacyBulletSpawner>() == null)
                    gameObject.AddComponent<LegacyBulletSpawner>();
            }

            var hud = gameObject.AddComponent<OfficialHudBinder>();
            hud.worldToScene = MapMujocoPointToCameraFrame;
            // Mapless legacy scenes keep the OSS decimetre scale; an imported
            // MuJoCo field maps the funnel through its calibrated visual scale.
            hud.unitsPerMetre = LegacyMode ? SourceSimulatorUnitsPerMetre : CurrentVisualScale();
            hud.Initialize(officialCanvas, font, client, cameraModes, camera);
            var fullscreen = gameObject.AddComponent<OfficialFullscreenController>();
            fullscreen.Initialize(hud.Layout);
            panels = gameObject.AddComponent<OfficialPanelController>();
            panels.Initialize(officialCanvas, font, client, controls, hud.Layout);
            RoleSelectionMenu training = FindFirstObjectByType<RoleSelectionMenu>(FindObjectsInactive.Include);
            panels.ConfigureRobotSwitch(SwitchSelectedRobot, CurrentSelectedActualRobotId(), PanelRobotIds(training));
            gameObject.AddComponent<OfficialRuleUiAdapter>().Initialize(hud.Layout);
            var seasonMap = gameObject.AddComponent<SeasonMapController>();
            seasonMap.Initialize(officialCanvas, client, controls, panels, hud.Layout.GetSprite("map_robot_lockline_yellow"));
            var tactical = gameObject.AddComponent<TacticalCommandController>();
            tactical.Initialize(officialCanvas, font, client, controls, panels, seasonMap, camera, hud.Layout.GetSprite("map_robot_lockline_yellow"));
            seasonMap.SetTacticalController(tactical);
            foreach (RuntimeMiniMap map in officialCanvas.GetComponentsInChildren<RuntimeMiniMap>(true))
            {
                // The authored map is the backend's 2026 field map, so markers use
                // the backend field bounds directly (same as 自定义UI网页). The
                // Unity-calibration callbacks would map them through the scaled
                // arena a second time and push them to the map edges.
                map.ruleRobotData = RuleRobotMapData;
                // The legacy map artwork is drawn with world +z pointing up (the
                // OSS MapUI places units at their raw world x/z), so bypass the
                // backend's y-flip and project with our own rectangle.
                if (LegacyMode) map.worldToMap = SimulatorMapCapture.WorldToMap;
            }
            // Enter operator mode immediately after Spawn.  The old flow left
            // cursor capture false while the full-screen HUD raycaster was
            // active, so WASD and mouse deltas were never delivered.
            // The OSS FPVController owns the cursor in the legacy build.
            if (!LegacyMode) controls.SetOperatorReady(false);
            Debug.Log($"[LegacyHud] First-person HUD installed ({(LegacyMode ? "original simulator physics" : "MuJoCo")}).");
        }

        private void OnCinemachineCameraActivated(ICinemachineCamera newCamera, ICinemachineCamera previousCamera)
        {
            // Cinemachine owns the actual render pose; this callback is reserved
            // for diagnostics and intentionally does not mutate camera transforms.
        }

        private static Component GetPrivateComponent(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) as Component : null;
        }

        private Camera CreateAuxiliaryCamera(string name, Camera reference, bool orthographic)
        {
            Camera result = new GameObject(name).AddComponent<Camera>();
            result.transform.SetParent(transform, false);
            result.CopyFrom(reference);
            result.enabled = false;
            result.orthographic = orthographic;
            if (orthographic) result.orthographicSize = 18f * CurrentVisualScale();
            result.depth = reference.depth + 1f;
            return result;
        }

        private float CurrentVisualScale()
        {
            foreach (BoundRobot robot in robots.Values) return robot.visualScale;
            return DefaultVisualScale;
        }

        private Vector3 MapMujocoPointToCameraFrame(Vector3 point)
        {
            CameraState source = latestState?.operatorCamera;
            if (source == null || !Valid(source.positionM) || !Valid(source.forwardM) || !Valid(source.upM)) return point;
            foreach (BoundRobot robot in robots.Values)
            {
                if (robot.cameraMount == null) continue;
                Vector3 sourceOrigin = CoordinateMapper.World(source.positionM);
                Quaternion sourceRotation = Quaternion.LookRotation(
                    CoordinateMapper.WorldVector(source.forwardM).normalized,
                    CoordinateMapper.WorldVector(source.upM).normalized);
                Vector3 local = Quaternion.Inverse(sourceRotation) * (point - sourceOrigin);
                return operatorCamera.transform.position + operatorCamera.transform.rotation * (local * robot.visualScale);
            }
            return point;
        }

        private Vector3? RaycastFieldSurface(Ray ray)
        {
            if (visualFieldSurface == null) return null;
            return visualFieldSurface.Raycast(ray, out RaycastHit hit, 200f)
                ? hit.point
                : (Vector3?)null;
        }

        private void ApplySharedMarkerVisual(SharedMarkerState marker)
        {
            if (marker == null || !marker.active || !Valid(marker.positionM))
            {
                if (sharedMarkerVisual != null) sharedMarkerVisual.SetActive(false);
                return;
            }
            BoundRobot selected = null;
            foreach (BoundRobot robot in robots.Values)
                if (robot.id == CurrentSelectedActualRobotId() && robot.calibrated) { selected = robot; break; }
            if (selected == null)
            {
                if (sharedMarkerVisual != null) sharedMarkerVisual.SetActive(false);
                return;
            }
            if (sharedMarkerVisual == null) sharedMarkerVisual = BuildSharedMarkerVisual();
            Vector3 mapped = CoordinateMapper.World(marker.positionM);
            Vector3 local = Quaternion.Inverse(selected.mujocoOriginRotation) * (mapped - selected.mujocoOriginPosition);
            sharedMarkerVisual.transform.position = selected.unityOriginPosition + selected.unityOriginRotation * (local * selected.visualScale);
            sharedMarkerVisual.transform.localScale = Vector3.one * selected.visualScale;
            sharedMarkerVisual.SetActive(true);
        }

        private GameObject BuildSharedMarkerVisual()
        {
            var root = new GameObject("Shared Red Ground Marker");
            root.transform.SetParent(transform, false);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            var material = new Material(shader) { color = new Color(1f, .03f, .02f) };
            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = "Marker Shaft";
            shaft.transform.SetParent(root.transform, false);
            shaft.transform.localPosition = new Vector3(0f, .52f, 0f);
            shaft.transform.localScale = new Vector3(.07f, .52f, .07f);
            Destroy(shaft.GetComponent<Collider>());
            shaft.GetComponent<Renderer>().sharedMaterial = material;
            var arrow = new GameObject("Marker Arrowhead");
            arrow.transform.SetParent(root.transform, false);
            arrow.transform.localPosition = new Vector3(0f, .03f, 0f);
            const int sides = 16;
            var vertices = new Vector3[sides + 1];
            var triangles = new int[sides * 3];
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * .18f, .34f, Mathf.Sin(angle) * .18f);
                triangles[i * 3] = 0; triangles[i * 3 + 1] = (i + 1) % sides + 1; triangles[i * 3 + 2] = i + 1;
            }
            var mesh = new Mesh { name = "Marker Arrowhead Mesh", vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            arrow.AddComponent<MeshFilter>().sharedMesh = mesh;
            arrow.AddComponent<MeshRenderer>().sharedMaterial = material;
            root.SetActive(false);
            return root;
        }

        private Vector3 UnityWorldToBackend(Vector3 unityWorld)
        {
            // Inverse of MapBackendWorldToUnityMap: the marker input is a Unity
            // world point, but the backend and the map store MuJoCo metres.
            foreach (BoundRobot robot in robots.Values)
            {
                if (!robot.calibrated || robot.visualScale <= 0.0001f) continue;
                Vector3 local = Quaternion.Inverse(robot.unityOriginRotation) *
                                (unityWorld - robot.unityOriginPosition);
                local /= robot.visualScale;
                Vector3 mapped = robot.mujocoOriginPosition + robot.mujocoOriginRotation * local;
                return new Vector3(mapped.x, mapped.z, mapped.y);
            }
            // No calibrated robot yet: keep the plain Unity -> MuJoCo axis swap.
            return new Vector3(unityWorld.x, unityWorld.z, unityWorld.y);
        }

        /// <summary>
        /// Refresh the referee/robot/manager snapshot at most every
        /// <see cref="SceneCacheSeconds"/>. Every 30 Hz caller goes through
        /// here, so a whole state packet costs one scene walk instead of one
        /// per robot (the old merge loop re-scanned the scene per referee).
        /// </summary>
        private void RefreshSceneCache(bool force = false)
        {
            float now = Time.unscaledTime;
            if (!force && now < nextSceneCacheAt) return;
            nextSceneCacheAt = now + SceneCacheSeconds;
            cachedRobots = FindObjectsByType<RobotController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            cachedReferees = FindObjectsByType<RefereeController>(FindObjectsSortMode.None);
            cachedManager = FindFirstObjectByType<GameManager>();
            ruleRobots.Clear();
            foreach (RefereeController referee in cachedReferees)
            {
                if (referee == null || !referee.IsSpawned || referee.robotTags.Contains(RobotTag.Building)) continue;
                int actualId = referee.RobotID.Value;
                bool sceneObject = IsSceneObject(referee);
                // Match the original FindRuleRobot: a runtime-spawned referee
                // wins over a scene-placed one; among scene objects the last
                // registered one is kept.
                if (ruleRobots.TryGetValue(actualId, out RefereeController existing) &&
                    !IsSceneObject(existing) && sceneObject) continue;
                ruleRobots[actualId] = referee;
            }
        }

        private static bool IsSceneObject(RefereeController referee)
        {
            NetworkObject networkObject = referee != null ? referee.GetComponent<NetworkObject>() : null;
            return networkObject == null || networkObject.IsSceneObject == true;
        }

        private static int RobotSampleKey(int actualId, int faction) => actualId * 2 + faction;

        private RefereeController FindRuleRobot(int actualId)
        {
            RefreshSceneCache();
            return ruleRobots.TryGetValue(actualId, out RefereeController referee) ? referee : null;
        }

        private RobotMasterRobot RuleRobotMapData(int actualId)
        {
            // Training Ground keeps the scene's blue sentry (ID 27) spawned for
            // rule bookkeeping, but the range only displays the vehicle the
            // operator picked. Without this filter the minimap drew a blue "7"
            // marker for a car that is not in play.
            if (SceneManager.GetActiveScene().name == "Training Ground" &&
                actualId != PacificSelectionState.SelectedActualRobotId) return null;
            RefereeController referee = FindRuleRobot(actualId);
            if (referee == null || referee.HPLimit.Value <= 0f) return null;
            int faction = referee.faction.Value == Faction.Blue ? 1 : 0;
            int key = RobotSampleKey(actualId, faction);
            if (!mapRobotCache.TryGetValue(key, out RobotMasterRobot data))
            {
                data = new RobotMasterRobot();
                mapRobotCache[key] = data;
            }
            data.id = actualId;
            data.faction = faction;
            data.hp = Mathf.RoundToInt(referee.HP.Value);
            data.maxHp = Mathf.RoundToInt(referee.HPLimit.Value);
            data.ammo = 0;
            data.energy = 0;
            data.economy = 0;
            data.buffs = null;
            return data;
        }

        private void PositionAuxiliaryCameras()
        {
            foreach (BoundRobot robot in robots.Values)
            {
                if (robot.chassis == null) continue;
                Vector3 origin = robot.chassis.position;
                if (overviewCamera != null)
                    overviewCamera.transform.SetPositionAndRotation(origin + Vector3.up * (26f * robot.visualScale), Quaternion.Euler(90f, 0f, 0f));
                if (ballisticsCamera != null)
                {
                    Vector3 aim = robot.cameraMount != null ? robot.cameraMount.forward : robot.chassis.forward;
                    Vector3 target = origin + aim * (8f * robot.visualScale) + Vector3.up * robot.visualScale;
                    Vector3 position = origin - robot.chassis.right * (7f * robot.visualScale) + Vector3.up * (2f * robot.visualScale);
                    ballisticsCamera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
                }
                return;
            }
        }

        private Canvas BuildCanvas()
        {
            Canvas canvas = new GameObject("Pacific Official First-Person HUD").AddComponent<Canvas>();
            canvas.transform.SetParent(transform, false);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // Expand fits the whole 1920x1080 layout into any window or
            // ultrawide screen instead of cropping it; matching width alone
            // cut off the bottom HUD on very wide aspect ratios.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.matchWidthOrHeight = 0.5f;
            canvas.gameObject.AddComponent<GraphicRaycaster>();
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                GameObject events = new GameObject("Pacific Official EventSystem");
                events.transform.SetParent(transform, false);
                events.AddComponent<EventSystem>();
                events.AddComponent<StandaloneInputModule>();
            }
            return canvas;
        }

        private Camera FindOperatorCamera()
        {
            // Camera.main is commonly the spectator camera in the original
            // scene. Prefer the spawned, selected robot's Cinemachine output
            // (the brain's Unity Camera is still the actual render camera).
            RefreshSceneCache(force: true);
            foreach (RobotController controller in cachedRobots)
            {
                RefereeController referee = controller.GetComponent<RefereeController>();
                if (referee == null || !referee.IsOwner || !IsSelectedLocalRobot(controller, referee)) continue;
                Transform mount = FindFirstPersonCameraMount(controller);
                if (mount == null) continue;
                CinemachineBrain brain = FindFirstObjectByType<CinemachineBrain>();
                if (brain != null && brain.OutputCamera != null) return brain.OutputCamera;
                Camera robotCamera = mount.GetComponentInChildren<Camera>(true);
                if (robotCamera != null) return robotCamera;
            }
            Camera main = Camera.main;
            if (main != null) return main;
            return FindFirstObjectByType<Camera>();
        }

        private static RobotState FindRobot(RobotState[] states, int id)
        {
            if (states == null) return null;
            foreach (RobotState state in states) if (state != null && state.id == id) return state;
            return null;
        }

        private static void ApplyEndpointArguments(MujocoClient target)
        {
            // The Python web API uses 18767; Unity's framed control and state
            // protocol uses the dedicated realtime endpoint (18772), matching
            // the single run-mujoco.sh launcher and the editor launcher.
            target.host = "127.0.0.1";
            target.port = 18772;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument.StartsWith("--mujoco-host=", StringComparison.OrdinalIgnoreCase))
                    target.host = argument.Substring("--mujoco-host=".Length);
                else if (argument.StartsWith("--mujoco-port=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(argument.Substring("--mujoco-port=".Length), out int port))
                    target.port = port;
            }
            if (target.port == 18770)
            {
                Debug.LogWarning("[MuJoCo] Port 18770 is the old web-preview API, not the Unity realtime-control protocol. Redirecting Unity to 18772.");
                target.port = 18772;
            }
        }

        private static GameObject GetPrivateGameObject(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) as GameObject : null;
        }

        private static Transform GetPrivateTransform(object target, string name)
        {
            GameObject value = GetPrivateGameObject(target, name);
            return value != null ? value.transform : null;
        }

        private static Transform FindFirstPersonCameraMount(RobotController controller)
        {
            FPVController fpv = controller.GetComponentInChildren<FPVController>(true);
            if (fpv != null)
            {
                FieldInfo field = typeof(FPVController).GetField("FPVCamera", BindingFlags.Instance | BindingFlags.NonPublic);
                Component virtualCamera = field?.GetValue(fpv) as Component;
                if (virtualCamera != null) return virtualCamera.transform;
            }
            // ARTINX robots can carry AI vision sensors (CameraController) that
            // stare along a patrol direction instead of the barrel. The Sentry
            // has two of them and no FPV assembly; using one threw the operator
            // view 90 deg off the gun (the P-panel Blue 7 wall).
            foreach (Camera childCamera in controller.GetComponentsInChildren<Camera>(true))
            {
                if (childCamera == null || childCamera.GetComponent<CameraController>() != null) continue;
                return childCamera.transform;
            }
            foreach (CinemachineVirtualCamera childCamera in controller.GetComponentsInChildren<CinemachineVirtualCamera>(true))
            {
                if (childCamera == null || childCamera.GetComponent<CameraController>() != null) continue;
                return childCamera.transform;
            }
            return null;
        }

        private static GameObject FindSyntheticFpvMount(RobotController controller)
        {
            // Switching away and back re-binds the same vehicle; reuse the
            // mount instead of stacking a second virtual camera on the muzzle.
            foreach (Transform child in controller.GetComponentsInChildren<Transform>(true))
                if (child != null && child.name == "MuJoCo Vehicle FPV Mount") return child.gameObject;
            return null;
        }

        private static bool Valid(float[] value) => value != null && value.Length >= 3;
    }
}
