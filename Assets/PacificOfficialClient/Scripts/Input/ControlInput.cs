using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

public enum BindableAction
{
    Forward, Backward, StrafeLeft, StrafeRight, SpinLeft, SpinRight,
    BarrelUp, BarrelDown, Boost, Brake, Fire, AutoLob,
    CameraOperator, CameraOverview, CameraBallistics,
}

public class ControlInput : MonoBehaviour
{
    public MujocoClient client;
    public CameraModeController cameras;
    public KeyCode forwardKey = KeyCode.W;
    public KeyCode backwardKey = KeyCode.S;
    public KeyCode strafeLeftKey = KeyCode.A;
    public KeyCode strafeRightKey = KeyCode.D;
    public KeyCode spinLeftKey = KeyCode.C;
    public KeyCode spinRightKey = KeyCode.V;
    public KeyCode modeToggleKey = KeyCode.G;
    public KeyCode barrelUpKey = KeyCode.UpArrow;
    public KeyCode barrelDownKey = KeyCode.DownArrow;
    public KeyCode boostKey = KeyCode.LeftShift;
    public KeyCode brakeKey = KeyCode.Space;
    public KeyCode fireKey = KeyCode.Mouse0;
    public KeyCode markerKey = KeyCode.Mouse2;
    // Keyboard fallback for machines without a middle mouse button.
    public KeyCode markerKeyAlt = KeyCode.F;
    public KeyCode autoLobKey = KeyCode.T;
    public KeyCode cameraOperatorKey = KeyCode.Alpha1;
    public KeyCode cameraOverviewKey = KeyCode.Alpha2;
    public KeyCode cameraBallisticsKey = KeyCode.Alpha3;
    // Loads the other round. Fixed rather than rebindable so the settings page keeps its
    // shape, and on the right shift because the left one has been 加速 since the first
    // client -- a toggle sharing it would fire every time the chassis was boosted.
    public KeyCode toggleAmmoKey = KeyCode.RightShift;
    [Range(0.05f, 2f)] public float mouseYawSensitivity = .75f;
    [Range(0.05f, 2f)] public float mousePitchSensitivity = .55f;
    bool cursorCaptured;
    bool operatorReady;
    bool uiBlocked;
    bool uiNeedsCursor;
    // Legacy build: the OSS RobotController/FPVController own the pointer, so
    // this component is added only for panel state and sensitivity sliders.
    // It must never write Cursor itself — a disabled ControlInput used to run
    // OnDisable() and hide the system cursor on the welcome and spawn screens.
    public bool passiveCursor;

    /// <summary>An official panel is open and wants the pointer free.</summary>
    public bool UiNeedsCursor => uiNeedsCursor;
    /// <summary>The legacy adapter mirrors the held trigger into IsShoot.</summary>
    public bool PrimaryFireHeld => primaryFireHeld;
    public bool UiBlocked => uiBlocked;
    public bool OperatorReady => operatorReady;
    public bool CursorCaptured => cursorCaptured;
    int suppressMouseFrames;
    // 小陀螺 spin latched by C/V: +1 counter-clockwise, -1 clockwise, 0 stopped.
    float chassisSpinLatch;
    // Which mode G last asked for. Latched locally so a second press inside one state
    // round-trip still flips it instead of resending the same mode, and so a server that
    // sends no controlMode at all still toggles.
    bool controlModeGyro;
    bool controlModeKnown;
    bool primaryFireHeld;
    int lastFireInputFrame = -1;

    public string InputDiagnostic =>
        $"enabled={enabled}, active={gameObject.activeInHierarchy}, ready={operatorReady}, " +
        $"uiBlocked={uiBlocked}, cameraMode={(cameras != null ? cameras.ActiveMode : -1)}, " +
        $"cursorCaptured={cursorCaptured}";

    void Awake() => LoadBindings();

    void Start()
    {
        SetCursorCaptured(false);
    }

