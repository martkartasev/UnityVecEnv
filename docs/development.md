# Development

Everything here is for working on UnityVecEnv itself. Using it needs only the [README](../README.md).

## Python package

From the repository root, install the checkout in editable mode with the `dev` and `onnx` extras, then run the tests and the linter:

```bash
pip install -e "./Python/unity_vecenv[dev,onnx]"
python -m pytest Python/unity_vecenv/tests
ruff check Python/unity_vecenv
```

## Unity package

In a Unity project, choose **Package Manager > + > Install package from disk** and select `Unity/package.json`, or reference the checkout from the project's `Packages/manifest.json`. The path is relative to the `Packages` folder; this example assumes the checkout sits next to the Unity project:

```json
"com.mka.gymvecenv": "file:../../UnityVecEnv/Unity"
```

## Regenerating the protobuf bindings

The wire format is defined in `Protobuf/communication.proto`. After editing it, regenerate both bindings. The Python half needs `grpcio-tools` in the active Python environment (`pip install grpcio-tools`); the C# half needs `protoc` on the path (see the [protobuf releases](https://github.com/protocolbuffers/protobuf/releases)):

```bash
bash ./Protobuf/generate_protos.sh
```

`--help` lists the options: another `.proto` file, another `protoc`, other output directories, or skipping one of the two languages. The generated files live in `Python/unity_vecenv/src/unity_vecenv/protobuf_gen` and `Unity/Runtime/Scripts/ProtobufGenerated`.

## Releasing

Set the same version in `Python/unity_vecenv/pyproject.toml` and `Unity/package.json`, commit, and push a tag `vX.Y.Z`. The publish workflow refuses a tag unless it has that form and both files carry exactly `X.Y.Z`; it then builds the Python package, runs the tests against the built wheel and uploads it to PyPI. The Unity package is installed straight from the tag.
