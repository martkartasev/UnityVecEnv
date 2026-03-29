import os
from dataclasses import dataclass
from typing import Literal, Optional, Sequence

import torch
import torch.nn as nn
import torch.onnx

ActionSpaceType = Literal["continuous", "discrete"]


@dataclass(frozen=True)
class UnityModelInputSpec:
    name: str
    shape: tuple[int, ...]
    observation_key: str | None = None
    normalize_uint8: bool = False
    dtype: torch.dtype = torch.float32


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
) -> tuple[UnityModelInputSpec, ...]:
    get_specs = getattr(agent, "get_unity_export_input_specs", None)
    if callable(get_specs):
        specs = get_specs(tuple(obs_shape) if obs_shape is not None else None)
        return tuple(
            UnityModelInputSpec(
                name=spec.input_name,
                shape=tuple(spec.shape),
                observation_key=spec.observation_key,
                normalize_uint8=spec.normalize_uint8,
                dtype=spec.dtype,
            )
            for spec in specs
        )

    if obs_shape is None:
        obs_dim = _infer_obs_dim(agent)
        obs_shape = (obs_dim,)

    return (
        UnityModelInputSpec(
            name="obs_continuous",
            shape=tuple(obs_shape),
            dtype=torch.float32,
        ),
    )


def _build_dummy_inputs(
    input_specs: Sequence[UnityModelInputSpec],
    device: torch.device,
) -> tuple[torch.Tensor, ...]:
    return tuple(
        torch.zeros((1,) + tuple(spec.shape), device=device, dtype=spec.dtype)
        for spec in input_specs
    )


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
        input_specs: Sequence[UnityModelInputSpec],
        export_value: bool = True,
        clamp_actions: bool = True,
    ):
        super().__init__()
        self.agent = agent
        self.action_space_type = action_space_type
        self.input_specs = tuple(input_specs)
        self.export_value = export_value
        self.clamp_actions = clamp_actions

    def _apply_obs_mask_if_present(self, obs: torch.Tensor) -> torch.Tensor:
        apply_obs_mask = getattr(self.agent, "_apply_obs_mask", None)
        if callable(apply_obs_mask):
            return apply_obs_mask(obs)
        return obs

    def _build_observation(self, inputs: tuple[torch.Tensor, ...]):
        if len(inputs) != len(self.input_specs):
            raise ValueError(f"Expected {len(self.input_specs)} inputs, received {len(inputs)}.")

        if len(self.input_specs) == 1 and self.input_specs[0].observation_key is None:
            return self._apply_obs_mask_if_present(inputs[0])

        observation = {}
        for spec, tensor in zip(self.input_specs, inputs):
            if spec.normalize_uint8:
                tensor = tensor / 255.0
            observation[spec.observation_key or spec.name] = tensor
        return observation

    def _compute_action(self, obs):
        forward_actor = getattr(self.agent, "forward_actor_for_unity", None)
        if callable(forward_actor):
            action = forward_actor(obs)
        elif self.action_space_type == "continuous":
            action = self.agent.actor_mean(obs)
        else:
            logits = self.agent.actor(obs)
            action = torch.argmax(logits, dim=-1)

        if self.action_space_type == "continuous" and self.clamp_actions:
            action = torch.clamp(action, -1.0, 1.0)
        return action

    def _compute_value(self, obs):
        forward_value = getattr(self.agent, "forward_value_for_unity", None)
        if callable(forward_value):
            return forward_value(obs)
        return self.agent.critic(obs)

    def forward(self, *inputs: torch.Tensor):
        obs = self._build_observation(inputs)
        action = self._compute_action(obs)

        if self.export_value:
            value = self._compute_value(obs)
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

    input_specs = _build_dummy_obs(agent=agent, device=device, obs_shape=obs_shape)

    wrapper = UnityInferenceExportPolicy(
        agent=agent,
        action_space_type=action_space_type,
        input_specs=input_specs,
        export_value=export_value,
        clamp_actions=clamp_actions,
    ).to(device).eval()

    dummy_inputs = _build_dummy_inputs(input_specs=input_specs, device=device)

    action_name = "action_continuous" if action_space_type == "continuous" else "action_discrete"
    output_names = [action_name]
    input_names = [spec.name for spec in input_specs]
    dynamic_axes = {name: {0: "batch"} for name in input_names}
    dynamic_axes[action_name] = {0: "batch"}

    if export_value:
        output_names.append("value")
        dynamic_axes["value"] = {0: "batch"}

    torch.onnx.export(
        wrapper,
        dummy_inputs,
        onnx_path,
        opset_version=opset,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        do_constant_folding=True,
    )

    if was_training:
        agent.train()

    print(f"Sentis-compatible ONNX saved to: {onnx_path}")
