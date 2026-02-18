import numpy as np
from gymnasium import spaces


def _safe_key(name: str | None, prefix: str, i: int) -> str:
    name = (name or "").strip()
    return name if name else f"{prefix}_{i}"


def _box_from_proto(space_proto, dtype=np.float32,
                    default_low=-np.inf, default_high=np.inf):
    """
    Build Box from:
      - continuousSize (int)
      - continuousRange: repeated MinMax { index, minValue, maxValue }
    Any dimension not specified in continuousRange uses defaults.
    """
    n = int(getattr(space_proto, "continuousSize", 0) or 0)
    if n <= 0:
        return None

    low = np.full((n,), default_low, dtype=dtype)
    high = np.full((n,), default_high, dtype=dtype)

    for mm in getattr(space_proto, "continuousRange", []) or []:
        idx = int(mm.index)
        if 0 <= idx < n:
            low[idx] = np.float32(mm.minValue)
            high[idx] = np.float32(mm.maxValue)

    return spaces.Box(low=low, high=high, shape=(n,), dtype=dtype)


def _discrete_from_proto(space_proto):
    """
    Build Discrete/MultiDiscrete from repeated discreteSize.
    """
    sizes = [int(s) for s in (getattr(space_proto, "discreteSize", []) or [])]
    sizes = [s for s in sizes if s > 0]
    if not sizes:
        return None
    if len(sizes) == 1:
        return spaces.Discrete(sizes[0])
    return spaces.MultiDiscrete(np.array(sizes, dtype=np.int64))


def space_from_proto(space_proto, *, dtype=np.float32,
                     default_low=-np.inf, default_high=np.inf) -> spaces.Space:
    """
    Map one Space proto to an appropriate Gymnasium space:
      - continuous only -> Box
      - discrete only   -> Discrete or MultiDiscrete
      - both            -> Dict({"continuous": Box, "discrete": ...})
    """
    box = _box_from_proto(space_proto, dtype=dtype, default_low=default_low, default_high=default_high)
    disc = _discrete_from_proto(space_proto)

    if box is not None and disc is not None:
        return spaces.Dict({"continuous": box, "discrete": disc})
    if box is not None:
        return box
    if disc is not None:
        return disc

    # Nothing specified — choose a sensible "empty" placeholder.
    return spaces.Box(
        low=np.array([], dtype=dtype),
        high=np.array([], dtype=dtype),
        shape=(0,),
        dtype=dtype
    )


def space_from_repeated(space_list, *, prefix: str,
                        dtype=np.float32,
                        default_low=-np.inf, default_high=np.inf) -> spaces.Space:
    """
    Convert a repeated Space field into spaces.Dict keyed by Space.name.
    Ensures unique keys (handles duplicates or missing names).
    """
    if len(space_list) == 1:
        sp = space_list[0]
        return space_from_proto(sp, dtype=dtype, default_low=default_low, default_high=default_high)

    out = {}
    used = set()
    for i, sp in enumerate(space_list):
        key = _safe_key(getattr(sp, "name", None), prefix, i)
        base = key
        k = 1
        while key in used:
            key = f"{base}_{k}"
            k += 1
        used.add(key)

        out[key] = space_from_proto(sp, dtype=dtype, default_low=default_low, default_high=default_high)

    return spaces.Dict(out)


def batch_space(single: spaces.Space, num_envs: int) -> spaces.Space:
    """
    Add a leading num_envs dimension / structure to a single-env space.
    Recursively handles Dict/Tuple.

    - Box: (shape) -> (num_envs, *shape) with broadcast low/high
    - Discrete: -> MultiDiscrete([n]*num_envs)
    - MultiDiscrete: -> MultiDiscrete(broadcast nvec)
    - MultiBinary: -> MultiBinary((num_envs, *shape))
    """
    if isinstance(single, spaces.Box):
        low = np.broadcast_to(single.low, (num_envs,) + single.shape)
        high = np.broadcast_to(single.high, (num_envs,) + single.shape)
        return spaces.Box(low=low, high=high, dtype=single.dtype)

    if isinstance(single, spaces.Discrete):
        return spaces.MultiDiscrete(np.full((num_envs,), single.n, dtype=np.int64))

    if isinstance(single, spaces.MultiDiscrete):
        nvec = np.broadcast_to(single.nvec, (num_envs,) + single.nvec.shape)
        return spaces.MultiDiscrete(nvec.astype(np.int64))

    if isinstance(single, spaces.MultiBinary):
        return spaces.MultiBinary((num_envs,) + single.shape)

    if isinstance(single, spaces.Dict):
        return spaces.Dict({k: batch_space(v, num_envs) for k, v in single.spaces.items()})

    if isinstance(single, spaces.Tuple):
        return spaces.Tuple(tuple(batch_space(s, num_envs) for s in single.spaces))

    raise TypeError(f"Unsupported space type for batching: {type(single)}")
