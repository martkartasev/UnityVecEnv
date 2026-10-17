# UnityVecEnv

Use a Unity scene as a reinforcement-learning environment for Python. UnityVecEnv implements Gymnasium's [`VectorEnv`](https://gymnasium.farama.org/api/vector/) interface: many copies of your agent run at once inside Unity while a Python training loop sends actions and receives what each agent sees, its reward and whether its episode has ended.

This package is the Python half. The Unity half is a Unity package from the same
[repository](https://github.com/martkartasev/UnityVecEnv); install both at the same version.

## Highlights

- Hundreds of agents in one Unity process, and several processes can be treated as one bigger environment.
- A standard Gymnasium `VectorEnv`, so existing Gymnasium-based trainers work unchanged.
- Observations as numbers, integer choices or camera images. Agents can report extra values, and Python can pass settings to the scene when it starts.
- Save a trained PyTorch network as ONNX and run it inside Unity without Python, through Unity's Inference Engine.

## Installation

```bash
pip install unity-vecenv
pip install "unity-vecenv[onnx]"    # optional: onnx and torch, for ONNX export and unity-vecenv onnx-rename
```

| Requirement | Version |
|---|---|
| Python | 3.10 or newer |
| Gymnasium | 1.2 or newer, below 2.0 |
| Unity Editor | 6000.0 or newer |
| Unity package `com.mka.gymvecenv` | the same version as this package, for example `0.1.9` with tag `v0.1.9` |

Install the Unity package through **Window > Package Manager > + > Install package from git URL** with the URL of the matching tag, for example

```text
https://github.com/martkartasev/UnityVecEnv.git?path=/Unity#v0.1.9
```

or add that URL under `"dependencies"` in `Packages/manifest.json` as `"com.mka.gymvecenv"`. The project is in alpha, so install a specific version on both sides, for example `pip install unity-vecenv==0.1.9`. Published versions are listed under [tags](https://github.com/martkartasev/UnityVecEnv/tags).

## Usage

The scene needs one C# class that extends `GymAgent`, attached to an object, with its observation and action sizes set in the Inspector; the [Unity guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/unity-usage.md) shows the five methods to implement. Build the project into a player, then drive it from Python. `num_envs` is how many copies of the agent Unity should run:

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

## Learn more

- [Python guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/python-usage.md): constructor options, seeding, custom info, the ONNX tools
- [Unity guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/unity-usage.md): scene layout, the agent API, running without Python
- [Issues](https://github.com/martkartasev/UnityVecEnv/issues)

## Author and license

Written by [Mart Kartasev](https://github.com/martkartasev). MIT, see
[LICENSE](https://github.com/martkartasev/UnityVecEnv/blob/master/LICENSE).
