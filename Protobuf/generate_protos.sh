#!/usr/bin/env bash

set -euo pipefail

proto_file="communication.proto"
python_out="../Python/unity_vecenv/src/unity_vecenv/protobuf_gen"
csharp_out="../Unity/Runtime/Scripts/ProtobufGenerated"
protoc_bin="protoc"
skip_python=false
skip_csharp=false

usage() {
  cat <<'EOF'
Usage: ./generate_protos.sh [options]

Options:
  --proto-file PATH   Path to the .proto file (default: communication.proto)
  --python-out PATH   Python output directory
                      (default: ../Python/unity_vecenv/src/unity_vecenv/protobuf_gen)
  --csharp-out PATH   C# output directory (default: ../Unity/Runtime/Scripts/ProtobufGenerated)
  --protoc BIN        Protoc executable name/path (default: protoc)
  --skip-python       Skip Python generation
  --skip-csharp       Skip C# generation
  -h, --help          Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --proto-file)
      proto_file="$2"
      shift 2
      ;;
    --python-out)
      python_out="$2"
      shift 2
      ;;
    --csharp-out)
      csharp_out="$2"
      shift 2
      ;;
    --protoc)
      protoc_bin="$2"
      shift 2
      ;;
    --skip-python)
      skip_python=true
      shift
      ;;
    --skip-csharp)
      skip_csharp=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"

if [[ ! -f "$proto_file" ]]; then
  echo "Proto file not found: $proto_file" >&2
  exit 1
fi

if [[ "$skip_python" != true ]]; then
  python -m grpc_tools.protoc -I ./ --python_out="$python_out" --pyi_out="$python_out" "$proto_file"
fi

if [[ "$skip_csharp" != true ]]; then
  "$protoc_bin" -I ./ --csharp_out="$csharp_out" "$proto_file"
fi

echo "Protobuf generation completed."
