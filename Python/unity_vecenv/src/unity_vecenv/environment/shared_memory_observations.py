"""Windows named shared-memory transport for batched Unity observations.

The HTTP response remains the synchronization barrier and carries observation
metadata.  The bulk byte fields are stored in a double-buffered named mapping
created by Unity.  This module opens that mapping and returns memoryviews over
the published slot, allowing NumPy to consume the data without another copy.
"""

from __future__ import annotations

from dataclasses import dataclass
import mmap
import struct
import sys
from typing import Mapping

import numpy as np


SHARED_MEMORY_MAGIC = b"UVESHM01"
SHARED_MEMORY_VERSION = 1
SHARED_MEMORY_HEADER_SIZE = 64
SHARED_MEMORY_SLOT_COUNT = 2

_HEADER = struct.Struct("<8sIIIIQQ")
_FLOAT32_SIZE = np.dtype("<f4").itemsize
_INT32_SIZE = np.dtype("<i4").itemsize


def shared_memory_mapping_name(channel: int, payload_size: int) -> str:
    """Return the Windows object name used by both Unity and Python."""

    return f"Local\\UnityVecEnv.Observations.{int(channel)}.{int(payload_size)}"


@dataclass(frozen=True)
class SharedObservationPayloads:
    """Views into one published shared-memory observation slot."""

    continuous: memoryview
    discrete: memoryview
    visual: Mapping[str, memoryview]
    sequence: int
    slot: int


