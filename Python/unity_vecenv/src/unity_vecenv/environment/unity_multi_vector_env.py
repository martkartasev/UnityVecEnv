import numpy as np
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence, Tuple, Union
from dataclasses import dataclass
from gymnasium import spaces
from gymnasium.vector import VectorEnv, AutoresetMode

from unity_vecenv.environment.unity_vector_env import _normalize_reset_seeds, _normalize_ui_strings

import threading

EnvironmentParameterValue = Union[str, float, int]


class _ThreadWorker:
    def __init__(self, env: VectorEnv):
        self.env = env
        self.lock = threading.Lock()
        self.has_action = threading.Event()
        self.has_result = threading.Event()
        self.closed = False

        self._pending_action = None  # ("step", actions) or ("reset", seed, options)
        self._result = None

        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self):
        while True:
            self.has_action.wait()
            self.has_action.clear()

            with self.lock:
                if self.closed:
                    return
                cmd = self._pending_action

            kind = cmd[0]
            if kind == "step":
                _, a, ui_strings = cmd
                if ui_strings:
                    res = self.env.step(a, ui_strings=ui_strings)
                else:
                    res = self.env.step(a)
            elif kind == "reset":
                _, seed, options = cmd
                res = self.env.reset(seed=seed, options=options)
            elif kind == "reinitialize":
                # A scene reload can fail (missing build-settings entry, dead
                # process). Hand the error back instead of killing this thread,
                # which would leave the caller blocked until its timeout.
                _, kwargs = cmd
                try:
                    res = self.env.reinitialize(**kwargs)
                except BaseException as exc:  # noqa: BLE001 - surfaced on the caller
                    res = exc
            else:
                raise RuntimeError(f"Unknown worker command: {kind}")

            with self.lock:
                self._result = res
            self.has_result.set()

    def submit_step(self, action: np.ndarray, ui_strings: Sequence[str]):
        with self.lock:
            self._pending_action = ("step", action, ui_strings)
            self._result = None
        self.has_result.clear()
        self.has_action.set()

    def submit_reset(self, seed, options):
        with self.lock:
            self._pending_action = ("reset", seed, options)
            self._result = None
        self.has_result.clear()
        self.has_action.set()

    def submit_reinitialize(self, kwargs: Dict[str, Any]):
        with self.lock:
            self._pending_action = ("reinitialize", dict(kwargs))
            self._result = None
        self.has_result.clear()
        self.has_action.set()

    def get_result(self, timeout: Optional[float] = None):
        ok = self.has_result.wait(timeout=timeout)
        if not ok:
            raise TimeoutError("Worker timed out waiting for result")
        with self.lock:
            return self._result

    def close(self):
        with self.lock:
            self.closed = True
        self.has_action.set()
        try:
            self.env.close()
        except Exception:
            pass


# -------- Flatten wrapper (thread workers) --------

@dataclass(frozen=True)
class _Slice:
    start: int
    end: int

    @property
    def size(self) -> int:
        return self.end - self.start


def _split_by_slices(space: spaces.Space, batched, slices: Sequence[_Slice]):
    if isinstance(space, spaces.Box):
        a = np.asarray(batched)
        return [a[slc.start:slc.end] for slc in slices]

    if isinstance(space, spaces.Discrete):
        a = np.asarray(batched, dtype=np.int64)
        return [a[slc.start:slc.end] for slc in slices]

    if isinstance(space, spaces.MultiDiscrete) or isinstance(space, spaces.MultiBinary):
        a = np.asarray(batched, dtype=np.int64)
        return [a[slc.start:slc.end] for slc in slices]

    if isinstance(space, spaces.Dict):
        return [
            {k: _split_by_slices(space.spaces[k], batched[k], [slc])[0] for k in space.spaces.keys()}
            for slc in slices
        ]

    if isinstance(space, spaces.Tuple):
        parts = []
        for slc in slices:
            parts.append(tuple(
                _split_by_slices(subsp, batched[i], [slc])[0]
                for i, subsp in enumerate(space.spaces)
            ))
        return parts

    raise TypeError(f"Unsupported space type: {type(space)}")