    void Update()
    {
        if (uiBlocked)
        {
            primaryFireHeld = MouseHeld(KeyCode.Mouse0);
            if (client != null) client.ClearAimDelta();
            client.Control(0f, 0f, 0f, 0f, 0f, false, true);
            return;
        }
        if (!operatorReady)
        {
            primaryFireHeld = MouseHeld(KeyCode.Mouse0);
            SetCursorState(false, true);
            return;
        }
        if (client == null) return;
        UpdateCursorCapture();
        SelectCameraHotkeys();
        // Keyboard drive remains live with the system cursor visible. Mouse
        // capture is required only for relative pointer aiming; coupling WASD
        // to a successful cursor-capture click made movement appear dead when
        // a HUD element intercepted that click.
        if (cameras != null && cameras.ActiveMode != 1) return;

        float forward = Axis(forwardKey, backwardKey);
        float strafe = Axis(strafeLeftKey, strafeRightKey);
        // 小陀螺 spin: C starts counter-clockwise and stops it again, V does the same
        // clockwise, so the two keys latch rather than needing to be held. The server
        // drops the rate outside 小陀螺, which is what keeps C/V inert in 自动跟随.
        if (KeyPressed(spinLeftKey)) chassisSpinLatch = chassisSpinLatch == 1f ? 0f : 1f;
        if (KeyPressed(spinRightKey)) chassisSpinLatch = chassisSpinLatch == -1f ? 0f : -1f;
        float turretYaw = 0f, pitch = 0f;
        if (suppressMouseFrames > 0) suppressMouseFrames--;
        else if (cursorCaptured)
        {
            // Mouse X always aims the gimbal. In 自动跟随 the chassis trails that aim;
            // in 小陀螺 the chassis spins on C/V and the gimbal holds its world heading,
            // which is what leaves the turret marker still while the chassis marker turns.
            // Match the custom UI Unity client: Input Manager Mouse X/Y are
            // normalized relative axes sent as live gimbal rates. Accumulating
            // raw Input System pixel deltas caused a delayed aim backlog.
            turretYaw = Mathf.Clamp(-Input.GetAxisRaw("Mouse X") * mouseYawSensitivity, -1f, 1f);
            pitch = Mathf.Clamp(Input.GetAxisRaw("Mouse Y") * mousePitchSensitivity, -1f, 1f);
        }
        // Barrel keys add to the mouse and stay live even while mouse frames are
        // suppressed. They are rebindable in the settings page, so they have to reach
        // the command; before this they were declared and rebindable but never read.
        pitch = Mathf.Clamp(pitch + Axis(barrelUpKey, barrelDownKey), -1f, 1f);
        client.Control(forward, strafe, chassisSpinLatch, turretYaw, pitch, KeyHeld(boostKey), KeyHeld(brakeKey));
        if (KeyPressed(modeToggleKey))
        {
            if (!controlModeKnown && client.State != null && !string.IsNullOrEmpty(client.State.controlMode))
                controlModeGyro = client.State.controlMode == "gyro";
            controlModeGyro = !controlModeGyro;
            controlModeKnown = true;
            chassisSpinLatch = 0f;   // a latched spin does not carry across a mode switch
            client.SetControlMode(controlModeGyro);
        }
        // Loading the other round: the server solves the sight from whatever is loaded,
        // so this is a two-way switch rather than a mode.
        if (KeyPressed(toggleAmmoKey) && client.State != null)
        {
            AmmoState loaded = client.State.ammo;
            string other = loaded != null && loaded.name == "17mm" ? "42mm" : "17mm";
            client.SetAmmo(other);
        }
        // Mouse0 is the simulator's fixed primary trigger. Keep a re-bound
        // secondary trigger if configured, but never let stale PlayerPrefs
        // remove left-click firing.
        bool primaryHeldNow = MouseHeld(KeyCode.Mouse0);
        bool firePressed = MousePressed(KeyCode.Mouse0) || (primaryHeldNow && !primaryFireHeld) ||
            (fireKey != KeyCode.Mouse0 && MousePressed(fireKey));
        primaryFireHeld = primaryHeldNow;
        if (firePressed) SubmitPrimaryFire("InputSystem/Legacy");
        if (MousePressed(markerKey) || KeyPressed(markerKeyAlt))
        {
            Camera camera = cameras ? cameras.ActiveCamera : Camera.main;
            if (camera)
            {
                // Mark where the crosshair actually hits the MuJoCo field mesh.
                // A fixed y=0 plane misses the imported terrain almost everywhere
                // in RMUC, so middle-click appeared to do nothing.
                Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                Vector3? fieldPoint = client.RaycastField?.Invoke(ray);
                if (fieldPoint.HasValue)
                {
                    Debug.Log($"[Marker] 3D field surface {fieldPoint.Value}");
                    client.MarkPoint(fieldPoint.Value);
                }
                else if (Physics.Raycast(ray, out RaycastHit hit, 500f, ~0, QueryTriggerInteraction.Collide) &&
                         hit.collider.GetComponentInParent<RobotController>() == null)
                {
                    Debug.Log($"[Marker] 3D ray hit {hit.collider.name} at {hit.point}");
                    client.MarkPoint(hit.point);
                }
                else
                {
                    Plane ground = new Plane(Vector3.up, Vector3.zero);
                    if (ground.Raycast(ray, out float distance))
                    {
                        Debug.Log($"[Marker] 3D plane fallback at {ray.GetPoint(distance)}");
                        client.MarkPoint(ray.GetPoint(distance));
                    }
                    else Debug.LogWarning("[Marker] 3D ray missed field and plane");
                }
            }
        }
        if (KeyPressed(autoLobKey) && !KeyHeld(KeyCode.LeftControl) && !KeyHeld(KeyCode.RightControl))
        {
            bool enabled = client.State?.aim != null && client.State.aim.autoCalibration;
            client.SetCalibration(!enabled);
        }
        SelectCameraHotkeys();
    }