class SharedMemoryObservationReader:
    """Open and validate observation mappings published by Unity.

    Mappings are cached by payload size.  Keeping old mappings alive also keeps
    NumPy arrays returned before a scene reinitialization valid for as long as
    their double-buffered slot has not been reused.
    """

    def __init__(self, channel: int):
        self.channel = int(channel)
        self._mappings: dict[int, mmap.mmap] = {}
        self._last_sequence = 0

    def read(self, observation, visual_specs, nr_agents: int) -> SharedObservationPayloads | None:
        """Return shared payload views, or ``None`` for an inline fallback.

        Unity deliberately leaves the protobuf byte fields populated if shared
        memory is unavailable.  Seeing any inline bulk field therefore selects
        the existing protobuf decoder instead of treating the response as an
        error.
        """

        visual_entries = list(observation.visual)
        if (
            len(observation.continuous_f32) != 0
            or len(observation.discrete_i32) != 0
            or any(len(entry.data) != 0 for entry in visual_entries)
        ):
            return None

        continuous_size = int(observation.continuous_size)
        discrete_size = int(observation.discrete_size)
        continuous_bytes = int(nr_agents) * continuous_size * _FLOAT32_SIZE
        discrete_bytes = int(nr_agents) * discrete_size * _INT32_SIZE

        visual_lengths: list[tuple[str, int]] = []
        seen_visuals: set[str] = set()
        for entry in visual_entries:
            key = str(entry.name)
            if key in seen_visuals:
                raise RuntimeError(f"Duplicate visual observation metadata for '{key}'.")
            seen_visuals.add(key)
            if key not in visual_specs:
                raise RuntimeError(f"Shared-memory response contains unknown visual observation '{key}'.")

            spec = visual_specs[key]
            itemsize = _FLOAT32_SIZE if int(spec.dataType) == 1 else 1
            element_count = int(np.prod(tuple(int(value) for value in spec.shape), dtype=np.int64))
            visual_lengths.append((key, int(nr_agents) * element_count * itemsize))

        missing_visuals = set(visual_specs.keys()) - seen_visuals
        if missing_visuals:
            raise RuntimeError(
                f"Shared-memory response is missing visual observation metadata: {sorted(missing_visuals)}"
            )

        payload_size = continuous_bytes + discrete_bytes + sum(length for _, length in visual_lengths)
        if payload_size == 0:
            return None
        if sys.platform != "win32":
            raise RuntimeError(
                "Shared-memory observations currently require Windows named mappings. "
                "Disable shared_memory_observations on this platform."
            )

        mapping = self._mapping_for_payload(payload_size)
        header = self._read_stable_header(mapping)
        magic, version, header_size, slot_count, slot, published_size, sequence = header

        if magic != SHARED_MEMORY_MAGIC:
            raise RuntimeError(
                f"Shared-memory observation mapping has invalid magic {magic!r}; "
                f"expected {SHARED_MEMORY_MAGIC!r}."
            )
        if version != SHARED_MEMORY_VERSION:
            raise RuntimeError(
                f"Shared-memory observation version is {version}, expected {SHARED_MEMORY_VERSION}."
            )
        if header_size != SHARED_MEMORY_HEADER_SIZE:
            raise RuntimeError(
                f"Shared-memory observation header size is {header_size}, "
                f"expected {SHARED_MEMORY_HEADER_SIZE}."
            )
        if slot_count != SHARED_MEMORY_SLOT_COUNT:
            raise RuntimeError(
                f"Shared-memory observation slot count is {slot_count}, "
                f"expected {SHARED_MEMORY_SLOT_COUNT}."
            )
        if not 0 <= slot < slot_count:
            raise RuntimeError(f"Shared-memory observation slot {slot} is outside [0, {slot_count}).")
        if published_size != payload_size:
            raise RuntimeError(
                f"Shared-memory observation payload has {published_size} bytes, expected {payload_size}."
            )
        if sequence <= 0:
            raise RuntimeError("Shared-memory observation mapping has not published a frame yet.")
        if sequence < self._last_sequence:
            raise RuntimeError(
                f"Shared-memory observation sequence moved backwards from "
                f"{self._last_sequence} to {sequence}."
            )
        self._last_sequence = sequence

        slot_start = SHARED_MEMORY_HEADER_SIZE + slot * payload_size
        slot_end = slot_start + payload_size
        slot_view = memoryview(mapping)[slot_start:slot_end]
        cursor = 0

        continuous = slot_view[cursor:cursor + continuous_bytes]
        cursor += continuous_bytes
        discrete = slot_view[cursor:cursor + discrete_bytes]
        cursor += discrete_bytes

        visual: dict[str, memoryview] = {}
        for key, length in visual_lengths:
            visual[key] = slot_view[cursor:cursor + length]
            cursor += length

        if cursor != payload_size:
            raise RuntimeError(
                f"Shared-memory observation layout consumed {cursor} bytes, expected {payload_size}."
            )

        return SharedObservationPayloads(
            continuous=continuous,
            discrete=discrete,
            visual=visual,
            sequence=sequence,
            slot=slot,
        )

    def close(self) -> None:
        mappings = self._mappings
        self._mappings = {}
        for mapping in mappings.values():
            try:
                mapping.close()
            except BufferError:
                # A returned NumPy array still exports this mapping.  Dropping
                # our reference lets the array own its remaining lifetime.
                pass

    def _mapping_for_payload(self, payload_size: int) -> mmap.mmap:
        mapping = self._mappings.get(payload_size)
        if mapping is not None:
            return mapping

        capacity = SHARED_MEMORY_HEADER_SIZE + SHARED_MEMORY_SLOT_COUNT * payload_size
        name = shared_memory_mapping_name(self.channel, payload_size)
        try:
            mapping = mmap.mmap(
                -1,
                capacity,
                tagname=name,
                access=mmap.ACCESS_READ,
            )
        except OSError as exc:
            raise RuntimeError(
                f"Could not open Unity shared-memory observation mapping '{name}' "
                f"with capacity {capacity} bytes."
            ) from exc

        self._mappings[payload_size] = mapping
        return mapping

    @staticmethod
    def _read_stable_header(mapping: mmap.mmap):
        # The synchronous HTTP response means the writer should be idle here.
        # Reading twice still protects against accidental concurrent access.
        for _ in range(8):
            first = _HEADER.unpack_from(mapping, 0)
            second = _HEADER.unpack_from(mapping, 0)
            if first == second:
                return first
        raise RuntimeError("Shared-memory observation header changed while it was being read.")

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()

