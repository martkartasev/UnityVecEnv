import subprocess
from typing import Any, Dict, Optional

import numpy as np
from gymnasium import spaces
from gymnasium.vector import VectorEnv, AutoresetMode

from unity_vecenv.environment.spaces import (
    _safe_key,
    batch_space,
    is_empty_placeholder_space,
    space_from_repeated,
    visual_spaces_from_repeated,
)
from unity_vecenv.environment.network_utils import is_port_in_use
from unity_vecenv.environment.unity_client import start_client
from unity_vecenv.environment.unity_process import start_unity_process
from unity_vecenv.protobuf_gen.communication_pb2 import (
    ResetParameters,
    Reset,
    BatchedResetResults,
    Step,
    Action,
    BatchedStepResults,
    InitializeEnvironments,
    AutoResetMode,
)

_FLOAT32_LE = np.dtype("<f4")
_INT32_LE = np.dtype("<i4")


class UnityVectorEnv(VectorEnv):

    def __init__(self,
                 executable_path: Optional[str] = None,
                 start_process: bool = True,
                 no_graphics: bool = True,
                 batch_mode: bool = True,
                 time_scale=10,
                 physics_steps_per_action: int = 10,
                 port: int = 50010,
                 num_envs: int = 1,
                 scene_load: str = "",
                 log_file: str = ""):
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

        self.process = start_unity_process(executable_path, scene_load=scene_load, port=self.port, nr_agents=num_envs, batch_mode=batch_mode, no_graphics=no_graphics, timescale=self.time_scale, log_file=log_file) if start_process else None
        self.client = start_client(port=self.port)

        environment_description = self.initialize_environment(num_envs)
        if environment_description.trueNumberOfEnvs == 0:
            raise RuntimeError("Failed to initialize environment connection. Number of envs returns 0.")

        self.num_envs = int(environment_description.trueNumberOfEnvs)

        self.single_action_space = space_from_repeated(
            environment_description.singleActionSpace,
            prefix="action",
            dtype=np.float32,
            default_low=-1.0,
            default_high=1.0
        )

        self.single_observation_space = space_from_repeated(
            environment_description.singleObservationSpace,
            prefix="obs",
            dtype=np.float32,
            default_low=-1.0,
            default_high=1.0
        )
        self._state_observation_space = self.single_observation_space
        self._visual_observation_specs = self._build_visual_spec_lookup(
            environment_description.singleVisualObservationSpace
        )
        self._visual_observation_spaces = visual_spaces_from_repeated(
            environment_description.singleVisualObservationSpace,
            prefix="visual",
        )
        self.single_observation_space = self._combine_observation_space(
            self._state_observation_space,
            self._visual_observation_spaces,
        )

        self.action_space = batch_space(self.single_action_space, self.num_envs)
        self.observation_space = batch_space(self.single_observation_space, self.num_envs)

    def initialize_environment(self, num_envs):
        init = InitializeEnvironments()
        init.autoResetMode = AutoResetMode.NextStep
        init.requestedNumberOfEnvs = num_envs
        environment_description = self.client.initialize(init)
        return environment_description

    def reset(self, seed: Optional[int] = None, options: Optional[dict] = None):
        reset_msg = Reset()
        reset_msg.reloadScene = False

        if options is not None:  # TODO: Proper seeding and options passing
            agent_inits = options["init"]
            for i in range(self.num_envs):
                reset_msg.envsToReset.append(self.map_reset_params_to_proto(i, agent_inits[i, :]))

        reset = self.client.reset(reset_msg)
        obs, info = self.reset_result_to_numpy(reset, self.num_envs)
        return obs, info

    def step(self, action):
        action_msg = self.map_action_to_proto(action)
        action_msg.stepCount = self.physics_steps_per_action
        action_msg.timeScale = self.time_scale
        step_result = self.client.step(action_msg)

        (obs, dones, truncates, rewards, info) = self.step_result_to_numpy(step_result)
        return obs, rewards, dones, truncates, info  # TODO see if info is worth keeping

    def render(self, mode='human'):
        pass
        # TODO: Screenshot/Video manager back into API

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

    def _decode_float_buffer(self, payload: bytes, expected_size: int, field_name: str) -> np.ndarray:
        arr = np.frombuffer(payload, dtype=_FLOAT32_LE)
        if arr.size != expected_size:
            raise RuntimeError(f"{field_name} has {arr.size} float32 values, expected {expected_size}.")
        return arr

    def _decode_int_buffer(self, payload: bytes, expected_size: int, field_name: str) -> np.ndarray:
        arr = np.frombuffer(payload, dtype=_INT32_LE)
        if arr.size != expected_size:
            raise RuntimeError(f"{field_name} has {arr.size} int32 values, expected {expected_size}.")
        return arr

    def _decode_bool_buffer(self, payload: bytes, expected_size: int, field_name: str) -> np.ndarray:
        arr = np.frombuffer(payload, dtype=np.bool_)
        if arr.size != expected_size:
            raise RuntimeError(f"{field_name} has {arr.size} boolean values, expected {expected_size}.")
        return arr.copy()

    def _decode_sparse_indices(self, payload: bytes, nr_agents: int, field_name: str) -> np.ndarray:
        if len(payload) % _INT32_LE.itemsize != 0:
            raise RuntimeError(f"{field_name} payload length {len(payload)} is not divisible by {_INT32_LE.itemsize}.")

        indices = self._decode_int_buffer(
            payload,
            len(payload) // _INT32_LE.itemsize,
            field_name,
        ).copy()
        if np.any(indices < 0) or np.any(indices >= nr_agents):
            raise RuntimeError(f"{field_name} contains indices outside [0, {nr_agents}).")
        if indices.size != np.unique(indices).size:
            raise RuntimeError(f"{field_name} contains duplicate indices.")
        return indices

    def _decode_columnar_custom(self, custom_payload, indices: np.ndarray, nr_agents: int, field_name: str):
        keys = np.asarray(custom_payload.keys, dtype=object)
        num_rows = int(indices.size)
        num_keys = int(keys.size)

        full_values = np.zeros((nr_agents, num_keys), dtype=np.float32)
        full_present = np.zeros((nr_agents, num_keys), dtype=np.bool_)
        full_mask = np.zeros((nr_agents,), dtype=np.bool_)

        if num_rows == 0:
            if num_keys != 0 or len(custom_payload.values_f32) != 0 or len(custom_payload.present) != 0:
                raise RuntimeError(f"{field_name} has payload data but no indices.")
            return keys, full_values, full_present, full_mask

        if num_keys == 0:
            raise RuntimeError(f"{field_name} has {num_rows} rows but no keys.")

        values = self._decode_float_buffer(
            custom_payload.values_f32,
            num_rows * num_keys,
            f"{field_name}.values_f32",
        ).reshape((num_rows, num_keys)).copy()
        present = self._decode_bool_buffer(
            custom_payload.present,
            num_rows * num_keys,
            f"{field_name}.present",
        ).reshape((num_rows, num_keys))

        full_values[indices] = values
        full_present[indices] = present
        full_mask[indices] = np.any(present, axis=1)
        return keys, full_values, full_present, full_mask

    def _populate_custom_info(self, info: Dict[str, Any], custom_keys, custom_values, custom_present):
        for column, raw_key in enumerate(custom_keys.tolist()):
            key = str(raw_key)
            mask_key = f"_{key}"
            if key.startswith("_"):
                raise RuntimeError(f"Custom info key '{key}' cannot start with '_' because that prefix is reserved for masks.")
            if key in info or mask_key in info:
                raise RuntimeError(f"Custom info key '{key}' collides with an existing info field.")

            present = custom_present[:, column].copy()
            info[key] = custom_values[:, column].copy()
            info[mask_key] = present

    def _build_visual_spec_lookup(self, visual_space_protos):
        lookup = {}
        used = set()
        for i, sp in enumerate(visual_space_protos):
            key = _safe_key(getattr(sp, "name", None), "visual", i)
            base = key
            suffix = 1
            while key in used:
                key = f"{base}_{suffix}"
                suffix += 1
            used.add(key)
            lookup[key] = sp
        return lookup

    def _combine_observation_space(self, state_space, visual_spaces):
        if not visual_spaces:
            return state_space

        combined = {}
        if state_space is not None and not is_empty_placeholder_space(state_space):
            combined["state"] = state_space
        combined.update(visual_spaces)

        if len(combined) == 1:
            return next(iter(combined.values()))

        return spaces.Dict(combined)

    def _observation_to_numpy(self, observation):
        continuous = np.asarray(observation.continuous, dtype=np.float32)
        discrete = np.asarray(observation.discrete, dtype=np.int32)
        sos = self.single_observation_space

        if isinstance(sos, spaces.Box):
            return continuous

        if isinstance(sos, spaces.Discrete):
            if discrete.size != 1:
                raise RuntimeError(f"Expected 1 discrete observation value, got {discrete.size}.")
            return int(discrete[0])

        if isinstance(sos, spaces.MultiDiscrete) or isinstance(sos, spaces.MultiBinary):
            return discrete

        if isinstance(sos, spaces.Dict):
            out = {}
            if "continuous" in sos.spaces:
                out["continuous"] = continuous
            if "discrete" in sos.spaces:
                discrete_space = sos.spaces["discrete"]
                if isinstance(discrete_space, spaces.Discrete):
                    if discrete.size != 1:
                        raise RuntimeError(f"Expected 1 discrete observation value, got {discrete.size}.")
                    out["discrete"] = int(discrete[0])
                else:
                    out["discrete"] = discrete
            return out

        return continuous

    def _batched_scalar_observation_to_numpy(self, observation, nr_agents, scalar_space):
        num_envs = int(observation.num_envs or nr_agents)
        if num_envs != nr_agents:
            raise RuntimeError(f"Batched observation reports {num_envs} envs, expected {nr_agents}.")

        continuous = None
        if observation.continuous_size > 0:
            continuous = self._decode_float_buffer(
                observation.continuous_f32,
                nr_agents * int(observation.continuous_size),
                "observation.continuous_f32",
            ).reshape((nr_agents, int(observation.continuous_size))).copy()

        discrete = None
        if observation.discrete_size > 0:
            discrete = self._decode_int_buffer(
                observation.discrete_i32,
                nr_agents * int(observation.discrete_size),
                "observation.discrete_i32",
            ).reshape((nr_agents, int(observation.discrete_size))).copy()

        sos = scalar_space
        if isinstance(sos, spaces.Box):
            if continuous is None:
                return np.empty((nr_agents, 0), dtype=sos.dtype)
            return continuous.astype(sos.dtype, copy=False)

        if isinstance(sos, spaces.Discrete):
            if discrete is None or discrete.shape[1] != 1:
                raise RuntimeError("Expected one discrete value per environment in batched observation.")
            return discrete[:, 0]

        if isinstance(sos, spaces.MultiDiscrete) or isinstance(sos, spaces.MultiBinary):
            if discrete is None:
                raise RuntimeError("Expected discrete batched observation payload.")
            return discrete

        if isinstance(sos, spaces.Dict):
            out = {}
            if "continuous" in sos.spaces:
                cont_space = sos.spaces["continuous"]
                if continuous is None:
                    out["continuous"] = np.empty((nr_agents, 0), dtype=cont_space.dtype)
                else:
                    out["continuous"] = continuous.astype(cont_space.dtype, copy=False)
            if "discrete" in sos.spaces:
                discrete_space = sos.spaces["discrete"]
                if discrete is None:
                    raise RuntimeError("Expected discrete batched observation payload.")
                if isinstance(discrete_space, spaces.Discrete):
                    if discrete.shape[1] != 1:
                        raise RuntimeError("Expected one discrete value per environment in batched observation.")
                    out["discrete"] = discrete[:, 0]
                else:
                    out["discrete"] = discrete
            return out

        raise TypeError(f"Unsupported single_observation_space: {type(sos)}")

    def _decode_visual_observations_to_numpy(self, observation, nr_agents):
        if not self._visual_observation_specs:
            if len(observation.visual) != 0:
                raise RuntimeError("Received visual observations but no visual observation spaces were negotiated.")
            return {}

        payload_lookup = {}
        for visual in observation.visual:
            key = str(visual.name)
            if key in payload_lookup:
                raise RuntimeError(f"Duplicate visual observation payload for '{key}'.")
            payload_lookup[key] = visual

        decoded = {}
        for key, spec in self._visual_observation_specs.items():
            if key not in payload_lookup:
                raise RuntimeError(f"Missing visual observation payload for '{key}'.")

            dtype = np.float32 if int(spec.dataType) == 1 else np.uint8
            shape = tuple(int(s) for s in spec.shape)
            expected = int(np.prod(shape, dtype=np.int64)) * nr_agents
            payload = bytes(payload_lookup[key].data)
            arr = np.frombuffer(payload, dtype=dtype)
            if arr.size != expected:
                raise RuntimeError(
                    f"Visual observation '{key}' has {arr.size} values, expected {expected}."
                )
            decoded[key] = arr.reshape((nr_agents,) + shape).copy()

        extra = set(payload_lookup.keys()) - set(self._visual_observation_specs.keys())
        if extra:
            raise RuntimeError(f"Received unknown visual observation payloads: {sorted(extra)}")

        return decoded

    def _batched_observation_to_numpy(self, observation, nr_agents):
        state_present = self._state_observation_space is not None and not is_empty_placeholder_space(
            self._state_observation_space
        )
        visual_present = len(self._visual_observation_specs) > 0

        if not visual_present:
            return self._batched_scalar_observation_to_numpy(
                observation,
                nr_agents,
                self._state_observation_space,
            )

        visual_obs = self._decode_visual_observations_to_numpy(observation, nr_agents)
        state_obs = None
        if state_present:
            state_obs = self._batched_scalar_observation_to_numpy(
                observation,
                nr_agents,
                self._state_observation_space,
            )

        if isinstance(self.single_observation_space, spaces.Dict):
            out = {}
            if state_present:
                out["state"] = state_obs
            out.update(visual_obs)
            return out

        if state_present:
            return {"state": state_obs, **visual_obs}

        if len(visual_obs) != 1:
            raise RuntimeError("Structured visual observations require a Dict observation space.")

        return next(iter(visual_obs.values()))

    def _zeros_like_observation(self, obs):
        if isinstance(obs, dict):
            return {key: self._zeros_like_observation(value) for key, value in obs.items()}
        if isinstance(obs, tuple):
            return tuple(self._zeros_like_observation(value) for value in obs)
        return np.zeros_like(obs)

    def _scatter_batched_observation(self, sparse_obs, indices: np.ndarray, obs_template):
        full_obs = self._zeros_like_observation(obs_template)
        if indices.size == 0:
            return full_obs

        if isinstance(full_obs, dict):
            for key in full_obs.keys():
                full_obs[key] = self._scatter_batched_observation(
                    sparse_obs[key],
                    indices,
                    full_obs[key],
                )
            return full_obs

        if isinstance(full_obs, tuple):
            return tuple(
                self._scatter_batched_observation(sparse_obs[i], indices, full_obs[i])
                for i in range(len(full_obs))
            )

        full_obs[indices] = sparse_obs
        return full_obs

    def reset_result_to_numpy(self, results: BatchedResetResults, nr_agents):
        obs = self._batched_observation_to_numpy(results.observation, nr_agents)
        info: Dict[str, Any] = {}

        custom_indices = self._decode_sparse_indices(results.custom_indices_i32, nr_agents, "custom_indices_i32")
        custom_keys, custom_values, custom_present, custom_mask = self._decode_columnar_custom(
            results.custom,
            custom_indices,
            nr_agents,
            "custom",
        )
        if custom_keys.size > 0:
            self._populate_custom_info(info, custom_keys, custom_values, custom_present)

        return obs, info

    def step_result_to_numpy(self, results: BatchedStepResults):
        obs = self._batched_observation_to_numpy(results.observation, self.num_envs)
        rewards = self._decode_float_buffer(results.rewards_f32, self.num_envs, "rewards_f32").copy()
        dones = self._decode_bool_buffer(results.dones, self.num_envs, "dones")
        truncates = self._decode_bool_buffer(results.truncates, self.num_envs, "truncates")
        info: Dict[str, Any] = {}

        custom_indices = self._decode_sparse_indices(results.custom_indices_i32, self.num_envs, "custom_indices_i32")
        custom_keys, custom_values, custom_present, custom_mask = self._decode_columnar_custom(
            results.custom,
            custom_indices,
            self.num_envs,
            "custom",
        )
        if custom_keys.size > 0:
            self._populate_custom_info(info, custom_keys, custom_values, custom_present)

        final_mask = np.zeros((self.num_envs,), dtype=np.bool_)
        final_observation = self._zeros_like_observation(obs)

        final_indices = self._decode_sparse_indices(results.final_indices_i32, self.num_envs, "final_indices_i32")
        if final_indices.size == 0:
            if int(results.final_info.final_observation.num_envs) != 0:
                raise RuntimeError("final_info has payload data but no indices.")
        else:
            final_observation_sparse = self._batched_observation_to_numpy(
                results.final_info.final_observation,
                final_indices.size,
            )

            final_mask[final_indices] = True
            final_observation = self._scatter_batched_observation(final_observation_sparse, final_indices, obs)

        info["final_observation"] = final_observation
        info["_final_observation"] = final_mask.copy()

        return obs, dones, truncates, rewards, info

    def map_action_to_proto(self, action):
        step = Step()

        def _append_action_msg(*, discrete=None, continuous=None):
            action_msg = Action()
            if discrete is not None:
                # store as repeated int32
                if np.isscalar(discrete):
                    action_msg.discrete.append(int(discrete))
                else:
                    action_msg.discrete.extend([int(x) for x in np.asarray(discrete).tolist()])
            if continuous is not None:
                if np.isscalar(continuous):
                    action_msg.continuous.append(float(continuous))
                else:
                    action_msg.continuous.extend([float(x) for x in np.asarray(continuous).tolist()])
            step.actions.append(action_msg)

        sas = self.single_action_space

        if isinstance(sas, spaces.Discrete):
            a = np.asarray(action)
            for i in range(self.num_envs):
                _append_action_msg(discrete=int(a[i]))

        elif isinstance(sas, spaces.MultiDiscrete) or isinstance(sas, spaces.MultiBinary):
            a = np.asarray(action)
            for i in range(self.num_envs):
                _append_action_msg(discrete=a[i])

        elif isinstance(sas, spaces.Box):
            a = np.asarray(action, dtype=np.float32)
            for i in range(self.num_envs):
                _append_action_msg(continuous=a[i].ravel())

        elif isinstance(sas, spaces.Dict):
            for i in range(self.num_envs):
                disc_i = None
                cont_i = None
                if "discrete" in action:
                    disc_i = np.asarray(action["discrete"])[i]
                if "continuous" in action:
                    cont_i = np.asarray(action["continuous"], dtype=np.float32)[i]
                _append_action_msg(discrete=disc_i, continuous=cont_i)

        else:
            raise TypeError(f"Unsupported single_action_space: {type(sas)}")

        return step