    void OnGUI()
    {
        Event current = Event.current;
        if (current == null || current.type != EventType.MouseDown || current.button != 0) return;
        if (!operatorReady || uiBlocked || client == null || (cameras != null && cameras.ActiveMode != 1)) return;
        SubmitPrimaryFire("IMGUI");
    }

    bool SubmitPrimaryFire(string source)
    {
        if (Time.frameCount == lastFireInputFrame) return false;
        lastFireInputFrame = Time.frameCount;
        return client != null && client.Fire();
    }

    void SelectCameraHotkeys()
    {
        if (!cameras) return;
        if (KeyPressed(cameraOperatorKey))
        {
            cameras.Select(1);
            SetCursorCaptured(Application.isFocused);
        }
        // Camera 2/3 are tactical previews, not alternate play views. The
        // MuJoCo + original-scene operator workflow must remain first-person;
        // keep those previews in their HUD panels instead of replacing the
        // player's render camera.
        if (KeyPressed(cameraOverviewKey) || KeyPressed(cameraBallisticsKey)) cameras.Select(1);
    }

    void OnDisable()
    {
        if (!passiveCursor) SetCursorState(false, false);
        if (client)
        {
            client.ClearAimDelta();
            client.Control(0f, 0f, 0f, 0f, 0f, false, true);
        }
    }

    void UpdateCursorCapture()
    {
        bool operatorView = cameras == null || cameras.ActiveMode == 1;
        if (!operatorView)
        {
            SetCursorState(false, false);
            return;
        }

        if (!cursorCaptured && Application.isFocused && MousePressed(KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2))
        {
            // The official HUD is a full-screen raycast canvas. Most of its
            // images are decorative and made the old IsPointerOverGameObject
            // test reject every click, so WASD/mouse capture never started.
            if (!PointerOverInteractiveControl()) SetCursorCaptured(true);
        }
    }

    public void SetCursorCaptured(bool captured)
    {
        SetCursorState(captured, !captured);
    }

    public void SetOperatorReady(bool ready)
    {
        operatorReady = ready;
        if (!ready) SetCursorState(false, true);
        else SetCursorCaptured(Application.isFocused);
    }

