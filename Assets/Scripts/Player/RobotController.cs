using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Cinemachine;

using CameraCommunication;

public class RobotController : NetworkBehaviour
{
    private RefereeController referee;

    // The referee system owns drive enable/disable (death, revive, match start)
    // and runs on the server. Owner-write let the host write only the robots it
    // owned and spammed "Write permissions (Owner) ... not allowed" for every
    // client-owned car; the server must be the single writer.
    public NetworkVariable<bool> Enabled = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> AutoOperate = new NetworkVariable<bool>(false);

    [SerializeField] private bool printLog = false;

    [Header("Action")]
    private PlayerInput Input;
    private InputAction move;
    private InputAction look;
    private InputAction shoot;
    [SerializeField] private InputAction Aim;
    [SerializeField] private InputAction Boost;
    [SerializeField] private float BoostPower = 240;
    [SerializeField] private InputAction Spin;

    // Legacy build: ControlInput (the same component the custom UI client
    // uses) writes these from the owner side, so they must accept owner writes.
    public NetworkVariable<bool> IsBoost = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<bool> IsShoot = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<bool> IsSpin = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    // 小陀螺 direction from C (+1) / V (-1). The legacy wheel model only had a
    // single positive spin; this keeps both directions consistent with the HUD.
    public NetworkVariable<float> SpinDirection = new NetworkVariable<float>(1f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    [Header("Chassis")]
    [SerializeField] private GameObject Base;
    private Vector3 LastPostion;
    private Quaternion LastRotation;

    public NetworkVariable<float> PowerRedundancies = new NetworkVariable<float>(5.0f);

    private PIDController moveControllerX;
    private PIDController moveControllerY;
    private PIDController followController;
    [SerializeField] private float[] moveControllerParameters = {0.02f, 0f, 0.015f};
    [SerializeField] private float[] followControllerParameters = {0.02f, 0f, 0.015f};
    public NetworkVariable<float> moveDirectionX = new NetworkVariable<float>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<float> moveDirectionY = new NetworkVariable<float>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    
    private float targetVx;
    private float targetVy;
    private float targetVw;

    private float vx;
    private float vy;
    private float vw;

    // Right-Front, Left-Front, Left-Back, Right-Back
    [SerializeField] private WheelController[] Wheels;
    private float[] wheelForce = new float[4];

    [Header("Gimbal")]
    [SerializeField]private GameObject Yaw;
    [SerializeField]private GameObject Pitch;
    private PIDController yawController;
    private PIDController pitchController;
    [SerializeField] private float[] yawControllerParameters = {80f, 0f, 0.015f};
    [SerializeField] private float[] pitchControllerParameters = {50f, 0f, 0.015f};
    public NetworkVariable<float> currentYawAngle = new NetworkVariable<float>(0);
    public NetworkVariable<float> yawTargetAngle = new NetworkVariable<float>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<float> maxYawSpeed = new NetworkVariable<float>(1800f);
    public NetworkVariable<float> yawPatrolSpeed = new NetworkVariable<float>(60f);
    
    public NetworkVariable<float> pitchTargetAngle = new NetworkVariable<float>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<float> currentPitchAngle = new NetworkVariable<float>(0);

    [SerializeField] private float pitchMax;
    [SerializeField] private float pitchMin;

    [SerializeField] private bool DisplayYaw = false;
    [SerializeField] private bool DisplayPitch = false;
    
    [Header("Shooter")]
    [SerializeField]private List<ShooterController> ShooterList = new List<ShooterController>();
    [SerializeField]private int CurrentShooter = 0;
    [SerializeField]private float ShootFrequency = 25f;
    private float _shootTimeoutDelta = 0f;

    [Header("Camera")]
    [SerializeField] private List<CameraController> CameraList = new List<CameraController>();

    private TargetInfo Target;
    [SerializeField] private float TargetOffsetThreshold = 4f;

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // Enabled is server-write; the referee controller publishes it on
            // the first server tick. Writing it here from a client owner would
            // be rejected.
            if (Yaw != null) yawTargetAngle.Value = Yaw.transform.eulerAngles.y;
            if (Pitch != null) pitchTargetAngle.Value = Pitch.transform.eulerAngles.z;
            // Host mode makes every server-owned vehicle IsOwner, including
            // the pre-placed sentry and the whole 7v7 roster. Only the car the
            // player actually selected may capture the pointer; otherwise the
            // selection/spawn screen loses its mouse.
            if (!AutoOperate.Value && !PacificSelectionState.MujocoMode &&
                IsActivelySelected() && !SelectionScreenActive() &&
                !Pacific.Mujoco.MujocoModeRulesPanel.AnyOpen)
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
        }
    }

    private bool IsActivelySelected()
    {
        if (referee == null) return false;
        int actualId = referee.RobotID.Value > 0 ? referee.RobotID.Value : referee.InitialRobotId;
        return actualId > 0 && PacificSelectionState.SelectedActualRobotId == actualId;
    }

    private static bool SelectionScreenActive()
        => RoleSelectionMenu.OpenMenus > 0 || ClassSelectionMenu.OpenMenus > 0;

    void Awake()
    {
        if (Input == null)
        {
            Input = new PlayerInput();
        }

        referee = gameObject.GetComponent<RefereeController>();
    }

    void Start()
    {
        moveControllerX = new PIDController(moveControllerParameters[0],
                                            moveControllerParameters[1],
                                            moveControllerParameters[2]);

        moveControllerY = new PIDController(moveControllerParameters[0],
                                            moveControllerParameters[1],
                                            moveControllerParameters[2]);

        followController = new PIDController(followControllerParameters[0],
                                            followControllerParameters[1],
                                            followControllerParameters[2]);

        yawController = new PIDController(yawControllerParameters[0],
                                        yawControllerParameters[1],
                                        yawControllerParameters[2]);

        pitchController = new PIDController(pitchControllerParameters[0],
                                            pitchControllerParameters[1],
                                            pitchControllerParameters[2]);
    
        LastPostion = Base.transform.position;

        Boost.performed += context =>
        {
            IsBoost.Value = !IsBoost.Value;
        };

        Boost.canceled += context =>
        {
            IsBoost.Value = !IsBoost.Value;
        };

        shoot.performed += context =>
        {
            IsShoot.Value = !IsShoot.Value;
        };

        shoot.canceled += context =>
        {
            IsShoot.Value = !IsShoot.Value;
        };

        Spin.performed += context =>
        {
            IsSpin.Value = !IsSpin.Value;
        };
    }

    void OnEnable()
    {
        move = Input.Player.Move;
        look = Input.Player.Look;
        shoot = Input.Player.Shoot;

        if (ExternalInput) InputDisable();
        else InputEnable();

        foreach (var cam in CameraList)
        {
            cam.OnTargetDetected += TargetDetectedHandler;
        }
    }

    void OnDisable()
    {
        InputDisable();

        foreach (var cam in CameraList)
        {
            cam.OnTargetDetected += TargetDetectedHandler;
        }
    }

    void Update()
    {
        if (!Enabled.Value) return;

        // With the official binding layer active the bridge owns the pointer and
        // the Shoot action is disabled; this legacy recapture path must not
        // toggle it back on.
        if (IsOwner && !ExternalInput)
        {
            if (UnityEngine.Cursor.lockState != CursorLockMode.Locked)
            {
                shoot.Disable();
                // Clicking the game re-captures the mouse; without this the
                // look input stays dead after an Alt-Tab or Esc. Never capture
                // while a rules page or a UI button is under the pointer.
                if (IsActivelySelected() && !SelectionScreenActive() &&
                    !Pacific.Mujoco.MujocoModeRulesPanel.AnyOpen &&
                    Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame &&
                    (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
                    UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            } else {
                shoot.Enable();
            }
        }
    }

    // Legacy build: ControlInput (the custom UI client's binding layer) owns
    // WASD/mouse and writes the command variables. The OSS PlayerInput actions
    // must stay off or they would add a second, differently scaled look/drive.
    public static bool ExternalInput;
    // The legacy camera sits on the turret at a different local axis than the
    // chassis movement basis, so W used to drive sideways on screen. The bridge
    // feeds the operator view's flat forward/right here; external input moves
    // relative to what the player actually sees.
    public static Vector3 ViewForward = Vector3.forward;
    public static Vector3 ViewRight = Vector3.right;
    // Legacy kinematic turret: the bridge writes Yaw/Pitch local rotations
    // from the aim targets, the same way the reference MuJoCo bridge applies
    // ApplyHeroPose. The OSS hinge motors are left alone in this mode.
    public static bool KinematicAim;
    // Kept for the non-external fallback path.
    public static float LegacyYawSensitivity = 1f;
    public static float LegacyPitchSensitivity = 1f;

    void FixedUpdate()
    {
        if (!Enabled.Value) return;

        if (IsOwner)
        {
            if (!AutoOperate.Value)
            {
                // External input: ControlInput has already written
                // moveDirectionX/Y and the yaw/pitch targets this frame.
                if (!ExternalInput)
                {
                Vector2 lookDelta = look.ReadValue<Vector2>();
                // Fall back to the pointer's raw delta if the action binding
                // ever comes back empty; the cursor is locked here, so this is
                // the same mouse motion the Look action should carry.
                if (lookDelta.sqrMagnitude < 1e-6f && Mouse.current != null)
                    lookDelta = Mouse.current.delta.ReadValue();
                lookDelta.x *= LegacyYawSensitivity;
                lookDelta.y *= LegacyPitchSensitivity;
                // Debug.Log($"[EngineerController] lookDelta: {lookDelta.x}, {lookDelta.y}");
                
                Vector2 moveDirection = move.ReadValue<Vector2>();
                moveDirectionX.Value = moveDirection.x;
                moveDirectionY.Value = moveDirection.y;
                // Debug.Log($"[EngineerController] moveDirection: {moveDirection.x}, {moveDirection.y}");

                // Debug.Log($"[RobotController] CursorLock: {UnityEngine.Cursor.lockState}");

                // Rotate to the last target position
                if (UnityEngine.Cursor.lockState != CursorLockMode.Locked)
                {
                    lookDelta = Vector2.zero;
                }

                if (Aim.ReadValue<float>() <= 0 || Target == null)
                {
                    yawTargetAngle.Value += lookDelta.x;
                    pitchTargetAngle.Value += lookDelta.y;
                } else {
                    LockTarget(Target.Position);
                }
                }
            } else {
                IsSpin.Value = true;

                if (Target != null)
                {
                    LockTarget(Target.Position);
                    IsShoot.Value = true;
                } else {
                    IsShoot.Value = false;
                    // loop yaw from 0 - 360, pitch from pitchmin - pitchmax
                    yawTargetAngle.Value += yawPatrolSpeed.Value * Time.deltaTime;
                    pitchTargetAngle.Value = 0;
                }
            }
            
            // Debug.Log($"[RobotController] yawTargetAngle {yawTargetAngle.Value} pitchTargetAngle {pitchTargetAngle.Value}");

            TickTarget();

            if (Yaw != null)
            {
                GraphDbg.Log("Yaw", Yaw.transform.eulerAngles.y);
            }
        }

        if (IsServer)
        {
            if (Yaw != null)
            {
                if (yawTargetAngle.Value < 0) yawTargetAngle.Value += 360;
                if (yawTargetAngle.Value > 360) yawTargetAngle.Value -= 360;

                currentYawAngle.Value = Yaw.transform.eulerAngles.y;
                float _yawDifference = yawTargetAngle.Value - currentYawAngle.Value;

                // Yaw joint exists
                if (_yawDifference > 180) {
                    _yawDifference -= 360;
                } else if (_yawDifference < -180) {
                    _yawDifference += 360;
                }

                HingeJoint yawJoint = Yaw.GetComponent<HingeJoint>();
                if (KinematicAim)
                {
                    // Bridge owns Yaw.localRotation (reference ApplyHeroPose).
                }
                else if (yawJoint != null)
                {
                    yawController.updatePara(yawControllerParameters[0],
                                            yawControllerParameters[1],
                                            yawControllerParameters[2]);

                    JointMotor yawMotor = yawJoint.motor;

                    yawMotor.targetVelocity = Mathf.Clamp(yawController.Update(_yawDifference, Time.deltaTime), -maxYawSpeed.Value, maxYawSpeed.Value);

                    // The prefab's yaw motor is off and its spring damper
                    // (damper=10) absorbs the direct Transform.Rotate below,
                    // leaving the turret at a fraction of a degree. Drive the
                    // joint the way the pitch joint already is driven.
                    yawJoint.useSpring = false;
                    yawJoint.useMotor = true;
                    yawJoint.motor = yawMotor;

                    // The motor owns the joint now; rotating the Transform of
                    // a non-kinematic Rigidbody as well double-drives it.

                    // Debug.Log($"[RobotController] yawTargetAngle {yawTargetAngle} yawDifference {_yawDifference} Yaw {Yaw.transform.eulerAngles.y} targetVelocity {yawMotor.targetVelocity}");
                } else {
                    // No Yaw Joint, update vw only;
                    targetVw = Mathf.Clamp(_yawDifference / 100, -1f, 1f);
                }

                if (DisplayYaw) Debug.DrawLine(Yaw.transform.position, Yaw.transform.position + Yaw.transform.right * 10, Color.blue);
            }
            
            if (Pitch != null)
            {
                if (pitchTargetAngle.Value < pitchMin) pitchTargetAngle.Value = pitchMin;
                if (pitchTargetAngle.Value > pitchMax) pitchTargetAngle.Value = pitchMax;

                float _currentPitchAngle = Pitch.transform.eulerAngles.z;

                if (_currentPitchAngle > 180) {
                    _currentPitchAngle -= 360;
                } else if (_currentPitchAngle < -180) {
                    _currentPitchAngle += 360;
                }

                currentPitchAngle.Value = _currentPitchAngle;

                if (!KinematicAim)
                {
                    HingeJoint pitchJoint = Pitch.GetComponent<HingeJoint>();
                    if (pitchJoint != null)
                    {
                        pitchController.updatePara(pitchControllerParameters[0],
                                                pitchControllerParameters[1],
                                                pitchControllerParameters[2]);

                        // Same prefab issue as the yaw joint: the spring
                        // damper fights the motor and keeps the barrel at its
                        // start angle.
                        pitchJoint.useSpring = false;
                        pitchJoint.useMotor = true;
                        JointMotor pitchMotor = pitchJoint.motor;

                        float _pitchDifference = pitchTargetAngle.Value - currentPitchAngle.Value;

                        // Debug.Log($"[RobotController] pitchTargetAngle {pitchTargetAngle} pitchDifference {_pitchDifference} Pitch {Pitch.transform.eulerAngles.z}");

                        pitchMotor.targetVelocity = pitchController.Update(_pitchDifference, Time.deltaTime);

                        pitchJoint.motor = pitchMotor;
                    }
                }

                if (DisplayPitch) Debug.DrawLine(Pitch.transform.position, Pitch.transform.position + Pitch.transform.right * 10, Color.green);
            }

            // Move
            // Debug.DrawLine(Yaw.transform.position, Yaw.transform.position - Yaw.transform.forward * moveDirectionX.Value * 100, Color.yellow);
            // Debug.DrawLine(Yaw.transform.position, Yaw.transform.position + Yaw.transform.right * moveDirectionY.Value * 100, Color.blue);

            if (ExternalInput || KinematicAim)
            {
                // Move relative to the operator view. The FPV mount's local
                // axis is not the chassis W axis; using Yaw.right here made W
                // drive sideways on screen.
                // The OSS chassis lateral axis is opposite the view's right;
                // use the view's right for D so A/D are not mirrored.
                Vector3 desiredWorld = ViewForward * moveDirectionY.Value -
                                       ViewRight * moveDirectionX.Value;
                if (desiredWorld.sqrMagnitude > 1f) desiredWorld.Normalize();
                targetVx = Vector3.Dot(desiredWorld, Base.transform.right);
                targetVy = -Vector3.Dot(desiredWorld, Base.transform.forward);
            }
            else
            {
                Vector3 x_Bx = moveDirectionX.Value * Vector3.Project(- Yaw.transform.forward, Base.transform.right);
                Vector3 x_By = moveDirectionX.Value * Vector3.Project(- Yaw.transform.forward, Base.transform.forward);
                Vector3 y_Bx = moveDirectionY.Value * Vector3.Project(Yaw.transform.right, Base.transform.right);
                Vector3 y_By = moveDirectionY.Value * Vector3.Project(Yaw.transform.right, Base.transform.forward);
                Vector3 _inputX = x_Bx + y_Bx;
                Vector3 _inputY = x_By + y_By;
                targetVx = Vector3.Dot(_inputX.normalized, Base.transform.right) * (_inputX).magnitude;
                targetVy = - Vector3.Dot(_inputY.normalized, Base.transform.forward) * (_inputY).magnitude;
            }
            // Debug.Log($"[RobotController] targetVx {targetVx} targetVy {targetVy}");

            moveControllerX = new PIDController(moveControllerParameters[0],
                                                moveControllerParameters[1],
                                                moveControllerParameters[2]);

            moveControllerY = new PIDController(moveControllerParameters[0],
                                                moveControllerParameters[1],
                                                moveControllerParameters[2]);

            // 小陀螺 or 底盘跟随云台
            if (IsSpin.Value){
                // C/V pick the direction; the OSS model used a fixed +0.5.
                targetVw = 0.5f * (SpinDirection.Value < 0f ? -1f : 1f);
            } else if (KinematicAim || Yaw.GetComponent<HingeJoint>() != null) {
                followController.updatePara(followControllerParameters[0],
                                            followControllerParameters[1],
                                            followControllerParameters[2]);
                
                float _angle = Vector3.SignedAngle(Yaw.transform.right, Base.transform.right, Base.transform.up);

                // float _vw = - (yawTargetAngle - lastYawTargetAngle) + followController.Update(_angle, Time.deltaTime);
                // float _vw = - (1/Time.deltaTime) * (yawTargetAngle - lastYawTargetAngle) * feedForwardFactor + followController.Update(_angle, Time.deltaTime);
                float _vw = followController.Update(_angle, Time.deltaTime);
                
                targetVw = -Mathf.Clamp(_vw, -0.5f, 0.5f);
                // Debug.Log("vw: " + targetVw);
            }

            Vector3 deltaPostion = Base.transform.position - LastPostion;
            float deltaW = (Base.transform.rotation.eulerAngles.y - LastRotation.eulerAngles.y) / Time.deltaTime;
            LastPostion = Base.transform.position;
            float deltaX = Vector3.Dot(deltaPostion.normalized, Base.transform.right) * (deltaPostion).magnitude;
            float deltaY = Vector3.Dot(deltaPostion.normalized, Base.transform.forward) * (deltaPostion).magnitude;;

            // Debug.Log($"[RobotContorller] deltaPostion {deltaPostion}");

            // Debug.Log($"[RobotController] targetVx {targetVx} targetVy {targetVy} targetVw {targetVw}");
            // Debug.Log($"[RobotController] deltaX {deltaX} deltaY {deltaY} deltaW {deltaW}");

            vx = Mathf.Clamp(moveControllerX.Update(targetVx - deltaX, Time.deltaTime), -1f, 1f);
            vy = Mathf.Clamp(moveControllerY.Update(targetVy + deltaY, Time.deltaTime), -1f, 1f);
            // vx = targetVx;
            // vy = targetVy;
            vw = targetVw;

            // Vector3 _velocity = new Vector3(vx, vy, vw).normalized;
            // vx = _velocity.x;
            // vy = _velocity.y;
            // vw = _velocity.z;

            // Debug.Log($"[RobotController] X {targetVx} / {deltaX/Time.deltaTime} Y {targetVy} / {deltaY/Time.deltaTime}");
            // Debug.Log($"[RobotController] vx {vx} vy {vy} vw {vw}");

            wheelForce[0] = vx-vy-vw;
            wheelForce[1] = vx+vy+vw;
            wheelForce[2] = vx-vy+vw;
            wheelForce[3] = vx+vy-vw;

            float _factor = 1;
            float powerlimit = referee.PowerLimit.Value - PowerRedundancies.Value;

            if (IsBoost.Value || powerlimit < 0) _factor = BoostPower / powerlimit;
            _factor = _factor * powerlimit / Wheels.Length;

            for (int i=0; i< Wheels.Length; i++)
            {
                Wheels[i].GetComponent<WheelController>().SetPower(wheelForce[i] * _factor);
            }

            // The OSS wheel model is authoritative. The old Training-only
            // direct velocity override made the Base climb walls and tumble
            // (the whole FPV view spun) whenever W hit terrain; the wheel
            // forces combined with the 200 W training power ceiling are enough.

            if (ShooterList.Count > 0)
            {
                if (IsShoot.Value)
                {
                    if (_shootTimeoutDelta > 1 / ShootFrequency)
                    {
                        if (HasHeatRedundancy(ShooterList[CurrentShooter]))
                        {
                            ShooterList[CurrentShooter].PullTrigger();
                            _shootTimeoutDelta = 0f;
                        } else {
                            int i = 0;
                            foreach (var shooter in ShooterList)
                            {
                                if (HasHeatRedundancy(shooter)) CurrentShooter = i;
                                i ++;
                            }
                        }
                    } else {
                        _shootTimeoutDelta += Time.deltaTime;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The legacy HUD adapter owns drive/aim through ControlInput. Scene
    /// robots enabled before the adapter existed still need their OSS actions
    /// switched off explicitly.
    /// </summary>
    public void UseExternalInput()
    {
        ExternalInput = true;
        InputDisable();
    }

    void InputEnable()
    {
        if (move != null) move.Enable();
        if (look != null) look.Enable();
        if (shoot != null) shoot.Enable();
        if (Aim != null) Aim.Enable();
        if (Boost != null) Boost.Enable();
        if (Spin != null) Spin.Enable();
    }

    void InputDisable()
    {
        if (move != null) move.Disable();
        if (look != null) look.Disable();
        if (shoot != null) shoot.Disable();
        if (Aim != null) Aim.Disable();
        if (Boost != null) Boost.Disable();
        if (Spin != null) Spin.Disable();
    }

    bool HasHeatRedundancy(ShooterController shooter)
    {
        return shooter.HeatLimit.Value - shooter.Heat.Value >= (shooter.Mode.Value == 0 ? 10 : 100);
    }

    /// <summary>
    /// Legacy input adapter: switch to the barrel that loads the requested
    /// round, mirroring the custom UI client's right-Shift toggle.
    /// </summary>
    public void SelectLegacyAmmo(string roundName)
    {
        int wanted = roundName == "42mm" ? 1 : 0;
        for (int i = 0; i < ShooterList.Count; i++)
        {
            if (ShooterList[i] == null || ShooterList[i].Type.Value != wanted) continue;
            CurrentShooter = i;
            return;
        }
    }

    /// <summary>0 = 17mm, 1 = 42mm, for the legacy HUD state pump.</summary>
    public int CurrentAmmoType()
    {
        if (ShooterList.Count == 0) return 0;
        int index = Mathf.Clamp(CurrentShooter, 0, ShooterList.Count - 1);
        return ShooterList[index] != null ? ShooterList[index].Type.Value : 0;
    }

    void TargetDetectedHandler(List<TargetInfo> targets)
    {
        if (targets.Count == 0) return;
        
        TargetInfo _target = null;
        
        foreach (var target in targets)
        {
            if (target.Faction == Faction.Neu) continue;
            if (target.Faction == referee.faction.Value) continue;

            if (Target == null)
            {
                Target = target;
            }

            if (_target == null)
            {
                _target = target;
            } else if (CompareTargetOffset(target.Position, Target.Position) && CompareTargetDistance(target.Position, Target.Position)) {
                _target = target;
            }
        }

        if (_target != null)
        {
            Target = _target;
            // if (printLog) Debug.Log($"[RobotController] TargetDetectedHandler {Target.ID} {Target.Faction} {Target.Position}");
            if (printLog) Debug.DrawLine(Pitch.transform.position, Target.Position, Color.red);
        }
    }

    bool CompareTargetDistance(Vector3 target1, Vector3 target2)
    {
        return Vector3.Distance(Pitch.transform.position, target1) < Vector3.Distance(Pitch.transform.position, target2);
    }

    bool CompareTargetOffset(Vector3 target1, Vector3 target2)
    {
        Vector3 offset1 = target1 - Pitch.transform.position;
        Vector3 offset2 = target2 - Pitch.transform.position;
        // Debug.Log($"[RobotController] offset1 {offset1} offset2 {offset2}");
        float diff1 = new Vector2(offset1.y, offset1.z).magnitude;
        float diff2 = new Vector2(offset2.y, offset2.z).magnitude;
        if (printLog) Debug.Log($"[RobotController] diff1 {diff1} diff2 {diff2}");
        return Mathf.Abs(diff1 - diff2) > TargetOffsetThreshold;
    }

    void LockTarget(Vector3 target)
    {
        // Projectile the target position to the yaw and pitch plane
        Vector3 _target = (target - Pitch.transform.position).normalized;
        Vector2 _gimbalOffset = GetGimbalOffset(_target);

        yawTargetAngle.Value = currentYawAngle.Value + _gimbalOffset.x;
        pitchTargetAngle.Value = currentPitchAngle.Value + _gimbalOffset.y;

        // Debug.DrawLine(Yaw.transform.position, Yaw.transform.position + _yawTarget * 10, Color.red);
        // Debug.DrawLine(Pitch.transform.position, Pitch.transform.position + _pitchTarget * 10, Color.red);

        // if (printLog) Debug.Log($"[RobotController] _gimbalOffset {_gimbalOffset}");
        // Debug.Log($"[RobotController] currentYawAngle {currentYawAngle.Value} currentPitchAngle {currentPitchAngle.Value} ");
    }

    Vector2 GetGimbalOffset(Vector3 target)
    {
        Vector3 _yawTarget = Vector3.ProjectOnPlane(target, Yaw.transform.up);
        Vector3 _pitchTarget = Vector3.ProjectOnPlane(target, Pitch.transform.forward);
        
        float _targetYawDifference = Vector3.SignedAngle(Yaw.transform.right, _yawTarget, Yaw.transform.up);
        float _targetPitchDifference = Vector3.SignedAngle(Pitch.transform.right, _pitchTarget, Pitch.transform.forward);

        return new Vector2(_targetYawDifference, _targetPitchDifference);
    }

    void TickTarget()
    {
        if (Target != null)
        {
            if (Target.IsValid)
            {
                Target.LastTime -= Time.deltaTime;
                // Debug.DrawLine(Pitch.transform.position, Target.Position, Color.cyan);
            } else {
                Target = null;
            }
        }
    }
}
