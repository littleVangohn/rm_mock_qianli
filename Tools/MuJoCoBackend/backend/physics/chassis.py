"""Planar omni-chassis force controller, independent from firing modes."""

from __future__ import annotations

import math

import mujoco
import numpy as np


# Shared by apply_chassis_control and predict_chassis_trajectory. The prediction is a
# roll-out of this exact law, so both must read the same numbers or the drawn path
# drifts away from what the chassis actually does.
SPEED_LIMIT = 2.5
SPEED_LIMIT_BOOST = 4.5
DRIVE_GAIN = 8.0
BRAKE_GAIN = 13.0
FORCE_PER_GAIN = 45.0
FORCE_CAP = 4000.0
YAW_RATE_LIMIT = math.radians(120.0)
TORQUE_PER_RATE = 180.0
TORQUE_CAP = 260.0
# 自动跟随. The gimbal turns at 90 deg/s, so a lower ceiling here keeps the chassis
# visibly trailing the turret marker instead of snapping onto it.
FOLLOW_YAW_RATE = math.radians(70.0)
FOLLOW_YAW_GAIN = 8.0
# The wheels sit on the heightfield with enough stiction that a proportional term
# alone stalls a few degrees short: measured, 40 N.m of yaw torque moves the chassis
# not at all and 80 N.m only just breaks it loose. Push through that when there is
# still a heading error and the chassis has all but stopped.
FOLLOW_BREAKAWAY = 130.0
FOLLOW_DEADBAND = math.radians(0.8)
FOLLOW_STALL_RATE = math.radians(5.0)

# Integration step for the prediction roll-out. The velocity lag time constant is
# mass / (FORCE_PER_GAIN * gain) ~= 0.2 s, so 0.01 s samples it ~20x over while keeping
# the roll-out cheap enough to run on the 30 Hz state path.
PREDICT_DT = 0.01


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _planar_target(command: dict, yaw: float, position: np.ndarray, bounds: dict[str, float]) -> np.ndarray:
    """World-frame velocity the controller steers toward, boundary fade included."""
    speed_limit = SPEED_LIMIT_BOOST if command["boost"] else SPEED_LIMIT
    local_target = np.array([command["forward"], command["strafe"]], dtype=float) * speed_limit
    rotation = np.array([[math.cos(yaw), -math.sin(yaw)], [math.sin(yaw), math.cos(yaw)]])
    world_target = rotation @ local_target
    safe_min = np.array([bounds["xMin"] + 1.0, bounds["yMin"] + 1.0])
    safe_max = np.array([bounds["xMax"] - 1.0, bounds["yMax"] - 1.0])
    for axis in range(2):
        if world_target[axis] < 0:
            distance = position[axis] - safe_min[axis]
        elif world_target[axis] > 0:
            distance = safe_max[axis] - position[axis]
        else:
            continue
        world_target[axis] *= clamp(distance / 2.0, 0.0, 1.0)
    return world_target


def _yaw_torque(command: dict, yaw: float, yaw_rate: float) -> float:
    if command.get("headingTarget") is not None:
        # 自动跟随: swing the chassis toward the gimbal heading at a bounded rate, so
        # the turret marker visibly leads the chassis marker instead of snapping to it.
        error = math.atan2(math.sin(command["headingTarget"] - yaw), math.cos(command["headingTarget"] - yaw))
        follow_rate = clamp(FOLLOW_YAW_GAIN * error, -FOLLOW_YAW_RATE, FOLLOW_YAW_RATE)
        torque = TORQUE_PER_RATE * (follow_rate - yaw_rate)
        if abs(error) > FOLLOW_DEADBAND and abs(yaw_rate) < FOLLOW_STALL_RATE:
            torque = math.copysign(max(abs(torque), FOLLOW_BREAKAWAY), error)
        return clamp(torque, -TORQUE_CAP, TORQUE_CAP)
    return clamp(TORQUE_PER_RATE * (command["yaw"] * YAW_RATE_LIMIT - yaw_rate), -TORQUE_CAP, TORQUE_CAP)


def _drive_force(model, hero_body_id: int, gain: float, target: np.ndarray, velocity: np.ndarray) -> np.ndarray:
    # The source prefabs have very different chassis masses. A fixed 900 N
    # command moves the light Hero but cannot break the Engineer's ground
    # friction, making W/S look disconnected despite valid control packets.
    mass_scale = max(1.0, float(model.body_mass[hero_body_id]) / 100.0)
    force = FORCE_PER_GAIN * gain * mass_scale * (target - velocity)
    magnitude = float(np.linalg.norm(force))
    if magnitude > FORCE_CAP:
        force *= FORCE_CAP / magnitude
    return force


