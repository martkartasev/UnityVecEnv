using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Scripts.VecEnv.Message;
using Scripts.VecEnv.Networking;
using UnityEngine;
using UnityEngine.Serialization;
using Action = System.Action;
using Debug = UnityEngine.Debug;
using Info = Scripts.VecEnv.Message.Info;
using Reset = Scripts.VecEnv.Message.Reset;
using Step = Scripts.VecEnv.Message.Step;

namespace Scripts.VecEnv.Core
{
    [DefaultExecutionOrder(-500)]
    public class GymVecEnvManager : MonoBehaviour
    {
        // Lazy initializer pattern, see https://csharpindepth.com/articles/singleton#lazy
        private static Lazy<GymVecEnvManager> _sLazy = new(CreateGymVecEnvManager);
        public static bool IsInitialized => _sLazy.IsValueCreated;
        public static GymVecEnvManager Instance => _sLazy.Value;

        public int physicsStepsPerGymStep = 10;
        public int timeoutMilliseconds = 3000;

        public event Action PreObservation;
        public event Action EarlyObservation;
        public event Action PostInitialize;

        private IExternalCommunication _communicator;
        private List<GymAgent> _agents = new();

        private bool _firstResetComplete;
        private bool _connectionInitialized;
        private bool _gymStepOngoing;
        private bool IsShuttingDown;

        private Step _gymStep;
        private EnvironmentDescription _environmentDescription;
        private Coroutine _disconnectedStepper;
        public AgentSpawner Spawner { get; set; }

        static GymVecEnvManager CreateGymVecEnvManager()
        {
            if (IsInitialized)
            {
                return Instance;
            }

            var manager = new GameObject("AgentManager");
            manager.hideFlags = HideFlags.HideInHierarchy;
            var agentManager = manager.AddComponent<GymVecEnvManager>();

            try
            {
                DontDestroyOnLoad(manager);
            }
            catch
            {
            }

            return agentManager;
        }

        private void Awake()
        {
            _communicator = CommunicatorHttpServer.Instance;
        }


        public void RegisterAgentDescription(GymAgent agentTemplate)
        {
            _environmentDescription = new EnvironmentDescription
            {
                ContinuousObservations = agentTemplate.continuousObservations,
                ContinuousActions = agentTemplate.continuousActions,
                DiscreteActions = agentTemplate.discreteActions.ToArray(),
            };
        }

        public void RegisterAgent(GymAgent externalAgent)
        {
            if (!_agents.Contains(externalAgent))
            {
                externalAgent.AssignIndex(_agents.Count);
                _agents.Add(externalAgent);
            }
        }

        public void UnregisterAgent(GymAgent externalAgent)
        {
            _agents.Remove(externalAgent);
        }

        private void Start()
        {
            _agents.ForEach(agent => agent.DoInitialize());
            PostInitialize?.Invoke();
        }

        public void FixedUpdate()
        {
            if (IsShuttingDown) return;

            Stopwatch timeout = new Stopwatch();
            timeout.Start();

            do
            {
                var fetchInitialize = _communicator.FetchInitialize();
                if (fetchInitialize.HasValue)
                {
                    StartCoroutine(DoInitialize(fetchInitialize.Value, init => _communicator.InitializeCompleted(init)));
                    return;
                }

                var fetchReset = _communicator.FetchReset();
                if (fetchReset.HasValue)
                {
                    StartCoroutine(DoReset(fetchReset.Value, obs => _communicator.ResetCompleted(obs)));
                    return;
                }

                if (!_connectionInitialized)
                {
#if UNITY_EDITOR
                    if (_disconnectedStepper == null) _disconnectedStepper = StartCoroutine(DisconnectedActionStepper());
#endif
                    return;
                }

                if (!_firstResetComplete)
                {
                    timeout.Restart();
                    return;
                }

                if (!_gymStepOngoing)
                {
                    var fetchNextStep = _communicator.FetchNextStep();
                    if (fetchNextStep.HasValue)
                    {
                        _gymStep = ReceiveStep(fetchNextStep.Value);
                        StartCoroutine(ManageStep(_gymStep,
                            (agentObservations, dones, rewards, infos) => _communicator.StepCompleted(agentObservations, dones, rewards, infos)
                        ));
                    }
                }

                if (!_gymStepOngoing && timeoutMilliseconds > 0 && timeout.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    Debug.Log($"No Step message in {timeoutMilliseconds}ms. Quitting. If needed, increase timeout with GymVecEnvManager.Instance.timeoutMilliseconds. ");
                    Shutdown();
                    break;
                }
            } while (!_gymStepOngoing); //TODO: Consider alternatives like stopping sim with time = 0 or doing manual Physics.Simulate. Fixed update becomes meaningless and breaks "Unity Native" though, needs consideration.
        }

        public void Shutdown()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
            IsShuttingDown = true;
            _communicator.Dispose();
        }

