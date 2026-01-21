import pytest

from unity_vecenv.environment.unity_vector_env import UnityVectorEnv


def test_execution():
    env = UnityVectorEnv(start_process=False, num_envs=80)
    reset = env.reset()
    print(reset)
    while True:
        sample = env.action_space.sample()
        obs, dones, rewards, _, _ = env.step(sample)
        print(obs)
