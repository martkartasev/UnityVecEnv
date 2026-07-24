# Unity Usage Guide

## Package Installation

Install the tagged package through **Window -> Package Manager -> + -> Install
package from git URL**, or add it directly to your project's
`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.mka.gymvecenv": "https://github.com/martkartasev/UnityVecEnv.git?path=/Unity#v0.1.9"
  }
}
```

For local UnityVecEnv development, use **Install package from disk** and select
`./Unity/package.json`.

---

## Scene Setup

### Required components

Your scene needs **at least one agent**: a GameObject with your `GymAgent` subclass attached.

By default, the manager clones that agent to reach the requested `agentCount`, based on what was configured on the Python end. Alternatively, you can have any number of `GymAgent`s preconfigured in the scene and opt not to send the agent count from the Python side. In that case, the Python environment will be informed of the corresponding count during initialization.

A `GymVecEnvManager` singleton is created automatically at runtime; you do not add it to the scene yourself.

### Environment prefab duplication

If you want to duplicate a full environment prefab instead of cloning a single `GymAgent`, add `GymEnvironmentTemplate` to the environment object you want the bootstrapper to pick up.

When a `GymEnvironmentTemplate` is present in the scene:

- `GymAgentManager` duplicates the tagged environment object instead of spawning agent copies.
- The requested count from the API is used when one is provided.
- If no explicit count is provided during initialization, `defaultEnvCount` on the template is used.
- After duplication, the normal initialization path discovers and registers the `GymAgent`s already present inside those duplicated environments.

`GymEnvironmentTemplate` fields:

| Field | Description |
|---|---|
| `cloneRoot` | Optional parent/root object to duplicate. Leave it empty when the component is already on the object that should be cloned. |
| `descriptionAgent` | Optional agent used to define the observation/action spaces. If omitted, the first `GymAgent` found under the cloned environment is used. |
| `cloneOffset` | Local-space offset applied between duplicated environment instances. Useful for laying out copies side by side. |
| `defaultEnvCount` | Number of environment copies to keep when no explicit count is requested during initialization. |

This setup is useful when one logical Gym environment includes more than just the agent itself, for example terrain, props, reset helpers, or a controller that spawns the agent.

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

There are also optional methods you can implement if desired:

```csharp
protected virtual AgentAction ProduceDummyAction(AgentAction dummyAgentAction)
{
    return dummyAgentAction;
}

protected virtual void CollectInfo(CustomInfoBuilder info)
{
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
    obs.AppendContinuous(transform.position);         // Vector3 -> 3 floats
    obs.AppendContinuous(transform.rotation);         // Quaternion -> 4 floats
    obs.AppendContinuous(someFloat);                  // single float
    obs.AppendContinuous(someFloatArray);             // float[]
    obs.AppendDiscrete(someInt);                      // single int
}
```

The total number of appended values must match `continuousObservations` and `discreteObservations` configured in the Inspector.

### Custom info

Override `CollectInfo` to send per-agent metadata to Python alongside each step result. This is useful for logging episode statistics such as distance travelled or collisions, and it now supports typed values instead of float-only scalars.

```csharp
using Scripts.VecEnv.Message;
using UnityEngine;

protected override void CollectInfo(CustomInfoBuilder info)
{
    info.Add("distance", _totalDistance);          // float -> (num_envs,)
    info.Add("collisions", _collisionCount);       // int -> (num_envs,)
    info.Add("grounded", _isGrounded);             // bool -> (num_envs,)
    info.Add("velocity", _rigidbody.velocity);     // Vector3 -> (num_envs, 3)
    info.Add("rotation", transform.rotation);      // Quaternion -> (num_envs, 4)
}
```

Each key must keep a single type across all agents and steps. On the Python side, every key still appears as a NumPy array plus a `_<key>` boolean presence mask:

- `float` -> `(num_envs,)` `float32`
- `int` -> `(num_envs,)` `int32`
- `bool` -> `(num_envs,)` `bool`
- `Vector3` -> `(num_envs, 3)` `float32`
- `Quaternion` -> `(num_envs, 4)` `float32`

Custom info is only transmitted for agents that override this method and add values. Existing `CollectInfo(Dictionary<string, float>)` overrides are still supported for backward compatibility, but new Unity code should use `CustomInfoBuilder`.

### Receiving initialization parameters

To respond to run-level initialization data passed from Python's `UnityVectorEnv(env_parameters={...})`, read the manager inside `Initialize()` or from a `PreInitialize` event subscriber:

```csharp
protected override void Initialize()
{
    var manager = GymVecEnvManager.Instance;
    var difficulty = manager.GetInitializationInt("difficulty", 1);
    var layout = manager.GetInitializationString("layout", "default");
    var windScale = manager.GetInitializationFloat("wind_scale", 0f);
}
```

