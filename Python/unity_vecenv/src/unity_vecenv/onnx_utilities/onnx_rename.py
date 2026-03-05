from __future__ import annotations

from pathlib import Path
from typing import Mapping

import onnx

DEFAULT_UNITY_RENAMES: dict[str, str] = {
    "obs": "obs_continuous",
    "action": "action_discrete",
    "action_mean": "action_continuous",
}


def rename_tensor(model: onnx.ModelProto, old_name: str, new_name: str) -> onnx.ModelProto:
    graph = model.graph

    for node in graph.node:
        node.input[:] = [new_name if x == old_name else x for x in node.input]
        node.output[:] = [new_name if x == old_name else x for x in node.output]

    for tensor in graph.input:
        if tensor.name == old_name:
            tensor.name = new_name

    for tensor in graph.output:
        if tensor.name == old_name:
            tensor.name = new_name

    for initializer in graph.initializer:
        if initializer.name == old_name:
            initializer.name = new_name

    for value_info in graph.value_info:
        if value_info.name == old_name:
            value_info.name = new_name

    return model


def rename_onnx_tensors(
    input_path: str | Path,
    output_path: str | Path,
    renames: Mapping[str, str],
    *,
    validate: bool = True,
) -> Path:
    source = Path(input_path)
    destination = Path(output_path)

    model = onnx.load(str(source))
    for old_name, new_name in renames.items():
        model = rename_tensor(model, old_name, new_name)

    if validate:
        onnx.checker.check_model(model)

    destination.parent.mkdir(parents=True, exist_ok=True)
    onnx.save(model, str(destination))
    return destination
