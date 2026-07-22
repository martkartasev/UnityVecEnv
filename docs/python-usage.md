# Python Usage Guide

## Installation

```bash
pip install -e ./Python/unity_vecenv
```

With GPU support:

```bash
pip install -e ./Python/unity_vecenv[cuda118] --extra-index-url https://download.pytorch.org/whl/cu118
pip install -e ./Python/unity_vecenv[cuda121] --extra-index-url https://download.pytorch.org/whl/cu121
```

---

## UnityVectorEnv

The primary class. Wraps a single Unity process as a [Gymnasium `VectorEnv`](https://gymnasium.farama.org/api/vector/).

```python
from gymnasium.vector import AutoresetMode
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv(
    executable_path="builds/MyGame.exe",
    num_envs=8,
    no_graphics=True,
    time_scale=10,
    physics_steps_per_action=10,
    port=50010,
    env_parameters={"difficulty": 2, "variant": "wide"},
    autoreset_mode=AutoresetMode.SAME_STEP,
)
```

### Connecting to a running Unity instance

Set `start_process=False` and ensure Unity is already listening on the specified port:

```python
env = UnityVectorEnv(
    start_process=False,
    port=50010,
    num_envs=4,
)
```

This is useful for connecting to the Unity Editor during development.


### Constructor parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `executable_path` | `str \| None` | `None` | Path to the Unity build. `None` to connect to an already-running instance. |
| `start_process` | `bool` | `True` | Whether to launch Unity automatically. |
| `no_graphics` | `bool` | `True` | Run Unity in headless mode (no window). |
| `time_scale` | `float` | `10` | Unity `Time.timeScale`. Values above `1` speed up simulation. |
| `physics_steps_per_action` | `int` | `10` | Fixed-update steps Unity runs between each action. |
| `port` | `int` | `50010` | Base HTTP port. Automatically incremented if the port is busy. |
| `num_envs` | `int` | `1` | Number of parallel agents to request from Unity. |
| `scene_load` | `str` | `""` | Scene name to load on startup (passed as a command-line argument). |
| `log_file` | `str` | `""` | Path for Unity's player log. |
| `env_parameters` | `Mapping[str, str \| float \| int] \| None` | `None` | Parameters sent during environment initialization and available in Unity before `GymAgent.Initialize()`. |
| `autoreset_mode` | `AutoresetMode \| str` | `AutoresetMode.NEXT_STEP` | `NEXT_STEP` returns the terminal observation; `SAME_STEP` resets before returning and stores terminal observations in `info["final_observation"]`. Strings `"next_step"` and `"same_step"` are also accepted. |

## Gymnasium API

`UnityVectorEnv` is a standard `gymnasium.vector.VectorEnv`. All the usual Gymnasium patterns work:

```python
obs, info = env.reset()

for _ in range(steps):
    action = env.action_space.sample()          # or from your policy
    obs, rewards, dones, truncates, info = env.step(action)

env.close()
```

### Displaying step text in Unity

Pass `ui_strings` to display ordered text lines in the top-left corner of the Unity Game view or graphical player:

```python
obs, rewards, dones, truncates, info = env.step(
    action,
    ui_strings=[
        "Episode 12",
        "Reward: 1.25",
    ],
)
```

The text remains visible until a later step replaces it. Calling `step` without `ui_strings`, or passing an empty list, removes the overlay. The overlay is created only when at least one string is supplied and is also removed when the environment resets or reinitializes.

This display requires a graphical Unity runtime, such as a running Unity Editor or a player launched with `no_graphics=False`. `FlattenedVectorEnvThreaded` broadcasts the same `ui_strings` list to every backing Unity process.


### Spaces

Spaces are negotiated with Unity at initialization time. The environment reports its observation and action specs; `UnityVectorEnv` converts them to the appropriate Gymnasium space automatically.

| Unity config | Python space |
|---|---|
| `continuousObservations > 0` only | `Box` |
| `discreteObservations` only | `Discrete` / `MultiDiscrete` |
| Both | `Dict{"continuous": Box, "discrete": ...}` |

`single_observation_space` and `single_action_space` reflect the per-agent space. `observation_space` and `action_space` are their batched counterparts.

### Info dictionary

After each `step`, the info dict contains:

| Key | Shape | Description |
|---|---|---|
| `"final_observation"` | `(num_envs, *obs_shape)` | Last observation before reset for done agents; zeros elsewhere. |
| `"_final_observation"` | `(num_envs,)` bool | Mask: `True` for agents that finished an episode this step. |
| `"<key>"` | `(num_envs,)` float | Custom per-agent scalar from `CollectInfo` in Unity. |
| `"_<key>"` | `(num_envs,)` bool | Presence mask for the corresponding custom key. |

Custom keys with a leading `_` are reserved for masks and will raise an error if returned from Unity.

After `reset`, the info dict contains only custom fields (no `final_observation`).

### Passing initialization parameters on reset

```python
import numpy as np

init = np.zeros((num_envs, param_size), dtype=np.float32)
obs, info = env.reset(options={"init": init})
```

Each row is passed to the corresponding agent's `GymReset` call on the Unity side.

### Passing environment parameters on initialize

Use `env_parameters` for run-level configuration that should be available before agents initialize:

```python
env = UnityVectorEnv(
    executable_path="builds/MyGame.exe",
    num_envs=8,
    env_parameters={
        "difficulty": 2,
        "wind_scale": 0.35,
        "layout": "wide",
    },
)
```

These values are sent in the `/initialize/` request, so they also work when `start_process=False` and Python connects to an already-running Unity Editor instance.

---

## FlattenedVectorEnvThreaded

Combines multiple `UnityVectorEnv` instances (each potentially a separate Unity process) into a single `VectorEnv`. Sub-environments run in parallel using background threads.

```python
from unity_vecenv import UnityVectorEnv
from unity_vecenv.environment.unity_multi_vector_env import FlattenedVectorEnvThreaded

env = FlattenedVectorEnvThreaded([
    lambda: UnityVectorEnv("builds/MyGame.exe", num_envs=8, port=50010),
    lambda: UnityVectorEnv("builds/MyGame.exe", num_envs=8, port=50011),
])
# env.num_envs == 16

obs, info = env.reset()
obs, rewards, dones, truncates, info = env.step(env.action_space.sample())
env.close()
```

Pass callables (lambdas or functions) rather than instantiated environments so that each instance is created in the right thread context.

### When to use it

- You need more agents than a single Unity build can run efficiently.
- You want to run multiple independent Unity scenes in parallel.
- You want to isolate different environment variants behind a unified API.

## ONNX Utilities

### CLI

Rename ONNX model inputs to match Unity's Inference Engine naming conventions:

```bash
unity-vecenv onnx-rename input.onnx output.onnx --unity-defaults
```

### Python API

```python
from unity_vecenv.onnx_utilities.onnx_rename import rename_onnx_inputs

rename_onnx_inputs("input.onnx", "output.onnx", mapping={"old_name": "new_name"})
```

---

## Communication details

Python talks to Unity over plain HTTP on `localhost`. Each call is a synchronous POST with a protobuf-encoded body:

| Endpoint | Request | Response |
|---|---|---|
| `/initialize/` | `InitializeEnvironments` | `EnvironmentDescription` |
| `/reset/` | `Reset` | `BatchedResetResults` |
| `/step/` | `Step` | `BatchedStepResults` |

The client retries up to 20 times with 0.25 s delays and uses a 30-second per-request timeout.

Observations are transmitted as raw byte arrays (`float32` little-endian for continuous, `int32` little-endian for discrete) and deserialized with `numpy.frombuffer` — no per-element parsing overhead.

Custom info uses a **sparse columnar** encoding: only agents that produced metadata are included, identified by an index list. This keeps payloads small when most agents have no custom data.
