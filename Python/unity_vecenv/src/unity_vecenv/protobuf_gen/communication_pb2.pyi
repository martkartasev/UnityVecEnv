from google.protobuf.internal import containers as _containers
from google.protobuf.internal import enum_type_wrapper as _enum_type_wrapper
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class AutoResetMode(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    NextStep: _ClassVar[AutoResetMode]
    SameStep: _ClassVar[AutoResetMode]
    Disabled: _ClassVar[AutoResetMode]

class VisualObservationDataType(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    UInt8: _ClassVar[VisualObservationDataType]
    Float32: _ClassVar[VisualObservationDataType]
NextStep: AutoResetMode
SameStep: AutoResetMode
Disabled: AutoResetMode
UInt8: VisualObservationDataType
Float32: VisualObservationDataType

class InitializeEnvironments(_message.Message):
    __slots__ = ("autoResetMode", "requestedNumberOfEnvs")
    AUTORESETMODE_FIELD_NUMBER: _ClassVar[int]
    REQUESTEDNUMBEROFENVS_FIELD_NUMBER: _ClassVar[int]
    autoResetMode: AutoResetMode
    requestedNumberOfEnvs: int
    def __init__(self, autoResetMode: _Optional[_Union[AutoResetMode, str]] = ..., requestedNumberOfEnvs: _Optional[int] = ...) -> None: ...

class EnvironmentDescription(_message.Message):
    __slots__ = ("singleObservationSpace", "singleActionSpace", "trueNumberOfEnvs", "singleVisualObservationSpace", "parameters")
    SINGLEOBSERVATIONSPACE_FIELD_NUMBER: _ClassVar[int]
    SINGLEACTIONSPACE_FIELD_NUMBER: _ClassVar[int]
    TRUENUMBEROFENVS_FIELD_NUMBER: _ClassVar[int]
    SINGLEVISUALOBSERVATIONSPACE_FIELD_NUMBER: _ClassVar[int]
    PARAMETERS_FIELD_NUMBER: _ClassVar[int]
    singleObservationSpace: _containers.RepeatedCompositeFieldContainer[Space]
    singleActionSpace: _containers.RepeatedCompositeFieldContainer[Space]
    trueNumberOfEnvs: int
    singleVisualObservationSpace: _containers.RepeatedCompositeFieldContainer[VisualObservationSpace]
    parameters: _containers.RepeatedCompositeFieldContainer[EnvironmentParameter]
    def __init__(self, singleObservationSpace: _Optional[_Iterable[_Union[Space, _Mapping]]] = ..., singleActionSpace: _Optional[_Iterable[_Union[Space, _Mapping]]] = ..., trueNumberOfEnvs: _Optional[int] = ..., singleVisualObservationSpace: _Optional[_Iterable[_Union[VisualObservationSpace, _Mapping]]] = ..., parameters: _Optional[_Iterable[_Union[EnvironmentParameter, _Mapping]]] = ...) -> None: ...

class EnvironmentParameter(_message.Message):
    __slots__ = ("key", "string_value", "float_value", "int_value")
    KEY_FIELD_NUMBER: _ClassVar[int]
    STRING_VALUE_FIELD_NUMBER: _ClassVar[int]
    FLOAT_VALUE_FIELD_NUMBER: _ClassVar[int]
    INT_VALUE_FIELD_NUMBER: _ClassVar[int]
    key: str
    string_value: str
    float_value: float
    int_value: int
    def __init__(self, key: _Optional[str] = ..., string_value: _Optional[str] = ..., float_value: _Optional[float] = ..., int_value: _Optional[int] = ...) -> None: ...

class Space(_message.Message):
    __slots__ = ("name", "continuousSize", "continuousRange", "discreteSize")
    NAME_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUSSIZE_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUSRANGE_FIELD_NUMBER: _ClassVar[int]
    DISCRETESIZE_FIELD_NUMBER: _ClassVar[int]
    name: str
    continuousSize: int
    continuousRange: _containers.RepeatedCompositeFieldContainer[MinMax]
    discreteSize: _containers.RepeatedScalarFieldContainer[int]
    def __init__(self, name: _Optional[str] = ..., continuousSize: _Optional[int] = ..., continuousRange: _Optional[_Iterable[_Union[MinMax, _Mapping]]] = ..., discreteSize: _Optional[_Iterable[int]] = ...) -> None: ...

class MinMax(_message.Message):
    __slots__ = ("index", "minValue", "maxValue")
    INDEX_FIELD_NUMBER: _ClassVar[int]
    MINVALUE_FIELD_NUMBER: _ClassVar[int]
    MAXVALUE_FIELD_NUMBER: _ClassVar[int]
    index: int
    minValue: float
    maxValue: float
    def __init__(self, index: _Optional[int] = ..., minValue: _Optional[float] = ..., maxValue: _Optional[float] = ...) -> None: ...

class Reset(_message.Message):
    __slots__ = ("envsToReset", "reloadScene")
    ENVSTORESET_FIELD_NUMBER: _ClassVar[int]
    RELOADSCENE_FIELD_NUMBER: _ClassVar[int]
    envsToReset: _containers.RepeatedCompositeFieldContainer[ResetParameters]
    reloadScene: bool
    def __init__(self, envsToReset: _Optional[_Iterable[_Union[ResetParameters, _Mapping]]] = ..., reloadScene: bool = ...) -> None: ...

class ResetParameters(_message.Message):
    __slots__ = ("index", "continuous")
    INDEX_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUS_FIELD_NUMBER: _ClassVar[int]
    index: int
    continuous: _containers.RepeatedScalarFieldContainer[float]
    def __init__(self, index: _Optional[int] = ..., continuous: _Optional[_Iterable[float]] = ...) -> None: ...

class Step(_message.Message):
    __slots__ = ("actions", "stepCount", "timeScale", "applyActionEveryPhysicsStep")
    ACTIONS_FIELD_NUMBER: _ClassVar[int]
    STEPCOUNT_FIELD_NUMBER: _ClassVar[int]
    TIMESCALE_FIELD_NUMBER: _ClassVar[int]
    APPLYACTIONEVERYPHYSICSSTEP_FIELD_NUMBER: _ClassVar[int]
    actions: _containers.RepeatedCompositeFieldContainer[Action]
    stepCount: int
    timeScale: float
    applyActionEveryPhysicsStep: bool
    def __init__(self, actions: _Optional[_Iterable[_Union[Action, _Mapping]]] = ..., stepCount: _Optional[int] = ..., timeScale: _Optional[float] = ..., applyActionEveryPhysicsStep: bool = ...) -> None: ...

class Action(_message.Message):
    __slots__ = ("agentIndex", "continuous", "discrete")
    AGENTINDEX_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUS_FIELD_NUMBER: _ClassVar[int]
    DISCRETE_FIELD_NUMBER: _ClassVar[int]
    agentIndex: int
    continuous: _containers.RepeatedScalarFieldContainer[float]
    discrete: _containers.RepeatedScalarFieldContainer[int]
    def __init__(self, agentIndex: _Optional[int] = ..., continuous: _Optional[_Iterable[float]] = ..., discrete: _Optional[_Iterable[int]] = ...) -> None: ...

class Observation(_message.Message):
    __slots__ = ("index", "continuous", "discrete")
    INDEX_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUS_FIELD_NUMBER: _ClassVar[int]
    DISCRETE_FIELD_NUMBER: _ClassVar[int]
    index: int
    continuous: _containers.RepeatedScalarFieldContainer[float]
    discrete: _containers.RepeatedScalarFieldContainer[int]
    def __init__(self, index: _Optional[int] = ..., continuous: _Optional[_Iterable[float]] = ..., discrete: _Optional[_Iterable[int]] = ...) -> None: ...

class VisualObservationSpace(_message.Message):
    __slots__ = ("name", "shape", "dataType", "low", "high")
    NAME_FIELD_NUMBER: _ClassVar[int]
    SHAPE_FIELD_NUMBER: _ClassVar[int]
    DATATYPE_FIELD_NUMBER: _ClassVar[int]
    LOW_FIELD_NUMBER: _ClassVar[int]
    HIGH_FIELD_NUMBER: _ClassVar[int]
    name: str
    shape: _containers.RepeatedScalarFieldContainer[int]
    dataType: VisualObservationDataType
    low: float
    high: float
    def __init__(self, name: _Optional[str] = ..., shape: _Optional[_Iterable[int]] = ..., dataType: _Optional[_Union[VisualObservationDataType, str]] = ..., low: _Optional[float] = ..., high: _Optional[float] = ...) -> None: ...

class BatchedVisualObservation(_message.Message):
    __slots__ = ("name", "data")
    NAME_FIELD_NUMBER: _ClassVar[int]
    DATA_FIELD_NUMBER: _ClassVar[int]
    name: str
    data: bytes
    def __init__(self, name: _Optional[str] = ..., data: _Optional[bytes] = ...) -> None: ...

class BatchedObservation(_message.Message):
    __slots__ = ("num_envs", "continuous_size", "discrete_size", "continuous_f32", "discrete_i32", "visual")
    NUM_ENVS_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUS_SIZE_FIELD_NUMBER: _ClassVar[int]
    DISCRETE_SIZE_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUS_F32_FIELD_NUMBER: _ClassVar[int]
    DISCRETE_I32_FIELD_NUMBER: _ClassVar[int]
    VISUAL_FIELD_NUMBER: _ClassVar[int]
    num_envs: int
    continuous_size: int
    discrete_size: int
    continuous_f32: bytes
    discrete_i32: bytes
    visual: _containers.RepeatedCompositeFieldContainer[BatchedVisualObservation]
    def __init__(self, num_envs: _Optional[int] = ..., continuous_size: _Optional[int] = ..., discrete_size: _Optional[int] = ..., continuous_f32: _Optional[bytes] = ..., discrete_i32: _Optional[bytes] = ..., visual: _Optional[_Iterable[_Union[BatchedVisualObservation, _Mapping]]] = ...) -> None: ...

class BatchedCustomInfo(_message.Message):
    __slots__ = ("keys", "values_f32", "present", "keys_i32", "values_i32", "present_i32", "keys_bool", "values_bool", "present_bool", "keys_vector3", "values_vector3_f32", "present_vector3", "keys_quaternion", "values_quaternion_f32", "present_quaternion")
    KEYS_FIELD_NUMBER: _ClassVar[int]
    VALUES_F32_FIELD_NUMBER: _ClassVar[int]
    PRESENT_FIELD_NUMBER: _ClassVar[int]
    KEYS_I32_FIELD_NUMBER: _ClassVar[int]
    VALUES_I32_FIELD_NUMBER: _ClassVar[int]
    PRESENT_I32_FIELD_NUMBER: _ClassVar[int]
    KEYS_BOOL_FIELD_NUMBER: _ClassVar[int]
    VALUES_BOOL_FIELD_NUMBER: _ClassVar[int]
    PRESENT_BOOL_FIELD_NUMBER: _ClassVar[int]
    KEYS_VECTOR3_FIELD_NUMBER: _ClassVar[int]
    VALUES_VECTOR3_F32_FIELD_NUMBER: _ClassVar[int]
    PRESENT_VECTOR3_FIELD_NUMBER: _ClassVar[int]
    KEYS_QUATERNION_FIELD_NUMBER: _ClassVar[int]
    VALUES_QUATERNION_F32_FIELD_NUMBER: _ClassVar[int]
    PRESENT_QUATERNION_FIELD_NUMBER: _ClassVar[int]
    keys: _containers.RepeatedScalarFieldContainer[str]
    values_f32: bytes
    present: bytes
    keys_i32: _containers.RepeatedScalarFieldContainer[str]
    values_i32: bytes
    present_i32: bytes
    keys_bool: _containers.RepeatedScalarFieldContainer[str]
    values_bool: bytes
    present_bool: bytes
    keys_vector3: _containers.RepeatedScalarFieldContainer[str]
    values_vector3_f32: bytes
    present_vector3: bytes
    keys_quaternion: _containers.RepeatedScalarFieldContainer[str]
    values_quaternion_f32: bytes
    present_quaternion: bytes
    def __init__(self, keys: _Optional[_Iterable[str]] = ..., values_f32: _Optional[bytes] = ..., present: _Optional[bytes] = ..., keys_i32: _Optional[_Iterable[str]] = ..., values_i32: _Optional[bytes] = ..., present_i32: _Optional[bytes] = ..., keys_bool: _Optional[_Iterable[str]] = ..., values_bool: _Optional[bytes] = ..., present_bool: _Optional[bytes] = ..., keys_vector3: _Optional[_Iterable[str]] = ..., values_vector3_f32: _Optional[bytes] = ..., present_vector3: _Optional[bytes] = ..., keys_quaternion: _Optional[_Iterable[str]] = ..., values_quaternion_f32: _Optional[bytes] = ..., present_quaternion: _Optional[bytes] = ...) -> None: ...

class BatchedFinalInfo(_message.Message):
    __slots__ = ("final_observation",)
    FINAL_OBSERVATION_FIELD_NUMBER: _ClassVar[int]
    final_observation: BatchedObservation
    def __init__(self, final_observation: _Optional[_Union[BatchedObservation, _Mapping]] = ...) -> None: ...

class BatchedStepResults(_message.Message):
    __slots__ = ("observation", "rewards_f32", "dones", "truncates", "custom_indices_i32", "custom", "final_indices_i32", "final_info")
    OBSERVATION_FIELD_NUMBER: _ClassVar[int]
    REWARDS_F32_FIELD_NUMBER: _ClassVar[int]
    DONES_FIELD_NUMBER: _ClassVar[int]
    TRUNCATES_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_INDICES_I32_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_FIELD_NUMBER: _ClassVar[int]
    FINAL_INDICES_I32_FIELD_NUMBER: _ClassVar[int]
    FINAL_INFO_FIELD_NUMBER: _ClassVar[int]
    observation: BatchedObservation
    rewards_f32: bytes
    dones: bytes
    truncates: bytes
    custom_indices_i32: bytes
    custom: BatchedCustomInfo
    final_indices_i32: bytes
    final_info: BatchedFinalInfo
    def __init__(self, observation: _Optional[_Union[BatchedObservation, _Mapping]] = ..., rewards_f32: _Optional[bytes] = ..., dones: _Optional[bytes] = ..., truncates: _Optional[bytes] = ..., custom_indices_i32: _Optional[bytes] = ..., custom: _Optional[_Union[BatchedCustomInfo, _Mapping]] = ..., final_indices_i32: _Optional[bytes] = ..., final_info: _Optional[_Union[BatchedFinalInfo, _Mapping]] = ...) -> None: ...

class BatchedResetResults(_message.Message):
    __slots__ = ("observation", "custom_indices_i32", "custom")
    OBSERVATION_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_INDICES_I32_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_FIELD_NUMBER: _ClassVar[int]
    observation: BatchedObservation
    custom_indices_i32: bytes
    custom: BatchedCustomInfo
    def __init__(self, observation: _Optional[_Union[BatchedObservation, _Mapping]] = ..., custom_indices_i32: _Optional[bytes] = ..., custom: _Optional[_Union[BatchedCustomInfo, _Mapping]] = ...) -> None: ...

class Screenshot(_message.Message):
    __slots__ = ("camera",)
    CAMERA_FIELD_NUMBER: _ClassVar[int]
    camera: Transform
    def __init__(self, camera: _Optional[_Union[Transform, _Mapping]] = ...) -> None: ...

class Transform(_message.Message):
    __slots__ = ("position", "euler", "orientation")
    POSITION_FIELD_NUMBER: _ClassVar[int]
    EULER_FIELD_NUMBER: _ClassVar[int]
    ORIENTATION_FIELD_NUMBER: _ClassVar[int]
    position: Vector3
    euler: Vector3
    orientation: Quaternion
    def __init__(self, position: _Optional[_Union[Vector3, _Mapping]] = ..., euler: _Optional[_Union[Vector3, _Mapping]] = ..., orientation: _Optional[_Union[Quaternion, _Mapping]] = ...) -> None: ...

class Vector3(_message.Message):
    __slots__ = ("x", "y", "z")
    X_FIELD_NUMBER: _ClassVar[int]
    Y_FIELD_NUMBER: _ClassVar[int]
    Z_FIELD_NUMBER: _ClassVar[int]
    x: float
    y: float
    z: float
    def __init__(self, x: _Optional[float] = ..., y: _Optional[float] = ..., z: _Optional[float] = ...) -> None: ...

class Quaternion(_message.Message):
    __slots__ = ("x", "y", "z", "w")
    X_FIELD_NUMBER: _ClassVar[int]
    Y_FIELD_NUMBER: _ClassVar[int]
    Z_FIELD_NUMBER: _ClassVar[int]
    W_FIELD_NUMBER: _ClassVar[int]
    x: float
    y: float
    z: float
    w: float
    def __init__(self, x: _Optional[float] = ..., y: _Optional[float] = ..., z: _Optional[float] = ..., w: _Optional[float] = ...) -> None: ...
