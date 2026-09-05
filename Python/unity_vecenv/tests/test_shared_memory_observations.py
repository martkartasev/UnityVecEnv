import gc
import mmap
import struct
import sys

import numpy as np
import pytest
from gymnasium import spaces

from unity_vecenv.environment.shared_memory_observations import (
    SHARED_MEMORY_HEADER_SIZE,
    SHARED_MEMORY_MAGIC,
    SHARED_MEMORY_SLOT_COUNT,
    SHARED_MEMORY_VERSION,
    SharedMemoryObservationReader,
    shared_memory_mapping_name,
)
from unity_vecenv.environment.unity_process import start_unity_process
from unity_vecenv.environment.unity_vector_env import UnityVectorEnv
from unity_vecenv.protobuf_gen.communication_pb2 import (
    BatchedObservation,
    BatchedResetResults,
    BatchedVisualObservation,
    VisualObservationSpace,
)


pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="Windows named mappings only")

_HEADER = struct.Struct("<8sIIIIQQ")


def _create_published_mapping(channel, payload, *, slot=0, sequence=1):
    payload = bytes(payload)
    capacity = SHARED_MEMORY_HEADER_SIZE + SHARED_MEMORY_SLOT_COUNT * len(payload)
    mapping = mmap.mmap(
        -1,
        capacity,
        tagname=shared_memory_mapping_name(channel, len(payload)),
        access=mmap.ACCESS_WRITE,
    )
    _HEADER.pack_into(
        mapping,
        0,
        SHARED_MEMORY_MAGIC,
        SHARED_MEMORY_VERSION,
        SHARED_MEMORY_HEADER_SIZE,
        SHARED_MEMORY_SLOT_COUNT,
        slot,
        len(payload),
        sequence,
    )
    start = SHARED_MEMORY_HEADER_SIZE + slot * len(payload)
    mapping[start:start + len(payload)] = payload
    return mapping, start


def _make_decoder(channel, visual_spec):
    env = UnityVectorEnv.__new__(UnityVectorEnv)
    env.num_envs = 2
    env._shared_observation_reader = SharedMemoryObservationReader(channel)
    env._visual_observation_specs = {visual_spec.name: visual_spec}
    env._state_observation_space = spaces.Dict(
        {
            "continuous": spaces.Box(-1.0, 1.0, shape=(3,), dtype=np.float32),
            "discrete": spaces.MultiDiscrete([4, 5]),
        }
    )
    env.single_observation_space = spaces.Dict(
        {
            "state": env._state_observation_space,
            visual_spec.name: spaces.Box(0, 255, shape=tuple(visual_spec.shape), dtype=np.uint8),
        }
    )
    return env


def test_reset_decodes_zero_copy_typed_views_from_shared_memory():
    channel = 730000 + (id(object()) % 100000)
    visual_spec = VisualObservationSpace(
        name="camera",
        shape=[2, 2, 1],
        dataType=0,
        low=0,
        high=255,
    )
    continuous = np.arange(6, dtype="<f4").reshape(2, 3) / 10
    discrete = np.arange(4, dtype="<i4").reshape(2, 2)
    visual = np.arange(8, dtype=np.uint8).reshape(2, 2, 2, 1)
    payload = continuous.tobytes() + discrete.tobytes() + visual.tobytes()
    producer, slot_start = _create_published_mapping(channel, payload, slot=1, sequence=7)

    observation = BatchedObservation(
        num_envs=2,
        continuous_size=3,
        discrete_size=2,
        visual=[BatchedVisualObservation(name="camera")],
    )
    env = _make_decoder(channel, visual_spec)

    obs, info = env.reset_result_to_numpy(BatchedResetResults(observation=observation), 2)

    np.testing.assert_array_equal(obs["state"]["continuous"], continuous)
    np.testing.assert_array_equal(obs["state"]["discrete"], discrete)
    np.testing.assert_array_equal(obs["camera"], visual)
    assert info == {}
    assert not obs["state"]["continuous"].flags.owndata
    assert not obs["state"]["discrete"].flags.owndata
    assert not obs["camera"].flags.owndata
    assert not obs["camera"].flags.writeable

    visual_offset = slot_start + continuous.nbytes + discrete.nbytes
    producer[visual_offset] = 231
    assert obs["camera"].reshape(-1)[0] == 231

    del obs
    gc.collect()
    env._shared_observation_reader.close()
    producer.close()


def test_reader_uses_inline_protobuf_as_transparent_fallback():
    visual_spec = VisualObservationSpace(name="camera", shape=[1, 1, 1], dataType=0)
    reader = SharedMemoryObservationReader(739999)
    observation = BatchedObservation(
        num_envs=1,
        continuous_size=1,
        continuous_f32=np.asarray([0.5], dtype="<f4").tobytes(),
        visual=[BatchedVisualObservation(name="camera", data=b"\x07")],
    )

    assert reader.read(observation, {"camera": visual_spec}, 1) is None
    reader.close()


