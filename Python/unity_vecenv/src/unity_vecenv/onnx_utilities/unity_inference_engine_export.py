import os
from typing import Literal, Optional, Sequence

import torch
import torch.nn as nn
import torch.onnx

ActionSpaceType = Literal["continuous", "discrete"]


def _infer_obs_dim(agent: nn.Module) -> int:
    if hasattr(agent, "obs_dim"):
        return int(agent.obs_dim)

    for attr in ("actor_mean", "actor", "critic"):
        module = getattr(agent, attr, None)
        if module is None:
            continue
        first_linear = next((m for m in module.modules() if isinstance(m, nn.Linear)), None)
        if first_linear is not None:
            return int(first_linear.in_features)

    raise ValueError(
        "Could not infer observation dimension from agent. "
        "Provide obs_shape explicitly when exporting."
    )


def _build_dummy_obs(
    agent: nn.Module,
    device: torch.device,
    obs_shape: Optional[Sequence[int]] = None,
) -> torch.Tensor:
    if obs_shape is None:
        obs_dim = _infer_obs_dim(agent)
        return torch.zeros(1, obs_dim, device=device, dtype=torch.float32)
    return torch.zeros((1,) + tuple(obs_shape), device=device, dtype=torch.float32)


def _infer_action_space_type(agent: nn.Module) -> ActionSpaceType:
    if hasattr(agent, "actor_mean"):
        return "continuous"
    if hasattr(agent, "actor"):
        return "discrete"
    raise ValueError(
        "Could not infer action-space type from agent. "
        "Expected 'actor_mean' (continuous) or 'actor' (discrete)."
    )


class UnityInferenceExportPolicy(nn.Module):
    """
    Export-friendly wrapper for Unity Sentis:
    - continuous policies: deterministic action = actor_mean(obs)
    - discrete policies: deterministic action = argmax(actor_logits(obs))
    - optional critic output
    """

    def __init__(
        self,
        agent: nn.Module,
        action_space_type: ActionSpaceType,
        export_value: bool = True,
        clamp_actions: bool = True,
    ):
        super().__init__()
        self.agent = agent
        self.action_space_type = action_space_type
        self.export_value = export_value
        self.clamp_actions = clamp_actions

    def _apply_obs_mask_if_present(self, obs: torch.Tensor) -> torch.Tensor:
        apply_obs_mask = getattr(self.agent, "_apply_obs_mask", None)
        if callable(apply_obs_mask):
            return apply_obs_mask(obs)
        return obs

    def forward(self, obs: torch.Tensor):
        obs = self._apply_obs_mask_if_present(obs)

        if self.action_space_type == "continuous":
            action = self.agent.actor_mean(obs)
            if self.clamp_actions:
                action = torch.clamp(action, -1.0, 1.0)
        else:
            logits = self.agent.actor(obs)
            action = torch.argmax(logits, dim=-1)

        if self.export_value:
            value = self.agent.critic(obs)
            return action, value
        return action


def export_unity_onnx(
    agent: nn.Module,
    onnx_path: str,
    device: "torch.device | str" = "cpu",
    obs_shape: Optional[Sequence[int]] = None,
    action_space_type: Optional[ActionSpaceType] = None,
    export_value: bool = True,
    clamp_actions: bool = True,
    opset: int = 15,
):
    out_dir = os.path.dirname(onnx_path) or "."
    os.makedirs(out_dir, exist_ok=True)

    device = torch.device(device)
    action_space_type = action_space_type or _infer_action_space_type(agent)

    was_training = agent.training
    agent.to(device).eval()

    wrapper = UnityInferenceExportPolicy(
        agent=agent,
        action_space_type=action_space_type,
        export_value=export_value,
        clamp_actions=clamp_actions,
    ).to(device).eval()

    dummy_obs = _build_dummy_obs(agent=agent, device=device, obs_shape=obs_shape)

    action_name = "action_continuous" if action_space_type == "continuous" else "action_discrete"
    output_names = [action_name]
    dynamic_axes = {
        "obs_continuous": {0: "batch"},
        action_name: {0: "batch"},
    }

    if export_value:
        output_names.append("value")
        dynamic_axes["value"] = {0: "batch"}

    torch.onnx.export(
        wrapper,
        (dummy_obs,),
        onnx_path,
        opset_version=opset,
        input_names=["obs_continuous"],
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        do_constant_folding=True,
    )

    if was_training:
        agent.train()

    print(f"Sentis-compatible ONNX saved to: {onnx_path}")
