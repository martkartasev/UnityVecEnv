import subprocess

from unity_vecenv.environment.protocol_constants import (
    ARG_AGENT_COUNT,
    ARG_CHANNEL,
    ARG_DECISION_PERIOD,
    ARG_SCENE,
    ARG_TIMEOUT,
    ARG_TIMESCALE,
)


def start_unity_process(executable_path: str,
                        nr_agents: int = 1,
                        port: int = 10000,
                        log_file: str = "./",
                        timescale: float = 1,
                        decision_period: int = 10,
                        timeout_ms: int = 0,
                        no_graphics: bool = True,
                        scene_load: str = ""):
    args = [executable_path,
            ARG_AGENT_COUNT, str(nr_agents),
            ARG_CHANNEL, str(port),
            ARG_TIMEOUT, str(timeout_ms)
            ]
    if log_file != "":
        args += ["-logfile", log_file + str(port) + ".log"]

    if timescale != 1:
        args += [ARG_TIMESCALE, str(timescale)]

    if scene_load != "":
        args += [ARG_SCENE, str(scene_load)]

    if decision_period != 10:
        args += [ARG_DECISION_PERIOD, str(decision_period)]

    if no_graphics:
        args += ["-headless", "-batchmode", "-nographics"]

    popen = subprocess.Popen(args)
    print("Started Unity process on port {}".format(port))
    return popen

