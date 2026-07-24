from importlib.metadata import PackageNotFoundError, version

from unity_vecenv.environment import FlattenedVectorEnvThreaded, UnityVectorEnv

try:
    __version__ = version("unity-vecenv")
except PackageNotFoundError:
    __version__ = "0+unknown"

__all__ = [
    "FlattenedVectorEnvThreaded",
    "UnityVectorEnv",
    "__version__",
]