    void SetCursorState(bool captured, bool visible)
    {
        cursorCaptured = captured;
        if (passiveCursor) return;
        Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = visible;
    }

    public void SetUiBlocked(bool blocked, bool needsCursor = false, RectTransform cursorTarget = null)
    {
        uiBlocked = blocked;
        uiNeedsCursor = blocked && needsCursor;
        if (blocked)
        {
            if (client) client.ClearAimDelta();
            SetCursorState(false, uiNeedsCursor);
            if (uiNeedsCursor && cursorTarget) StartCoroutine(CenterCursorAfterLayout(cursorTarget));
        }
        else
        {
            bool operatorView = cameras == null || cameras.ActiveMode == 1;
            suppressMouseFrames = 2;
            SetCursorState(operatorView && Application.isFocused, false);
        }
    }

    IEnumerator CenterCursorAfterLayout(RectTransform target)
    {
        yield return new WaitForEndOfFrame();
        // The legacy bridge centres the pointer for every menu; this path only
        // serves the MuJoCo operator input layer.
        if (passiveCursor) yield break;
        if (!uiBlocked || !uiNeedsCursor || !target || !Application.isFocused) yield break;
        Vector2 center = new Vector2(Screen.width * .5f, Screen.height * .5f);
        // Input System screen coordinates are relative to the Game view in the
        // editor and to the player window in builds. Win32 GetActiveWindow()
        // refers to the whole editor, which displaced the cursor into its
        // chrome (or even the Windows Start menu) when P opened.
        if (Mouse.current != null) Mouse.current.WarpCursorPosition(center);
    }

    void OnApplicationFocus(bool focused)
    {
        if (passiveCursor) return;
        if (!focused) SetCursorState(false, operatorReady ? false : true);
        else
        {
            suppressMouseFrames = 2;
            if (uiBlocked) SetCursorState(false, uiNeedsCursor);
            else if (!operatorReady) SetCursorState(false, true);
            else SetCursorState(cameras == null || cameras.ActiveMode == 1, false);
        }
    }

    static float Axis(KeyCode positive, KeyCode negative)
        => (KeyHeld(positive) ? 1f : 0f) - (KeyHeld(negative) ? 1f : 0f);

    static bool KeyHeld(KeyCode key)
    {
        if (Keyboard.current == null || !TryMapKey(key, out Key mapped)) return false;
        return Keyboard.current[mapped].isPressed;
    }

    static bool KeyPressed(KeyCode key)
    {
        if (Keyboard.current == null || !TryMapKey(key, out Key mapped)) return false;
        return Keyboard.current[mapped].wasPressedThisFrame;
    }

    static bool MousePressed(KeyCode key)
    {
        int button = key == KeyCode.Mouse0 ? 0 : key == KeyCode.Mouse1 ? 1 : key == KeyCode.Mouse2 ? 2 : -1;
        if (button < 0) return false;

        // Unity's Game view can consume the Input System press edge while it
        // acquires focus or changes Cursor.lockState. The legacy edge remains
        // available when Active Input Handling is "Both" (this project uses
        // value 2), so accept either source. This is still a one-frame edge,
        // not continuous fire while the button is held.
        bool legacy = Input.GetMouseButtonDown(button);
        if (Mouse.current == null) return legacy;
        if (button == 0) return legacy || Mouse.current.leftButton.wasPressedThisFrame;
        if (button == 1) return legacy || Mouse.current.rightButton.wasPressedThisFrame;
        if (button == 2) return legacy || Mouse.current.middleButton.wasPressedThisFrame;
        return false;
    }

    static bool MousePressed(params KeyCode[] keys)
    {
        foreach (KeyCode key in keys) if (MousePressed(key)) return true;
        return false;
    }

