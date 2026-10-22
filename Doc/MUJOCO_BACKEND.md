# MuJoCo first-person integration

The Unity adapter and Python MuJoCo backend source are both stored in this
repository. The backend is `Tools/MuJoCoBackend/backend`; launchers do not load
code from `D:\\Pacific_doorlock\\自定义UI网页`, and that directory is not
modified by these scripts.

## Start

From `D:\Pacific_doorlock` in WSL, run:

```sh
sh run-mujoco.sh
```

That is the only launch entry. It starts `backend.server` in WSL Ubuntu-22.04
using the existing `/home/van/mujoco-env` environment, waits for the HTTP and
realtime ports, then opens the Unity project in Play mode. HTTP uses port
18767 and the framed realtime TCP protocol uses port 18772; override them with
`MUJOCO_HTTP_PORT` / `MUJOCO_TCP_PORT` if needed.

## Robot mapping and model scope

The source simulator's role slots map as follows: IDs 1/21 Hero, 2/22 Engineer,
3–6/23–26 Infantry, and 7/27 Sentry (red/blue respectively). The backend
generates independent articulated MuJoCo bodies for all 14 IDs; it applies
class-specific chassis mass and body-size profiles derived from the source
prefabs. Those values are approximations, not validated CAD or inertia data.
Realtime control selects one robot ID/class at a time and rejects mismatched
control IDs. Chassis, turret, barrel and operator camera state are read from
that selected robot's MuJoCo model.

Unity continues to render the original open-source arena and vehicle prefabs;
the MuJoCo body is the source of pose/control state in this mode. The Unity
adapter is under `Assets/Scripts/MujocoBackend` and the Pacific UI integration
is under `Assets/PacificOfficialClient`.

## Validate

Compile the integration by opening the project in Unity, or run a batch-mode
compile with `-quit -batchmode -projectPath <project>` and check the log for
`error CS`. Runtime validation must confirm backend startup, selected ID/class
acknowledgements, and that control changes the selected MuJoCo body's qpos
while non-selected bodies remain independent. Match HP, ammunition, economy and
other referee data should only be presented as authoritative when sourced from
the simulator's referee rules.

The backend's built-in RobotMaster values are explicitly `simulated` test data.
In a hosted Unity match, `PacificOfficialUiBridge` replaces them with the live
open-source `RefereeController`/`GameManager` values for all seven slots on both
factions, including slot 5. Do not treat the fallback snapshot as official rule
state when the Unity referee list is not yet available.
