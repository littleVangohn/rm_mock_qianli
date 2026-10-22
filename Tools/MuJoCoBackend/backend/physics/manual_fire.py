"""Manual firing prediction from the current physical gimbal pose."""

from __future__ import annotations

import math
import time

import numpy as np

# Fixed integration step. Because every step advances the clock by exactly this much,
# a trajectory index carries its own time of flight: sample i is i * PREDICTION_STEP_S
# after the shot. The HUD funnel line is stamped from that instead of re-integrating.
PREDICTION_STEP_S = 0.004

# The sight scale and the dispersion envelope only need the shape of the path, not the
# 4 ms resolution the funnel line needs. A coarser step makes the Monte Carlo cheap
# enough to run in the physics server's own loop: the systematic integration error is
# the same for every sample, so it cancels out of the spread.
COARSE_STEP_S = 0.016


class HeightfieldSampler:
    def __init__(self, model, name: str, bounds: dict[str, float], base_z: float) -> None:
        hfield = model.hfield(name)
        address = int(hfield.adr[0])
        self.rows = int(hfield.nrow[0])
        self.columns = int(hfield.ncol[0])
        self.size = np.asarray(hfield.size, dtype=float).copy()
        self.data = np.asarray(
            model.hfield_data[address:address + self.rows * self.columns], dtype=float
        ).reshape(self.rows, self.columns).copy()
        self.bounds = dict(bounds)
        self.base_z = base_z

    def height_at(self, x: float, y: float) -> float:
        column = np.clip((x - self.bounds["xMin"]) / (self.bounds["xMax"] - self.bounds["xMin"]) * (self.columns - 1), 0.0, self.columns - 1)
        row = np.clip((y - self.bounds["yMin"]) / (self.bounds["yMax"] - self.bounds["yMin"]) * (self.rows - 1), 0.0, self.rows - 1)
        c0, r0 = int(math.floor(column)), int(math.floor(row))
        c1, r1 = min(c0 + 1, self.columns - 1), min(r0 + 1, self.rows - 1)
        cx, ry = column - c0, row - r0
        row0 = (1.0 - cx) * self.data[r0, c0] + cx * self.data[r0, c1]
        row1 = (1.0 - cx) * self.data[r1, c0] + cx * self.data[r1, c1]
        return self.base_z + float((1.0 - ry) * row0 + ry * row1) * float(self.size[2])


def predict_manual_shot(
    origin: np.ndarray, yaw: float, pitch: float, terrain: HeightfieldSampler,
    speed: float, drag_k: float, gravity: float, ball_radius: float,
) -> dict:
    velocity = speed * np.array([
        math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), math.sin(pitch),
    ])
    state = np.r_[origin.copy(), velocity]
    points = [origin.copy()]
    dt = PREDICTION_STEP_S

    def derivative(current: np.ndarray) -> np.ndarray:
        current_velocity = current[3:]
        acceleration = -drag_k * np.linalg.norm(current_velocity) * current_velocity
        acceleration[2] -= gravity
        return np.r_[current_velocity, acceleration]

    elapsed = 0.0
    for _ in range(1250):
        a = derivative(state)
        b = derivative(state + 0.5 * dt * a)
        c = derivative(state + 0.5 * dt * b)
        d = derivative(state + dt * c)
        state += dt * (a + 2 * b + 2 * c + d) / 6.0
        elapsed += dt
        point = state[:3].copy()
        ground = terrain.height_at(float(point[0]), float(point[1])) + ball_radius
        if elapsed > 0.04 and point[2] <= ground:
            point[2] = ground
            points.append(point)
            break
        points.append(point)
        if point[2] < -1.0:
            break
    return {
        "mode": "manual", "origin": origin.copy(), "yaw": yaw, "pitch": pitch,
        "tof": elapsed, "trajectory": np.asarray(points), "clearance": 0.0,
        "clear": True, "solved_at": time.monotonic(),
    }


# --------------------------------------------------------------------------- sight scale
#
# The operator's sight scale answers one question per mark: if the barrel points at a
# target D metres away, how far below the crosshair does the round cross that range?
# That is a property of the round and the launch direction alone -- not of where the
# chassis is, and only very weakly of how far the barrel is elevated -- which is what
# lets the front end draw it as a fixed ladder under the crosshair.
#
# Everything below is deliberately terrain-free: the scale says where the round is at a
# slant range from the muzzle, and whether terrain would have stopped it earlier is the
# funnel line's job, not the scale's.


def launch_direction(yaw: float, pitch: float) -> tuple[float, float, float]:
    return (
        math.cos(pitch) * math.cos(yaw),
        math.cos(pitch) * math.sin(yaw),
        math.sin(pitch),
    )


