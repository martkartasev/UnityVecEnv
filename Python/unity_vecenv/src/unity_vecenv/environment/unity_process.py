import os
import subprocess


def start_unity_process(executable_path: str,
                        nr_agents: int = 1,
                        port: int = 10000,
                        log_file: str = "",
                        timescale: float = 1,
                        decision_period: int = 10,
                        no_graphics: bool = True):
    executable_path = os.path.join(os.path.dirname(__file__), executable_path)  # '../../WinBuild/Residual/SAM-RL.exe'
    args = ["C:/Users/Mart9/Workspace/BoundaryValueBTRL/Build/BoundaryValueBTRL.exe",
            "-agents", str(nr_agents),  # Number of agents
            "-channel", str(port),  # Param to change connection port. If you want to start multiple instances
            ]
    if log_file != "":
        args += ["-log", log_file]

    if timescale != 1:
        args += ["-timescale", str(timescale)]

    if decision_period != 10:
        args += ["-decision_period", str(decision_period)]

    if no_graphics:
        args += ["-headless", "-batchmode", "-nographics"]  # "-nographics" causes no renderer

    popen = subprocess.Popen(args)
    print("Started Unity process on port {}".format(port))
    return popen
