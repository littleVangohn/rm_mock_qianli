using System;
using UnityEngine;

[Serializable] public class SteerTrajectoryState { public bool active; public TrajectoryPoint[] points; public float[] yawDeg; }
[Serializable] public class HeroState { public int id; public string robotClass; public float[] positionM; public float[] linearVelocityMps; public float[] angularVelocityDps; public float yawDeg; public float linearSpeedMps; public float angularSpeedDps; public float turretWorldYawDeg; public float barrelPitchDeg; public SteerTrajectoryState steerTrajectory; }
[Serializable] public class TrajectoryPoint { public float x; public float y; public float z; }
// One sample of the predicted round path: the world point and its time of flight, which
// is what lets the funnel line lay a 0.1 s ladder on the curve.
[Serializable] public class FunnelPoint { public float x; public float y; public float z; public float t; }
[Serializable] public class FunnelState { public FunnelPoint[] points; public float tofS; public FunnelPoint impactM; }
// One rung of the sight's holdover ladder.  sightDeg is the angle below the crosshair at
// which the round crosses this range, measured from the camera rather than the bore --
// the camera rides above the barrel, and the difference is visible on screen.
[Serializable] public class RangeMarkState { public float distanceM; public float sightDeg; public float dropM; }
[Serializable] public class RangeScaleState { public float referenceDistanceM; public float computedAtPitchDeg; public RangeMarkState[] marks; }
// The gun's group at the reference range, as one sigma half-extents in metres.  The
// overlay multiplies by however many sigmas it draws.
[Serializable] public class DispersionState { public float referenceDistanceM; public float sigmas; public float sigmaWidthM; public float sigmaHeightM; public float sigmaMrad; public int samples; }
[Serializable] public class AimState { public bool available; public bool autoCalibration; public bool ready; public string reason; public float actualTurretYawDeg; public float actualBarrelPitchDeg; public float distanceM; public float tofS; public float clearanceM; public bool clear; public FunnelState funnel; public TrajectoryPoint[] trajectory; public RangeScaleState rangeScale; public DispersionState dispersion; }
// The round the launcher is loaded with.  The sight solves from it, so the ladder and
// the funnel change when it does.
[Serializable] public class AmmoState { public string name; public string label; public float speed; public float dragK; public float mass; public float radius; }
[Serializable] public class SightTargetState { public float distanceM; public float[] positionM; public float[] normalM; public float radius; public bool flashing; }
[Serializable] public class ProjectileState { public bool active; public bool visible; public string status; public bool hit; public float[] impactM; public float[] positionM; public float[] velocityMps; public float[] originM; public float directionDeg; public int shotId; public int shooterId; public string roundName; }
[Serializable] public class RobotState { public int id; public string source; public string robotClass; public float x; public float y; public float z; public float yawDeg; }
[Serializable] public class RobotMasterRobot { public int id; public int faction; public int hp; public int maxHp; public int ammo; public int energy; public int economy; public string[] buffs; public bool firing; public bool hasRecentHit; public float hitDirectionDeg; }
[Serializable] public class RobotMasterState { public bool connected; public bool simulated; public RobotMasterRobot[] robots; }
[Serializable] public class MatchRuleState { public float timeLeftS; public int gameStatus; public int redCoins; public int blueCoins; public int redBaseHp; public int redBaseMaxHp; public int blueBaseHp; public int blueBaseMaxHp; public int redOutpostHp; public int redOutpostMaxHp; public int blueOutpostHp; public int blueOutpostMaxHp; public int localRobotId; public int localFaction; }
[Serializable] public class ModulesState { public bool autoCalibration; public bool superPower; public bool gyro; public bool autoAim; public bool frictionWheel; }
[Serializable] public class FrameBounds { public float xMin; public float xMax; public float yMin; public float yMax; }
[Serializable] public class FrameState { public FrameBounds bounds; }
[Serializable] public class CameraState { public float[] positionM; public float[] forwardM; public float[] upM; public float fovDeg; }
[Serializable] public class SharedMarkerState { public bool active; public float[] positionM; public float setAt; }
[Serializable] public class MujocoState { public string schema; public bool connected; public float simulationTimeS; public HeroState hero; public AimState aim; public ProjectileState projectile; public ProjectileState enemyProjectile; public SharedMarkerState sharedMarker; public CameraState operatorCamera; public RobotState[] robots; public SightTargetState[] sightTargets; public RobotMasterState robotMaster; public MatchRuleState rules; public string controlMode; public ModulesState modules; public FrameState frame; public AmmoState ammo; public int selectedRobotId; public string selectedRobotClass; }