def _concat_by_space(space: spaces.Space, parts: Sequence[Any]):
    if isinstance(space, spaces.Box):
        return np.concatenate([np.asarray(p) for p in parts], axis=0)

    if isinstance(space, spaces.Discrete):
        return np.concatenate([np.asarray(p, dtype=np.int64) for p in parts], axis=0)

    if isinstance(space, spaces.MultiDiscrete) or isinstance(space, spaces.MultiBinary):
        return np.concatenate([np.asarray(p, dtype=np.int64) for p in parts], axis=0)

    if isinstance(space, spaces.Dict):
        return {k: _concat_by_space(space.spaces[k], [p[k] for p in parts]) for k in space.spaces.keys()}

    if isinstance(space, spaces.Tuple):
        return tuple(_concat_by_space(subsp, [p[i] for p in parts]) for i, subsp in enumerate(space.spaces))

    raise TypeError(f"Unsupported space type: {type(space)}")

def _concat_info_value(parts: Sequence[Any]):
    sample = next((p for p in parts if p is not None), None)
    if sample is None:
        return None

    if isinstance(sample, np.ndarray):
        return np.concatenate([np.asarray(p) for p in parts if p is not None], axis=0)

    if isinstance(sample, dict):
        return {
            key: _concat_info_value([p[key] if p is not None else None for p in parts])
            for key in sample.keys()
        }

    if isinstance(sample, tuple):
        return tuple(_concat_info_value([p[i] if p is not None else None for p in parts]) for i in range(len(sample)))

    raise TypeError(f"Unsupported info value type: {type(sample)}")


def _merge_infos(infos_per_env: Sequence[Dict[str, Any]], slices: Sequence[_Slice]) -> Dict[str, Any]:
    out: Dict[str, Any] = {}
    keys = set()
    for info in infos_per_env:
        keys.update(info.keys())

    n_total = slices[-1].end if slices else 0

    for k in keys:
        vals_raw = [info.get(k, None) for info in infos_per_env]
        sample = next((v for v in vals_raw if v is not None), None)
        if sample is None:
            continue

        if isinstance(sample, np.ndarray):
            sample_arr = np.asarray(sample)
            merged = np.zeros((n_total,) + sample_arr.shape[1:], dtype=sample_arr.dtype)
            for value, slc in zip(vals_raw, slices):
                if value is None:
                    continue
                arr = np.asarray(value, dtype=sample_arr.dtype)
                length = min(arr.shape[0], slc.size)
                merged[slc.start:slc.start + length] = arr[:length]
            out[k] = merged
            continue

        if isinstance(sample, dict) or isinstance(sample, tuple):
            out[k] = _concat_info_value(vals_raw)
            continue

        out[k] = vals_raw

    return out


def _copy_environment_parameters(env: VectorEnv) -> Dict[str, EnvironmentParameterValue]:
    raw_parameters = getattr(env, "environment_parameters", None)
    if raw_parameters is None:
        return {}
    return dict(raw_parameters)


def _validate_environment_parameters(envs: Sequence[VectorEnv]) -> Dict[str, EnvironmentParameterValue]:
    expected = _copy_environment_parameters(envs[0])
    for index, env in enumerate(envs[1:], start=1):
        actual = _copy_environment_parameters(env)
        if actual != expected:
            raise ValueError(
                f"Sub-environment {index} reported different environment_parameters than the first sub-environment."
            )
    return expected

def _batch_space(single: spaces.Space, num_envs: int) -> spaces.Space:
    if isinstance(single, spaces.Box):
        low = np.broadcast_to(single.low, (num_envs,) + single.shape)
        high = np.broadcast_to(single.high, (num_envs,) + single.shape)
        return spaces.Box(low=low, high=high, dtype=single.dtype)

    if isinstance(single, spaces.Discrete):
        # vector of independent discrete actions
        return spaces.MultiDiscrete(
            np.full((num_envs,), single.n, dtype=np.int64)
        )

    if isinstance(single, spaces.MultiDiscrete):
        nvec = np.broadcast_to(single.nvec, (num_envs,) + single.nvec.shape)
        return spaces.MultiDiscrete(nvec.astype(np.int64))

    if isinstance(single, spaces.MultiBinary):
        return spaces.MultiBinary((num_envs,) + single.shape)

    if isinstance(single, spaces.Dict):
        return spaces.Dict({
            k: _batch_space(v, num_envs)
            for k, v in single.spaces.items()
        })

    if isinstance(single, spaces.Tuple):
        return spaces.Tuple(
            tuple(_batch_space(s, num_envs) for s in single.spaces)
        )

    raise TypeError(f"Unsupported space type: {type(single)}")

