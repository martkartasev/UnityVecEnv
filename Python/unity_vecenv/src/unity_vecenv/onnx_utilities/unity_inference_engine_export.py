import numpy as np
import torch
import torch.nn as nn
import os
import torch.onnx


class InferenceEnginePolicy(nn.Module):
    """
    Export-friendly wrapper:
    - Sentis / Inference Engine does not like sampling
    - deterministic action = actor_mean(obs)
    - (optional) also output value network
    """

    def __init__(self, agent: nn.Module, export_value: bool = True, clamp_actions: bool = True):
        super().__init__()
        self.agent = agent
        self.export_value = export_value
        self.clamp_actions = clamp_actions

    def forward(self, obs: torch.Tensor):
        # obs expected shape: (B, obs_dim...) already flattened by caller if needed
        obs = self.agent._apply_obs_mask(obs)

        action_mean = self.agent.actor_mean(obs)

        # If your Unity env expects [-1, 1] actions, clamp here (ClipAction does this at runtime too).
        if self.clamp_actions:
            action_mean = torch.clamp(action_mean, -1.0, 1.0)

        if self.export_value:
            value = self.agent.critic(obs)
            return action_mean, value
        return action_mean


def export_unity_onnx(agent, onnx_path: str, device: torch.device):
    os.makedirs(os.path.dirname(onnx_path), exist_ok=True)

    was_training = agent.training
    agent.eval()
    wrapper = InferenceEnginePolicy(agent, export_value=True, clamp_actions=True).to(device).eval()

    dummy_obs = torch.zeros(1, agent.obs_dim, device=device, dtype=torch.float32)

    #TODO: Write a proper exporter that supports different space types. Merge with other exporter.
    torch.onnx.export(
        wrapper,
        (dummy_obs,),
        onnx_path,
        opset_version=15,
        input_names=["obs_continuous"],
        output_names=["action_continuous", "value"],
        dynamic_axes={
            "obs_continuous": {0: "batch"},
            "action_continuous": {0: "batch"},
            "value": {0: "batch"},
        },
        do_constant_folding=True,
    )

    if was_training:
        agent.train()
    print(f"Sentis-compatible ONNX saved to: {onnx_path}")
