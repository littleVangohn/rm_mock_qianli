#!/usr/bin/env python3
"""Realtime RMUC 2026 MuJoCo bridge for the Pacific Doorlock UI."""

from __future__ import annotations

import argparse
import copy
import json
import math
import random
import signal
import threading
import time
import xml.etree.ElementTree as ET
from collections import deque
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

import mujoco
import numpy as np

from .physics.auto_fire import load_geometry_tree, solve_low_arc
from .physics.chassis import (
    apply_chassis_control, clamp, decimate_chassis_trajectory, predict_chassis_trajectory,
)
from .physics.manual_fire import (
    HeightfieldSampler, PREDICTION_STEP_S, dispersion_envelope, drop_table,
    perturbed_velocity, predict_manual_shot,
)
from .transport.realtime import RealtimeTCPServer


@dataclass(frozen=True)
class Ammo:
    """One projectile the launcher can be loaded with."""

    name: str
    label: str
    speed: float
    drag_k: float
    mass: float
    radius: float


TARGET = np.array([11.456334, 1.644304, 1.079304], dtype=float)
TARGET_NORMAL = np.array([0.4617486132350339, 0.0, -0.8870108331782217])
TARGET_WIDTH_AXIS = np.array([0.0, 1.0, 0.0])
TARGET_HEIGHT_AXIS = np.array([0.8870108331782217, 0.0, 0.4617486132350339])
TARGET_HALF_WIDTH = 0.05442350000459356
TARGET_HALF_HEIGHT = 0.046926976668543445
# The OSS shooter multiplies its configured speed by ten because its arena is
# authored at ten Unity units per metre.  RMUC2024_42mm-Default stores 16, so
# the equivalent MuJoCo launch speed is 16 m/s (not 18 m/s).
SPEED = 16.0
# Operator control modes.  "gyro" (小陀螺) spins the chassis on C/V while the
# gimbal holds its world heading; "follow" (自动跟随) turns the gimbal with the
# mouse and lets the chassis trail it.  The pose widget shows which one is live:
# gyro turns the chassis marker only, follow moves the turret marker first.
CONTROL_MODE_FOLLOW = "follow"
CONTROL_MODE_GYRO = "gyro"
CONTROL_MODES = (CONTROL_MODE_FOLLOW, CONTROL_MODE_GYRO)
DRAG_K = 0.009177231380230855
BALL_MASS = 0.0445
BALL_RADIUS = 0.02125
G = 9.81
# The two rounds this launcher can be loaded with, translated from the OSS
# ShooterController/prefab scale. The hero's default 42mm configuration is 16 m/s;
# the infantry's 17mm runs 12/15/18/30 m/s by performance level, and this backend uses
# the original simulator's fully upgraded 30 m/s value.
# Switching rounds changes the muzzle speed and, through it, the whole sight picture --
# the ladder and the funnel both solve from whatever is loaded.
#
# drag_k is drag force over mass, so it tracks frontal area per unit mass: pi*r^2 / m.
# 17mm against 42mm, r 0.0085 vs 0.02125 and m 0.0032 vs 0.0445, is a factor of 2.225.
AMMO_17MM_DRAG_FACTOR = 2.225
AMMO_42MM = Ammo("42mm", "42mm 弹丸", SPEED, DRAG_K, BALL_MASS, BALL_RADIUS)
AMMO_17MM = Ammo("17mm", "17mm 弹丸", 30.0, DRAG_K * AMMO_17MM_DRAG_FACTOR, 0.0032, 0.0085)
AMMO_TYPES = (AMMO_42MM, AMMO_17MM)
HERO_GROUND_OFFSET = 0.158419
MAP_BOUNDS = {"xMin": -14.876, "xMax": 14.876, "yMin": -6.400656, "yMax": 9.649343}
# A control packet older than this is treated as released, in the physics and in the HUD.
CONTROL_TIMEOUT_S = 0.25
# HUD steer overlay: shown only while A/D commands a strafe, and drawn over this horizon.
STEER_STRAFE_THRESHOLD = 0.05
# 0.6 s, not longer: the roll-out is exact only in free space, and terrain contact makes
# the real chassis travel 20-40% shorter over 1.2 s. Halving the horizon halves that gap.
STEER_HORIZON_S = 0.6
STEER_POINT_LIMIT = 24
# HUD funnel line: the predicted trajectory resampled at a fixed spacing so the front end
# can lay a 0.1 s ladder on it without knowing the integrator's step.
FUNNEL_STEP_S = 0.05
FUNNEL_POINT_LIMIT = 64

# Firing dispersion.  Zero: a fired round follows the predicted path exactly, so the
# funnel line and the rungs say what will happen and a target can be hit by aiming at it
# rather than by guessing a hold.  Raise it and the rounds scatter; the regression tests
# rely on the default being zero for a deterministic shot.
SHOT_SPREAD_MRAD = 0.0
SHOT_SPEED_SIGMA = 0.01

# The sight's group box is drawn from this instead of from the rounds, because with ideal
# shots there is no group to measure.  It is a nominal figure for the weapon the sight
# belongs to -- what the box says about the gun, not about this simulation's shots.
# Set it to SHOT_SPREAD_MRAD (and raise that) to make the box a real statistic again.
DISPERSION_NOMINAL_MRAD = 45.0
# The sight's group box is quoted at this range and drawn with this many sigmas.
DISPERSION_REFERENCE_M = 5.0
DISPERSION_SIGMAS = 1.0
DISPERSION_SAMPLES = 256
# The box is a statistic, and a statistic that redrew its own sample every refresh would
# crawl on screen.  A fixed seed makes the same parameters always produce the same box.
DISPERSION_SEED = 20261001
# Sight scale marks, in metres of slant range from the muzzle, five metres apart.  25 m
# is where a level-barrel ladder stops fitting the 720-unit centre overlay.
RANGE_SCALE_MARKS_M = (5.0, 10.0, 15.0, 20.0, 25.0)
# The ladder is solved at this barrel elevation and then left alone, so it is a fixed
# ruler rather than a readout that slides as the gun is raised.  It does mean the rungs
# no longer sit exactly on the funnel line during a steep 吊射 (a lobbed round is in the
# air longer and falls further, worth a few degrees at 20 deg of elevation); the funnel
# remains the ground truth and the ladder is the reference it is read against.
REFERENCE_PITCH_RAD = 0.0

# Two calibration discs hung in front of the spawn point, one on the 5 m rung and one on
# the 10 m, so the ruler can be checked by shooting at it instead of by trusting it.  The
# faces sit at the muzzle's own height: aim a rung at a disc's centre and the round has to
# arrive there, which is exactly the claim the ruler makes.
SIGHT_TARGET_RANGES_M = (5.0, 10.0)
SIGHT_TARGET_RADIUS_M = 0.25
# The near disc hangs this far above the muzzle, the far one sits on the ground.  The gap
# does two jobs: two targets at clearly different heights cannot be mistaken for each
# other, and a low near disc would intercept the shots aimed at the far one -- a round
# sent at a target on the ground at 10 m passes the 5 m plane only 0.14 m below the
# muzzle, straight through the middle of anything hung there.
SIGHT_TARGET_NEAR_RISE_M = 0.35
# The two discs stand at deliberately different heights: the near one on the line of
# fire, at the muzzle's own height, and the one behind it set down on the ground.  A pair
# at a common height fails twice over -- the far disc hides behind the near one on screen
# (a quarter-metre disc at 5 m already subtends more than the degree that separates them),
# and there is nothing to hang it on anyway: the round's own height at 10 m is 0.02 m,
# under the 0.31 m of terrain there, because this gun is lobbing by then or not arriving.
#
# What the pair is for is unchanged: put the rung for a disc's range on its centre and
# the round arrives there, whatever height the disc is at.
SIGHT_TARGET_MIN_CLEARANCE_M = 0.0
# How long a disc stays lit after taking a hit.  Long on purpose: with the spread this
# gun has, a hit is an event worth noticing, and the shooter is looking at the sight, not
# at the disc.
SIGHT_TARGET_FLASH_S = 1.0
# The rule target's collision group; the round is in group 10, and a contact needs one
# side's contype to meet the other's conaffinity.
SIGHT_TARGET_CONTYPE = 8
SIGHT_TARGET_CONAFFINITY = 8


def serialize_trajectory(solution: dict | None, limit: int = 28) -> list[dict[str, float]]:
    if not solution or solution.get("trajectory") is None:
        return []
    points = np.asarray(solution["trajectory"], dtype=float)
    if points.ndim != 2 or points.shape[0] == 0 or points.shape[1] < 3:
        return []
    count = min(limit, points.shape[0])
    indices = np.linspace(0, points.shape[0] - 1, count, dtype=int)
    sampled = points[indices, :3]
    return [{"x": float(point[0]), "y": float(point[1]), "z": float(point[2])} for point in sampled]


def serialize_funnel(solution: dict | None, step_s: float = FUNNEL_STEP_S, limit: int = FUNNEL_POINT_LIMIT) -> list[dict[str, float]]:
    """Resample a predicted trajectory into timed points, always keeping the impact.

    The last point is the terrain impact, so the front end can treat the end of the
    drawn line as the landing point without a second message.
    """
    if not solution or solution.get("trajectory") is None:
        return []
    points = np.asarray(solution["trajectory"], dtype=float)
    if points.ndim != 2 or points.shape[0] == 0 or points.shape[1] < 3:
        return []
    stride = max(1, int(round(step_s / PREDICTION_STEP_S)))
    indices = list(range(0, points.shape[0], stride))[:limit]
    if indices[-1] != points.shape[0] - 1:
        indices.append(points.shape[0] - 1)
    return [
        {
            "x": float(points[index, 0]), "y": float(points[index, 1]), "z": float(points[index, 2]),
            "t": float(index * PREDICTION_STEP_S),
        }
        for index in indices
    ]


def yaw_from_quaternion(quaternion: np.ndarray) -> float:
    w, x, y, z = quaternion
    return math.atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (y * y + z * z))


