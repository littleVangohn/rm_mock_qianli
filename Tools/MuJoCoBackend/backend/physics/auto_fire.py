"""Automatic fixed-target low-arc solver, independent from chassis control."""

from __future__ import annotations

import math
import time
from pathlib import Path

import numpy as np
from scipy.optimize import brentq
from scipy.spatial import cKDTree


def load_geometry_tree(obj_path: Path) -> cKDTree:
    points: list[np.ndarray] = []
    vertex_index = 0
    with obj_path.open("r", encoding="utf-8", errors="ignore") as stream:
        for line in stream:
            if not line.startswith("v "):
                continue
            if vertex_index % 2 == 0:
                points.append(np.fromstring(line[2:], sep=" ", count=3))
            vertex_index += 1
    if not points:
        raise RuntimeError(f"No OBJ vertices found in {obj_path}")
    return cKDTree(np.asarray(points, dtype=float))


def solve_low_arc(
    origin: np.ndarray, target: np.ndarray, cloud_tree: cKDTree,
    speed: float, drag_k: float, gravity: float, ball_radius: float, clearance_margin: float,
) -> dict | None:
    def residual(pitch: float) -> float:
        distance = float(np.linalg.norm(target[:2] - origin[:2]))
        if distance < 0.05:
            return float(origin[2] - target[2])
        x, z = 0.0, float(origin[2])
        vx, vz = speed * math.cos(pitch), speed * math.sin(pitch)
        dt = 0.004

        def derivative(px: float, pz: float, pvx: float, pvz: float) -> tuple[float, float, float, float]:
            current_speed = math.hypot(pvx, pvz)
            return pvx, pvz, -drag_k * current_speed * pvx, -drag_k * current_speed * pvz - gravity

        for _ in range(1500):
            previous = (x, z, vx, vz)
            a = derivative(x, z, vx, vz)
            b = derivative(x + 0.5 * dt * a[0], z + 0.5 * dt * a[1], vx + 0.5 * dt * a[2], vz + 0.5 * dt * a[3])
            c = derivative(x + 0.5 * dt * b[0], z + 0.5 * dt * b[1], vx + 0.5 * dt * b[2], vz + 0.5 * dt * b[3])
            d = derivative(x + dt * c[0], z + dt * c[1], vx + dt * c[2], vz + dt * c[3])
            x += dt * (a[0] + 2 * b[0] + 2 * c[0] + d[0]) / 6.0
            z += dt * (a[1] + 2 * b[1] + 2 * c[1] + d[1]) / 6.0
            vx += dt * (a[2] + 2 * b[2] + 2 * c[2] + d[2]) / 6.0
            vz += dt * (a[3] + 2 * b[3] + 2 * c[3] + d[3]) / 6.0
            if x >= distance:
                alpha = (distance - previous[0]) / max(x - previous[0], 1e-12)
                return previous[1] + alpha * (z - previous[1]) - target[2]
            if z < -0.5 or vx <= 0:
                break
        return z - target[2]

    angles = np.deg2rad(np.linspace(1.0, 62.0, 32))
    values = [residual(angle) for angle in angles]
    bracket = next(((left, right) for left, right, low, high in zip(angles[:-1], angles[1:], values[:-1], values[1:]) if low <= 0 < high), None)
    if bracket is None:
        return None
    pitch = brentq(residual, *bracket, xtol=1e-9)
    delta = target[:2] - origin[:2]
    distance = float(np.linalg.norm(delta))
    direction = delta / distance
    state = np.r_[origin.copy(), speed * np.array([math.cos(pitch) * direction[0], math.cos(pitch) * direction[1], math.sin(pitch)])]
    trajectory = [origin.copy()]
    elapsed, dt = 0.0, 0.002

    def derivative3(current: np.ndarray) -> np.ndarray:
        velocity = current[3:]
        acceleration = -drag_k * np.linalg.norm(velocity) * velocity
        acceleration[2] -= gravity
        return np.r_[velocity, acceleration]

    for _ in range(3000):
        previous = state.copy()
        a = derivative3(state); b = derivative3(state + 0.5 * dt * a)
        c = derivative3(state + 0.5 * dt * b); d = derivative3(state + dt * c)
        state += dt * (a + 2 * b + 2 * c + d) / 6.0
        elapsed += dt
        travelled = float(np.dot(state[:2] - origin[:2], direction))
        if travelled >= distance:
            old = float(np.dot(previous[:2] - origin[:2], direction))
            alpha = (distance - old) / max(travelled - old, 1e-12)
            state[:3] = previous[:3] + alpha * (state[:3] - previous[:3])
            elapsed -= dt * (1.0 - alpha)
            trajectory.append(state[:3].copy())
            break
        trajectory.append(state[:3].copy())
    trajectory_array = np.asarray(trajectory)
    from_origin = np.linalg.norm(trajectory_array - origin, axis=1)
    to_target = np.linalg.norm(trajectory_array - target, axis=1)
    query = trajectory_array[(from_origin > 0.75) & (to_target > 0.35)]
    clearance = float(np.min(cloud_tree.query(query, workers=-1)[0])) if len(query) else 0.0
    return {
        "mode": "auto", "origin": origin.copy(), "yaw": math.atan2(delta[1], delta[0]),
        "pitch": pitch, "tof": elapsed, "trajectory": trajectory_array,
        "clearance": clearance, "clear": clearance > ball_radius + clearance_margin,
        "solved_at": time.monotonic(),
    }