@pytest.mark.parametrize("reader_enabled", [False, True])
def test_reset_decodes_owned_inline_scalar_buffers(reader_enabled):
    continuous = np.arange(6, dtype="<f4").reshape(2, 3) / 10
    discrete = np.arange(4, dtype="<i4").reshape(2, 2)
    env = UnityVectorEnv.__new__(UnityVectorEnv)
    env.num_envs = 2
    env._shared_observation_reader = (
        SharedMemoryObservationReader(749998) if reader_enabled else None
    )
    env._visual_observation_specs = {}
    env._state_observation_space = spaces.Dict(
        {
            "continuous": spaces.Box(-1.0, 1.0, shape=(3,), dtype=np.float32),
            "discrete": spaces.MultiDiscrete([4, 5]),
        }
    )
    env.single_observation_space = env._state_observation_space
    observation = BatchedObservation(
        num_envs=2,
        continuous_size=3,
        continuous_f32=continuous.tobytes(),
        discrete_size=2,
        discrete_i32=discrete.tobytes(),
    )

    obs, info = env.reset_result_to_numpy(BatchedResetResults(observation=observation), 2)

    np.testing.assert_array_equal(obs["continuous"], continuous)
    np.testing.assert_array_equal(obs["discrete"], discrete)
    assert info == {}
    assert obs["continuous"].flags.owndata
    assert obs["continuous"].flags.writeable
    assert obs["discrete"].flags.owndata
    assert obs["discrete"].flags.writeable
    if env._shared_observation_reader is not None:
        env._shared_observation_reader.close()


def test_double_buffer_lifetime_and_close_with_exported_views():
    channel = 750000 + (id(object()) % 100000)
    first_value = np.asarray([1.25], dtype="<f4")
    producer, first_offset = _create_published_mapping(
        channel,
        first_value.tobytes(),
        slot=0,
        sequence=1,
    )
    reader = SharedMemoryObservationReader(channel)
    observation = BatchedObservation(num_envs=1, continuous_size=1)

    first_payload = reader.read(observation, {}, 1)
    first = np.frombuffer(first_payload.continuous, dtype="<f4")

    second_value = np.asarray([2.5], dtype="<f4")
    second_offset = SHARED_MEMORY_HEADER_SIZE + second_value.nbytes
    producer[second_offset:second_offset + second_value.nbytes] = second_value.tobytes()
    _HEADER.pack_into(
        producer,
        0,
        SHARED_MEMORY_MAGIC,
        SHARED_MEMORY_VERSION,
        SHARED_MEMORY_HEADER_SIZE,
        SHARED_MEMORY_SLOT_COUNT,
        1,
        second_value.nbytes,
        2,
    )
    second_payload = reader.read(observation, {}, 1)
    second = np.frombuffer(second_payload.continuous, dtype="<f4")

    np.testing.assert_array_equal(first, first_value)
    np.testing.assert_array_equal(second, second_value)

    third_value = np.asarray([3.75], dtype="<f4")
    producer[first_offset:first_offset + third_value.nbytes] = third_value.tobytes()
    _HEADER.pack_into(
        producer,
        0,
        SHARED_MEMORY_MAGIC,
        SHARED_MEMORY_VERSION,
        SHARED_MEMORY_HEADER_SIZE,
        SHARED_MEMORY_SLOT_COUNT,
        0,
        third_value.nbytes,
        3,
    )
    third_payload = reader.read(observation, {}, 1)
    third = np.frombuffer(third_payload.continuous, dtype="<f4")

    np.testing.assert_array_equal(first, third_value)
    np.testing.assert_array_equal(second, second_value)
    np.testing.assert_array_equal(third, third_value)

    reader.close()
    np.testing.assert_array_equal(second, second_value)
    producer.close()


def test_reader_rejects_an_invalid_header():
    channel = 740000 + (id(object()) % 100000)
    spec = VisualObservationSpace(name="camera", shape=[1, 1, 1], dataType=0)
    producer, _ = _create_published_mapping(channel, b"\x01")
    producer[0:8] = b"BROKEN!!"
    reader = SharedMemoryObservationReader(channel)
    observation = BatchedObservation(
        num_envs=1,
        visual=[BatchedVisualObservation(name="camera")],
    )

    with pytest.raises(RuntimeError, match="invalid magic"):
        reader.read(observation, {"camera": spec}, 1)

    reader.close()
    producer.close()


def test_process_launch_adds_shared_memory_flag(monkeypatch):
    captured = {}

    class FakeProcess:
        pass

    def fake_popen(args):
        captured["args"] = args
        return FakeProcess()

    monkeypatch.setattr("unity_vecenv.environment.unity_process.subprocess.Popen", fake_popen)
    process = start_unity_process(
        "UnityEnvironment.exe",
        batch_mode=False,
        no_graphics=False,
        shared_memory_observations=True,
    )

    assert isinstance(process, FakeProcess)
    assert "-sharedmemoryobservations" in captured["args"]