def _axis_quaternion(axis, angle: float) -> np.ndarray:
    unit = np.asarray(axis, dtype=float)
    unit = unit / np.linalg.norm(unit)
    return np.r_[math.cos(angle * 0.5), unit * math.sin(angle * 0.5)]


def _quat_mul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """a then b, in MuJoCo's (w, x, y, z) order."""
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return np.array([
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    ])


class LiveSimulation:
    def __init__(
        self, sim_root: Path, web_root: Path,
        shot_spread_mrad: float = SHOT_SPREAD_MRAD, shot_seed: int | None = None,
    ) -> None:
        self.sim_root = sim_root
        self.web_root = web_root
        # Spread the fired rounds carry, and the generator behind it.  A zero spread
        # draws nothing at all, which is what keeps the regression tests deterministic.
        # Milliradians, so divide by a thousand rather than reaching for math.radians:
        # that would read 45 mrad as 45 degrees and turn the gun into a shotgun.
        self.shot_spread_rad = shot_spread_mrad / 1000.0
        # One switch for the whole spread: with the aim error off the round has to be the
        # deterministic one the regression tests assert, so the speed term goes with it.
        self.shot_speed_sigma = SHOT_SPEED_SIGMA if shot_spread_mrad > 0.0 else 0.0
        self.dispersion_sigma_rad = DISPERSION_NOMINAL_MRAD / 1000.0
        self.shot_rng = random.Random(shot_seed)
        self.scene_path = sim_root / "sim" / "mjcf" / "scene_2026_live.xml"
        self.arena_asset_root = Path(__file__).resolve().parents[1] / "arena" / "rmuc2024"
        self.rmuc2024_layout = json.loads((self.arena_asset_root / "layout.json").read_text(encoding="utf-8"))
        self.active_arena = "training"
        self.map_bounds = dict(MAP_BOUNDS)
        self.ammo = AMMO_42MM
        self.model, self.sight_targets = self._build_model_with_sight_targets()
        self.data = mujoco.MjData(self.model)
        self.lock = threading.RLock()
        self.stop_event = threading.Event()
        self.robot_classes = {robot_id: self._class_for_robot_id(robot_id) for robot_id in (*range(1, 8), *range(21, 28))}
        self.selected_robot_id = 1
        self.selected_robot_class = self.robot_classes[self.selected_robot_id]
        self._bind_robot_model(self.selected_robot_id)
        # The turret hinge is unlimited in the scene, so its servo has to be too.
        # A +-180 deg ctrl range makes a world-stabilised gimbal unwind the long
        # way round every time the chassis crosses the wrap point.
        self.model.actuator_ctrllimited[self.turret_actuator_id] = 0
        self.turret_damping = float(self.model.joint("turret_yaw").damping[0])
        self.turret_servo_gain = float(self.model.actuator_gainprm[self.turret_actuator_id][0])
        self.projectile_body_id = int(self.model.body("projectile_42mm").id)
        self.projectile_joint = self.model.joint("projectile_free")
        self.projectile_qadr = int(self.projectile_joint.qposadr[0])
        self.projectile_vadr = int(self.projectile_joint.dofadr[0])
        self.projectile_geom_id = int(self.model.geom("projectile").id)
        self.operator_camera_id = int(self.model.camera("operator_aim").id)
        self.target_geom_id = int(self.model.geom("target_plate").id)
        self.drive_geom_id = int(self.model.geom("field_2026_drive").id)
        self.rmuc2024_geom_id = int(self.model.geom("field_rmuc2024_collision").id)
        self.drive_geom_ids = {self.drive_geom_id}
        self.training_qpos0 = self.model.qpos0.copy()
        self.training_static_geom_masks = {}
        for geom_id in range(self.model.ngeom):
            if int(self.model.geom_bodyid[geom_id]) != 0 or geom_id == self.rmuc2024_geom_id:
                continue
            contype = int(self.model.geom_contype[geom_id])
            conaffinity = int(self.model.geom_conaffinity[geom_id])
            if contype or conaffinity:
                self.training_static_geom_masks[geom_id] = (contype, conaffinity)
        self.chassis_heading_target = 0.0
        self.control = {"forward": 0.0, "strafe": 0.0, "yaw": 0.0, "turretYaw": 0.0, "pitch": 0.0, "boost": False, "brake": False, "seq": 0, "headingTarget": 0.0}
        self.last_control_at = 0.0
        # RobotMaster is not connected yet; keep a deterministic demo payload for the Unity HUD.
        self.robotmaster = {
            "connected": False,
            "simulated": True,
            "robots": {
                1: {"hp": 258, "maxHp": 260, "ammo": 18, "energy": 72, "economy": 120, "buffs": ["speed"], "firing": False, "hasRecentHit": False, "hitDirectionDeg": 0.0},
                2: {"hp": 214, "maxHp": 260, "ammo": 12, "energy": 55, "economy": 96, "buffs": [], "firing": False, "hasRecentHit": False, "hitDirectionDeg": 0.0},
                3: {"hp": 178, "maxHp": 200, "ammo": 21, "energy": 63, "economy": 84, "buffs": ["armor"], "firing": False, "hasRecentHit": False, "hitDirectionDeg": 0.0},
                4: {"hp": 190, "maxHp": 200, "ammo": 16, "energy": 48, "economy": 72, "buffs": [], "firing": False, "hasRecentHit": False, "hitDirectionDeg": 0.0},
            },
        }
        self.modules = {"autoCalibration": False, "superPower": False, "gyro": False, "autoAim": False, "frictionWheel": False}
        self.auto_calibration = False
        self.modules["autoCalibration"] = False
        self.control_mode = CONTROL_MODE_FOLLOW
        self.manual_pitch = 0.0
        self.manual_turret_yaw = 0.0
        self.solution: dict | None = None
        # Ballistic path of the gun as it is physically pointed, refreshed by the funnel
        # observer: {"points": [{x, y, z, t}, ...], "tofS": float, "impactM": point}.
        self.observed_funnel: dict | None = None
        # Sight aids, refreshed by the ballistics observer: the barrel-line drop ladder
        # and the group box at the reference range.  See ballistics_loop.
        self.observed_ballistics: dict | None = None
        self.flight = {"active": False, "status": "idle", "hit": False, "missM": None, "impactM": None, "firedAt": None, "tofS": None}
        self.flight_robot_id = self.selected_robot_id
        # Attacker bearings around the hero: front, right, rear, left.
        self.enemy_directions = (math.pi, -math.pi / 2.0, 0.0, math.pi / 2.0)
        self.enemy_direction_index = 0
        self.enemy_next_fire = 20.0
        self.enemy_flight = {"active": False, "status": "idle", "hit": False, "positionM": None, "originM": None, "directionDeg": None, "firedAt": None, "impactM": None}
        self.enemy_trajectory: np.ndarray | None = None
        self.enemy_trajectory_index = 0
        self.shared_marker: dict | None = None
        self.robotmaster["robots"][self.selected_robot_id]["firing"] = False
        self.previous_projectile_position: np.ndarray | None = None
        self.events: deque[dict] = deque(maxlen=12)
        self.last_event_key = ""
        self.last_event_at = 0.0
        # The realtime transport publishes this snapshot. Building it on the
        # simulation thread avoids the client thread fighting the simulation
        # lock/GIL for a full state during projectile flight.
        self.state_snapshot: dict | None = None
        self.state_snapshot_lock = threading.Lock()
        self.next_snapshot_at = 0.0
        self.terrain = HeightfieldSampler(self.model, "rmuc2026_drive", MAP_BOUNDS, -0.199878)
        self.training_cloud_tree = load_geometry_tree(sim_root / "sim" / "assets" / "rmuc2026_field.obj")
        self.rmuc2024_cloud_tree = load_geometry_tree(self.arena_asset_root / "rmuc2024_collision.obj")
        self.cloud_tree = self.training_cloud_tree
        self._reset_locked()
        self.add_event("system", "MuJoCo 2026 实时仿真已启动")

    def _build_model_with_sight_targets(self) -> tuple[mujoco.MjModel, list[dict]]:
        """Load the scene and hang the calibration discs on it.

        The discs are added here rather than in the scene XML because that file belongs
        to the simulation project, not to this client, and because they only exist to
        check the sight.  Everything else about the scene is the file's own.

        Poses come from the scene itself: the spawn pose is read back out of the loaded
        model, so the discs follow the hero if the spawn is ever moved.
        """
        # The reference simulator offers seven slots per faction: Hero, Engineer,
        # Infantry x4 and Sentry. Build each as a distinct MuJoCo articulated body;
        # the blue ids use the same class profiles at their own field-side spawns.
        tree = ET.parse(self.scene_path)
        xml_root = tree.getroot()
        worldbody = xml_root.find("worldbody")
        asset_section = xml_root.find("asset")
        if asset_section is None:
            raise ValueError(f"MuJoCo scene has no asset section: {self.scene_path}")
        collision_asset = self.arena_asset_root / "rmuc2024_collision.obj"
        if not collision_asset.is_file():
            raise FileNotFoundError(f"RMUC2024 collision export is missing: {collision_asset}")
        ET.SubElement(asset_section, "mesh", {
            "name": "rmuc2024_collision", "file": "rmuc2024_collision.obj",
        })
        ET.SubElement(worldbody, "geom", {
            "name": "field_rmuc2024_collision", "type": "mesh", "mesh": "rmuc2024_collision",
            "rgba": "0 0 0 0", "contype": "0", "conaffinity": "0", "group": "4",
            "friction": "0.28 0.001 0.0001",
        })
        template = worldbody.find("body[@name='hero']")
        if template is None:
            raise ValueError(f"MuJoCo scene has no hero body: {self.scene_path}")
        # The source scene parks the hero on the raised side at (-12.61, 5.89),
        # immediately behind a tall visual-field barrier. Reserve slot 1 in
        # the same red-side formation as slots 2-7 so the physics, map and FPV
        # all begin at one unobstructed pose rather than moving only the view.
        template.set("pos", "-12.2 -5.0 0.958541")
        actuator_root = xml_root.find("actuator")
        sensor_root = xml_root.find("sensor")
        actuator_templates = list(actuator_root) if actuator_root is not None else []
        sensor_templates = list(sensor_root) if sensor_root is not None else []
        role_profiles = {1: "Hero", 2: "Engineer", 3: "Infantry", 4: "Infantry", 5: "Infantry", 6: "Infantry", 7: "Sentry"}
        # Primary chassis Rigidbody masses read from the four source prefabs.
        class_mass = {"Hero": 200.0, "Engineer": 400.0, "Infantry": 150.0, "Sentry": 300.0}
        chassis_size = {"Hero": (0.38, 0.31, 0.11), "Engineer": (0.48, 0.42, 0.12), "Infantry": (0.34, 0.28, 0.10), "Sentry": (0.42, 0.34, 0.11)}
        for robot_id in (*range(2, 8), *range(21, 28)):
            slot = robot_id - 20 if robot_id >= 20 else robot_id
            faction_index = 1 if robot_id >= 20 else 0
            robot_class = role_profiles[slot]
            prefix = f"robot_{robot_id}_"
            names: dict[str, str] = {}
            body = copy.deepcopy(template)
            for element in body.iter():
                name = element.get("name")
                if name:
                    names[name] = prefix + name
            for element in body.iter():
                for key, value in list(element.attrib.items()):
                    if key == "name" or value in names:
                        element.set(key, names.get(value, value))
            body.set("pos", f"{12.2 if faction_index else -12.2} {-5.0 + (slot - 1) * 2.2:.4f} 0.958541")
            body.set("euler", "0 0 180" if faction_index else "0 0 0")
            inertial = body.find("inertial")
            if inertial is not None:
                inertial.set("mass", str(class_mass[robot_class]))
                inertia_scale = class_mass[robot_class] / 45.0
                old = [float(v) for v in inertial.get("diaginertia", "2 2.4 3.2").split()]
                inertial.set("diaginertia", " ".join(f"{v * inertia_scale:.6g}" for v in old))
            low = body.find("geom[@name='chassis_lower']")
            high = body.find("geom[@name='chassis_upper']")
            if low is not None:
                low.set("size", " ".join(map(str, chassis_size[robot_class])))
            if high is not None:
                sx, sy, sz = chassis_size[robot_class]
                high.set("size", f"{sx * .82:.4f} {sy * .84:.4f} {sz * .72:.4f}")
            worldbody.append(body)
            for actuator in actuator_templates:
                clone = copy.deepcopy(actuator)
                old_name = clone.get("name", "")
                clone.set("name", prefix + old_name)
                joint = clone.get("joint")
                if joint in names:
                    clone.set("joint", names[joint])
                actuator_root.append(clone)
            for sensor in sensor_templates:
                clone = copy.deepcopy(sensor)
                old_name = clone.get("name", "")
                clone.set("name", prefix + old_name)
                for key, value in list(clone.attrib.items()):
                    if value in names:
                        clone.set(key, names[value])
                sensor_root.append(clone)
        # MuJoCo 3.3's MjSpec.from_string accepts include/assets mappings, not a
        # filesystem base path. Supply referenced scene assets explicitly so the
        # in-memory multi-robot XML can be compiled from its original directory.
        asset_root = self.scene_path.parent
        asset_files = {}
        include_files = {}
        for include_node in xml_root.findall(".//include"):
            relative = include_node.get("file")
            if relative:
                include_path = (asset_root / relative).resolve()
                include_files[relative] = include_path.read_bytes()
        for asset_node in xml_root.findall(".//asset/*"):
            for attribute in ("file",):
                relative = asset_node.get(attribute)
                if relative:
                    if relative == "rmuc2024_collision.obj":
                        asset_files[relative] = collision_asset.read_bytes()
                        continue
                    resolved = (asset_root / relative).resolve()
                    if not resolved.is_file():
                        resolved = (self.sim_root / "sim" / "assets" / Path(relative).name).resolve()
                    asset_key = str(resolved.relative_to(asset_root)) if resolved.is_relative_to(asset_root) else relative
                    asset_files[asset_key] = resolved.read_bytes()
        spec = mujoco.MjSpec.from_string(
            ET.tostring(xml_root, encoding="unicode"),
            include=include_files, assets=asset_files,
        )
        # Compile once to measure where the spawn points and how high the muzzle sits;
        # adding bodies in place afterwards recompiles without re-reading the meshes.
        model = spec.compile()
        data = mujoco.MjData(model)
        mujoco.mj_forward(model, data)
        hero = model.body("hero")
        yaw = yaw_from_quaternion(np.asarray(hero.quat, dtype=float))
        forward = np.array([math.cos(yaw), math.sin(yaw), 0.0])
        # Ranges are measured from the muzzle, because that is where the rungs are
        # measured from too: 0.66 m of barrel would otherwise put every disc short of
        # the mark it is supposed to stand on.
        spawn = np.asarray(data.site("muzzle_origin").xpos, dtype=float).copy()
        spawn[2] = 0.0
        height = float(data.site("muzzle_origin").xpos[2])
        # A cylinder's axis is its own Z, so the face is a quarter turn about Y and then
        # the spawn's heading; the post is a capsule, which stands up unaided.
        face = _quat_mul(_axis_quaternion((0.0, 0.0, 1.0), yaw), _axis_quaternion((0.0, 1.0, 0.0), math.pi / 2.0))
        ground_sampler = HeightfieldSampler(model, "rmuc2026_drive", MAP_BOUNDS, -0.199878)
        targets: list[dict] = []
        for index, distance in enumerate(SIGHT_TARGET_RANGES_M):
            centre = spawn + forward * distance
            ground = ground_sampler.height_at(float(centre[0]), float(centre[1]))
            resting = ground + SIGHT_TARGET_RADIUS_M + SIGHT_TARGET_MIN_CLEARANCE_M
            centre[2] = height + SIGHT_TARGET_NEAR_RISE_M if index == 0 else resting
            post_bottom = -0.8   # below the field, so the post reads as planted
            body = spec.worldbody.add_body(
                name=f"sight_target_{distance:g}m", pos=centre.tolist(),
            )
            # The scene's own target material, so a calibration disc reads as a target
            # rather than as scenery.
            body.add_geom(
                name=f"sight_face_{distance:g}m", type=mujoco.mjtGeom.mjGEOM_CYLINDER,
                size=[SIGHT_TARGET_RADIUS_M, 0.012, 0.0], quat=face.tolist(),
                material="target_mat", contype=SIGHT_TARGET_CONTYPE,
                conaffinity=SIGHT_TARGET_CONAFFINITY,
            )
            post_body = spec.worldbody.add_body(
                name=f"sight_post_{distance:g}m",
                pos=[centre[0], centre[1], (centre[2] + post_bottom) * 0.5],
            )
            post_body.add_geom(
                name=f"sight_post_geom_{distance:g}m", type=mujoco.mjtGeom.mjGEOM_CAPSULE,
                size=[0.018, (centre[2] - post_bottom) * 0.5, 0.0], rgba=[0.20, 0.22, 0.24, 1.0],
                contype=0, conaffinity=0,
            )
            # Kept for the hit test: the face is 24 mm thick and the round covers 36 mm
            # per physics step, so a contact alone would let it tunnel straight through.
            targets.append({
                "distanceM": float(distance),
                "centre": centre.copy(),
                "normal": forward.copy(),
                "radius": SIGHT_TARGET_RADIUS_M,
                "hitAt": None,
            })
        return spec.compile(), targets

    def add_event(self, event_type: str, message: str) -> None:
        now = time.time()
        key = f"{event_type}:{message}"
        if key == self.last_event_key and now - self.last_event_at < 1.0:
            return
        self.last_event_key, self.last_event_at = key, now
        self.events.appendleft({"at": now, "type": event_type, "message": message})

    @staticmethod
    def _class_for_robot_id(robot_id: int) -> str:
        slot = robot_id - 20 if robot_id >= 20 else robot_id
        return {1: "Hero", 2: "Engineer", 3: "Infantry", 4: "Infantry",
                5: "Infantry", 6: "Infantry", 7: "Sentry"}.get(slot, "")

    def _bind_robot_model(self, robot_id: int) -> None:
        robot_class = self.robot_classes.get(robot_id)
        if not robot_class:
            raise ValueError(f"unsupported robot id: {robot_id}")
        if getattr(self, "selected_robot_class", robot_class) != robot_class and hasattr(self, "manual_pitch"):
            self.manual_pitch = 0.0
            self.manual_turret_yaw = 0.0
            self.auto_calibration = False
            self.solution = None
        previous_robot_class = getattr(self, "selected_robot_class", None)
        self.selected_robot_id = robot_id
        self.selected_robot_class = robot_class
        # The physical projectile model is shared, but the selected vehicle's
        # loaded round follows its original shooter class.
        if robot_class != "Hero":
            self.ammo = AMMO_17MM
        elif previous_robot_class != "Hero":
            self.ammo = AMMO_42MM
        if hasattr(self, "robotmaster"):
            self.robotmaster["robots"].setdefault(robot_id, {
                "hp": 0, "maxHp": 0, "ammo": 0, "energy": 0, "economy": 0,
                "buffs": [], "firing": False, "hasRecentHit": False,
                "hitDirectionDeg": 0.0,
            })
        prefix = "" if robot_id == 1 else f"robot_{robot_id}_"
        self.hero_body_id = int(self.model.body(prefix + "hero").id)
        self.hero_x_joint = self.model.joint(prefix + "hero_x")
        self.hero_y_joint = self.model.joint(prefix + "hero_y")
        self.hero_z_joint = self.model.joint(prefix + "hero_z")
        self.hero_yaw_joint = self.model.joint(prefix + "hero_yaw")
        self.hero_qadrs = np.array([
            int(self.hero_x_joint.qposadr[0]), int(self.hero_y_joint.qposadr[0]),
            int(self.hero_z_joint.qposadr[0]), int(self.hero_yaw_joint.qposadr[0])
        ])
        self.hero_linear_vadrs = np.array([
            int(self.hero_x_joint.dofadr[0]), int(self.hero_y_joint.dofadr[0]), int(self.hero_z_joint.dofadr[0])
        ])
        self.hero_yaw_vadr = int(self.hero_yaw_joint.dofadr[0])
        self.turret_joint = self.model.joint(prefix + "turret_yaw")
        self.barrel_joint = self.model.joint(prefix + "barrel_pitch")
        self.turret_qadr = int(self.turret_joint.qposadr[0])
        self.barrel_qadr = int(self.barrel_joint.qposadr[0])
        self.turret_actuator_id = int(self.model.actuator(prefix + "turret_yaw_servo").id)
        self.barrel_actuator_id = int(self.model.actuator(prefix + "barrel_pitch_servo").id)
        self.operator_camera_id = int(self.model.camera(prefix + "operator_aim").id)
        self.muzzle_site_name = prefix + "muzzle_origin"
        self.hero_center_site_name = prefix + "hero_center"
        self.lidar_site_name = prefix + "lidar_origin"
        self.projectile_spawn_site_name = self.muzzle_site_name
        self.wheel_geom_id = int(self.model.geom(prefix + "hero_collision_wheels").id)
        self.model.actuator_ctrllimited[self.turret_actuator_id] = 0
        self.turret_damping = float(self.turret_joint.damping[0])
        self.turret_servo_gain = float(self.model.actuator_gainprm[self.turret_actuator_id][0])

    def select_robot(self, payload: dict) -> dict:
        robot_id = int(payload.get("robotId", 0))
        robot_class = str(payload.get("robotClass", ""))
        expected = self._class_for_robot_id(robot_id)
        if not expected:
            return {"ok": False, "error": f"unsupported robot id: {robot_id}"}
        if robot_class.casefold() != expected.casefold():
            return {"ok": False, "error": f"robot {robot_id} is {expected}, not {robot_class}"}
        with self.lock:
            self._bind_robot_model(robot_id)
            self.manual_turret_yaw = yaw_from_quaternion(np.asarray(self.data.xquat[self.hero_body_id], dtype=float))
            self.manual_pitch = float(self.data.qpos[self.barrel_qadr])
            self.control = {"forward": 0.0, "strafe": 0.0, "yaw": 0.0, "turretYaw": 0.0, "pitch": 0.0,
                            "boost": False, "brake": True, "seq": 0, "headingTarget": None, "robotId": robot_id}
            if robot_id < 20:
                self.model.geom_rgba[self.wheel_geom_id][0:3] = (0.78, 0.04, 0.035)
            else:
                self.model.geom_rgba[self.wheel_geom_id][0:3] = (0.035, 0.12, 0.78)
            self.solution = None
            self.observed_funnel = None
            self.auto_calibration = False
            self.last_control_at = time.monotonic()
        self.add_event("robot", f"MuJoCo 模型已切换：{robot_class} / {robot_id}")
        return {"ok": True, "robotId": robot_id, "robotClass": robot_class}

    def _reset_locked(self) -> None:
        mujoco.mj_resetData(self.model, self.data)
        self.data.qpos[self.projectile_qadr:self.projectile_qadr + 3] = (0.0, 0.0, -5.0)
        self.data.qpos[self.projectile_qadr + 3:self.projectile_qadr + 7] = (1.0, 0.0, 0.0, 0.0)
        self.data.qvel[:] = 0.0
        self.data.xfrc_applied[:] = 0.0
        mujoco.mj_forward(self.model, self.data)
        self.chassis_heading_target = yaw_from_quaternion(np.asarray(self.data.xquat[self.hero_body_id], dtype=float))
        self.solution = None
        self.observed_funnel = None
        self.observed_ballistics = None
        self.auto_calibration = False
        self.manual_pitch = 0.0
        # Manual turret target is stored in world space.  The actuator still receives
        # a chassis-relative angle below, which makes chassis rotation leave the turret
        # pointing at the same world direction.
        self.manual_turret_yaw = self.chassis_heading_target
        self.data.ctrl[self.turret_actuator_id] = 0.0
        self.data.ctrl[self.barrel_actuator_id] = self.manual_pitch
        self.flight = {"active": False, "status": "idle", "hit": False, "missM": None, "impactM": None, "firedAt": None, "tofS": None}
        self.flight_robot_id = self.selected_robot_id
        self.previous_projectile_position = None
        self.last_control_at = 0.0
        self.enemy_direction_index = 0
        self.enemy_next_fire = 20.0
        self.enemy_flight = {"active": False, "status": "idle", "hit": False, "positionM": None, "originM": None, "directionDeg": None, "firedAt": None, "impactM": None}
        self.enemy_trajectory = None
        self.enemy_trajectory_index = 0
        self.shared_marker = None
        for robot in self.robotmaster["robots"].values():
            robot["hasRecentHit"] = False
            robot["hitDirectionDeg"] = 0.0
            # fire() sets this and only the terminal branches of _update_flight_locked
            # clear it. A reset mid-flight reaches neither, so it would latch true forever
            # and the HUD would show the robot firing for the rest of the session.
            robot["firing"] = False

    def reset(self) -> dict:
        with self.lock:
            self._reset_locked()
            self.add_event("reset", "英雄已恢复至 2026 初始位姿")
        return {"ok": True}

    def set_ammo(self, payload: dict) -> dict:
        """Load the other round.

        Everything the sight draws is solved from the loaded round, so the ladder and the
        funnel follow the switch on their own -- they are invalidated here and rebuilt by
        their own loops.  The calibration discs stay where they are: their height comes
        from the muzzle and the ground, not from the drop, so they still mark the ranges
        they were put at; only the rungs used to hit them change.
        """
        wanted = str(payload.get("ammo", "")).strip()
        chosen = next((round_ for round_ in AMMO_TYPES if round_.name == wanted), None)
        if chosen is None:
            return {"ok": False, "reason": f"未知弹种：{wanted}"}
        with self.lock:
            if self.selected_robot_class != "Hero" and chosen.name != "17mm":
                return {"ok": False, "reason": "This vehicle has no 42mm shooter"}
            if chosen.name == self.ammo.name:
                return {"ok": True, "ammo": self.ammo.name}
            self.ammo = chosen
            self.solution = None
            self.observed_funnel = None
            self.observed_ballistics = None
            self.manual_pitch = float(self.data.qpos[self.barrel_qadr])
            self.manual_turret_yaw = yaw_from_quaternion(
                np.asarray(self.data.xquat[self.hero_body_id], dtype=float)
            ) + float(self.data.qpos[self.turret_qadr])
            self.add_event("ammo", f"已切换为 {chosen.label}，初速 {chosen.speed:g} m/s")
        return {"ok": True, "ammo": chosen.name}

    def set_marker(self, payload: dict) -> dict:
        try:
            point = [float(payload.get("x", payload.get("markerX"))), float(payload.get("y", payload.get("markerY"))), float(payload.get("z", payload.get("markerZ", 0.0)))]
        except (KeyError, TypeError, ValueError):
            return {"ok": False, "reason": "标点坐标无效"}
        with self.lock:
            self.shared_marker = {"positionM": point, "active": True, "setAt": time.time()}
            self.add_event("marker", f"队友标记位置 ({point[0]:.1f}, {point[1]:.1f})")
        return {"ok": True}

    def update_control(self, payload: dict) -> dict:
        robot_id = int(payload.get("robotId", 0))
        if robot_id != self.selected_robot_id:
            return {"ok": False, "error": f"selected MuJoCo robot is {self.selected_robot_id}; rejected control for {robot_id}"}
        with self.lock:
            self.control = {
                "forward": clamp(float(payload.get("forward", 0.0)), -1.0, 1.0),
                "strafe": clamp(float(payload.get("strafe", 0.0)), -1.0, 1.0),
                "yaw": clamp(float(payload.get("yaw", 0.0)), -1.0, 1.0),
                "turretYaw": clamp(float(payload.get("turretYaw", 0.0)), -1.0, 1.0),
                "pitch": clamp(float(payload.get("pitch", 0.0)), -1.0, 1.0),
                "boost": bool(payload.get("boost", False)),
                "brake": bool(payload.get("brake", False)),
                "seq": int(payload.get("seq", 0)),
                "headingTarget": None,
                "robotId": robot_id,
            }
            self.last_control_at = time.monotonic()
        return {"ok": True, "seq": self.control["seq"]}

    def set_calibration(self, payload: dict) -> dict:
        enabled = bool(payload.get("enabled", False))
        with self.lock:
            self.auto_calibration = enabled
            self.modules["autoCalibration"] = enabled
            self.solution = None
            if not enabled:
                self.manual_pitch = float(self.data.qpos[self.barrel_qadr])
                _speed, _angular, chassis_yaw, _qpos, _qvel = self._motion()
                self.manual_turret_yaw = math.atan2(
                    math.sin(chassis_yaw + float(self.data.qpos[self.turret_qadr])),
                    math.cos(chassis_yaw + float(self.data.qpos[self.turret_qadr])),
                )
                self.data.ctrl[self.turret_actuator_id] = math.atan2(
                    math.sin(self.manual_turret_yaw - chassis_yaw),
                    math.cos(self.manual_turret_yaw - chassis_yaw),
                )
                self.data.ctrl[self.barrel_actuator_id] = self.manual_pitch
            if not self.flight["active"]:
                self.flight.update(status="idle", hit=False, missM=None, impactM=None, firedAt=None, tofS=None)
            self.add_event("calibration", "自动吊射校准已开启" if enabled else "自动吊射校准已关闭")
        return {"ok": True, "enabled": enabled}

    def set_modules(self, payload: dict) -> dict:
        allowed = set(self.modules)
        with self.lock:
            for key, value in payload.items():
                if key in allowed:
                    self.modules[key] = bool(value)
            self.auto_calibration = self.modules["autoCalibration"]
            self.solution = None
        return {"ok": True, "modules": dict(self.modules)}

    def set_control_mode(self, payload: dict) -> dict:
        mode = str(payload.get("mode", ""))
        if mode not in CONTROL_MODES:
            return {"ok": False, "error": f"unsupported control mode: {mode}"}
        with self.lock:
            self.control_mode = mode
            self.manual_turret_yaw = math.atan2(
                math.sin(self.manual_turret_yaw), math.cos(self.manual_turret_yaw)
            )
            self.add_event(
                "mode",
                "小陀螺模式：C 逆时针自旋 / V 顺时针自旋" if mode == CONTROL_MODE_GYRO
                else "自动跟随模式：底盘跟随云台转动",
            )
        return {"ok": True, "mode": mode}

    def _motion(self) -> tuple[float, float, float, np.ndarray, np.ndarray]:
        position = np.asarray(self.data.xpos[self.hero_body_id], dtype=float).copy()
        quaternion = np.asarray(self.data.xquat[self.hero_body_id], dtype=float).copy()
        linear_velocity = np.asarray(self.data.qvel[self.hero_linear_vadrs], dtype=float)
        yaw_velocity = float(self.data.qvel[self.hero_yaw_vadr])
        qpos = np.r_[position, quaternion]
        qvel = np.array([linear_velocity[0], linear_velocity[1], linear_velocity[2], 0.0, 0.0, yaw_velocity])
        yaw = yaw_from_quaternion(quaternion)
        return float(np.linalg.norm(qvel[:2])), abs(float(qvel[5])), yaw, qpos, qvel

    def _fire_gate_locked(self) -> tuple[bool, str]:
        if self.selected_robot_class == "Engineer":
            return False, "Engineer has no shooter"
        if self.flight["active"]:
            return False, "弹丸仍在飞行"
        # Ordinary manual fire follows the actual muzzle pose. A computed
        # ballistic solution is only a prerequisite in auto-calibration mode;
        # requiring a stationary chassis and a fresh solved arc for every
        # manual click made the original simulator's moving fire impossible.
        if not self.auto_calibration:
            return True, "ready"
        linear_speed, angular_speed, _yaw, _qpos, _qvel = self._motion()
        if linear_speed >= 0.15:
            return False, "底盘尚未停车"
        # 小陀螺 spins on purpose, and that is not a reason to hold fire: the gimbal holds
        # its world heading, so the aim is unaffected by the chassis turning underneath.
        # Only the launch point orbits, and fire() spawns from the solution's own origin
        # rather than the live muzzle, so the shot still follows the solved arc.
        spinning = self.control_mode == CONTROL_MODE_GYRO
        if not spinning and math.degrees(angular_speed) >= 5.0:
            return False, "底盘仍在旋转"
        solution = self.solution
        if solution is None:
            return False, "当前位置无低弧解"
        if time.monotonic() - solution["solved_at"] > 0.5:
            return False, "弹道解正在更新"
        muzzle = np.asarray(self.data.site(self.muzzle_site_name).xpos, dtype=float)
        if not spinning and np.linalg.norm(muzzle - solution["origin"]) > 0.05:
            return False, "位姿变化后弹道解已过期"
        _linear_speed, _angular_speed, chassis_yaw, _qpos, _qvel = self._motion()
        actual_yaw = chassis_yaw + float(self.data.qpos[self.turret_qadr])
        actual_pitch = float(self.data.qpos[self.barrel_qadr])
        if solution["mode"] == "auto":
            desired_turret = math.atan2(math.sin(solution["yaw"] - chassis_yaw), math.cos(solution["yaw"] - chassis_yaw))
            turret_error = math.atan2(
                math.sin(desired_turret - float(self.data.qpos[self.turret_qadr])),
                math.cos(desired_turret - float(self.data.qpos[self.turret_qadr])),
            )
            if abs(math.degrees(turret_error)) > 2.0 or abs(math.degrees(solution["pitch"] - actual_pitch)) > 1.0:
                return False, "云台与枪管尚未对准"
            if not solution["clear"]:
                return False, "预测轨迹未通过净空检查"
        else:
            yaw_error = math.atan2(math.sin(solution["yaw"] - actual_yaw), math.cos(solution["yaw"] - actual_yaw))
            if abs(math.degrees(yaw_error)) > 1.0 or abs(math.degrees(solution["pitch"] - actual_pitch)) > 1.0:
                return False, "手动角度变化后落点更新中"
        return True, "可以发射"

    def fire(self) -> dict:
        with self.lock:
            ready, reason = self._fire_gate_locked()
            if not ready:
                self.add_event("reject", reason)
                return {"ok": False, "reason": reason}
            solution = self.solution
            if not self.auto_calibration:
                _speed, _angular_speed, chassis_yaw, _qpos, _qvel = self._motion()
                solution = {
                    "origin": np.asarray(self.data.site(self.muzzle_site_name).xpos, dtype=float).copy(),
                    "yaw": chassis_yaw + float(self.data.qpos[self.turret_qadr]),
                    "pitch": float(self.data.qpos[self.barrel_qadr]),
                    "mode": "manual",
                }
            assert solution is not None
            # The scene has one reusable free-body projectile. Match its
            # dynamics and contact radius to the selected 17/42 mm round,
            # instead of only changing the launch-speed label in the HUD.
            self.model.body_mass[self.projectile_body_id] = self.ammo.mass
            inertia = 0.4 * self.ammo.mass * self.ammo.radius ** 2
            self.model.body_inertia[self.projectile_body_id] = (inertia, inertia, inertia)
            self.model.geom_size[self.projectile_geom_id][0] = self.ammo.radius
            self.data.qpos[self.projectile_qadr:self.projectile_qadr + 3] = solution["origin"]
            self.data.qpos[self.projectile_qadr + 3:self.projectile_qadr + 7] = (1.0, 0.0, 0.0, 0.0)
            # The round leaves with its own error, not exactly along the solve: that is
            # what makes the sight's group box a measurement instead of a decoration.
            self.data.qvel[self.projectile_vadr:self.projectile_vadr + 3] = perturbed_velocity(
                solution["yaw"], solution["pitch"], self.ammo.speed,
                self.shot_spread_rad, self.shot_speed_sigma, self.shot_rng,
            )
            self.data.qvel[self.projectile_vadr + 3:self.projectile_vadr + 6] = 0.0
            mujoco.mj_forward(self.model, self.data)
            self.shot_sequence = getattr(self, "shot_sequence", 0) + 1
            self.flight = {"active": True, "status": "flying", "hit": False, "missM": None,
                           "impactM": None, "firedAt": float(self.data.time), "tofS": None,
                           "shotId": self.shot_sequence, "shooterId": self.selected_robot_id,
                           "originM": solution["origin"].tolist(),
                           "roundName": self.ammo.name}
            self.flight_robot_id = self.selected_robot_id
            self.robotmaster["robots"][self.flight_robot_id]["firing"] = True
            self.robotmaster["robots"][self.flight_robot_id]["ammo"] = max(
                0, self.robotmaster["robots"][self.flight_robot_id]["ammo"] - 1)
            self.previous_projectile_position = solution["origin"].copy()
            self.add_event("fire", f"{('自动' if solution['mode'] == 'auto' else '手动')}弹道已发射，俯仰 {math.degrees(solution['pitch']):.2f}°")
            return {"ok": True, "shotId": self.shot_sequence}

    def _command_turret_locked(self, world_yaw: float) -> None:
        """Aim the gimbal at a world heading, keeping the servo error short-way.

        The turret hinge is unlimited, so the command has to stay continuous with
        the joint's current angle: a plain atan2 wrap would make the servo unwind
        a full turn the wrong way each time the chassis crosses +-180 deg.
        """
        chassis_yaw = yaw_from_quaternion(np.asarray(self.data.xquat[self.hero_body_id], dtype=float))
        current = float(self.data.qpos[self.turret_qadr])
        desired = world_yaw - chassis_yaw
        # Velocity feed-forward. Holding a world heading while the chassis turns drags
        # the hinge against its own damping, and the servo would carry a steady-state
        # error of damping * rate / kp (~9 deg at full gyro spin) without this term.
        if self.turret_servo_gain > 0.0:
            desired -= self.turret_damping / self.turret_servo_gain * float(self.data.qvel[self.hero_yaw_vadr])
        self.data.ctrl[self.turret_actuator_id] = current + math.atan2(
            math.sin(desired - current), math.cos(desired - current)
        )

    def _chassis_command_locked(self, command: dict) -> dict:
        """The command the chassis actually gets, after the 自动跟随 override.

        Shared with the HUD steer prediction: the drawn path has to be a roll-out of the
        command the physics applies, not of the raw packet, or it lies in 跟随 mode.
        """
        if self.control_mode == CONTROL_MODE_FOLLOW:
            # 自动跟随: chase the gimbal heading rather than take a spin rate, so the
            # turret marker leads and the chassis marker trails behind it. The spin rate
            # is dropped here whatever else is on -- C/V belong to 小陀螺 alone, and a
            # stray rate would shove the chassis while it is meant to be following.
            return {**command, "yaw": 0.0, "headingTarget": self.manual_turret_yaw}
        return command

    def _apply_control_locked(self) -> None:
        now = time.monotonic()
        command = self.control if now - self.last_control_at <= CONTROL_TIMEOUT_S else {
            "forward": 0.0, "strafe": 0.0, "yaw": 0.0, "turretYaw": 0.0,
            "pitch": 0.0, "boost": False, "brake": True, "headingTarget": None,
        }
        solution = self.solution
        if not self.auto_calibration:
            # Manual aim integrates in world space in both modes: the gimbal never
            # inherits chassis rotation, which is what leaves the turret marker
            # standing still while 小陀螺 spins the chassis underneath it.
            turret_target = self.manual_turret_yaw + float(command["turretYaw"]) * math.radians(90.0) * float(self.model.opt.timestep)
            self.manual_turret_yaw = math.atan2(math.sin(turret_target), math.cos(turret_target))
            self.manual_pitch = clamp(
                self.manual_pitch + float(command["pitch"]) * math.radians(30.0) * float(self.model.opt.timestep),
                math.radians(-10.0), math.radians(45.0),
            )
        elif solution is not None:
            # Keep the manual hold target continuous for when auto mode is disabled.
            self.manual_turret_yaw = solution["yaw"]
            self.manual_pitch = clamp(solution["pitch"], math.radians(-10.0), math.radians(70.0))

        linear_speed, _angular_speed, yaw, qpos, qvel = self._motion()
        command = self._chassis_command_locked(command)
        apply_chassis_control(
            self.model, self.data, self.hero_body_id, command, yaw, qpos, qvel, MAP_BOUNDS,
        )

        self.data.ctrl[self.barrel_actuator_id] = self.manual_pitch
        self._command_turret_locked(self.manual_turret_yaw)
        if linear_speed > 0.1 and any(abs(float(command[key])) > 0.05 for key in ("forward", "strafe")):
            self.add_event("motion", "英雄底盘正在响应键盘控制")

    def _steer_trajectory_locked(self, yaw: float, qpos: np.ndarray, qvel: np.ndarray) -> dict:
        """Chassis path the current command is heading into, for the HUD steer overlay.

        A/D lateral input opens the gate, either alone or combined with W/S. The chassis
        is omnidirectional, so Unity draws the resulting translation vector rather than
        car-like steering rails. Pure forward/reverse intentionally stays hidden.
        """
        idle = {"active": False, "points": [], "yawDeg": []}
        # 小陀螺 spins the chassis on C/V, where a strafe path says nothing useful.
        if self.control_mode == CONTROL_MODE_GYRO:
            return idle
        if abs(float(self.control["strafe"])) <= STEER_STRAFE_THRESHOLD:
            return idle
        if time.monotonic() - self.last_control_at > CONTROL_TIMEOUT_S:
            return idle
        points = predict_chassis_trajectory(
            self.model, self.data, self.hero_body_id, self._chassis_command_locked(self.control),
            yaw, qpos, qvel,
            MAP_BOUNDS, self.hero_linear_vadrs, self.hero_yaw_vadr, STEER_HORIZON_S,
        )
        points = decimate_chassis_trajectory(points, STEER_POINT_LIMIT)
        if points.shape[0] < 2:
            return idle
        return {
            "active": True,
            "points": [{"x": float(row[0]), "y": float(row[1]), "z": float(row[2])} for row in points],
            "yawDeg": [math.degrees(float(row[3])) for row in points],
        }

    def _apply_projectile_drag_locked(self) -> None:
        if not self.flight["active"]:
            self.data.xfrc_applied[self.projectile_body_id, :3] = 0.0
            return
        velocity = np.asarray(self.data.qvel[self.projectile_vadr:self.projectile_vadr + 3], dtype=float)
        self.data.xfrc_applied[self.projectile_body_id, :3] = \
            -self.ammo.mass * self.ammo.drag_k * np.linalg.norm(velocity) * velocity

    def _update_flight_locked(self) -> None:
        if not self.flight["active"]:
            return
        position = np.asarray(self.data.xpos[self.projectile_body_id], dtype=float).copy()
        previous_position = self.previous_projectile_position
        for target in self.sight_targets:
            if previous_position is None:
                break
            before = float(np.dot(previous_position - target["centre"], target["normal"]))
            after = float(np.dot(position - target["centre"], target["normal"]))
            if before * after > 0.0 or abs(before - after) <= 1e-10:
                continue
            crossing = previous_position + before / (before - after) * (position - previous_position)
            miss = float(np.linalg.norm(crossing - target["centre"]))
            if miss > target["radius"] + self.ammo.radius:
                continue
            self.flight.update(active=False, status="hit", hit=True, missM=miss,
                               impactM=crossing.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
            self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
            target["hitAt"] = time.monotonic()
            self.add_event("hit", f"弹丸命中 {target['distanceM']:g} m 校准靶，偏差 {miss * 1000:.0f} mm")
            return
        for contact in self.data.contact:
            if {int(contact.geom1), int(contact.geom2)} == {self.projectile_geom_id, self.target_geom_id}:
                self.flight.update(active=False, status="hit", hit=True, impactM=position.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
                self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
                self.add_event("hit", "弹丸命中 2026 权威靶面")
                return
        for contact in self.data.contact:
            if {int(contact.geom1), int(contact.geom2)} == {self.projectile_geom_id, self.drive_geom_id}:
                self.flight.update(active=False, status="landed", hit=False, impactM=position.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
                self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
                self.add_event("landed", "弹丸已落地")
                return
        previous = self.previous_projectile_position
        if previous is not None:
            old_signed = float(np.dot(previous - TARGET, TARGET_NORMAL))
            new_signed = float(np.dot(position - TARGET, TARGET_NORMAL))
            if old_signed * new_signed <= 0 and abs(old_signed - new_signed) > 1e-10:
                alpha = old_signed / (old_signed - new_signed)
                crossing = previous + alpha * (position - previous)
                delta = crossing - TARGET
                width_error = float(np.dot(delta, TARGET_WIDTH_AXIS))
                height_error = float(np.dot(delta, TARGET_HEIGHT_AXIS))
                miss = math.hypot(width_error, height_error)
                if abs(width_error) <= TARGET_HALF_WIDTH + self.ammo.radius and abs(height_error) <= TARGET_HALF_HEIGHT + self.ammo.radius:
                    self.flight.update(active=False, status="hit", hit=True, missM=miss, impactM=crossing.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
                    self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
                    self.add_event("hit", f"弹丸穿越靶面，偏差 {miss * 1000:.1f} mm")
                    return
                self.flight.update(active=False, status="miss", hit=False, missM=miss, impactM=crossing.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
                self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
                self.add_event("miss", f"弹丸越过靶面，偏差 {miss * 1000:.1f} mm")
                return
        self.previous_projectile_position = position
        if position[2] < -0.5 or self.data.time - self.flight["firedAt"] > 5.0:
            self.flight.update(active=False, status="miss", hit=False, impactM=position.tolist(), tofS=float(self.data.time - self.flight["firedAt"]))
            self.robotmaster["robots"][self.flight_robot_id]["firing"] = False
            self.add_event("miss", "弹丸未到达靶面")

    def _start_enemy_attack_locked(self) -> None:
        hero = np.asarray(self.data.xpos[self.hero_body_id], dtype=float).copy()
        angle = self.enemy_directions[self.enemy_direction_index % len(self.enemy_directions)]
        self.enemy_direction_index += 1
        direction = np.array([math.cos(angle), math.sin(angle), 0.0])
        origin = hero - direction * 8.0 + np.array([0.0, 0.0, 1.45])
        target = hero + np.array([0.0, 0.0, 0.82])
        solution = solve_low_arc(
            origin, target, self.cloud_tree, self.ammo.speed, self.ammo.drag_k, G, self.ammo.radius, 0.06,
        )
        if solution is None:
            # Preserve a visible, guaranteed-to-impact demo shot if terrain
            # clearance rejects every physical low-arc bracket.
            points = []
            for fraction in np.linspace(0.0, 1.0, 180):
                point = origin * (1.0 - fraction) + target * fraction
                point[2] += 1.0 * math.sin(math.pi * fraction)
                points.append(point)
            trajectory = np.asarray(points)
        else:
            trajectory = np.asarray(solution["trajectory"], dtype=float)
        # HUD direction is the bearing from our armor toward the attacker.
        # The trajectory vector points attacker -> hero, so reverse it here.
        direction_deg = (math.degrees(angle + math.pi) + 360.0) % 360.0
        self.enemy_trajectory = trajectory
        self.enemy_trajectory_index = 0
        self.enemy_flight = {"active": True, "status": "flying", "hit": False, "positionM": origin.tolist(), "originM": origin.tolist(), "directionDeg": direction_deg, "firedAt": float(self.data.time), "impactM": None}
        self.add_event("enemy_fire", f"敌方从 {direction_deg:.0f}° 方位发射来袭弹")

    def _update_enemy_attack_locked(self) -> None:
        for robot in self.robotmaster["robots"].values():
            if self.data.time - float(robot.get("lastHitAt", -1e9)) > 0.8:
                robot["hasRecentHit"] = False
        if not self.enemy_flight["active"]:
            if self.data.time >= self.enemy_next_fire:
                self._start_enemy_attack_locked()
                self.enemy_next_fire = self.data.time + 4.0
            return
        assert self.enemy_trajectory is not None
        self.enemy_trajectory_index += max(1, int(round(float(self.model.opt.timestep) / 0.002)))
        if self.enemy_trajectory_index < len(self.enemy_trajectory):
            self.enemy_flight["positionM"] = self.enemy_trajectory[self.enemy_trajectory_index].tolist()
            return
        impact = self.enemy_trajectory[-1].copy()
        hero = np.asarray(self.data.xpos[self.hero_body_id], dtype=float)
        hit = float(np.linalg.norm(impact[:2] - hero[:2])) < 0.6 and abs(float(impact[2] - (hero[2] + 0.82))) < 0.6
        self.enemy_flight.update(active=False, status="hit" if hit else "miss", hit=hit, positionM=impact.tolist(), impactM=impact.tolist())
        if hit:
            robot = self.robotmaster["robots"][self.selected_robot_id]
            robot["hp"] = max(0, int(robot["hp"]) - 20)
            robot["hasRecentHit"] = True
            robot["hitDirectionDeg"] = float(self.enemy_flight["directionDeg"])
            robot["lastHitAt"] = float(self.data.time)
            self.add_event("armor_hit", f"装甲板受击：{self.enemy_flight['directionDeg']:.0f}°，生命值 -20")
        else:
            self.add_event("enemy_miss", "来袭弹未命中装甲板")

    def simulation_loop(self) -> None:
        timestep = float(self.model.opt.timestep)
        next_step = time.perf_counter()
        while not self.stop_event.is_set():
            now = time.perf_counter()
            steps = 0
            while now >= next_step and steps < 10:
                with self.lock:
                    self._apply_control_locked()
                    self._apply_projectile_drag_locked()
                    mujoco.mj_step(self.model, self.data)
                    if not all(np.isfinite(values).all() for values in (self.data.qpos, self.data.qvel, self.data.qacc)):
                        self.add_event("stability", "MuJoCo state became non-finite; simulation reset safely")
                        self._reset_locked()
                        next_step = now + timestep
                        break
                    self._update_flight_locked()
                    self._update_enemy_attack_locked()
                next_step += timestep
                steps += 1
                # Yield after every step. A burst of catch-up steps otherwise
                # holds the GIL long enough to starve the realtime publisher.
                time.sleep(0)
            if now - next_step > 0.1:
                next_step = now
            snapshot_at = time.perf_counter()
            if snapshot_at >= self.next_snapshot_at:
                self.next_snapshot_at = snapshot_at + 1.0 / 30.0
                snapshot = self.state()
                with self.state_snapshot_lock:
                    self.state_snapshot = snapshot
            time.sleep(max(0.0, min(0.001, next_step - time.perf_counter())))

    def solver_loop(self) -> None:
        last_origin: np.ndarray | None = None
        last_angles: tuple[float, float] | None = None
        last_mode: str | None = None
        while not self.stop_event.is_set():
            with self.lock:
                enabled = self.auto_calibration
                origin = np.asarray(self.data.site(self.muzzle_site_name).xpos, dtype=float).copy()
                _speed, _angular, chassis_yaw, _qpos, _qvel = self._motion()
                actual_yaw = chassis_yaw + float(self.data.qpos[self.turret_qadr])
                actual_pitch = float(self.data.qpos[self.barrel_qadr])
                cached = self.solution
            mode = "auto" if enabled else "manual"
            angles = (actual_yaw, actual_pitch)
            angle_stable = last_angles is not None and max(
                abs(math.atan2(math.sin(angles[0] - last_angles[0]), math.cos(angles[0] - last_angles[0]))),
                abs(angles[1] - last_angles[1]),
            ) < math.radians(0.5)
            if cached is not None and cached["mode"] == mode and last_mode == mode and last_origin is not None and np.linalg.norm(origin - last_origin) < 0.02 and (enabled or angle_stable):
                with self.lock:
                    cached["solved_at"] = time.monotonic()
                self.stop_event.wait(0.12 if not enabled else 0.25)
                continue
            try:
                solution = solve_low_arc(
                    origin, TARGET, self.cloud_tree, self.ammo.speed, self.ammo.drag_k, G, self.ammo.radius, 0.060,
                ) if enabled else predict_manual_shot(
                    origin, actual_yaw, actual_pitch, self.terrain,
                    self.ammo.speed, self.ammo.drag_k, G, self.ammo.radius,
                )
            except Exception as error:
                solution = None
                self.add_event("solver", f"弹道解算失败：{error}")
            with self.lock:
                self.solution = solution if self.auto_calibration == enabled else None
            last_origin = origin
            last_angles = angles
            last_mode = mode
            self.stop_event.wait(0.12 if not enabled else 0.25)

    def funnel_observer_loop(self) -> None:
        """Publish the ballistic path of the gun as it is actually pointed.

        The only inputs are the muzzle site's world pose and the gimbal's own yaw and
        pitch, so the line says where this robot's next round would go and nothing about
        any target. It deliberately ignores the auto calibration switch: the drawn funnel
        always tracks the physical barrel, whether or not a target solution is running.
        """
        last_origin: np.ndarray | None = None
        last_angles: tuple[float, float] | None = None
        while not self.stop_event.is_set():
            with self.lock:
                origin = np.asarray(self.data.site(self.muzzle_site_name).xpos, dtype=float).copy()
                _speed, _angular, chassis_yaw, _qpos, _qvel = self._motion()
                yaw = chassis_yaw + float(self.data.qpos[self.turret_qadr])
                pitch = float(self.data.qpos[self.barrel_qadr])
                funnel_missing = self.observed_funnel is None
            angles = (yaw, pitch)
            changed = funnel_missing or last_origin is None or np.linalg.norm(origin - last_origin) >= 0.01 or last_angles is None or max(
                abs(math.atan2(math.sin(yaw - last_angles[0]), math.cos(yaw - last_angles[0]))),
                abs(pitch - last_angles[1]),
            ) >= math.radians(0.2)
            if changed:
                try:
                    prediction = predict_manual_shot(
                        origin, yaw, pitch, self.terrain,
                        self.ammo.speed, self.ammo.drag_k, G, self.ammo.radius,
                    )
                    points = serialize_funnel(prediction)
                    with self.lock:
                        self.observed_funnel = {
                            "points": points,
                            "tofS": prediction["tof"] if points else None,
                            "impactM": points[-1] if points else None,
                        }
                    last_origin, last_angles = origin, angles
                except Exception as error:
                    self.add_event("observer", f"弹道预测失败：{error}")
            self.stop_event.wait(0.1)

    def refresh_ballistics(self) -> None:
        """Solve the two sight aids once and publish them.

        Both are built from the same constants the fired round is given, so the ladder
        says where a round crosses each range and the box says how tightly rounds group
        there.  Neither needs the target, the terrain, the chassis or even the gimbal:
        they are solved in the muzzle's own frame at a level barrel, so they come out
        as constants and the rungs cannot crawl as the gun is elevated.  A scope's
        reticle is etched for the same reason -- the shooter moves the rifle, not the
        ruler.
        """
        # Barrel at zero elevation, pointing along +X, with +Z up: the same frame the
        # fired round is described in, minus the pose.
        forward = np.array([1.0, 0.0, 0.0])
        up = np.array([0.0, 0.0, 1.0])
        try:
            # Where the sight sits relative to the muzzle, read from the model rather
            # than copied in: the camera is not on the bore, and this offset is the whole
            # reason a mark is drawn below the round's true angle.
            offset = np.asarray(self.model.camera("operator_aim").pos, dtype=float) - \
                np.asarray(self.model.site("muzzle_origin").pos, dtype=float)
            sight_origin = offset
            marks = drop_table(
                RANGE_SCALE_MARKS_M, self.ammo.speed, self.ammo.drag_k, G, REFERENCE_PITCH_RAD,
                origin=(0.0, 0.0, 0.0),
            )
            for mark in marks:
                point = np.array([mark["x"], mark["y"], mark["z"]], dtype=float)
                view = point - sight_origin
                mark["sightDeg"] = math.degrees(math.atan2(
                    -float(np.dot(view, up)), float(np.dot(view, forward)),
                ))
            # Its own generator, re-seeded every pass: see DISPERSION_SEED.
            envelope = dispersion_envelope(
                DISPERSION_REFERENCE_M, DISPERSION_SAMPLES,
                self.dispersion_sigma_rad, 0.0,
                random.Random(DISPERSION_SEED), self.ammo.speed, self.ammo.drag_k, G, REFERENCE_PITCH_RAD,
            )
        except Exception as error:
            self.add_event("observer", f"瞄准表尺解算失败：{error}")
            return
        with self.lock:
            self.observed_ballistics = {
                "rangeScale": {
                    "referenceDistanceM": DISPERSION_REFERENCE_M,
                    "referencePitchDeg": math.degrees(REFERENCE_PITCH_RAD),
                    # Only what the overlay draws with.  The drop stays for diagnostics;
                    # the front end places the rungs from the sight angle alone.
                    "marks": [
                        {
                            "distanceM": mark["distanceM"],
                            "sightDeg": mark["sightDeg"],
                            "dropM": mark["dropM"],
                        }
                        for mark in marks
                    ],
                },
                "dispersion": {
                    "referenceDistanceM": DISPERSION_REFERENCE_M,
                    "sigmas": DISPERSION_SIGMAS,
                    **envelope,
                },
            }

    def ballistics_loop(self) -> None:
        """Solve the sight aids once, and again only if a reset throws them away.

        They are constants, so there is nothing to keep up to date: this exists so the
        first state packet already carries a ladder, and so a reset -- which clears them
        -- republishes the same numbers instead of leaving the overlay blank.
        """
        while not self.stop_event.is_set():
            with self.lock:
                missing = self.observed_ballistics is None
            if missing:
                self.refresh_ballistics()
            self.stop_event.wait(0.25)

    def latest_state(self) -> dict | None:
        with self.state_snapshot_lock:
            return self.state_snapshot

    def state(self) -> dict:
        with self.lock:
            linear_speed, angular_speed, yaw, qpos, qvel = self._motion()
            muzzle = np.asarray(self.data.site(self.muzzle_site_name).xpos, dtype=float)
            ready, reason = self._fire_gate_locked()
            solution = self.solution
            # Empty until the ballistics observer has run once, rather than absent, so the
            # front end can bind to the shape of the payload from its very first frame.
            ballistics = self.observed_ballistics or {}
            range_scale = ballistics.get("rangeScale") or {
                "referenceDistanceM": DISPERSION_REFERENCE_M,
                "referencePitchDeg": math.degrees(REFERENCE_PITCH_RAD),
                "marks": [],
            }
            dispersion = ballistics.get("dispersion") or {
                "referenceDistanceM": DISPERSION_REFERENCE_M,
                "sigmas": DISPERSION_SIGMAS,
                "sigmaWidthM": 0.0,
                "sigmaHeightM": 0.0,
                "sigmaMrad": DISPERSION_NOMINAL_MRAD,
                "samples": 0,
            }
            aim = {
                "available": solution is not None,
                "autoCalibration": self.auto_calibration,
                "mode": solution["mode"] if solution else ("auto" if self.auto_calibration else "manual"),
                "ready": ready,
                "reason": reason,
                "targetM": TARGET.tolist(),
                "distanceM": float(np.linalg.norm(TARGET - muzzle)),
                "yawDeg": math.degrees(solution["yaw"]) if solution else None,
                "pitchDeg": math.degrees(solution["pitch"]) if solution else None,
                "tofS": solution["tof"] if solution else None,
                "clearanceM": solution["clearance"] if solution and solution["mode"] == "auto" else None,
                "clear": solution["clear"] if solution else False,
                "ageMs": (time.monotonic() - solution["solved_at"]) * 1000.0 if solution else None,
                "actualTurretYawDeg": math.degrees(yaw + float(self.data.qpos[self.turret_qadr])),
                "actualBarrelPitchDeg": math.degrees(float(self.data.qpos[self.barrel_qadr])),
                "manualPitchDeg": math.degrees(self.manual_pitch),
                "funnel": self.observed_funnel or {"points": [], "tofS": None, "impactM": None},
                "trajectory": serialize_trajectory(solution),
                "rangeScale": range_scale,
                "dispersion": dispersion,
            }
            hero = {
                "id": self.selected_robot_id,
                "robotClass": self.selected_robot_class,
                "positionM": qpos[:3].tolist(),
                "yawDeg": math.degrees(yaw),
                "linearVelocityMps": qvel[:3].tolist(),
                "linearSpeedMps": linear_speed,
                "angularVelocityDps": np.degrees(qvel[3:6]).tolist(),
                "angularSpeedDps": math.degrees(angular_speed),
                "muzzleM": muzzle.tolist(),
                "groundHeightM": float(qpos[2] - HERO_GROUND_OFFSET),
                "turretRelativeYawDeg": math.degrees(float(self.data.qpos[self.turret_qadr])),
                "turretWorldYawDeg": math.degrees(yaw + float(self.data.qpos[self.turret_qadr])),
                "barrelPitchDeg": math.degrees(float(self.data.qpos[self.barrel_qadr])),
                "steerTrajectory": self._steer_trajectory_locked(yaw, qpos, qvel),
            }
            robots = []
            for robot_id, robot_class in self.robot_classes.items():
                prefix = "" if robot_id == 1 else f"robot_{robot_id}_"
                body_id = int(self.model.body(prefix + "hero").id)
                position = np.asarray(self.data.xpos[body_id], dtype=float)
                robot_yaw = yaw_from_quaternion(np.asarray(self.data.xquat[body_id], dtype=float))
                robots.append({
                    "id": robot_id, "role": robot_class, "robotClass": robot_class,
                    "source": "mujoco", "x": float(position[0]), "y": float(position[1]), "z": float(position[2]),
                    "yawDeg": math.degrees(robot_yaw),
                })
            robotmaster = {"connected": self.robotmaster["connected"], "simulated": self.robotmaster["simulated"], "robots": [
                dict({"id": robot_id}, **values) for robot_id, values in self.robotmaster["robots"].items()
            ]}
            projectile = dict(self.flight)
            projectile.update({
                "visible": bool(self.flight["active"]),
                "positionM": np.asarray(self.data.xpos[self.projectile_body_id], dtype=float).tolist(),
                "velocityMps": np.asarray(
                    self.data.qvel[self.projectile_vadr:self.projectile_vadr + 3], dtype=float,
                ).tolist(),
            })
            enemy_projectile = dict(self.enemy_flight)
            enemy_projectile["visible"] = bool(self.enemy_flight["active"])
            camera_matrix = np.asarray(self.data.cam_xmat[self.operator_camera_id], dtype=float).reshape(3, 3)
            operator_camera = {
                "positionM": np.asarray(self.data.cam_xpos[self.operator_camera_id], dtype=float).tolist(),
                "forwardM": (-camera_matrix[:, 2]).tolist(),
                "upM": camera_matrix[:, 1].tolist(),
                "fovDeg": 58.0,
            }
            return {
                "schema": "pacific-doorlock.mujoco-live.v1",
                "connected": True,
                "selectedRobotId": self.selected_robot_id,
                "selectedRobotClass": self.selected_robot_class,
                "updatedAt": time.time(),
                "simulationTimeS": float(self.data.time),
                "source": {"scene": str(self.scene_path), "field": "RMUC 2026", "poseMode": "mujoco_live_truth", "mapKind": "rmuc2026_obj_projection"},
                "frame": {"name": "field_2026", "bounds": MAP_BOUNDS},
                "hero": hero,
                "aim": aim,
                "projectile": projectile,
                "enemyProjectile": enemy_projectile,
                "sharedMarker": dict(self.shared_marker) if self.shared_marker else {"active": False, "positionM": None, "setAt": None},
                "operatorCamera": operator_camera,
                "robots": robots,
                # The discs only the server can place: their height depends on the round's
                # drop and on the terrain under them, neither of which the client knows.
                "sightTargets": [
                    {
                        "distanceM": target["distanceM"],
                        "positionM": np.asarray(target["centre"], dtype=float).tolist(),
                        "normalM": np.asarray(target["normal"], dtype=float).tolist(),
                        "radius": target["radius"],
                        "flashing": target["hitAt"] is not None
                                    and time.monotonic() - target["hitAt"] < SIGHT_TARGET_FLASH_S,
                    }
                    for target in self.sight_targets
                ],
                "robotMaster": robotmaster,
                "controlMode": self.control_mode,
                "ammo": {
                    "name": self.ammo.name,
                    "label": self.ammo.label,
                    "speed": self.ammo.speed,
                    "dragK": self.ammo.drag_k,
                    "mass": self.ammo.mass,
                    "radius": self.ammo.radius,
                },
                "modules": dict(self.modules),
                "events": list(self.events),
            }

    def start(self) -> list[threading.Thread]:
        threads = [
            threading.Thread(target=self.simulation_loop, name="mujoco-simulation", daemon=True),
            threading.Thread(target=self.solver_loop, name="ballistic-solver", daemon=True),
            threading.Thread(target=self.funnel_observer_loop, name="funnel-observer", daemon=True),
            threading.Thread(target=self.ballistics_loop, name="ballistics-observer", daemon=True),
        ]
        for thread in threads:
            thread.start()
        return threads

    def stop(self) -> None:
        self.stop_event.set()


class LiveHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: tuple[str, int], simulation: LiveSimulation):
        self.simulation = simulation
        super().__init__(address, LiveRequestHandler)


class LiveRequestHandler(BaseHTTPRequestHandler):
    server: LiveHTTPServer

    def log_message(self, fmt: str, *args: object) -> None:
        if not self.path.startswith(("/api/events", "/api/frame.mjpg")):
            super().log_message(fmt, *args)

    def send_json(self, payload: dict, status: int = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def read_json(self) -> dict:
        length = int(self.headers.get("Content-Length", "0"))
        if length > 8192:
            raise ValueError("request body is too large")
        return json.loads(self.rfile.read(length).decode("utf-8") or "{}")

    def do_POST(self) -> None:
        path = urlsplit(self.path).path
        try:
            payload = self.read_json()
            if path == "/api/control":
                result = self.server.simulation.update_control(payload)
            elif path == "/api/fire":
                result = self.server.simulation.fire()
            elif path == "/api/reset":
                result = self.server.simulation.reset()
            elif path == "/api/camera":
                self.send_json({"ok": False, "error": "camera selection moved to Unity"}, HTTPStatus.GONE)
                return
            elif path == "/api/calibration":
                result = self.server.simulation.set_calibration(payload)
            elif path == "/api/ammo":
                result = self.server.simulation.set_ammo(payload)
            elif path == "/api/modules":
                result = self.server.simulation.set_modules(payload)
            elif path == "/api/robotmaster":
                result = {"ok": True, "robotMaster": self.server.simulation.state()["robotMaster"]}
            else:
                self.send_json({"ok": False, "error": "not found"}, HTTPStatus.NOT_FOUND)
                return
            self.send_json(result, HTTPStatus.OK if result.get("ok", True) else HTTPStatus.CONFLICT)
        # TypeError/AttributeError as well, not just ValueError: a body of
        # {"forward": null} reaches float(None), and a non-object body reaches
        # payload.get. Both used to escape to socketserver, which dropped the connection
        # without ever sending a response. (json.JSONDecodeError subclasses ValueError.)
        except (ValueError, TypeError, AttributeError) as error:
            self.send_json({"ok": False, "error": str(error)}, HTTPStatus.BAD_REQUEST)

    def do_GET(self) -> None:
        path = urlsplit(self.path).path
        if path == "/api/state":
            self.send_json(self.server.simulation.state())
        elif path == "/api/modules":
            self.send_json(self.server.simulation.state()["modules"])
        elif path == "/api/robotmaster":
            self.send_json(self.server.simulation.state()["robotMaster"])
        elif path in {"/api/events", "/api/frame.mjpg", "/api/frame.jpg"}:
            self.send_json({
                "ok": False,
                "error": "realtime streams were removed; Unity renders native 3D via TCP port 8770",
            }, HTTPStatus.GONE)
        else:
            self.send_json({"ok": False, "error": "MuJoCo backend exposes API endpoints only; use UnityClient as the frontend."}, HTTPStatus.NOT_FOUND)

def main() -> None:
    parser = argparse.ArgumentParser(description="Run the RMUC 2026 MuJoCo live UI bridge")
    parser.add_argument("--sim-root", type=Path, default=Path("/mnt/c/Users/ty/Desktop/RM_WK_SIM"))
    parser.add_argument("--web-root", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--realtime-port", type=int, default=8770)
    # Firing dispersion, in milliradians per aim axis.  0 restores the deterministic
    # shot the regression tests assert; the default is the visible spread the sight's
    # group box is drawn from.
    parser.add_argument("--shot-spread-mrad", type=float, default=SHOT_SPREAD_MRAD)
    parser.add_argument("--shot-seed", type=int, default=None)
    # The round the launcher starts loaded with; -1 keeps the default.
    parser.add_argument("--ammo", default="", choices=("", "42mm", "17mm"))
    args = parser.parse_args()

    simulation = LiveSimulation(
        args.sim_root.resolve(), args.web_root.resolve(),
        shot_spread_mrad=args.shot_spread_mrad, shot_seed=args.shot_seed,
    )
    if args.ammo:
        simulation.set_ammo({"ammo": args.ammo})
    threads = simulation.start()
    server = LiveHTTPServer((args.host, args.port), simulation)
    realtime = RealtimeTCPServer(args.host, args.realtime_port, simulation)
    threads.append(realtime.start())

    def shutdown(_signum: int, _frame: object) -> None:
        simulation.stop()
        realtime.stop()
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)
    print(f"MuJoCo backend API: http://127.0.0.1:{args.port}/api/state", flush=True)
    print(f"Unity realtime TCP: 127.0.0.1:{args.realtime_port}", flush=True)
    print("Unity frontend project: 自定义UI网页/UnityClient", flush=True)
    try:
        server.serve_forever(poll_interval=0.2)
    finally:
        simulation.stop()
        realtime.stop()
        server.server_close()
        for thread in threads:
            thread.join(timeout=2.0)


if __name__ == "__main__":
    main()
