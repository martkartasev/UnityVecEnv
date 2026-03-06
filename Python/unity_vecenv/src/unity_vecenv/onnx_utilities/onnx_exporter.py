from typing import Optional, Sequence

import torch
from torch import nn

from unity_vecenv.onnx_utilities.unity_inference_engine_export import export_unity_onnx


def export_unity_onnx_simple(
    agent: nn.Module,
    obs_shape: Sequence[int],
    out_path: str,
    opset: int = 13,
    device: "torch.device | str" = "cpu",
    action_space_type: Optional[str] = None,
):
    """Backward-compatible wrapper over unified exporter."""
    export_unity_onnx(
        agent=agent,
        onnx_path=out_path,
        device=device,
        obs_shape=obs_shape,
        action_space_type=action_space_type,
        export_value=True,
        clamp_actions=True,
        opset=opset,
    )
