# UnityVecEnv

A framework for running Unity environments as [Gymnasium](https://gymnasium.farama.org/)-compatible vectorized environments for reinforcement learning. Multiple agents train in parallel inside Unity while Python drives the training loop.

## Overview

UnityVecEnv bridges Unity and Python over a local HTTP connection using Protocol Buffers. Unity runs an embedded HTTP server; Python acts as the client. Observations and actions are binary-serialized in batches for minimal overhead.

```
Python training loop
      │  HTTP + Protobuf
      ▼
UnityVectorEnv  ──────────►  Unity (CommunicatorHttpServer)
  • sends actions                 • runs physics steps
  • receives obs/rewards          • collects observations
  • Gymnasium VectorEnv API       • serializes batched results
```

## Quick Start

```python
from unity_vecenv import UnityVectorEnv

env = UnityVectorEnv(
    executable_path="path/to/MyGame.exe",
    num_envs=16,
    no_graphics=True,
    time_scale=10,
)

obs, info = env.reset()

for _ in range(1000):
    actions = env.action_space.sample()
    obs, rewards, dones, truncates, info = env.step(actions)

env.close()
```

For more detailed information, see both the [Python side](./docs/python-usage.md) and and [Unity side](./docs/unity-usage.md) documentation.

## Installation

### Unity Package

Install `./Unity/package.json` via **Package Manager → Install package from disk**, or add a relative path to your project's `manifest.json`:

```json
{
  "dependencies": {
    "com.unityvecenv": "file:../../UnityVecEnv/Unity"
  }
}
```

### Python Package

```bash
pip install -e ./Python/unity_vecenv
```

Optional GPU convenience installs:

```bash
# CUDA 11.8
pip install -e ./Python/unity_vecenv[cuda118] --extra-index-url https://download.pytorch.org/whl/cu118

# CUDA 12.1
pip install -e ./Python/unity_vecenv[cuda121] --extra-index-url https://download.pytorch.org/whl/cu121
```


See [docs/python-usage.md](docs/python-usage.md) for the full Python API reference and [docs/unity-usage.md](docs/unity-usage.md) for implementing agents in Unity.

## Managing Protobuf

If for some reason, you need have the need to change the API definition, edit `Protobuf/communication.proto`, then regenerate both Python and C# bindings.

### One-command generation (from repo root)

```bash
bash ./Protobuf/generate_protos.sh
```

### Manual — Python

```bash
pip install grpcio-tools

python -m grpc_tools.protoc \
  -I ./Protobuf \
  --python_out=./Python/unity_vecenv/src/unity_vecenv/protobuf_gen \
  --pyi_out=./Python/unity_vecenv/src/unity_vecenv/protobuf_gen \
  ./Protobuf/communication.proto
```

### Manual — C#

Install [`protoc`](https://github.com/protocolbuffers/protobuf/releases) or [Grpc.Tools](https://www.nuget.org/packages/Grpc.Tools), then:

```bash
protoc -I ./Protobuf \
  --csharp_out=./Unity/Runtime/Scripts/ProtobufGenerated \
  ./Protobuf/communication.proto
```
