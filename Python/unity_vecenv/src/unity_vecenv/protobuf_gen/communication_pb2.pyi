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
NextStep: AutoResetMode
SameStep: AutoResetMode
Disabled: AutoResetMode

class InitializeEnvironments(_message.Message):
    __slots__ = ("autoResetMode", "requestedNumberOfEnvs")
    AUTORESETMODE_FIELD_NUMBER: _ClassVar[int]
    REQUESTEDNUMBEROFENVS_FIELD_NUMBER: _ClassVar[int]
    autoResetMode: AutoResetMode
    requestedNumberOfEnvs: int
    def __init__(self, autoResetMode: _Optional[_Union[AutoResetMode, str]] = ..., requestedNumberOfEnvs: _Optional[int] = ...) -> None: ...

class EnvironmentDescription(_message.Message):
    __slots__ = ("singleObservationSpace", "singleActionSpace", "trueNumberOfEnvs")
    SINGLEOBSERVATIONSPACE_FIELD_NUMBER: _ClassVar[int]
    SINGLEACTIONSPACE_FIELD_NUMBER: _ClassVar[int]
    TRUENUMBEROFENVS_FIELD_NUMBER: _ClassVar[int]
    singleObservationSpace: Space
    singleActionSpace: Space
    trueNumberOfEnvs: int
    def __init__(self, singleObservationSpace: _Optional[_Union[Space, _Mapping]] = ..., singleActionSpace: _Optional[_Union[Space, _Mapping]] = ..., trueNumberOfEnvs: _Optional[int] = ...) -> None: ...

class Space(_message.Message):
    __slots__ = ("continuousSize", "continuousRange", "discreteSize")
    CONTINUOUSSIZE_FIELD_NUMBER: _ClassVar[int]
    CONTINUOUSRANGE_FIELD_NUMBER: _ClassVar[int]
    DISCRETESIZE_FIELD_NUMBER: _ClassVar[int]
    continuousSize: int
    continuousRange: MinMax
    discreteSize: int
    def __init__(self, continuousSize: _Optional[int] = ..., continuousRange: _Optional[_Union[MinMax, _Mapping]] = ..., discreteSize: _Optional[int] = ...) -> None: ...

class MinMax(_message.Message):
    __slots__ = ("minValue", "maxValue")
    MINVALUE_FIELD_NUMBER: _ClassVar[int]
    MAXVALUE_FIELD_NUMBER: _ClassVar[int]
    minValue: float
    maxValue: float
    def __init__(self, minValue: _Optional[float] = ..., maxValue: _Optional[float] = ...) -> None: ...

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

class Observations(_message.Message):
    __slots__ = ("observations",)
    OBSERVATIONS_FIELD_NUMBER: _ClassVar[int]
    observations: _containers.RepeatedCompositeFieldContainer[Observation]
    def __init__(self, observations: _Optional[_Iterable[_Union[Observation, _Mapping]]] = ...) -> None: ...

class StepResult(_message.Message):
    __slots__ = ("observation", "reward", "done", "truncated")
    OBSERVATION_FIELD_NUMBER: _ClassVar[int]
    REWARD_FIELD_NUMBER: _ClassVar[int]
    DONE_FIELD_NUMBER: _ClassVar[int]
    TRUNCATED_FIELD_NUMBER: _ClassVar[int]
    observation: Observation
    reward: float
    done: bool
    truncated: bool
    def __init__(self, observation: _Optional[_Union[Observation, _Mapping]] = ..., reward: _Optional[float] = ..., done: bool = ..., truncated: bool = ...) -> None: ...

class StepResults(_message.Message):
    __slots__ = ("stepResults", "infos")
    STEPRESULTS_FIELD_NUMBER: _ClassVar[int]
    INFOS_FIELD_NUMBER: _ClassVar[int]
    stepResults: _containers.RepeatedCompositeFieldContainer[StepResult]
    infos: Info
    def __init__(self, stepResults: _Optional[_Iterable[_Union[StepResult, _Mapping]]] = ..., infos: _Optional[_Union[Info, _Mapping]] = ...) -> None: ...

class Screenshot(_message.Message):
    __slots__ = ("camera",)
    CAMERA_FIELD_NUMBER: _ClassVar[int]
    camera: Transform
    def __init__(self, camera: _Optional[_Union[Transform, _Mapping]] = ...) -> None: ...

class Info(_message.Message):
    __slots__ = ("final_infos", "final_observations", "custom")
    FINAL_INFOS_FIELD_NUMBER: _ClassVar[int]
    FINAL_OBSERVATIONS_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_FIELD_NUMBER: _ClassVar[int]
    final_infos: _containers.RepeatedCompositeFieldContainer[FinalInfo]
    final_observations: _containers.RepeatedCompositeFieldContainer[Observation]
    custom: _containers.RepeatedScalarFieldContainer[float]
    def __init__(self, final_infos: _Optional[_Iterable[_Union[FinalInfo, _Mapping]]] = ..., final_observations: _Optional[_Iterable[_Union[Observation, _Mapping]]] = ..., custom: _Optional[_Iterable[float]] = ...) -> None: ...

class FinalInfo(_message.Message):
    __slots__ = ("agentIndex", "episode_reward", "episode_length", "custom")
    AGENTINDEX_FIELD_NUMBER: _ClassVar[int]
    EPISODE_REWARD_FIELD_NUMBER: _ClassVar[int]
    EPISODE_LENGTH_FIELD_NUMBER: _ClassVar[int]
    CUSTOM_FIELD_NUMBER: _ClassVar[int]
    agentIndex: int
    episode_reward: float
    episode_length: float
    custom: _containers.RepeatedScalarFieldContainer[float]
    def __init__(self, agentIndex: _Optional[int] = ..., episode_reward: _Optional[float] = ..., episode_length: _Optional[float] = ..., custom: _Optional[_Iterable[float]] = ...) -> None: ...

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
