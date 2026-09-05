import numbers
import subprocess
from typing import Any, Dict, Mapping, Optional, Sequence, Union

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
from unity_vecenv.environment.shared_memory_observations import SharedMemoryObservationReader
from unity_vecenv.environment.unity_client import start_client
from unity_vecenv.environment.unity_process import start_unity_process
from unity_vecenv.protobuf_gen.communication_pb2 import (
    ResetParameters,
    Reset,
    BatchedResetResults,
    Step,
    Action,
    BatchedStepResults,
    EnvironmentParameter as EnvironmentParameterProto,
    InitializeEnvironments,
    AutoResetMode,
)

_FLOAT32_LE = np.dtype("<f4")
_INT32_LE = np.dtype("<i4")
_INT32_MIN = -(2 ** 31)
_INT32_MAX = 2 ** 31 - 1
EnvironmentParameterValue = Union[str, float, int]


def _normalize_ui_strings(ui_strings: Optional[Sequence[str]]) -> list[str]:
    if ui_strings is None:
        return []

    if isinstance(ui_strings, (str, bytes)):
        raise TypeError("ui_strings must be a sequence of strings or None, not a single string.")

    try:
        normalized = list(ui_strings)
    except TypeError as exc:
        raise TypeError("ui_strings must be a sequence of strings or None.") from exc

    for index, value in enumerate(normalized):
        if not isinstance(value, str):
            raise TypeError(
                f"ui_strings[{index}] must be a string, got {type(value).__name__}."
            )

    return normalized


def _validate_reset_seed(seed: Any, *, label: str) -> int:
    if isinstance(seed, bool) or not isinstance(seed, numbers.Integral):
        raise TypeError(f"{label} must be an integer, got {type(seed).__name__}.")

    value = int(seed)
    if value < _INT32_MIN or value > _INT32_MAX:
        raise ValueError(
            f"{label} must fit a signed int32 ({_INT32_MIN}..{_INT32_MAX}), got {value}."
        )
    return value


def _normalize_reset_seeds(
    seed: Optional[Union[int, Sequence[int]]],
    num_envs: int,
) -> list[Optional[int]]:
    if seed is None:
        return [None] * num_envs

    if isinstance(seed, numbers.Integral) and not isinstance(seed, bool):
        base_seed = _validate_reset_seed(seed, label="seed")
        seeds = []
        for index in range(num_envs):
            seeds.append(_validate_reset_seed(base_seed + index, label=f"seed + env index {index}"))
        return seeds

    if isinstance(seed, (str, bytes)):
        raise TypeError("seed must be an integer, a sequence of integers, or None.")

    try:
        seed_values = list(seed)
    except TypeError as exc:
        raise TypeError("seed must be an integer, a sequence of integers, or None.") from exc

    if len(seed_values) != num_envs:
        raise ValueError(f"seed sequence length must be {num_envs}, got {len(seed_values)}.")

    return [
        _validate_reset_seed(value, label=f"seed[{index}]")
        for index, value in enumerate(seed_values)
    ]


def _normalize_autoreset_mode(mode: Union[AutoresetMode, str]) -> AutoresetMode:
    if isinstance(mode, AutoresetMode):
        normalized = mode
    elif isinstance(mode, str):
        name = mode.strip().lower().replace("-", "_")
        modes_by_name = {
            "next_step": AutoresetMode.NEXT_STEP,
            "samestep": AutoresetMode.SAME_STEP,
            "same_step": AutoresetMode.SAME_STEP,
            "nextstep": AutoresetMode.NEXT_STEP,
        }
        if name not in modes_by_name:
            raise ValueError(f"Unsupported autoreset mode '{mode}'; expected 'next_step' or 'same_step'.")
        normalized = modes_by_name[name]
    else:
        raise TypeError("autoreset_mode must be a gymnasium AutoresetMode or string.")

    if normalized not in (AutoresetMode.NEXT_STEP, AutoresetMode.SAME_STEP):
        raise ValueError(f"Unsupported autoreset mode {normalized}; only next-step and same-step are implemented.")
    return normalized


