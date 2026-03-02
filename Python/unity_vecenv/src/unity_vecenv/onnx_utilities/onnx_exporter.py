import os

import torch
from torch import nn

#TODO: Merge with other exporter
class UnityPolicyExport(nn.Module):
    def __init__(self, agent: nn.Module):
        super().__init__()
        self.actor = agent.actor
        self.critic = agent.critic

    def forward(self, obs: torch.Tensor):
        logits = self.actor(obs)
        action = torch.argmax(logits, dim=-1)  # baked-in argmax
        value = self.critic(obs)
        return action, value


def export_unity_onnx_simple(
    agent: nn.Module,
    obs_shape,
    out_path: str,
    opset: int = 13,
):
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)

    agent_cpu = agent.to("cpu").eval()
    export_model = UnityPolicyExport(agent_cpu).eval()

    dummy_obs = torch.zeros((1,) + tuple(obs_shape), dtype=torch.float32)

    torch.onnx.export(
        export_model,
        dummy_obs,
        out_path,
        export_params=True,
        opset_version=opset,
        do_constant_folding=True,
        input_names=["obs_continuous"],
        output_names=["action_discrete", "value"],
        dynamic_axes={
            "obs_continuous": {0: "batch"},
            "action_discrete": {0: "batch"},
            "value": {0: "batch"},
        },
    )

    print(f"✅ Exported argmax ONNX to: {out_path}")