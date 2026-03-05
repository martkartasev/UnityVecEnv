# Installation

## Installing the Unity package

Install `./Unity/package.json` from Unity Package Manager using "Install from Disk", or set a relative path in your project's package manifest.

## Using the Python library

```bash
pip install -e ./python/unity_vecenv
```

Optional CUDA convenience installs:

```bash
pip install -e UnityVecEnv/python/unity_vecenv/[cuda118] --extra-index-url https://download.pytorch.org/whl/cu118
pip install -e UnityVecEnv/python/unity_vecenv/[cuda121] --extra-index-url https://download.pytorch.org/whl/cu121
```

## CLI

After installation, a CLI entrypoint is available:

```bash
unity-vecenv onnx-rename input.onnx output.onnx --unity-defaults
```

# Managing Protobuf

To change the API, regenerate both Python and C# files after editing `Protobuf/communication.proto`.

## One-command generation

From repo root:

```powershell
pwsh ./Protobuf/generate_protos.ps1
```

## Manual Python generation

Install `grpcio-tools` first:

```bash
python -m pip install grpcio-tools
```

Then in `./Protobuf`:

```bash
python -m grpc_tools.protoc -I ./ --python_out=../Python/unity_vecenv/src/unity_vecenv/protobuf_gen --pyi_out=../Python/unity_vecenv/src/unity_vecenv/protobuf_gen ./communication.proto
```

## Manual C# generation

Install `protoc` (for example via [Grpc.Tools](https://www.nuget.org/packages/Grpc.Tools)), then in `./Protobuf` run:

```bash
protoc -I ./ --csharp_out=../Unity/Runtime/Scripts/ProtobufGenerated ./communication.proto
```
