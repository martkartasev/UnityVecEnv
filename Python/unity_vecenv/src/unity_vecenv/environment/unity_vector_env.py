import subprocess
from typing import Optional

import numpy as np
from gymnasium import spaces
from gymnasium.vector import VectorEnv, AutoresetMode

from unity_vecenv.environment.network_utils import is_port_in_use
from unity_vecenv.environment.unity_client import start_client
from unity_vecenv.environment.unity_process import start_unity_process
from unity_vecenv.protobuf_gen.communication_pb2 import ResetParameters, Reset, Observations, Step, Action, StepResults, InitializeEnvironments, AutoResetMode


class UnityVectorEnv(VectorEnv):

    def __init__(self,
                 start_process: bool = True,
                 no_graphics: bool = True,
                 time_scale=10,
                 physics_steps_per_action: int = 10,
                 port: int = 50010,
                 num_envs: int = 1):
        super(UnityVectorEnv, self).__init__()

        self.metadata = {
            "autoreset_mode": AutoresetMode.NEXT_STEP,
            "num_envs": num_envs,
            "time_scale": time_scale,
            "physics_steps_per_action": physics_steps_per_action,
        }
        self.time_scale = time_scale
        self.physics_steps_per_action = physics_steps_per_action
        self.port = port
        while (start_process and is_port_in_use(self.port) or
               (not start_process and not is_port_in_use(self.port))):
            self.port += 1

        self.process = start_unity_process("", port=self.port, nr_agents=num_envs, no_graphics=no_graphics, timescale=self.time_scale) if start_process else None
        self.client = start_client(port=self.port)

        environment_description = self.initialize_environment(num_envs)
        if environment_description.trueNumberOfEnvs == 0:
            raise RuntimeError("Failed to initialize environment connection. Number of envs returns 0.")

        self.num_envs = environment_description.trueNumberOfEnvs

        self.single_action_space = spaces.Box(low=-1, high=1, shape=(environment_description.singleActionSpace.continuousSize,), dtype=np.float32)
        self.single_observation_space = spaces.Box(low=-1, high=1, shape=(environment_description.singleObservationSpace.continuousSize,), dtype=np.float32)
        # TODO: Ranges ?
        self.action_space = spaces.Box(low=-1, high=1, shape=(self.num_envs, environment_description.singleActionSpace.continuousSize), dtype=np.float32)
        self.observation_space = spaces.Box(low=-1, high=1, shape=(self.num_envs, environment_description.singleObservationSpace.continuousSize), dtype=np.float32)

    def initialize_environment(self, num_envs):
        init = InitializeEnvironments()
        init.autoResetMode = AutoResetMode.NextStep
        init.requestedNumberOfEnvs = num_envs
        environment_description = self.client.initialize(init)
        return environment_description

    def reset(self, seed: Optional[int] = None, options: Optional[dict] = None):
        reset_msg = Reset()
        reset_msg.reloadScene = False

        if options is not None:
            agent_inits = options["init"]
            for i in range(self.num_envs):
                reset_msg.envsToReset.append(self.map_reset_params_to_proto(i, agent_inits[i, :]))

        reset = self.client.reset(reset_msg)
        obs = self.reset_result_to_numpy(reset, self.num_envs)
        return obs, None

    def step(self, action):
        action_msg = self.map_action_to_proto(action)
        action_msg.stepCount = self.physics_steps_per_action
        action_msg.timeScale = self.time_scale
        step_result = self.client.step(action_msg)

        (obs, dones, truncates, rewards, info) = self.step_result_to_numpy(step_result)
        return obs, rewards, dones, truncates, info  # TODO see if info is worth keeping

    def render(self, mode='human'):
        pass
        # TODO: Screenshot manager back into API

    def close(self):
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()

    def map_reset_params_to_proto(self, i, initialization):
        params = ResetParameters()
        params.index = i
        params.continuous.extend(initialization)
        return params

    def reset_result_to_numpy(self, results: Observations, nr_agents):
        obs = np.zeros((nr_agents, self.single_observation_space.shape[0]))
        for i, observation in enumerate(results.observations):
            obs[i, :] = np.array(observation.continuous, dtype=np.float32)
        return obs

    def step_result_to_numpy(self, results: StepResults):
        obs = np.zeros((self.num_envs, self.single_observation_space.shape[0]))
        dones = np.zeros(self.num_envs)
        truncates = np.zeros(self.num_envs)
        rewards = np.zeros(self.num_envs)
        for i, result in enumerate(results.stepResults):
            obs[i, :] = np.array(result.observation.continuous, dtype=np.float32)
            dones[i] = result.done
            truncates[i] = result.truncated
            rewards[i] = result.reward

        # --- Parse infos (Gymnasium-like) ---
        info = {}

        # defaults: vector-env-style lists aligned with agent index
        final_info = [None] * self.num_envs
        final_observation = [None] * self.num_envs

        # Step-level custom info (global array)
        if hasattr(results, "infos") and results.infos is not None:
            # final_infos: per-agent terminal episode stats
            for fi in results.infos.final_infos:
                idx = int(fi.agentIndex)
                if 0 <= idx < self.num_envs:
                    d = {
                        "episode": {
                            "r": float(fi.episode_reward),
                            "l": float(fi.episode_length),
                        }
                    }
                    # keep custom if present
                    if len(fi.custom) > 0:
                        d["custom"] = np.asarray(fi.custom, dtype=np.float32)
                    final_info[idx] = d

            # final_observations: terminal obs (before reset), indexed by Observation.index
            for fo in results.infos.final_observations:
                idx = int(fo.index)
                if 0 <= idx < self.num_envs:
                    # If you also have discrete obs, you may want to store both.
                    final_observation[idx] = np.asarray(fo.continuous, dtype=np.float32)

            # custom: global custom floats on this step
            if len(results.infos.custom) > 0:
                info["custom"] = np.asarray(results.infos.custom, dtype=np.float32)
            else:
                info["custom"] = np.zeros((0,), dtype=np.float32)

        # only include keys if something actually happened (optional, but common)
        if any(x is not None for x in final_info):
            info["final_info"] = final_info
        if any(x is not None for x in final_observation):
            info["final_observation"] = final_observation

        return obs, dones, truncates, rewards, info

    def map_action_to_proto(self, action):
        step = Step()
        for i in range(self.num_envs):
            action_msg = Action()
            action_msg.continuous.extend(action[i, :])  #

            step.actions.append(action_msg)
        return step