def apply_chassis_control(
    model, data, hero_body_id: int, command: dict, yaw: float,
    qpos: np.ndarray, qvel: np.ndarray, bounds: dict[str, float],
) -> None:
    world_target = _planar_target(command, yaw, qpos, bounds)
    gain = BRAKE_GAIN if command["brake"] else DRIVE_GAIN
    force = _drive_force(model, hero_body_id, gain, world_target, qvel[:2])
    torque = _yaw_torque(command, yaw, qvel[5])
    data.xfrc_applied[hero_body_id] = (force[0], force[1], 0.0, 0.0, 0.0, torque)


def chassis_inertia(model, data, hero_linear_vadrs, hero_yaw_vadr) -> tuple[float, float, float, float]:
    """Effective chassis mass, yaw inertia and the joint damping that opposes them.

    Mass and inertia come from the mass matrix rather than body_mass/body_inertia: those
    describe one body only and omit the turret and barrel, which would make the prediction
    accelerate far faster than the real chassis does.

    The chassis joints carry damping (18 N.s/m on x/y, 2 on yaw), so the steady-state
    speed is 0.95 x the commanded target, not the target -- leaving it out makes the drawn
    path overshoot by roughly a tenth of its length.
    """
    mass_matrix = np.zeros((model.nv, model.nv))
    mujoco.mj_fullM(model, mass_matrix, data.qM)
    mass = float(np.mean([mass_matrix[index, index] for index in hero_linear_vadrs[:2]]))
    inertia = float(mass_matrix[hero_yaw_vadr, hero_yaw_vadr])
    linear_damping = float(np.mean([model.dof_damping[index] for index in hero_linear_vadrs[:2]]))
    yaw_damping = float(model.dof_damping[hero_yaw_vadr])
    return mass, inertia, linear_damping, yaw_damping


def predict_chassis_trajectory(
    model, data, hero_body_id: int, command: dict, yaw: float,
    qpos: np.ndarray, qvel: np.ndarray, bounds: dict[str, float],
    hero_linear_vadrs, hero_yaw_vadr, horizon_s: float = 1.2, dt: float = PREDICT_DT,
) -> np.ndarray:
    """Roll the chassis control law forward, as an (N, 4) array of x, y, z, yaw.

    The controller is a first-order law on velocity and yaw rate, so integrating it
    directly reproduces what mj_step would do without cloning mjData or competing with
    the 500 Hz loop for the simulation lock. Row 0 is the current pose, so the caller
    can draw straight from the array.
    """
    mass, inertia, linear_damping, yaw_damping = chassis_inertia(
        model, data, hero_linear_vadrs, hero_yaw_vadr)
    if mass <= 0.0 or inertia <= 0.0:
        return np.empty((0, 4))

    position = np.asarray(qpos[:2], dtype=float).copy()
    height = float(qpos[2])
    velocity = np.asarray(qvel[:2], dtype=float).copy()
    heading = float(yaw)
    yaw_rate = float(qvel[5])
    gain = BRAKE_GAIN if command["brake"] else DRIVE_GAIN
    steps = max(1, int(round(horizon_s / dt)))
    points = np.empty((steps + 1, 4), dtype=float)
    points[0] = (position[0], position[1], height, heading)
    for step in range(1, steps + 1):
        world_target = _planar_target(command, heading, position, bounds)
        force = _drive_force(model, hero_body_id, gain, world_target, velocity)
        torque = _yaw_torque(command, heading, yaw_rate)
        # Semi-implicit, the way MuJoCo integrates joint damping: solving the damped step
        # implicitly keeps the roll-out stable at this step size and matches the real
        # settling time instead of running away from it.
        velocity = (mass * velocity + dt * force) / (mass + dt * linear_damping)
        yaw_rate = (inertia * yaw_rate + dt * torque) / (inertia + dt * yaw_damping)
        position = position + velocity * dt
        heading = math.atan2(math.sin(heading + yaw_rate * dt), math.cos(heading + yaw_rate * dt))
        points[step] = (position[0], position[1], height, heading)
    return points


def decimate_chassis_trajectory(points: np.ndarray, limit: int = 24) -> np.ndarray:
    """Thin the roll-out for the wire, always keeping both endpoints."""
    if points.ndim != 2 or points.shape[0] == 0:
        return np.empty((0, 4))
    count = min(limit, points.shape[0])
    if count < 2:
        return points[:count]
    indices = np.linspace(0, points.shape[0] - 1, count, dtype=int)
    return points[indices]
