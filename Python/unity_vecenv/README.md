# UnityVecEnv

UnityVecEnv connects Python reinforcement-learning code to Unity environments
through a Gymnasium-compatible vector environment API.

## Installation

Install the Python client from PyPI:

```bash
pip install unity-vecenv
```

Install the optional ONNX export and model-renaming utilities:

```bash
pip install "unity-vecenv[onnx]"
```

For CUDA-enabled inference or export, install the appropriate PyTorch build for
your system before installing the ONNX extra.

## Basic usage

```python
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv(
    executable_path="path/to/MyGame.exe",
    num_envs=16,
    no_graphics=True,
    time_scale=10,
)

observations, info = env.reset()
observations, rewards, terminated, truncated, info = env.step(
    env.action_space.sample()
)
env.close()
```

The Unity player must contain the matching UnityVecEnv package. See the
[repository documentation](https://github.com/martkartasev/UnityVecEnv/tree/master/docs)
for Unity setup, the complete Python API, and protocol details.

## License

UnityVecEnv is available under the
[MIT License](https://github.com/martkartasev/UnityVecEnv/blob/master/LICENSE).