        private IEnumerator DoInitialize(InitializeEnvironment initializeEnvironments, Action<EnvironmentDescription> callback)
        {
            while (!Bootstrap.LoadingDone) yield return new WaitForFixedUpdate();

            if (_agents.Count != initializeEnvironments.AgentCount)
            {
                Spawner.SpawnAgents(initializeEnvironments.AgentCount);
            }

            _gymStepOngoing = false;
            _firstResetComplete = false;

            yield return new WaitForFixedUpdate();
            Spawner.InitializeEnvAndRegisterAgents();
            _agents.ForEach(agent => agent.DoInitialize());
            PostInitialize?.Invoke();

            _environmentDescription.AgentCount = _agents.Count;
            _connectionInitialized = true;
            callback?.Invoke(_environmentDescription);
        }

        private IEnumerator DoReset(Reset reset, Action<AgentObservation[]> callback)
        {
            Spawner.InitializeEnvAndRegisterAgents();
            foreach (var externalAgent in _agents)
            {
                externalAgent.DoReset();
            }

            _gymStepOngoing = false;

            yield return new WaitForFixedUpdate();

            PreObservation?.Invoke();
            callback.Invoke(_agents.Select(agent => agent.ProduceObservation()).ToArray());

            _firstResetComplete = true;
        }


        private Step ReceiveStep(Step step)
        {
            Time.timeScale = step.TimeScale;

            if (step.PhysicsStepCount == 0) step.PhysicsStepCount = physicsStepsPerGymStep;

            for (int i = 0; i < _agents.Count; i++)
            {
                _agents[i].DoSetAction(step.AgentActions[i]);
            }

            _gymStepOngoing = true;
            return step;
        }

        private IEnumerator ManageStep(Step step, Action<AgentObservation[], EnvironmentState[], float[], Info> completedCallback)
        {
            for (int i = 0; i < step.PhysicsStepCount; i++)
            {
                if (!_gymStepOngoing) yield break;

                if (i == step.PhysicsStepCount / 2)
                {
                    EarlyObservation?.Invoke();
                }

                yield return new WaitForFixedUpdate();
            }

            var rewards = _agents.Select(agent => agent.DoCollectReward()).ToArray();
            var dones = _agents.Select(agent => agent.DoGymStep()).ToArray();
            var doneAgents = _agents.FindAll(agent => agent.IsDone() != EnvironmentState.Running).ToList();

            PreObservation?.Invoke();
            Info infos = new Info();
            if (doneAgents.Count > 0)
            {
                infos.Observations = doneAgents.Select(agent => agent.ProduceObservation()).ToArray();
                infos.Infos = doneAgents.Select(agent => new AgentInfo
                {
                    EpisodeLength = agent.GetCurrentStep(),
                    EpisodeReward = agent.GetEpisodeReward(),
                    AgentIndex = agent.GymAgentIndex
                }).ToArray();
            }

            //TODO: Implement autoreset_mode, currently default to next.
            var agentObservations = _agents.Select(agent => agent.ProduceObservation()).ToArray();
            completedCallback.Invoke(agentObservations, dones, rewards, infos);

            doneAgents.ForEach(agent => agent.DoReset());

            _gymStepOngoing = false;
        }

        private IEnumerator DisconnectedActionStepper()
        {
            var enabledAgents = _agents.FindAll(agent => agent.isActiveAndEnabled);
            enabledAgents.ForEach(agent => agent.DoReset());
            enabledAgents.ForEach(agent => agent.ProduceObservation());
            enabledAgents.ForEach(agent => agent.DoInternalAction());

            while (!_gymStepOngoing && !_firstResetComplete && !_connectionInitialized)
            {
                for (int i = 0; i < physicsStepsPerGymStep; i++)
                {
                    if (_gymStepOngoing || _firstResetComplete || _connectionInitialized)
                    {
                        _disconnectedStepper = null;
                        yield break;
                    }

                    if (i == physicsStepsPerGymStep / 2)
                    {
                        EarlyObservation?.Invoke();
                    }

                    yield return new WaitForFixedUpdate();
                }

                enabledAgents = _agents.FindAll(agent => agent.isActiveAndEnabled);
                enabledAgents.ForEach(agent => agent.DoCollectReward());
                enabledAgents.ForEach(agent => agent.DoGymStep());
                PreObservation?.Invoke();
                enabledAgents.ForEach(agent => agent.ProduceObservation());
                enabledAgents.FindAll(agent => agent.IsDone() != EnvironmentState.Running).ForEach(agent => { agent.DoReset(); });
                enabledAgents.ForEach(agent => agent.DoInternalAction());
            }

            _disconnectedStepper = null;
        }

        private void OnDestroy()
        {
            _communicator.Dispose();
        }

        private void OnApplicationQuit()
        {
            IsShuttingDown = true;
            _communicator.Dispose();
        }

        public void ClearAgents()
        {
            _agents.Clear();
        }
    }
}