`InitializationParameters` is populated before `PreInitialize`, agent spawning, and `GymAgent.Initialize()`. Available value types are `string`, `float`, and `int`.

Per-episode reset data is still passed separately through Python's `reset(options={"init": ...})`.

---

## GymVecEnvManager

The singleton manager is created automatically and persists across scene loads. You can access it via `GymVecEnvManager.Instance` from anywhere.

### Key settings

| Field | Default | Description |
|---|---|---|
| `physicsStepsPerGymStep` | `10` | Fallback physics steps per action if Python doesn't specify one. |
| `timeoutMilliseconds` | `3000` | How long to wait for a step message from Python before quitting. |
| `SpawnMode` | `Gym` | `Gym`: manager matches the requested count by duplicating `GymEnvironmentTemplate` instances when present, otherwise by spawning/removing agents. `Disabled`: use agents already in scene. |

### Execution order

The framework sets fixed execution orders internally:

| Component | Order |
|---|---|
| `GymAgentManager` | `-501` |
| `GymVecEnvManager` | `-500` |
| `GymAgent` subclasses | `-50` |

Do not change these unless you have a specific reason to.

---

### Lifecycle events

Hook into these events for cross-cutting concerns, for example randomizing the environment layout on each reset:

```csharp
void OnEnable()
{
    GymVecEnvManager.Instance.PreInitialize += OnPreInitialize;
    GymVecEnvManager.Instance.PostInitialize += OnPostInitialize;
    GymVecEnvManager.Instance.PreStep += OnPreStep;
    GymVecEnvManager.Instance.PreObservation += OnPreObservation;
    GymVecEnvManager.Instance.PostObservation += OnPostObservation;
    GymVecEnvManager.Instance.EarlyObservation += OnEarlyObservation;
    GymVecEnvManager.Instance.PreStepReset += OnPreStepReset;
    GymVecEnvManager.Instance.PostStepReset += OnPostStepReset;
    GymVecEnvManager.Instance.PostStep += OnPostStep;
}
```

| Event | When it fires |
|---|---|
| `PreInitialize` | Before agents are spawned during initialization. |
| `PostInitialize` | After all agents are initialized and `Initialize()` has been called. |
| `PreStep` | At the start of a step, before the physics-step loop begins. |
| `PreObservation` | Immediately before `CollectObservation` is called on all agents. |
| `PostObservation` | Immediately after observations and infos have been collected for the current reset or step. |
| `EarlyObservation` | Halfway through the physics steps, useful for mid-step state capture. |
| `PreStepReset` | After a step result has been produced, immediately before done agents are reset. |
| `PostStepReset` | Immediately after done agents have been reset at the end of a step. |
| `PostStep` | After the step is fully complete. |

During a normal connected step, the event order is:

`PreStep -> EarlyObservation -> PreObservation -> PostObservation -> PreStepReset -> PostStepReset -> PostStep`

During an explicit environment reset, only the observation events fire:

`PreObservation -> PostObservation`

These step events also fire in the disconnected Editor stepping loop, so subscribers can use the same hooks with or without a live Python connection.

---

## Disconnected mode (Editor)

When running in the Unity Editor **without** a Python connection, `GymVecEnvManager` falls back to a disconnected stepping loop. Agents will use their `inferencePolicy` ONNX model, if set, or the value returned by `ProduceDummyAction`. This lets you iterate on agent behaviour and visuals without running a Python training script.

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

Assign a trained ONNX model as a `ModelAsset` to the `inferencePolicy` field on your agent. The Unity Inference Engine runs the model locally; no Python process is needed.

Use the CLI tool to rename ONNX inputs if the model was exported from a framework that uses different input names than Unity expects:

```bash
pip install "unity-vecenv[onnx]"
unity-vecenv onnx-rename model.onnx model_unity.onnx --unity-defaults
```

---

## Build configuration

When building for training, not Editor play mode:

- Set **Scripting Backend** to IL2CPP for best runtime performance.
- Use **Server Build** (headless) if you pass `no_graphics=True` from Python.
- The HTTP server port defaults to `50010`. Pass `--port <n>` on the command line or let `UnityVectorEnv` handle port selection automatically.

Command-line arguments accepted by the Unity build:

| Argument | Description |
|---|---|
| `--port <n>` | HTTP server port |
| `--num-agents <n>` | Number of agents or environments to request, depending on scene setup |
| `--scene <name>` | Scene to load |
| `--timescale <n>` | Initial `Time.timeScale` |
| `--logfile <path>` | Player log path |
