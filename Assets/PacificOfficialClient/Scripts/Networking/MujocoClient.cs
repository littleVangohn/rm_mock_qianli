using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class MujocoClient : MonoBehaviour
{
    const int MaxMessageBytes = 64 * 1024;
    const string Schema = "pacific-doorlock.realtime.v2";

    public string host = "127.0.0.1";
    // 8770, not 8766: the live referee bridge (referee_live_server.py) owns 8766 on
    // Windows and speaks robomaster.referee.v1, which this client rejects by schema.
    public int port = 8770;
    // Legacy build: the original simulator is authoritative, so the HUD is fed
    // by PublishLocalState instead of a TCP endpoint and no network thread runs.
    public bool LocalMode;
    public MujocoState State { get; private set; }
    public bool IsConnected => LocalMode || (socketConnected && SecondsSince(lastStateTicks) < 0.5);
    public event Action<MujocoState> StateUpdated;
    public event Action<CommandFeedback> CommandCompleted;
    public Func<int, string, bool> FireAllowed;
    public Func<int, string, string> FireBlockReason;
    // Converts a Unity world point into the backend's MuJoCo field frame. The
    // bridge installs this once the selected robot is calibrated.
    public Func<Vector3, Vector3> UnityToBackendWorld;
    // Ray against the imported MuJoCo field surface only, so the crosshair
    // marker cannot land on the robot's own armor or a legacy collider.
    public Func<Ray, Vector3?> RaycastField;
    public event Action<int, string> FireQueued;
    // Legacy build: the OSS physics runs in-process instead of behind the TCP
    // backend. ControlInput feeds the same command shape into these callbacks.
    public Action<float, float, float, float, float, bool, bool> LocalControl;
    public Action<bool> LocalControlMode;
    public Action<string> LocalAmmo;

    readonly object stateLock = new object();
    readonly object socketLock = new object();
    readonly ConcurrentQueue<string> outbound = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<CommandFeedback> commandFeedback = new ConcurrentQueue<CommandFeedback>();
    Thread networkThread;
    TcpClient activeSocket;
    volatile bool running;
    volatile bool socketConnected;
    long lastStateTicks;
    long outboundSequence;
    long deliveredSequence = -1;
    StateSample previousSample;
    StateSample currentSample;
    ControlPayload latestControl = new ControlPayload();
    ControlPayload lastQueuedControl = new ControlPayload();
    readonly ControlPayload queuedControl = new ControlPayload();
    readonly ControlMessage controlMessage = new ControlMessage { schema = Schema };
    int selectedRobotId = 1;
    string selectedRobotClass = "Hero";
    bool hasSelection;
    bool selectionDirty;
    bool selectionWasConnected;
    bool controlDirty = true;
    float nextControlAt;
    float lastControlAt;
    float pendingYawDeg;
    float pendingPitchDeg;
    float sampledYawRateSeconds;
    float sampledPitchRateSeconds;
    float sampledAimSeconds;
    // Playback delay and server-clock estimate for smooth pose interpolation.
    const double InterpolationDelaySeconds = 1.0 / 30.0;
    double smoothedClockOffset;
    long offsetSampleTicks;
    bool clockInitialized;

    /// <summary>
    /// Legacy build entry point: hands a locally assembled state to every HUD
    /// subscriber exactly as a backend packet would.
    /// </summary>
    public void PublishLocalState(MujocoState state)
    {
        if (state == null) return;
        State = state;
        StateUpdated?.Invoke(state);
    }

    void Start()
    {
        Application.targetFrameRate = 90;
        if (LocalMode) return;
        running = true;
        networkThread = new Thread(NetworkLoop) { IsBackground = true, Name = "MuJoCo TCP client" };
        networkThread.Start();
    }

    void Update()
    {
        if (LocalMode) return;
        while (commandFeedback.TryDequeue(out CommandFeedback feedback)) CommandCompleted?.Invoke(feedback);
        StateSample sample;
        lock (stateLock) sample = currentSample;
        if (sample != null && sample.sequence != deliveredSequence)
        {
            deliveredSequence = sample.sequence;
            State = sample.state;
            StateUpdated?.Invoke(State);
        }

        bool connected = IsConnected;
        if (connected && !selectionWasConnected) selectionDirty = true;
        selectionWasConnected = connected;
        if (!connected || !hasSelection) return;
        if (selectionDirty)
        {
            outbound.Enqueue(JsonUtility.ToJson(new CommandMessage
            {
                type = "command", schema = Schema,
                seq = Interlocked.Increment(ref outboundSequence), name = "select_robot",
                payload = new CommandPayload { robotId = selectedRobotId, robotClass = selectedRobotClass }
            }));
            selectionDirty = false;
        }
        bool active = latestControl.IsActive || Mathf.Abs(sampledYawRateSeconds) > 0.0001f ||
            Mathf.Abs(sampledPitchRateSeconds) > 0.0001f || Mathf.Abs(pendingYawDeg) > 0.001f ||
            Mathf.Abs(pendingPitchDeg) > 0.001f;
        float interval = active || controlDirty ? 1f / 30f : 0.1f;
        if (Time.unscaledTime < nextControlAt) return;
        nextControlAt = Time.unscaledTime + interval;
        string type = active || controlDirty ? "control" : "heartbeat";
        // A one-shot mouse-rate packet must be followed by a zero-rate packet;
        // otherwise the backend holds that rate until its 250 ms timeout.
        controlDirty = QueueControl(type);
    }

    void OnDestroy()
    {
        running = false;
        CloseActiveSocket();
        if (networkThread != null && networkThread.IsAlive) networkThread.Join(500);
        networkThread = null;
    }

    public void Control(float forward, float strafe, float chassisYaw, float turretYaw, float pitch, bool boost, bool brake)
    {
        if (LocalMode)
        {
            LocalControl?.Invoke(forward, strafe, chassisYaw, turretYaw, pitch, boost, brake);
            return;
        }
        // Reused across frames: this runs every rendered frame and the old
        // per-call allocations were pure GC churn.
        ControlPayload next = latestControl;
        next.robotId = selectedRobotId;
        next.forward = forward;
        next.strafe = strafe;
        next.yaw = chassisYaw;
        next.turretYaw = turretYaw;
        next.pitch = pitch;
        next.boost = boost;
        next.brake = brake;
        // Mouse rates are sampled every render frame and averaged into the
        // next 30 Hz packet. Their per-frame changes must not force a packet
        // at render FPS or cause intermediate deltas to be lost.
        bool driveChanged = next.robotId != lastQueuedControl.robotId ||
            next.forward != lastQueuedControl.forward || next.strafe != lastQueuedControl.strafe ||
            next.yaw != lastQueuedControl.yaw || next.boost != lastQueuedControl.boost ||
            next.brake != lastQueuedControl.brake;
        if (driveChanged)
        {
            controlDirty = true;
            nextControlAt = 0f;
        }
        lastQueuedControl.CopyFrom(next);
        float sampleSeconds = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 1f / 30f);
        sampledYawRateSeconds += Mathf.Clamp(turretYaw, -1f, 1f) * sampleSeconds;
        sampledPitchRateSeconds += Mathf.Clamp(pitch, -1f, 1f) * sampleSeconds;
        sampledAimSeconds += sampleSeconds;
    }

    public void ClearAimDelta()
    {
        if (LocalMode) return;
        if (Mathf.Abs(pendingYawDeg) < 0.001f && Mathf.Abs(pendingPitchDeg) < 0.001f &&
            Mathf.Abs(sampledYawRateSeconds) < 0.0001f && Mathf.Abs(sampledPitchRateSeconds) < 0.0001f) return;
        pendingYawDeg = 0f;
        pendingPitchDeg = 0f;
        sampledYawRateSeconds = 0f;
        sampledPitchRateSeconds = 0f;
        sampledAimSeconds = 0f;
        controlDirty = true;
        nextControlAt = 0f;
    }

    public void SelectRobot(int robotId, string robotClass)
    {
        int nextId = Mathf.Max(1, robotId);
        string nextClass = string.IsNullOrWhiteSpace(robotClass) ? "Hero" : robotClass;
        if (hasSelection && selectedRobotId == nextId && string.Equals(selectedRobotClass, nextClass, StringComparison.OrdinalIgnoreCase)) return;
        hasSelection = true;
        selectedRobotId = nextId;
        selectedRobotClass = nextClass;
        selectionDirty = true;
        // Force a control refresh even if the axes are at rest, so the backend
        // sees the selected id before the first movement command.
        controlDirty = true;
        nextControlAt = 0f;
    }

    public bool Fire()
    {
        // The legacy adapter keeps IsShoot in sync with the held mouse button,
        // so a press needs no backend round trip.
        if (LocalMode) return true;
        if (!IsConnected)
        {
            commandFeedback.Enqueue(new CommandFeedback("fire", false, "MUJOCO_OFFLINE"));
            return false;
        }
        if (!hasSelection)
        {
            commandFeedback.Enqueue(new CommandFeedback("fire", false, "ROBOT_NOT_SELECTED"));
            return false;
        }
        string roundName = State?.ammo?.name;
        string blockReason = FireBlockReason?.Invoke(selectedRobotId, roundName);
        if (!string.IsNullOrEmpty(blockReason))
        {
            // A local rule rejection produces no backend acknowledgement. Send
            // its exact cause through the normal feedback channel instead.
            commandFeedback.Enqueue(new CommandFeedback("fire", false, blockReason));
            return false;
        }
        if (FireAllowed != null && !FireAllowed(selectedRobotId, roundName))
        {
            // A local referee rejection produces no backend command/ack. Route
            // it through the normal feedback channel so left click is never
            // silently ignored.
            commandFeedback.Enqueue(new CommandFeedback("fire", false, "REFEREE_BLOCKED"));
            return false;
        }
        QueueCommand("fire", new CommandPayload());
        FireQueued?.Invoke(selectedRobotId, roundName);
        return true;
    }
    public void MarkFieldPoint(float x, float y)
    {
        // Already in backend field metres (used by the M map click).
        UnityEngine.Debug.Log($"[Marker] map/field send ({x:F2}, {y:F2})");
        QueueCommand("marker", new CommandPayload { markerX = x, markerY = y, markerZ = 0f });
    }

    public void MarkPoint(Vector3 worldPoint)
    {
        Vector3 point = UnityToBackendWorld != null
            ? UnityToBackendWorld(worldPoint)
            : new Vector3(worldPoint.x, worldPoint.z, worldPoint.y);
        UnityEngine.Debug.Log($"[Marker] 3D send unity={worldPoint} backend={point} converter={(UnityToBackendWorld != null)}");
        QueueCommand("marker", new CommandPayload
        {
            markerX = point.x, markerY = point.y, markerZ = point.z,
        });
    }
    public void ResetSimulation() => QueueCommand("reset", new CommandPayload());
    public void SetCalibration(bool enabled) => QueueCommand("calibration", new CommandPayload { enabled = enabled });
    public void SetControlMode(bool gyro)
    {
        if (LocalMode) { LocalControlMode?.Invoke(gyro); return; }
        QueueCommand("mode", new CommandPayload { mode = gyro ? "gyro" : "follow" });
    }

    public void SetAmmo(string name)
    {
        if (LocalMode) { LocalAmmo?.Invoke(name); return; }
        QueueCommand("ammo", new CommandPayload { ammo = name });
    }

    public bool TryGetSamples(out StateSample previous, out StateSample current, out double targetSimulationTime)
    {
        lock (stateLock)
        {
            previous = previousSample;
            current = currentSample;
        }
        if (current == null)
        {
            targetSimulationTime = 0;
            return false;
        }
        // Reconstruct the server's simulation clock instead of trusting the
        // arrival time of each packet. A packet delayed by a few milliseconds
        // would otherwise move the render target by the same amount, which is
        // visible as a hitch even though the state itself is correct. The
        // offset is smoothed over several packets; a large jump (reset,
        // reconnect) re-locks immediately.
        if (current.receivedTicks != offsetSampleTicks)
        {
            offsetSampleTicks = current.receivedTicks;
            double receivedAt = current.receivedTicks / (double)Stopwatch.Frequency;
            double offset = current.state.simulationTimeS - receivedAt;
            if (!clockInitialized)
            {
                smoothedClockOffset = offset;
                clockInitialized = true;
            }
            else if (Math.Abs(offset - smoothedClockOffset) > 0.25)
            {
                smoothedClockOffset = offset;
            }
            else
            {
                smoothedClockOffset += (offset - smoothedClockOffset) * 0.1;
            }
        }
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double target = now + smoothedClockOffset - InterpolationDelaySeconds;
        if (previous != null) target = Math.Max(target, previous.state.simulationTimeS);
        targetSimulationTime = Math.Min(target, current.state.simulationTimeS + 0.1);
        return true;
    }

    bool QueueControl(string type)
    {
        ControlPayload payload = queuedControl;
        payload.CopyFrom(latestControl);
        if (sampledAimSeconds > 0.0001f)
        {
            payload.turretYaw = Mathf.Clamp(sampledYawRateSeconds / sampledAimSeconds, -1f, 1f);
            payload.pitch = Mathf.Clamp(sampledPitchRateSeconds / sampledAimSeconds, -1f, 1f);
        }
        sampledYawRateSeconds = 0f;
        sampledPitchRateSeconds = 0f;
        sampledAimSeconds = 0f;
        float now = Time.unscaledTime;
        float elapsed = lastControlAt > 0f ? Mathf.Clamp(now - lastControlAt, 1f / 120f, 0.1f) : 1f / 30f;
        lastControlAt = now;
        float yawRate = Mathf.Clamp(pendingYawDeg / (90f * elapsed), -1f, 1f);
        float pitchRate = Mathf.Clamp(pendingPitchDeg / (30f * elapsed), -1f, 1f);
        pendingYawDeg -= yawRate * 90f * elapsed;
        pendingPitchDeg -= pitchRate * 30f * elapsed;
        payload.turretYaw = Mathf.Clamp(payload.turretYaw + yawRate, -1f, 1f);
        payload.pitch = Mathf.Clamp(payload.pitch + pitchRate, -1f, 1f);
        payload.seq = Interlocked.Increment(ref outboundSequence);
        controlMessage.type = type;
        controlMessage.seq = payload.seq;
        controlMessage.payload = payload;
        outbound.Enqueue(JsonUtility.ToJson(controlMessage));
        return Mathf.Abs(payload.turretYaw) > 0.001f || Mathf.Abs(payload.pitch) > 0.001f ||
            Mathf.Abs(pendingYawDeg) > 0.001f || Mathf.Abs(pendingPitchDeg) > 0.001f;
    }

    void QueueCommand(string name, CommandPayload payload)
    {
        if (!IsConnected) return;
        outbound.Enqueue(JsonUtility.ToJson(new CommandMessage
        {
            type = "command", schema = Schema,
            seq = Interlocked.Increment(ref outboundSequence), name = name, payload = payload,
        }));
    }

    void NetworkLoop()
    {
        double[] delays = { 0.25, 0.5, 1.0, 2.0 };
        int attempt = 0;
        while (running)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                lock (socketLock) activeSocket = client;
                client.Connect(host, port);
                socketConnected = true;
                attempt = 0;
                while (outbound.TryDequeue(out _)) { }
                RunConnected(client);
            }
            catch (Exception exception)
            {
                socketConnected = false;
                if (attempt == 0 || attempt % 10 == 0)
                    UnityEngine.Debug.LogWarning($"[MuJoCo] Realtime TCP {host}:{port} disconnected: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                socketConnected = false;
                CloseActiveSocket();
            }
            if (!running) break;
            double delay = delays[Math.Min(attempt++, delays.Length - 1)];
            int slices = (int)(delay / 0.05);
            for (int i = 0; i < slices && running; i++) Thread.Sleep(50);
        }
    }

    void RunConnected(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        var readBuffer = new byte[128 * 1024];
        var chunk = new byte[16 * 1024];
        int buffered = 0;
        long lastStateSequence = -1;
        while (running && client.Connected)
        {
            while (outbound.TryDequeue(out string json)) WriteFrame(stream, json);
            if (!stream.DataAvailable)
            {
                Thread.Sleep(2);
                continue;
            }
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) return;
            if (buffered + read > readBuffer.Length) throw new InvalidDataException("realtime receive buffer overflow");
            Buffer.BlockCopy(chunk, 0, readBuffer, buffered, read);
            buffered += read;
            int consumed = 0;
            while (buffered - consumed >= 4)
            {
                int size = (readBuffer[consumed] << 24) | (readBuffer[consumed + 1] << 16) |
                           (readBuffer[consumed + 2] << 8) | readBuffer[consumed + 3];
                if (size <= 0 || size > MaxMessageBytes) throw new InvalidDataException("invalid realtime message size");
                if (buffered - consumed < size + 4) break;
                string json = Encoding.UTF8.GetString(readBuffer, consumed + 4, size);
                consumed += size + 4;
                var message = JsonUtility.FromJson<InboundMessage>(json);
                if (message == null || message.schema != Schema) continue;
                if (message.type == "state" && message.payload != null)
                {
                    if (message.seq <= lastStateSequence) continue;
                    lastStateSequence = message.seq;
                    long received = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref lastStateTicks, received);
                    lock (stateLock)
                    {
                        previousSample = currentSample;
                        currentSample = new StateSample(message.seq, received, message.payload);
                    }
                }
                else if (message.type == "ack")
                {
                    AckMessage ack = JsonUtility.FromJson<AckMessage>(json);
                    commandFeedback.Enqueue(new CommandFeedback(message.name, message.ok, message.error,
                        ack?.payload?.shotId ?? 0));
                }
            }
            if (consumed > 0)
            {
                Buffer.BlockCopy(readBuffer, consumed, readBuffer, 0, buffered - consumed);
                buffered -= consumed;
            }
        }
    }

    static void WriteFrame(NetworkStream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxMessageBytes) throw new InvalidDataException("outbound realtime message is too large");
        byte[] header =
        {
            (byte)(body.Length >> 24), (byte)(body.Length >> 16),
            (byte)(body.Length >> 8), (byte)body.Length,
        };
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
    }

    void CloseActiveSocket()
    {
        TcpClient client;
        lock (socketLock)
        {
            client = activeSocket;
            activeSocket = null;
        }
        if (client == null) return;
        try { client.Close(); } catch { }
    }

    static double SecondsSince(long timestamp)
    {
        if (timestamp == 0) return double.PositiveInfinity;
        return (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;
    }
}

