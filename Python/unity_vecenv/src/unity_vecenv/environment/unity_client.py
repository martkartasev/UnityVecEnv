import time

import requests
from google.protobuf.message import DecodeError

from unity_vecenv.protobuf_gen.communication_pb2 import Reset, Observations, Step, StepResults, EnvironmentDescription, InitializeEnvironments


def start_client(port: int = 50010):
    client = SimClient(port)
    print("Client started, configured for port " + str(port))  # Confirm server started
    return client


class SimClient:
    def __init__(self, port):
        self.port = port

    def initialize(self, message: InitializeEnvironments) -> EnvironmentDescription:
        attempts = 0
        environment_description = None

        while attempts < 20:
            try:
                obs_bytes = self.do_request(InitializeEnvironments.SerializeToString(message), "initialize", timeout=30)
                environment_description = EnvironmentDescription.FromString(obs_bytes)
                break
            except DecodeError:
                print("Bad host issue, retrying...")
                time.sleep(1)
                attempts += 1

        if attempts >= 20:
            raise RuntimeError("Failed to initialize environment connection")

        return environment_description

    def reset(self, message: Reset) -> Observations:
        obs_bytes = self.do_request(Reset.SerializeToString(message), "reset", timeout=30)
        observations = Observations.FromString(obs_bytes)

        return observations

    def step(self, message: Step) -> StepResults:
        obs_bytes = self.do_request(Step.SerializeToString(message), "step", timeout=30)
        observations = StepResults.FromString(obs_bytes)
        return observations


    def do_request(self, msg, method, **kwargs):
        attempts = 0
        while attempts < 20:
            try:
                response = requests.post(
                    f'http://localhost:{self.port}/{method}',
                    data=msg,
                    headers={
                        'Content-Type': 'application/octet-stream',
                    },
                    **kwargs
                )
                response.raise_for_status()
                return response.content
            except (ConnectionRefusedError, ConnectionError, requests.exceptions.ConnectionError):
                print("Connection refused, retrying...")
                time.sleep(1)
                attempts += 1

        print("Failed to connect after multiple attempts.")
        return None