def _rollout(
    origin, yaw: float, pitch: float, speed: float, drag_k: float, gravity: float,
    step_s: float, max_slant_m: float,
) -> tuple[list[tuple[float, float, float]], list[float]]:
    """Free-flight RK4 rollout, stopped once the round is `max_slant_m` from the muzzle.

    Uses the same state layout and weighting as predict_manual_shot so the two
    integrators agree; only the step and the stop test differ.
    """
    px, py, pz = origin
    dx, dy, dz = launch_direction(yaw, pitch)
    vx, vy, vz = speed * dx, speed * dy, speed * dz
    ox, oy, oz = px, py, pz
    points = [(px, py, pz)]
    slants = [0.0]

    def acceleration(ax, ay, az):
        norm = math.sqrt(ax * ax + ay * ay + az * az)
        return (-drag_k * norm * ax, -drag_k * norm * ay, -drag_k * norm * az - gravity)

    for _ in range(int(20.0 / step_s) + 1):
        k1 = acceleration(vx, vy, vz)
        k2 = acceleration(vx + .5 * step_s * k1[0], vy + .5 * step_s * k1[1], vz + .5 * step_s * k1[2])
        k3 = acceleration(vx + .5 * step_s * k2[0], vy + .5 * step_s * k2[1], vz + .5 * step_s * k2[2])
        k4 = acceleration(vx + step_s * k3[0], vy + step_s * k3[1], vz + step_s * k3[2])
        # The four stages sample the velocity as well as the acceleration; the position
        # advances on the weighted stage velocities, exactly as the shot predictor's
        # combined-state step does, so both integrators return the same path.
        stages = (
            (vx, vy, vz),
            (vx + .5 * step_s * k1[0], vy + .5 * step_s * k1[1], vz + .5 * step_s * k1[2]),
            (vx + .5 * step_s * k2[0], vy + .5 * step_s * k2[1], vz + .5 * step_s * k2[2]),
            (vx + step_s * k3[0], vy + step_s * k3[1], vz + step_s * k3[2]),
        )
        weights = (1.0, 2.0, 2.0, 1.0)
        px += step_s * sum(w * s[0] for w, s in zip(weights, stages)) / 6.0
        py += step_s * sum(w * s[1] for w, s in zip(weights, stages)) / 6.0
        pz += step_s * sum(w * s[2] for w, s in zip(weights, stages)) / 6.0
        vx += step_s * (k1[0] + 2 * k2[0] + 2 * k3[0] + k4[0]) / 6.0
        vy += step_s * (k1[1] + 2 * k2[1] + 2 * k3[1] + k4[1]) / 6.0
        vz += step_s * (k1[2] + 2 * k2[2] + 2 * k3[2] + k4[2]) / 6.0
        points.append((px, py, pz))
        slants.append(math.sqrt((px - ox) ** 2 + (py - oy) ** 2 + (pz - oz) ** 2))
        if slants[-1] >= max_slant_m:
            break
    return points, slants


def _drop_angles(points, slants, forward, origin) -> list[float]:
    """Angle of each sample below the launch line, in degrees.

    Measured against the line the barrel points along rather than against gravity, so
    the number is what the crosshair has to be lifted by -- the same thing a scope's
    holdover ladder is graduated in.  Offsets are taken from the muzzle, never from the
    world origin: the same path fired from the far side of the field has to read the
    same.
    """
    fx, fy, fz = forward
    ox, oy, oz = origin
    angles = []
    for (px, py, pz), slant in zip(points, slants):
        along = (px - ox) * fx + (py - oy) * fy + (pz - oz) * fz
        perpendicular = math.sqrt(max(slant * slant - along * along, 0.0))
        angles.append(math.degrees(math.atan2(perpendicular, max(along, 1e-9))))
    return angles


def drop_table(
    distances_m, speed: float, drag_k: float, gravity: float,
    pitch: float = 0.0, yaw: float = 0.0, step_s: float = PREDICTION_STEP_S,
    origin=(0.0, 0.0, 0.0),
) -> list[dict]:
    """Where the round is at each slant range from the muzzle.

    Ranges are measured straight from the muzzle, because that is the distance a
    shooter means by "that target is 20 m away".  Each mark carries both the angle
    below the barrel line and the world point it came from, so a caller that knows the
    sight's own pose can re-measure the angle against the sight line instead.
    """
    ranges = [float(value) for value in distances_m]
    if not ranges:
        return []
    forward = launch_direction(yaw, pitch)
    points, slants = _rollout(origin, yaw, pitch, speed, drag_k, gravity, step_s, max(ranges))
    angles = _drop_angles(points, slants, forward, origin)
    marks = []
    for distance in ranges:
        index = int(np.searchsorted(slants, distance))
        index = min(max(index, 1), len(points) - 1)
        span = slants[index] - slants[index - 1]
        weight = 0.0 if span <= 0.0 else (distance - slants[index - 1]) / span
        point = tuple(
            points[index - 1][axis] + weight * (points[index][axis] - points[index - 1][axis])
            for axis in range(3)
        )
        angle = float(np.interp(distance, slants, angles))
        marks.append({
            "distanceM": distance,
            "dropDeg": angle,
            "dropM": distance * math.tan(math.radians(angle)),
            "x": point[0], "y": point[1], "z": point[2],
        })
    return marks


