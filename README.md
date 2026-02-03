# Installation

## Installing the Unity package

Install the "./Unity/Package.json" from the Unity Package Manager using "Install from Disk" or by defining a relative path from the project's package manifest.

## Using the python library
```
pip install -e ./python/unity_vecenv
```


# Managing Protobuf

---

## Python

To change the API you need to regenerate the C# and Python files after updating the proto file.

To compile protos for python, install grpcio-tools https://grpc.io/docs/languages/python/quickstart/

```
python -m pip install grpcio-tools
```

Then in the [Protobuf folder](./Protobuf), run:

```
conda activate SMARCRL
python -m grpc_tools.protoc -I ./ --python_out=../Python/unity_vecenv/src/unity_vecenv/protobuf_gen --pyi_out=../Python/unity_vecenv/src/unity_vecenv/protobuf_gen  ./communication.proto
```
## C#

To compile protos for C#, I suggest downloading the tools package https://www.nuget.org/packages/Grpc.Tools
Extracting the correct binary inside the "Tools" folder in the package, and adding it to your "PATH" environment
variables.
For windows, I also suggest renaming "grpc_csharp_plugin.exe" to "protoc-gen-grpc_csharp.exe". Allows using the plugin
more easily.

The commands corresponding for c# compilation are then as follows:

Then in the [Protobuf folder](./Protobuf), run:

```
protoc -I ./ --csharp_out=../Unity/Runtime/Scripts/ProtobufGenerated  ./communication.proto
```


