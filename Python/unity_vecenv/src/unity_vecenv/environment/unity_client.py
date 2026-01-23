import time

import requests
import traceback
from google.protobuf.message import DecodeError

from unity_vecenv.protobuf_gen.communication_pb2 import Reset, Observations, Step, StepResults, EnvironmentDescription, InitializeEnvironments


def start_client(port: int = 50010):
    client = SimClient(port)
    print("Client started, configured for port " + str(port))  # Confirm server started
    return client


class SimClient:
    def __init__(self, port):
        self.port = port
        self.session = requests.Session()

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
                response = self.session.post(
                    f'http://127.0.0.1:{self.port}/{method}/',
                    data=msg,
                    headers={
                        'Content-Type': 'application/x-protobuf',
                    },
                    allow_redirects=False,
                    **kwargs
                )
                if response.status_code != 200:
                    print("status:", response.status_code)
                    print("headers:", response.headers.get("Content-Type"), response.headers.get("Location"))
                    print("body head:", response.content[:200])
                    print(socket.getaddrinfo("localhost", self.port))
                response.raise_for_status()
                return response.content
            except (ConnectionRefusedError, ConnectionError, requests.exceptions.ConnectionError,requests.exceptions.RequestException) as e:
                attempts += 1
                # Got to determine if this is a problem long term

        print("Connection failed after multiple attempts.")
        return None