    static bool MouseHeld(KeyCode key)
    {
        int button = key == KeyCode.Mouse0 ? 0 : key == KeyCode.Mouse1 ? 1 : key == KeyCode.Mouse2 ? 2 : -1;
        if (button < 0) return false;
        bool legacy = Input.GetMouseButton(button);
        if (Mouse.current == null) return legacy;
        if (button == 0) return legacy || Mouse.current.leftButton.isPressed;
        if (button == 1) return legacy || Mouse.current.rightButton.isPressed;
        return legacy || Mouse.current.middleButton.isPressed;
    }

    static bool TryMapKey(KeyCode key, out Key mapped)
    {
        switch (key)
        {
            case KeyCode.W: mapped = Key.W; return true;
            case KeyCode.S: mapped = Key.S; return true;
            case KeyCode.A: mapped = Key.A; return true;
            case KeyCode.D: mapped = Key.D; return true;
            case KeyCode.C: mapped = Key.C; return true;
            case KeyCode.V: mapped = Key.V; return true;
            case KeyCode.T: mapped = Key.T; return true;
            case KeyCode.G: mapped = Key.G; return true;
            case KeyCode.UpArrow: mapped = Key.UpArrow; return true;
            case KeyCode.DownArrow: mapped = Key.DownArrow; return true;
            case KeyCode.LeftShift: mapped = Key.LeftShift; return true;
            case KeyCode.RightShift: mapped = Key.RightShift; return true;
            case KeyCode.Space: mapped = Key.Space; return true;
            case KeyCode.Alpha1: mapped = Key.Digit1; return true;
            case KeyCode.Alpha2: mapped = Key.Digit2; return true;
            case KeyCode.Alpha3: mapped = Key.Digit3; return true;
            case KeyCode.LeftControl: mapped = Key.LeftCtrl; return true;
            case KeyCode.RightControl: mapped = Key.RightCtrl; return true;
            default: mapped = Key.None; return false;
        }
    }

    static bool PointerOverInteractiveControl()
    {
        EventSystem events = EventSystem.current;
        if (events == null) return false;
        PointerEventData data = new PointerEventData(events) { position = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero };
        var hits = new System.Collections.Generic.List<RaycastResult>();
        events.RaycastAll(data, hits);
        foreach (RaycastResult hit in hits)
        {
            if (hit.gameObject.GetComponentInParent<Selectable>() != null) return true;
            if (hit.gameObject.GetComponentInParent<TMP_InputField>() != null) return true;
        }
        return false;
    }

    public KeyCode GetBinding(BindableAction action)
    {
        switch (action)
        {
            case BindableAction.Forward: return forwardKey;
            case BindableAction.Backward: return backwardKey;
            case BindableAction.StrafeLeft: return strafeLeftKey;
            case BindableAction.StrafeRight: return strafeRightKey;
            case BindableAction.SpinLeft: return spinLeftKey;
            case BindableAction.SpinRight: return spinRightKey;
            case BindableAction.BarrelUp: return barrelUpKey;
            case BindableAction.BarrelDown: return barrelDownKey;
            case BindableAction.Boost: return boostKey;
            case BindableAction.Brake: return brakeKey;
            case BindableAction.Fire: return fireKey;
            case BindableAction.AutoLob: return autoLobKey;
            case BindableAction.CameraOperator: return cameraOperatorKey;
            case BindableAction.CameraOverview: return cameraOverviewKey;
            case BindableAction.CameraBallistics: return cameraBallisticsKey;
            default: return fireKey;
        }
    }

    public void SetBinding(BindableAction action, KeyCode key)
    {
        KeyCode previous = GetBinding(action);
        foreach (BindableAction other in System.Enum.GetValues(typeof(BindableAction)))
        {
            if (other.Equals(action) || GetBinding(other) != key) continue;
            ApplyBinding(other, previous);
            PlayerPrefs.SetInt("Pacific.Bind." + other, (int)previous);
            break;
        }
        ApplyBinding(action, key);
        PlayerPrefs.SetInt("Pacific.Bind." + action, (int)key);
        PlayerPrefs.Save();
    }