class FlattenedVectorEnvThreaded(VectorEnv):
    def __init__(self, envs: Union[Sequence[VectorEnv], Sequence[Callable[[], VectorEnv]]]):
        super().__init__()
        if len(envs) == 0:
            raise ValueError("Need at least one sub-VectorEnv")

        if callable(envs[0]):
            self.envs = [fn() for fn in envs]  # type: ignore[misc]
        else:
            self.envs = list(envs)  # type: ignore[assignment]

        self._rebuild_layout()

        # workers
        self.workers = [_ThreadWorker(e) for e in self.envs]

        self._pending_kind: Optional[str] = None

    def _rebuild_layout(self) -> None:
        """Recompute slices, spaces, metadata and buffers from the sub-envs.

        Runs at construction and again after :meth:`reinitialize`, because a
        re-initialized process may come back with a different scene's spaces or
        a different agent count.
        """
        # slices
        self._slices: List[_Slice] = []
        cur = 0
        for e in self.envs:
            m = int(e.num_envs)
            self._slices.append(_Slice(cur, cur + m))
            cur += m
        self.num_envs = cur

        # spaces (assume identical single spaces)
        e0 = self.envs[0]
        sa0 = getattr(e0, "single_action_space", None) or e0.single_action_space
        so0 = getattr(e0, "single_observation_space", None) or e0.single_observation_space

        self.single_action_space = sa0
        self.single_observation_space = so0

        self.action_space = _batch_space(sa0, self.num_envs)
        self.observation_space = _batch_space(so0, self.num_envs)

        self.metadata = dict(getattr(e0, "metadata", {}) or {})
        self.metadata["num_envs"] = self.num_envs
        self.metadata.setdefault("autoreset_mode", AutoresetMode.NEXT_STEP)
        self.environment_parameters = _validate_environment_parameters(self.envs)
        self.metadata["environment_parameters"] = dict(self.environment_parameters)

        self._rew_buf = np.empty((self.num_envs,), dtype=np.float32)
        self._done_buf = np.empty((self.num_envs,), dtype=np.bool_)
        self._trunc_buf = np.empty((self.num_envs,), dtype=np.bool_)

        if isinstance(self.single_observation_space, spaces.Box):
            obs_shape = (self.num_envs,) + self.single_observation_space.shape
            self._obs_buf = np.empty(obs_shape, dtype=self.single_observation_space.dtype)
        else:
            self._obs_buf = None  # structured or discrete obs

    def reinitialize(
            self,
            num_envs: Optional[int] = None,
            env_parameters: Optional[Mapping[str, EnvironmentParameterValue]] = None,
            autoreset_mode: Optional[Union[AutoresetMode, str]] = None,
            scene_load: Optional[str] = None,
            reload_scene: bool = True,
            timeout: Optional[float] = None,
    ):
        """Re-initialize every Unity instance concurrently, optionally into another scene.

        ``num_envs`` is per instance, matching construction. Instances reload in
        parallel, so this costs about one reload rather than one per instance.
        """
        kwargs = {
            "num_envs": num_envs,
            "env_parameters": env_parameters,
            "autoreset_mode": autoreset_mode,
            "scene_load": scene_load,
            "reload_scene": reload_scene,
        }
        for worker in self.workers:
            worker.submit_reinitialize(kwargs)
        results = [worker.get_result(timeout=timeout) for worker in self.workers]

        failures = [result for result in results if isinstance(result, BaseException)]
        if failures:
            raise RuntimeError(
                f"{len(failures)} of {len(results)} Unity instances failed to "
                f"re-initialize; first error: {failures[0]!r}"
            ) from failures[0]

        self._pending_kind = None
        self._rebuild_layout()
        return results

    def reset(
            self,
            *,
            seed: Optional[Union[int, Sequence[int]]] = None,
            options: Optional[dict] = None,
    ):
        self.reset_async(seed=seed, options=options)
        obs, info = self.reset_wait()
        if info is None:
            info = {}
        return obs, info

    def step(
            self,
            actions: np.ndarray,
            ui_strings: Optional[Sequence[str]] = None,
    ):
        self.step_async(actions, ui_strings=ui_strings)
        obs, rewards, dones, truncs, info = self.step_wait()
        if info is None:
            info = {}
        return obs, rewards, dones, truncs, info

    # --- splitting helpers ---

    def _split_actions(self, actions):
        return _split_by_slices(self.single_action_space, actions, self._slices)

    def _split_inits_from_options(self, options: Optional[dict]) -> List[Optional[dict]]:
        if options is None:
            return [None for _ in self.envs]
        if "init" not in options or options["init"] is None:
            return [options for _ in self.envs]

        init = np.asarray(options["init"])
        if init.shape[0] != self.num_envs:
            raise ValueError(f'options["init"] first dim must be {self.num_envs}, got {init.shape}')

        out = []
        for slc in self._slices:
            o = dict(options)
            o["init"] = init[slc.start:slc.end]
            out.append(o)
        return out

    # --- async API ---

    def reset_async(self, seed=None, options=None):
        if self._pending_kind is not None:
            raise RuntimeError("reset_async called while another async call is pending")

        seeds_per_env = [None] * len(self.envs)
        if seed is None:
            seeds_per_env = [None] * len(self.envs)
        else:
            flattened_seeds = _normalize_reset_seeds(seed, self.num_envs)
            for i, slc in enumerate(self._slices):
                seeds_per_env[i] = flattened_seeds[slc.start:slc.end]

        options_per_env = self._split_inits_from_options(options)

        for w, s, o in zip(self.workers, seeds_per_env, options_per_env):
            w.submit_reset(s, o)

        self._pending_kind = "reset"

    def reset_wait(self, timeout: Optional[float] = None):
        if self._pending_kind != "reset":
            raise RuntimeError("reset_wait called without pending reset_async")

        results = [w.get_result(timeout=timeout) for w in self.workers]
        self._pending_kind = None

        obs_parts = []
        infos = []
        for obs, info in results:
            obs_parts.append(obs)
            infos.append(info or {})

        obs = _concat_by_space(self.single_observation_space, obs_parts)
        return obs, _merge_infos(infos, self._slices)

    def step_async(self, actions, ui_strings: Optional[Sequence[str]] = None):
        if self._pending_kind is not None:
            raise RuntimeError("step_async called while another async call is pending")

        normalized_ui_strings = tuple(_normalize_ui_strings(ui_strings))
        parts = self._split_actions(actions)
        for w, a_part in zip(self.workers, parts):
            w.submit_step(a_part, normalized_ui_strings)

        self._pending_kind = "step"

    def step_wait(self, timeout: Optional[float] = None):
        if self._pending_kind != "step":
            raise RuntimeError("step_wait called without pending step_async")

        results = [w.get_result(timeout=timeout) for w in self.workers]
        self._pending_kind = None

        infos = []
        obs_parts = []
        for (obs, rew, done, trunc, info), slc in zip(results, self._slices):
            rew = np.asarray(rew, dtype=np.float32)
            done = np.asarray(done, dtype=np.bool_)
            trunc = np.asarray(trunc, dtype=np.bool_)

            self._rew_buf[slc.start:slc.end] = rew
            self._done_buf[slc.start:slc.end] = done
            self._trunc_buf[slc.start:slc.end] = trunc

            obs_parts.append(obs)
            infos.append(info or {})

        merged_info = _merge_infos(infos, self._slices)

        if self._obs_buf is not None:
            obs_cat = np.concatenate([np.asarray(o) for o in obs_parts], axis=0)
            self._obs_buf[...] = obs_cat
            obs_out = self._obs_buf
        else:
            obs_out = _concat_by_space(self.single_observation_space, obs_parts)

        return obs_out, self._rew_buf, self._done_buf, self._trunc_buf, merged_info

    def close(self):
        for w in self.workers:
            w.close()



