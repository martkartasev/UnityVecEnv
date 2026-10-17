# UnityVecEnv

Use a Unity scene as a reinforcement-learning environment for Python. UnityVecEnv implements Gymnasium's [`VectorEnv`](https://gymnasium.farama.org/api/vector/) interface: many copies of your agent run at once inside Unity while a Python training loop sends actions and receives what each agent sees, its reward and whether its episode has ended.

## Highlights

- Hundreds of agents in one Unity process, and several processes can be treated as one bigger environment.
- A standard Gymnasium `VectorEnv`, so existing Gymnasium-based trainers work unchanged.
- Observations as numbers, integer choices or camera images. Agents can report extra values, and Python can pass settings to the scene when it starts.
- Save a trained PyTorch network as ONNX and run it inside Unity without Python, through Unity's Inference Engine.

## Installation

```bash
pip install unity-vecenv                  # Python 3.10 or newer
pip install "unity-vecenv[onnx]"          # optional: onnx and torch, for ONNX export and unity-vecenv onnx-rename
```

In Unity (6000.0 or newer), open **Window > Package Manager > + > Install package from git URL** and paste the URL of the tag that matches your Python version, for example

```text
https://github.com/martkartasev/UnityVecEnv.git?path=/Unity#v0.1.9
```

or add that URL under `"dependencies"` in `Packages/manifest.json` as `"com.mka.gymvecenv"`. The project is in alpha and the two halves must carry the same version, so install a specific version on both sides, for example `pip install unity-vecenv==0.1.9` with tag `v0.1.9`. Published versions are listed under [tags](https://github.com/martkartasev/UnityVecEnv/tags).

## Usage

On the Unity side, write a C# class that extends `GymAgent`, attach it to an object in your scene, and set in the Inspector how many numbers it observes and how many it outputs as actions. The component that talks to Python is added when the game runs.

```csharp
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;

public class MyAgent : GymAgent
{
    protected override void GymReset() { }                                   // start a new episode
    protected override void SetAction(AgentAction action) { }                // apply action.Continuous[...]
    protected override void CollectObservation(ref AgentObservation obs)     // what the agent sees
    {
        obs.AppendContinuous(transform.position);
    }
    protected override float CollectReward() { return 0f; }                  // this step's reward
    protected override EnvironmentState GymStep()                            // Running, or Done / Truncated
    {
        return EnvironmentState.Running;
    }
}
```

Build the project into a player, then drive it from Python. `num_envs` is how many copies of the agent Unity should run:

```python
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv("path/to/MyGame.exe", num_envs=16)
obs, info = env.reset()
obs, rewards, terminated, truncated, info = env.step(env.action_space.sample())
env.close()
```

To use the Editor instead of a build, press Play in Unity and attach to the port it prints in the Console:

```python
env = UnityVectorEnv(start_process=False, port=50010, num_envs=4)
```

Each Unity process listens on its own port, so several of them combine like this:

```python
from unity_vecenv import FlattenedVectorEnvThreaded, UnityVectorEnv

def make_env(i):
    return lambda: UnityVectorEnv("path/to/MyGame.exe", num_envs=16, port=50010 + i)

env = FlattenedVectorEnvThreaded([make_env(i) for i in range(4)])
```

The [Python guide](docs/python-usage.md) covers the constructor options, seeding, custom info and the ONNX tools. The [Unity guide](docs/unity-usage.md) covers scene layout, the agent API and running without Python.

## Learn more

- [Python guide](docs/python-usage.md) and [Unity guide](docs/unity-usage.md)
- [Development](docs/development.md): working on the package itself
- [PyPI](https://pypi.org/project/unity-vecenv/) and [issues](https://github.com/martkartasev/UnityVecEnv/issues)


## Author and license

Written by [Mart Kartasev](https://github.com/martkartasev). MIT, see [LICENSE](LICENSE).