def sample_shot_error(rng, dir_sigma_rad: float, speed_sigma: float) -> tuple[float, float, float]:
    """One round's deviation: (yaw offset, pitch offset, speed multiplier).

    Both angles get an independent draw about the barrel axis and the speed a
    proportional one. A zero sigma draws nothing at all, so switching the spread off
    restores the exact deterministic shot and does not consume the generator.
    """
    dyaw = rng.gauss(0.0, dir_sigma_rad) if dir_sigma_rad > 0.0 else 0.0
    dpitch = rng.gauss(0.0, dir_sigma_rad) if dir_sigma_rad > 0.0 else 0.0
    scale = 1.0 + rng.gauss(0.0, speed_sigma) if speed_sigma > 0.0 else 1.0
    return dyaw, dpitch, scale


def perturbed_velocity(
    yaw: float, pitch: float, speed: float, dir_sigma_rad: float, speed_sigma: float, rng,
):
    """The velocity a fired round actually leaves with, spread included."""
    dyaw, dpitch, scale = sample_shot_error(rng, dir_sigma_rad, speed_sigma)
    direction = launch_direction(yaw + dyaw, pitch + dpitch)
    return np.array(direction, dtype=float) * (speed * scale)


def dispersion_envelope(
    distance_m: float, samples: int, dir_sigma_rad: float, speed_sigma: float, rng,
    speed: float, drag_k: float, gravity: float,
    pitch: float = 0.0, yaw: float = 0.0, step_s: float = COARSE_STEP_S,
) -> dict:
    """How wide the group is at `distance_m`, from the same spread the rounds get.

    Monte Carlo over the firing perturbation, measured perpendicular to the launch
    line. The result is one standard deviation, as a half-extent in metres: the HUD
    multiplies by however many sigmas it wants to draw, so the coverage it shows is
    decided in one place instead of being baked in here.
    """
    if samples <= 0 or (dir_sigma_rad <= 0.0 and speed_sigma <= 0.0):
        return {
            "sigmaWidthM": 0.0, "sigmaHeightM": 0.0,
            "sigmaMrad": dir_sigma_rad * 1000.0, "samples": 0,
        }

    forward = launch_direction(yaw, pitch)
    fx, fy, fz = forward
    # A frame across the launch line: "right" is horizontal, "up" lies in the vertical
    # plane through the barrel. The group is measured in that frame, so the numbers do
    # not change as the chassis turns.
    rx, ry, rz = fy, -fx, 0.0
    right_norm = math.hypot(rx, ry)
    if right_norm < 1e-9:
        rx, ry, rz = 1.0, 0.0, 0.0
        right_norm = 1.0
    rx, ry, rz = rx / right_norm, ry / right_norm, rz / right_norm
    ux = ry * fz - rz * fy
    uy = rz * fx - rx * fz
    uz = rx * fy - ry * fx

    def point_at(points, slants):
        """Where the path crosses `distance_m`, to the resolution of the step."""
        index = int(np.searchsorted(slants, distance_m))
        index = min(max(index, 1), len(points) - 1)
        span = slants[index] - slants[index - 1]
        weight = 0.0 if span <= 0.0 else (distance_m - slants[index - 1]) / span
        return tuple(
            points[index - 1][axis] + weight * (points[index][axis] - points[index - 1][axis])
            for axis in range(3)
        )

    reach = distance_m * 1.6 + 1.0
    # The reference is the undisturbed round's own point at this range, not a point on
    # the launch line: the round has already fallen a long way by the time it gets here,
    # and measuring against the line would fold that fall -- and how much of it the
    # aim error adds or removes -- into what is supposed to be the group's width.
    nominal_points, nominal_slants = _rollout(
        (0.0, 0.0, 0.0), yaw, pitch, speed, drag_k, gravity, step_s, reach,
    )
    if nominal_slants[-1] < distance_m:
        return {
            "sigmaWidthM": 0.0, "sigmaHeightM": 0.0,
            "sigmaMrad": dir_sigma_rad * 1000.0, "samples": 0,
        }
    nominal = point_at(nominal_points, nominal_slants)

    lateral, vertical = [], []
    for _ in range(samples):
        dyaw, dpitch, scale = sample_shot_error(rng, dir_sigma_rad, speed_sigma)
        points, slants = _rollout(
            (0.0, 0.0, 0.0), yaw + dyaw, pitch + dpitch, speed * scale,
            drag_k, gravity, step_s, reach,
        )
        if slants[-1] < distance_m:
            continue
        point = point_at(points, slants)
        dx, dy, dz = (point[axis] - nominal[axis] for axis in range(3))
        lateral.append(dx * rx + dy * ry + dz * rz)
        vertical.append(dx * ux + dy * uy + dz * uz)

    if len(lateral) < 2:
        return {
            "sigmaWidthM": 0.0, "sigmaHeightM": 0.0,
            "sigmaMrad": dir_sigma_rad * 1000.0, "samples": len(lateral),
        }
    return {
        "sigmaWidthM": float(np.std(lateral)),
        "sigmaHeightM": float(np.std(vertical)),
        "sigmaMrad": dir_sigma_rad * 1000.0,
        "samples": len(lateral),
    }
