import numpy as np
from typing import Any, Dict, List, Optional, Sequence, Tuple, Union, Callable
from dataclasses import dataclass
from gymnasium import spaces
from gymnasium.vector import VectorEnv, AutoresetMode

import threading


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
                _, a = cmd
                res = self.env.step(a)
            elif kind == "reset":
                _, seed, options = cmd
                res = self.env.reset(seed=seed, options=options)
            else:
                raise RuntimeError(f"Unknown worker command: {kind}")

            with self.lock:
                self._result = res
            self.has_result.set()

    def submit_step(self, action: np.ndarray):
        with self.lock:
            self._pending_action = ("step", action)
            self._result = None
        self.has_result.clear()
        self.has_action.set()

    def submit_reset(self, seed, options):
        with self.lock:
            self._pending_action = ("reset", seed, options)
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

def _merge_infos(infos_per_env: Sequence[Dict[str, Any]], slices: Sequence[_Slice]) -> Dict[str, Any]:
    out: Dict[str, Any] = {}
    keys = set()
    for info in infos_per_env:
        keys.update(info.keys())

    n_total = slices[-1].end if slices else 0

    for k in keys:
        if k == "final_info":
            merged = [None] * n_total
            for info, slc in zip(infos_per_env, slices):
                if "final_info" not in info:
                    continue
                fi = info["final_info"]
                for j in range(min(len(fi), slc.size)):
                    merged[slc.start + j] = fi[j]
            if any(x is not None for x in merged):
                out["final_info"] = merged
            continue

        if k == "final_observation":
            merged = [None] * n_total
            for info, slc in zip(infos_per_env, slices):
                if "final_observation" not in info:
                    continue
                fo = info["final_observation"]
                for j in range(min(len(fo), slc.size)):
                    merged[slc.start + j] = fo[j]
            if any(x is not None for x in merged):
                out["final_observation"] = merged
            continue

        if k == "custom":
            vals = [info.get("custom", None) for info in infos_per_env]
            vals = [v for v in vals if v is not None]
            if not vals:
                continue
            if all(isinstance(v, np.ndarray) for v in vals):
                try:
                    out["custom"] = np.concatenate(vals, axis=0)
                except Exception:
                    out["custom"] = vals
            else:
                out["custom"] = vals
            continue

        vals_raw = [info.get(k, None) for info in infos_per_env]
        if all(isinstance(v, np.ndarray) for v in vals_raw if v is not None):
            vals = [v for v in vals_raw if v is not None]
            try:
                out[k] = np.concatenate(vals, axis=0)
                continue
            except Exception:
                pass

        out[k] = vals_raw

    return out

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

        # workers
        self.workers = [_ThreadWorker(e) for e in self.envs]

        self._pending_kind: Optional[str] = None

        self._rew_buf = np.empty((self.num_envs,), dtype=np.float32)
        self._done_buf = np.empty((self.num_envs,), dtype=np.bool_)
        self._trunc_buf = np.empty((self.num_envs,), dtype=np.bool_)

        if isinstance(self.single_observation_space, spaces.Box):
            obs_shape = (self.num_envs,) + self.single_observation_space.shape
            self._obs_buf = np.empty(obs_shape, dtype=self.single_observation_space.dtype)
        else:
            self._obs_buf = None  # structured or discrete obs

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

    def step(self, actions: np.ndarray):
        self.step_async(actions)
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
        elif isinstance(seed, (int, np.integer)):
            base = int(seed)
            seeds_per_env = [base + i for i in range(len(self.envs))]
        else:
            seed_list = list(seed)
            if len(seed_list) != self.num_envs:
                raise ValueError(f"seed sequence length must be {self.num_envs}, got {len(seed_list)}")
            for i, slc in enumerate(self._slices):
                seeds_per_env[i] = seed_list[slc.start:slc.end]

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

    def step_async(self, actions):
        if self._pending_kind is not None:
            raise RuntimeError("step_async called while another async call is pending")

        parts = self._split_actions(actions)
        for w, a_part in zip(self.workers, parts):
            w.submit_step(a_part)

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
