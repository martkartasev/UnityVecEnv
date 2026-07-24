from importlib.metadata import version

import unity_vecenv
from unity_vecenv.environment import FlattenedVectorEnvThreaded, UnityVectorEnv


def test_public_environment_exports():
    assert unity_vecenv.UnityVectorEnv is UnityVectorEnv
    assert unity_vecenv.FlattenedVectorEnvThreaded is FlattenedVectorEnvThreaded


def test_version_matches_installed_distribution():
    assert unity_vecenv.__version__ == version("unity-vecenv")
