param(
    [string]$ProtoFile = "communication.proto",
    [string]$PythonOut = "../Python/unity_vecenv/src/unity_vecenv/protobuf_gen",
    [string]$CSharpOut = "../Unity/Runtime/Scripts/ProtobufGenerated",
    [string]$Protoc = "protoc",
    [switch]$SkipPython,
    [switch]$SkipCSharp
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

if (-not (Test-Path $ProtoFile)) {
    throw "Proto file not found: $ProtoFile"
}

if (-not $SkipPython) {
    python -m grpc_tools.protoc -I ./ --python_out=$PythonOut --pyi_out=$PythonOut $ProtoFile
}

if (-not $SkipCSharp) {
    & $Protoc -I ./ --csharp_out=$CSharpOut $ProtoFile
}

Write-Host "Protobuf generation completed."
