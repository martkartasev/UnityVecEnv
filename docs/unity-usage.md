# Unity Usage Guide

## Package Installation

Install `./Unity/package.json` via **Window → Package Manager → + → Install package from disk**, or add a relative path directly to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.unityvecenv": "file:../../UnityVecEnv/Unity"
  }
}
```

---

## Scene Setup

### Required components

Your scene needs **At least one agent** — a GameObject with your `GymAgent` subclass attached. The manager will clone it to reach the requested `agentCount`, based on what was configured on the Python end.

A `GymVecEnvManager` singleton is created automatically at runtime; you do not add it to the scene yourself.

## Implementing GymAgent

Subclass `GymAgent` and implement the five abstract methods:

```csharp
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;
using System.Collections.Generic;

public class MyAgent : GymAgent
{
    // Called once after all agents are initialized.
    protected override void Initialize() { }

    // Called at the start of each episode.
    protected override void GymReset() { }

    // Apply the action received from Python.
    protected override void SetAction(AgentAction action)
    {
        float move = action.Continuous[0];
        // ...
    }

    // Fill the observation struct with the current state.
    protected override void CollectObservation(ref AgentObservation obs)
    {
        obs.AppendContinuous(transform.position);
        obs.AppendContinuous(GetComponent<Rigidbody>().velocity);
    }

    // Return the reward for this step.
    protected override float CollectReward() => ComputeMyReward();

    // Return Running, Done, or Truncated.
    protected override EnvironmentState GymStep()
    {
        if (FellOff()) return EnvironmentState.Done;
        return EnvironmentState.Running;
    }
}
```

### Inspector configuration

Configure these fields in the Inspector on your agent prefab:

| Field | Description |
|---|---|
| `continuousObservations` | Number of continuous (float) observation values. |
| `discreteObservations` | List of discrete observation sizes (one entry per discrete value). |
| `continuousActions` | Number of continuous (float) action values. |
| `discreteActions` | List of discrete action branch sizes. |
| `gymSteps` | Maximum steps per episode. Agent is auto-truncated when reached. Set to 0 to disable. |
| `inferencePolicy` | Optional ONNX `ModelAsset` for local inference (no Python required). |

### EnvironmentState return values

| Value | Meaning |
|---|---|
| `EnvironmentState.Running` | Episode continues. |
| `EnvironmentState.Done` | Episode ended (terminal). |
| `EnvironmentState.Truncated` | Episode ended due to step limit (non-terminal). |

Returning `Done` or `Truncated` from `GymStep` causes `GymVecEnvManager` to automatically call `DoReset` on the agent at the end of the current step, before the next observation is sent to Python.

### Collecting observations

`AgentObservation` provides chainable append helpers:

```csharp
protected override void CollectObservation(ref AgentObservation obs)
{
    obs.AppendContinuous(transform.position);        // Vector3 → 3 floats
    obs.AppendContinuous(transform.rotation);        // Quaternion → 4 floats
    obs.AppendContinuous(someFloat);                 // single float
    obs.AppendContinuous(someFloatArray);             // float[]
    obs.AppendDiscrete(someInt);                     // single int
}
```

The total number of appended values must match `continuousObservations` and `discreteObservations` configured in the Inspector.

### Custom info

Override `CollectInfo` to send per-agent scalar metadata to Python alongside each step result. This is useful for logging episode statistics (e.g., distance travelled, collisions).

```csharp
protected override void CollectInfo(Dictionary<string, float> metadata)
{
    metadata["distance"] = _totalDistance;
    metadata["collisions"] = _collisionCount;
}
```

Custom info is only transmitted for agents that override this method and populate the dictionary. On the Python side, each key appears as a `(num_envs,)` float array, with a corresponding `_<key>` boolean mask indicating which agents provided a value.

### Receiving initialization parameters

To respond to per-episode initialization data passed from Python's `reset(options={"init": ...})`:

```csharp
protected override void GymReset()
{
    // Access initialization parameters if needed.
    // Currently parameters are passed via ResetParameters on the proto;
    // override GymReset and read from your own stored state.
}
```

---


## GymVecEnvManager

The singleton manager is created automatically and persists across scene loads. You can access it via `GymVecEnvManager.Instance` from anywhere.

### Key settings

| Field | Default | Description |
|---|---|---|
| `physicsStepsPerGymStep` | `10` | Fallback physics steps per action if Python doesn't specify one. |
| `timeoutMilliseconds` | `3000` | How long to wait for a step message from Python before quitting. |
| `SpawnMode` | `Gym` | `Gym`: manager spawns/removes agents to match `num_envs`. `Disabled`: use agents already in scene. |

### Execution order

The framework sets fixed execution orders internally:

| Component | Order |
|---|---|
| `GymAgentManager` | −501 |
| `GymVecEnvManager` | −500 |
| `GymAgent` subclasses | −50 |

Do not change these unless you have a specific reason to.

---

### Lifecycle events

Hook into these events for cross-cutting concerns (e.g., randomizing the environment layout on each reset):

```csharp
void OnEnable()
{
    GymVecEnvManager.Instance.PreInitialize  += OnPreInitialize;
    GymVecEnvManager.Instance.PostInitialize += OnPostInitialize;
    GymVecEnvManager.Instance.PreObservation += OnPreObservation;
    GymVecEnvManager.Instance.EarlyObservation += OnEarlyObservation;
}
```

| Event | When it fires |
|---|---|
| `PreInitialize` | Before agents are spawned during initialization. |
| `PostInitialize` | After all agents are initialized and `Initialize()` has been called. |
| `PreObservation` | Immediately before `CollectObservation` is called on all agents. |
| `EarlyObservation` | Halfway through the physics steps (useful for mid-step state capture). |

---

## Disconnected mode (Editor)

When running in the Unity Editor **without** a Python connection, `GymVecEnvManager` falls back to a disconnected stepping loop. Agents will use their `inferencePolicy` ONNX model (if set) or the value returned by `ProduceDummyAction`. This lets you iterate on agent behaviour and visuals without running a Python training script.

```csharp
// Provide a no-op or heuristic action when running without Python:
protected override AgentAction ProduceDummyAction(AgentAction dummy)
{
    dummy.Continuous[0] = 1.0f;   // always move forward
    return dummy;
}
```

---

## Local ONNX Inference

Assign a trained ONNX model as a `ModelAsset` to the `inferencePolicy` field on your agent. The Unity Inference Engine runs the model locally — no Python process needed.

Use the CLI tool to rename ONNX inputs if the model was exported from a framework that uses different input names than Unity expects:

```bash
unity-vecenv onnx-rename model.onnx model_unity.onnx --unity-defaults
```

---

## Build configuration

When building for training (not Editor play mode):

- Set **Scripting Backend** to IL2CPP for best runtime performance.
- Use **Server Build** (headless) if you pass `no_graphics=True` from Python.
- The HTTP server port defaults to `50010`. Pass `--port <n>` on the command line or let `UnityVectorEnv` handle port selection automatically.

Command-line arguments accepted by the Unity build:

| Argument | Description |
|---|---|
| `--port <n>` | HTTP server port |
| `--num-agents <n>` | Number of agents (overrides scene default) |
| `--scene <name>` | Scene to load |
| `--timescale <n>` | Initial `Time.timeScale` |
| `--logfile <path>` | Player log path |