public sealed class StateSample
{
    public readonly long sequence;
    public readonly long receivedTicks;
    public readonly MujocoState state;
    public StateSample(long sequence, long receivedTicks, MujocoState state)
    {
        this.sequence = sequence;
        this.receivedTicks = receivedTicks;
        this.state = state;
    }
}

public sealed class CommandFeedback
{
    public readonly string name;
    public readonly bool ok;
    public readonly string error;
    public readonly int shotId;
    public CommandFeedback(string name, bool ok, string error, int shotId = 0)
    {
        this.name = name;
        this.ok = ok;
        this.error = error;
        this.shotId = shotId;
    }
}

[Serializable] sealed class AckPayload { public int shotId; }
[Serializable] sealed class AckMessage { public AckPayload payload; }

[Serializable]
public class ControlPayload
{
    public int robotId = 1;
    public float forward;
    public float strafe;
    public float yaw;
    public float turretYaw;
    public float pitch;
    public bool boost;
    public bool brake;
    public long seq;
    public bool IsActive => Mathf.Abs(forward) > 0.001f || Mathf.Abs(strafe) > 0.001f ||
                            Mathf.Abs(yaw) > 0.001f || Mathf.Abs(turretYaw) > 0.001f ||
                            Mathf.Abs(pitch) > 0.001f || boost || brake;
    public ControlPayload Copy() => (ControlPayload)MemberwiseClone();
    public void CopyFrom(ControlPayload other)
    {
        if (other == null) return;
        robotId = other.robotId;
        forward = other.forward;
        strafe = other.strafe;
        yaw = other.yaw;
        turretYaw = other.turretYaw;
        pitch = other.pitch;
        boost = other.boost;
        brake = other.brake;
        seq = other.seq;
    }
}

[Serializable] class ControlMessage { public string type; public string schema; public long seq; public ControlPayload payload; }
[Serializable] class CommandMessage { public string type; public string schema; public long seq; public string name; public CommandPayload payload; }
[Serializable] class CommandPayload
{
    public int robotId;
    public string robotClass;
    public bool enabled;
    public string mode;
    public string ammo;
    public float markerX;
    public float markerY;
    public float markerZ;
}
[Serializable] class InboundMessage
{
    public string type;
    public string schema;
    public long seq;
    public string name;
    public bool ok;
    public string error;
    public MujocoState payload;
}
