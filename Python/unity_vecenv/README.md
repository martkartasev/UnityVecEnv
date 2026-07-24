# UnityVecEnv

UnityVecEnv connects Unity environments to Python reinforcement-learning code
through a [Gymnasium](https://gymnasium.farama.org/)-compatible vector
environment API.

> **Development status: Alpha.** UnityVecEnv is under active development. APIs,
> the communication protocol, and package structure may change between minor
> releases. Pin exact versions, use matching Python and Unity package versions,
> and evaluate it carefully before using it in production-critical workloads.

## Features

- A standard Gymnasium `VectorEnv` interface for Unity environments.
- Many agents batched inside one Unity process.
- Multiple Unity processes exposed as one environment with
  `FlattenedVectorEnvThreaded`.
- Continuous, discrete, and mixed action/observation spaces, plus visual
  observations.
- Gymnasium `NEXT_STEP` and `SAME_STEP` autoreset modes.
- Typed per-agent info values and run-level environment parameters.
- Optional ONNX export and Unity tensor-renaming utilities.

## How it works

```text
Python training loop
        |
        | Gymnasium VectorEnv API
        v
unity-vecenv Python client
        |
        | localhost HTTP + Protocol Buffers
        v
Unity Editor or built player
        |
        v
GymVecEnvManager + GymAgent instances
```

Unity runs the simulation and an embedded local HTTP server. Python sends
batched actions and receives observations, rewards, termination flags, and
custom info values.

## Requirements

| Component | Supported version |
|---|---|
| Python | 3.10 or newer |
| Unity Editor | Unity 6 (6000.0) baseline |
| Gymnasium | 1.2 or newer, below 2.0 |
| Unity Inference Engine | 2.4.1, installed automatically by the Unity package |

During the Alpha phase, use the same release number for both packages. For
example, Python package `0.1.9` should be paired with Unity tag `v0.1.9`.

## Installation

### Python client

```bash
pip install unity-vecenv
```

This installs only the Python client. It does not install the Unity Editor, the
Unity package, or a built Unity environment.

Install the optional ONNX export and model-renaming utilities with:

```bash
pip install "unity-vecenv[onnx]"
```

For CUDA-enabled inference or export, install the appropriate PyTorch build for
your system before installing the ONNX extra.

### Unity package

In Unity, select **Window > Package Manager > + > Install package from git
URL**, or add the package to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.mka.gymvecenv": "https://github.com/martkartasev/UnityVecEnv.git?path=/Unity#vX.Y.Z"
  }
}
```

Replace `X.Y.Z` with the version installed in Python. Tagged releases are listed
on the [GitHub Releases](https://github.com/martkartasev/UnityVecEnv/releases)
page.

Your Unity scene needs a `GymAgent` subclass. The `GymVecEnvManager` is created
automatically at runtime. See the
[Unity setup guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/unity-usage.md)
for agent implementation and scene configuration.

## Usage

### Launch a built Unity environment

```python
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv(
    executable_path="path/to/MyGame.exe",
    num_envs=16,
    no_graphics=True,
    time_scale=10,
)

try:
    observations, info = env.reset()

    for _ in range(1_000):
        actions = env.action_space.sample()
        observations, rewards, terminated, truncated, info = env.step(actions)
finally:
    env.close()
```

### Connect to the Unity Editor

Start the configured scene in Play Mode, then connect to its listening port
without starting another Unity process:

```python
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv(
    start_process=False,
    port=50010,
    num_envs=4,
)
```

## Documentation and support

- [Python usage guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/python-usage.md)
- [Unity setup guide](https://github.com/martkartasev/UnityVecEnv/blob/master/docs/unity-usage.md)
- [Release notes](https://github.com/martkartasev/UnityVecEnv/releases)
- [Issue tracker](https://github.com/martkartasev/UnityVecEnv/issues)

## License

UnityVecEnv is available under the
[MIT License](https://github.com/martkartasev/UnityVecEnv/blob/master/LICENSE).