def _normalize_environment_parameters(
    parameters: Optional[Mapping[str, EnvironmentParameterValue]],
) -> Dict[str, EnvironmentParameterValue]:
    if parameters is None:
        return {}

    normalized: Dict[str, EnvironmentParameterValue] = {}
    for raw_key, value in parameters.items():
        key = str(raw_key).strip()
        if not key:
            raise ValueError("Environment parameter key cannot be empty.")
        if key in normalized:
            raise ValueError(f"Duplicate environment parameter key '{key}'.")
        normalized[key] = value

    return normalized


def _environment_parameter_to_proto(
    key: str,
    value: EnvironmentParameterValue,
) -> EnvironmentParameterProto:
    parameter = EnvironmentParameterProto()
    parameter.key = key

    if isinstance(value, str):
        parameter.string_value = value
    elif isinstance(value, numbers.Integral):
        parameter.int_value = int(value)
    elif isinstance(value, numbers.Real):
        parameter.float_value = float(value)
    else:
        raise TypeError(
            f"Unsupported environment parameter '{key}' value type {type(value).__name__}; "
            "expected str, int, or float."
        )

    return parameter


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
                 log_file: str = "",
                 env_parameters: Optional[Mapping[str, EnvironmentParameterValue]] = None,
                 autoreset_mode: Union[AutoresetMode, str] = AutoresetMode.NEXT_STEP,
                 reload_scene: bool = False,
                 shared_memory_observations: bool = False):
        super(UnityVectorEnv, self).__init__()
        self.initialization_parameters = _normalize_environment_parameters(env_parameters)
        self.autoreset_mode = _normalize_autoreset_mode(autoreset_mode)

        self.metadata = {
            "autoreset_mode": self.autoreset_mode,
            "num_envs": num_envs,
            "time_scale": time_scale,
            "physics_steps_per_action": physics_steps_per_action,
            "initialization_parameters": dict(self.initialization_parameters),
            "shared_memory_observations": bool(shared_memory_observations),
        }
        self.time_scale = time_scale
        self.physics_steps_per_action = physics_steps_per_action
        self.port = port
        while (start_process and is_port_in_use(self.port) or
               (not start_process and not is_port_in_use(self.port))):
            self.port += 1

        self._shared_memory_observations = bool(shared_memory_observations)
        self._shared_observation_reader = (
            SharedMemoryObservationReader(self.port)
            if self._shared_memory_observations
            else None
        )
        self.process = start_unity_process(
            executable_path,
            scene_load=scene_load,
            port=self.port,
            nr_agents=num_envs,
            batch_mode=batch_mode,
            no_graphics=no_graphics,
            timescale=self.time_scale,
            log_file=log_file,
            shared_memory_observations=self._shared_memory_observations,
        ) if start_process else None
        self.client = start_client(port=self.port)

        self.scene_load = scene_load
        # reload_scene makes this first initialize take the same reload path as every
        # later reinitialize(), so a freshly launched process and a reused one start
        # from an identical scene. Costs one extra scene load at startup.
        environment_description = self.initialize_environment(
            num_envs,
            self.initialization_parameters,
            self.autoreset_mode,
            reload_scene=reload_scene,
        )
        self._apply_environment_description(environment_description)

    def _apply_environment_description(self, environment_description) -> None:
        """Adopt the spaces and env count returned by an initialize call.

        Shared by construction and :meth:`reinitialize`, because a re-initialized
        process may return a different scene's spaces and agent count.
        """
        if environment_description.trueNumberOfEnvs == 0:
            raise RuntimeError("Failed to initialize environment connection. Number of envs returns 0.")

        self.environment_parameters = self._decode_environment_parameters(environment_description)
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
        self.metadata["num_envs"] = self.num_envs
        self.metadata["environment_parameters"] = dict(self.environment_parameters)

    def initialize_environment(
            self,
            num_envs,
            env_parameters: Optional[Mapping[str, EnvironmentParameterValue]] = None,
            autoreset_mode: AutoresetMode = AutoresetMode.NEXT_STEP,
            scene_load: Optional[str] = None,
            reload_scene: bool = False,
    ):
        init = InitializeEnvironments()
        proto_modes = {
            AutoresetMode.NEXT_STEP: AutoResetMode.NextStep,
            AutoresetMode.SAME_STEP: AutoResetMode.SameStep,
        }
        normalized_autoreset_mode = _normalize_autoreset_mode(autoreset_mode)
        init.autoResetMode = proto_modes[normalized_autoreset_mode]
        init.requestedNumberOfEnvs = num_envs
        for key, value in _normalize_environment_parameters(env_parameters).items():
            init.parameters.append(_environment_parameter_to_proto(key, value))
        if scene_load:
            init.sceneName = str(scene_load)
        init.reloadScene = bool(reload_scene)
        environment_description = self.client.initialize(init)
        return environment_description

    def reinitialize(
            self,
            num_envs: Optional[int] = None,
            env_parameters: Optional[Mapping[str, EnvironmentParameterValue]] = None,
            autoreset_mode: Optional[Union[AutoresetMode, str]] = None,
            scene_load: Optional[str] = None,
            reload_scene: bool = True,
    ):
        """Re-initialize this live Unity process, optionally into another scene.

        Reuses the running process instead of paying a full relaunch. With
        ``reload_scene`` (the default) Unity reloads the scene in Single mode, so
        the environment starts from the same state as a freshly launched process
        rather than inheriting residual physics and sensor state from the previous
        episodes. Observation and action spaces are rebuilt from the response,
        because another scene may describe different spaces or agent counts.
        """
        if num_envs is None:
            num_envs = self.num_envs
        if env_parameters is None:
            env_parameters = self.initialization_parameters
        else:
            env_parameters = _normalize_environment_parameters(env_parameters)
        if autoreset_mode is None:
            autoreset_mode = self.autoreset_mode

        self.initialization_parameters = dict(env_parameters)
        self.autoreset_mode = _normalize_autoreset_mode(autoreset_mode)
        if scene_load:
            self.scene_load = str(scene_load)
        self.metadata["autoreset_mode"] = self.autoreset_mode
        self.metadata["initialization_parameters"] = dict(self.initialization_parameters)

        environment_description = self.initialize_environment(
            num_envs,
            self.initialization_parameters,
            self.autoreset_mode,
            scene_load=scene_load,
            reload_scene=reload_scene,
        )
        self._apply_environment_description(environment_description)
        return environment_description

    def _decode_environment_parameters(self, environment_description) -> Dict[str, EnvironmentParameterValue]:
        parameters: Dict[str, EnvironmentParameterValue] = {}
        for parameter in getattr(environment_description, "parameters", []):
            key = str(parameter.key).strip()
            if not key:
                raise RuntimeError("Environment parameter key cannot be empty.")
            if key in parameters:
                raise RuntimeError(f"Duplicate environment parameter key '{key}'.")

            value_field = parameter.WhichOneof("value")
            if value_field == "string_value":
                value: EnvironmentParameterValue = str(parameter.string_value)
            elif value_field == "float_value":
                value = float(parameter.float_value)
            elif value_field == "int_value":
                value = int(parameter.int_value)
            else:
                raise RuntimeError(f"Environment parameter '{key}' is missing a typed value.")

            parameters[key] = value

        return parameters

    def reset(
            self,
            seed: Optional[Union[int, Sequence[int]]] = None,
            options: Optional[dict] = None,
    ):
        reset_msg = Reset()
        reset_msg.reloadScene = False
        reset_seeds = _normalize_reset_seeds(seed, self.num_envs)

        agent_inits = None
        if options is not None:
            if "init" not in options or options["init"] is None:
                raise ValueError('reset options must contain a non-null "init" value.')
            agent_inits = np.asarray(options["init"])
            if agent_inits.ndim == 0 or agent_inits.shape[0] != self.num_envs:
                raise ValueError(
                    f'options["init"] first dimension must be {self.num_envs}, got {agent_inits.shape}.'
                )

        for index, reset_seed in enumerate(reset_seeds):
            if reset_seed is None and agent_inits is None:
                continue
            initialization = () if agent_inits is None else agent_inits[index]
            reset_msg.envsToReset.append(
                self.map_reset_params_to_proto(index, initialization, seed=reset_seed)
            )

        reset = self.client.reset(reset_msg)
        obs, info = self.reset_result_to_numpy(reset, self.num_envs)
        return obs, info

    def step(self, action, ui_strings: Optional[Sequence[str]] = None):
        normalized_ui_strings = _normalize_ui_strings(ui_strings)
        action_msg = self.map_action_to_proto(action)
        action_msg.stepCount = self.physics_steps_per_action
        action_msg.timeScale = self.time_scale
        action_msg.uiStrings.extend(normalized_ui_strings)
        step_result = self.client.step(action_msg)

        (obs, dones, truncates, rewards, info) = self.step_result_to_numpy(step_result)
        return obs, rewards, dones, truncates, info  # TODO see if info is worth keeping

    def render(self, mode='human'):
        pass
        # TODO: Screenshot/Video manager back into API

    def close(self):
        if self._shared_observation_reader is not None:
            self._shared_observation_reader.close()
            self._shared_observation_reader = None
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()

    def map_reset_params_to_proto(self, i, initialization=(), *, seed: Optional[int] = None):
        params = ResetParameters()
        params.index = i
        params.continuous.extend(initialization)
        if seed is not None:
            params.seed = _validate_reset_seed(seed, label=f"seed[{i}]")
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

    def _decode_bool_buffer(
        self,
        payload: bytes,
        expected_size: int,
        field_name: str,
        copy: bool = True,
    ) -> np.ndarray:
        arr = np.frombuffer(payload, dtype=np.bool_)
        if arr.size != expected_size:
            raise RuntimeError(f"{field_name} has {arr.size} boolean values, expected {expected_size}.")
        return arr.copy() if copy else arr

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

    def _decode_custom_section(
        self,
        *,
        keys,
        values_payload: bytes,
        present_payload: bytes,
        indices: np.ndarray,
        nr_agents: int,
        field_prefix: str,
        values_field_name: str,
        present_field_name: str,
        decode_values,
        dtype,
        value_shape=(),
    ):
        keys = np.asarray(keys, dtype=object)
        num_rows = int(indices.size)
        num_keys = int(keys.size)
        value_shape = tuple(value_shape)
        values_per_key = int(np.prod(value_shape, dtype=np.int64)) if value_shape else 1

        full_values = np.zeros((nr_agents, num_keys) + value_shape, dtype=dtype)
        full_present = np.zeros((nr_agents, num_keys), dtype=np.bool_)

        if num_rows == 0:
            if num_keys != 0 or len(values_payload) != 0 or len(present_payload) != 0:
                raise RuntimeError(f"{field_prefix} has payload data but no indices.")
            return keys, full_values, full_present

        if num_keys == 0:
            if len(values_payload) != 0 or len(present_payload) != 0:
                raise RuntimeError(f"{field_prefix} has payload data but no keys.")
            return keys, full_values, full_present

        values = decode_values(
            values_payload,
            num_rows * num_keys * values_per_key,
            f"{field_prefix}.{values_field_name}",
        ).reshape((num_rows, num_keys) + value_shape)
        present = self._decode_bool_buffer(
            present_payload,
            num_rows * num_keys,
            f"{field_prefix}.{present_field_name}",
            copy=False,
        ).reshape((num_rows, num_keys))

        full_values[indices] = values
        full_present[indices] = present
        return keys, full_values, full_present

    def _populate_typed_custom_info(self, info: Dict[str, Any], custom_payload, indices: np.ndarray, nr_agents: int):
        section_specs = (
            ("keys", "values_f32", "present", self._decode_float_buffer, np.float32, ()),
            ("keys_i32", "values_i32", "present_i32", self._decode_int_buffer, np.int32, ()),
            (
                "keys_bool",
                "values_bool",
                "present_bool",
                lambda payload, expected_size, field_name: self._decode_bool_buffer(
                    payload,
                    expected_size,
                    field_name,
                    copy=False,
                ),
                np.bool_,
                (),
            ),
            ("keys_vector3", "values_vector3_f32", "present_vector3", self._decode_float_buffer, np.float32, (3,)),
            (
                "keys_quaternion",
                "values_quaternion_f32",
                "present_quaternion",
                self._decode_float_buffer,
                np.float32,
                (4,),
            ),
        )

        for keys_field, values_field, present_field, decode_values, dtype, value_shape in section_specs:
            custom_keys, custom_values, custom_present = self._decode_custom_section(
                keys=getattr(custom_payload, keys_field),
                values_payload=getattr(custom_payload, values_field),
                present_payload=getattr(custom_payload, present_field),
                indices=indices,
                nr_agents=nr_agents,
                field_prefix="custom",
                values_field_name=values_field,
                present_field_name=present_field,
                decode_values=decode_values,
                dtype=dtype,
                value_shape=value_shape,
            )
            if custom_keys.size > 0:
                self._populate_custom_info(info, custom_keys, custom_values, custom_present)

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

    def _batched_scalar_observation_to_numpy(
        self,
        observation,
        nr_agents,
        scalar_space,
        shared_payloads=None,
    ):
        num_envs = int(observation.num_envs or nr_agents)
        if num_envs != nr_agents:
            raise RuntimeError(f"Batched observation reports {num_envs} envs, expected {nr_agents}.")

        continuous = None
        if observation.continuous_size > 0:
            payload = (
                shared_payloads.continuous
                if shared_payloads is not None
                else observation.continuous_f32
            )
            continuous = self._decode_float_buffer(
                payload,
                nr_agents * int(observation.continuous_size),
                "observation.continuous_f32",
            ).reshape((nr_agents, int(observation.continuous_size)))
            if shared_payloads is None:
                continuous = continuous.copy()

        discrete = None
        if observation.discrete_size > 0:
            payload = (
                shared_payloads.discrete
                if shared_payloads is not None
                else observation.discrete_i32
            )
            discrete = self._decode_int_buffer(
                payload,
                nr_agents * int(observation.discrete_size),
                "observation.discrete_i32",
            ).reshape((nr_agents, int(observation.discrete_size)))
            if shared_payloads is None:
                discrete = discrete.copy()

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

    def _decode_visual_observations_to_numpy(self, observation, nr_agents, shared_payloads=None):
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
            payload = (
                shared_payloads.visual[key]
                if shared_payloads is not None
                else bytes(payload_lookup[key].data)
            )
            arr = np.frombuffer(payload, dtype=dtype)
            if arr.size != expected:
                raise RuntimeError(
                    f"Visual observation '{key}' has {arr.size} values, expected {expected}."
                )
            decoded[key] = arr.reshape((nr_agents,) + shape)
            if shared_payloads is None:
                decoded[key] = decoded[key].copy()

        extra = set(payload_lookup.keys()) - set(self._visual_observation_specs.keys())
        if extra:
            raise RuntimeError(f"Received unknown visual observation payloads: {sorted(extra)}")

        return decoded

    def _batched_observation_to_numpy(self, observation, nr_agents, shared_payloads=None):
        state_present = self._state_observation_space is not None and not is_empty_placeholder_space(
            self._state_observation_space
        )
        visual_present = len(self._visual_observation_specs) > 0

        if not visual_present:
            return self._batched_scalar_observation_to_numpy(
                observation,
                nr_agents,
                self._state_observation_space,
                shared_payloads,
            )

        visual_obs = self._decode_visual_observations_to_numpy(
            observation,
            nr_agents,
            shared_payloads,
        )
        state_obs = None
        if state_present:
            state_obs = self._batched_scalar_observation_to_numpy(
                observation,
                nr_agents,
                self._state_observation_space,
                shared_payloads,
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
        shared_payloads = self._shared_payloads_for(results.observation, nr_agents)
        obs = self._batched_observation_to_numpy(
            results.observation,
            nr_agents,
            shared_payloads,
        )
        info: Dict[str, Any] = {}

        custom_indices = self._decode_sparse_indices(results.custom_indices_i32, nr_agents, "custom_indices_i32")
        self._populate_typed_custom_info(info, results.custom, custom_indices, nr_agents)

        return obs, info

    def step_result_to_numpy(self, results: BatchedStepResults):
        shared_payloads = self._shared_payloads_for(results.observation, self.num_envs)
        obs = self._batched_observation_to_numpy(
            results.observation,
            self.num_envs,
            shared_payloads,
        )
        rewards = self._decode_float_buffer(results.rewards_f32, self.num_envs, "rewards_f32").copy()
        dones = self._decode_bool_buffer(results.dones, self.num_envs, "dones")
        truncates = self._decode_bool_buffer(results.truncates, self.num_envs, "truncates")
        info: Dict[str, Any] = {}

        custom_indices = self._decode_sparse_indices(results.custom_indices_i32, self.num_envs, "custom_indices_i32")
        self._populate_typed_custom_info(info, results.custom, custom_indices, self.num_envs)

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

    def _shared_payloads_for(self, observation, nr_agents):
        if self._shared_observation_reader is None:
            return None
        return self._shared_observation_reader.read(
            observation,
            self._visual_observation_specs,
            nr_agents,
        )

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