    void ApplyBinding(BindableAction action, KeyCode key)
    {
        switch (action)
        {
            case BindableAction.Forward: forwardKey = key; break;
            case BindableAction.Backward: backwardKey = key; break;
            case BindableAction.StrafeLeft: strafeLeftKey = key; break;
            case BindableAction.StrafeRight: strafeRightKey = key; break;
            case BindableAction.SpinLeft: spinLeftKey = key; break;
            case BindableAction.SpinRight: spinRightKey = key; break;
            case BindableAction.BarrelUp: barrelUpKey = key; break;
            case BindableAction.BarrelDown: barrelDownKey = key; break;
            case BindableAction.Boost: boostKey = key; break;
            case BindableAction.Brake: brakeKey = key; break;
            case BindableAction.Fire: fireKey = key; break;
            case BindableAction.AutoLob: autoLobKey = key; break;
            case BindableAction.CameraOperator: cameraOperatorKey = key; break;
            case BindableAction.CameraOverview: cameraOverviewKey = key; break;
            case BindableAction.CameraBallistics: cameraBallisticsKey = key; break;
        }
    }

    void SetBindingDirect(BindableAction action, KeyCode key)
    {
        ApplyBinding(action, key);
        PlayerPrefs.SetInt("Pacific.Bind." + action, (int)key);
    }

    public void ResetBindings()
    {
        SetBindingDirect(BindableAction.Forward, KeyCode.W);
        SetBindingDirect(BindableAction.Backward, KeyCode.S);
        SetBindingDirect(BindableAction.StrafeLeft, KeyCode.A);
        SetBindingDirect(BindableAction.StrafeRight, KeyCode.D);
        SetBindingDirect(BindableAction.SpinLeft, KeyCode.C);
        SetBindingDirect(BindableAction.SpinRight, KeyCode.V);
        SetBindingDirect(BindableAction.BarrelUp, KeyCode.UpArrow);
        SetBindingDirect(BindableAction.BarrelDown, KeyCode.DownArrow);
        SetBindingDirect(BindableAction.Boost, KeyCode.LeftShift);
        SetBindingDirect(BindableAction.Brake, KeyCode.Space);
        SetBindingDirect(BindableAction.Fire, KeyCode.Mouse0);
        SetBindingDirect(BindableAction.AutoLob, KeyCode.T);
        SetBindingDirect(BindableAction.CameraOperator, KeyCode.Alpha1);
        SetBindingDirect(BindableAction.CameraOverview, KeyCode.Alpha2);
        SetBindingDirect(BindableAction.CameraBallistics, KeyCode.Alpha3);
        PlayerPrefs.SetInt("Pacific.Bind.Version", BindingVersion);
        PlayerPrefs.Save();
    }

    // Bumped when a bindable action is added or removed; stale keys from earlier layouts
    // are dropped below so a removed action cannot come back through PlayerPrefs.
    const int BindingVersion = 6;

    void LoadBindings()
    {
        foreach (BindableAction action in System.Enum.GetValues(typeof(BindableAction)))
        {
            string key = "Pacific.Bind." + action;
            if (PlayerPrefs.HasKey(key)) SetBindingDirect(action, (KeyCode)PlayerPrefs.GetInt(key));
        }
        PlayerPrefs.DeleteKey("Pacific.Bind.Reset");
        if (PlayerPrefs.GetInt("Pacific.Bind.Version", 1) != BindingVersion)
        {
            SetBindingDirect(BindableAction.Fire, KeyCode.Mouse0);
            if (autoLobKey == KeyCode.Tab) SetBindingDirect(BindableAction.AutoLob, KeyCode.T);
            SetBindingDirect(BindableAction.CameraOperator, KeyCode.Alpha1);
            SetBindingDirect(BindableAction.CameraOverview, KeyCode.Alpha2);
            SetBindingDirect(BindableAction.CameraBallistics, KeyCode.Alpha3);
            PlayerPrefs.DeleteKey("Pacific.Bind.AssistAim");
            PlayerPrefs.SetInt("Pacific.Bind.Version", BindingVersion);
        }
        PlayerPrefs.Save();
    }
}
