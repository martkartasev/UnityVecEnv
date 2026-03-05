import time

import requests
from google.protobuf.message import DecodeError

from unity_vecenv.environment.protocol_constants import (
    CONTENT_TYPE_PROTOBUF,
    DEFAULT_HOST,
    ENDPOINT_INITIALIZE,
    ENDPOINT_RESET,
    ENDPOINT_STEP,
)
from unity_vecenv.protobuf_gen.communication_pb2 import Reset, Observations, Step, StepResults, EnvironmentDescription, InitializeEnvironments


def start_client(port: int = 50010):
    client = SimClient(port)
    print("Client started, configured for port " + str(port))
    return client


class SimClient:
    def __init__(self, port):
        self.port = port
        self.session = requests.Session()

    def initialize(self, message: InitializeEnvironments) -> EnvironmentDescription:
        obs_bytes = self.do_request(InitializeEnvironments.SerializeToString(message), ENDPOINT_INITIALIZE, timeout=30)
        try:
            return EnvironmentDescription.FromString(obs_bytes)
        except DecodeError as exc:
            raise RuntimeError("Failed to decode initialize response") from exc

    def reset(self, message: Reset) -> Observations:
        obs_bytes = self.do_request(Reset.SerializeToString(message), ENDPOINT_RESET, timeout=30)
        try:
            return Observations.FromString(obs_bytes)
        except DecodeError as exc:
            raise RuntimeError("Failed to decode reset response") from exc

    def step(self, message: Step) -> StepResults:
        obs_bytes = self.do_request(Step.SerializeToString(message), ENDPOINT_STEP, timeout=30)
        try:
            return StepResults.FromString(obs_bytes)
        except DecodeError as exc:
            raise RuntimeError("Failed to decode step response") from exc

    def do_request(self, msg, method, **kwargs):
        max_attempts = 20
        retry_delay_sec = 0.25
        last_error = None

        for attempt in range(1, max_attempts + 1):
            try:
                response = self.session.post(
                    f"http://{DEFAULT_HOST}:{self.port}/{method}/",
                    data=msg,
                    headers={
                        "Content-Type": CONTENT_TYPE_PROTOBUF,
                    },
                    allow_redirects=False,
                    **kwargs,
                )

                if response.status_code != 200:
                    print("status:", response.status_code)
                    print("headers:", response.headers.get("Content-Type"), response.headers.get("Location"))
                    print("body head:", response.content[:200])

                response.raise_for_status()
                return response.content
            except requests.exceptions.RequestException as exc:
                last_error = exc
                print(f"Request failed on attempt {attempt}/{max_attempts} at port {self.port}: {exc}")
                if attempt < max_attempts:
                    time.sleep(retry_delay_sec)

        raise RuntimeError(f"Connection failed after {max_attempts} attempts to port {self.port} for method '{method}'") from last_error
