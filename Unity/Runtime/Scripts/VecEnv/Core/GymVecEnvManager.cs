using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Scripts.VecEnv.Message;
using Scripts.VecEnv.Networking;
using UnityEngine;
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
        public static int physicsStepsPerGymStep = 10;

        public event System.Action PreObservation;

        private IExternalCommunication _communicator;
        private List<GymAgent> _agents = new();
        private int _physicsStepsRemaining;
        private bool _firstResetComplete;
        private bool _initialized;

        private bool _gymStepOngoing;
        private Step _gymStep;
        private EnvironmentDescription _environmentDescription;
        public AgentAndManagerSpawner Spawner { get; set; }

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
                ContinuousActions = agentTemplate.continuousActions
            };
        }

        public void RegisterAgent(GymAgent externalAgent)
        {
            externalAgent.AssignIndex(_agents.Count);
            _agents.Add(externalAgent);
        }

        public void UnregisterAgent(GymAgent externalAgent)
        {
            _agents.Remove(externalAgent);
        }

        private void Start()
        {
            _agents.ForEach(agent => agent.DoInitialize());
        }

        public void FixedUpdate()
        {
            do
            {
                var fetchInitialize = _communicator.FetchInitialize();
                if (fetchInitialize.HasValue)
                {
                    var e = DoInitialize(fetchInitialize.Value, init => _communicator.InitializeCompleted(init));
                    StartCoroutine(e);
                    return;
                }

                var fetchReset = _communicator.FetchReset();
                if (!_firstResetComplete && !fetchReset.HasValue)
                {
#if UNITY_EDITOR
                    DummyStep();
#endif
                    return;
                }

                if (fetchReset.HasValue)
                {
                    var e = DoReset(fetchReset.Value, obs => _communicator.ResetCompleted(obs));
                    StartCoroutine(e);
                    return;
                }

                if (_gymStepOngoing)
                {
                    var continueProcessingStep = ManageStep(_gymStep, _gymStep.ApplyActionEveryStep);
                    if (continueProcessingStep)
                        return;
                    PreObservation?.Invoke();

                    var rewards = _agents.Select(agent => agent.DoCollectReward()).ToArray();
                    var dones = _agents.Select(agent => agent.DoStep()).ToArray();
                    var doneAgents = _agents.FindAll(agent => agent.IsDone() != EnvironmentState.Running).ToList();

                    Info infos = new Info();
                    if (doneAgents.Count > 0)
                    {
                        infos.Observations = doneAgents.Select(agent => agent.ProduceObservation()).ToArray();
                        infos.Infos = doneAgents.Select(agent => new AgentInfo
                        {
                            EpisodeLength = agent.GetCurrentStep(),
                            EpisodeReward = agent.GetEpisodeReward(),
                            AgentIndex = agent.AgentIndex
                        }).ToArray();
                    }

                    //TODO: Implement autoreset_mode, currently default to next.
                    _communicator.StepCompleted(
                        _agents.Select(agent => agent.ProduceObservation()).ToArray(),
                        dones,
                        rewards,
                        infos);

                    doneAgents.ForEach(agent => agent.DoReset());
                }


                var fetchNextStep = _communicator.FetchNextStep();
                if (fetchNextStep.HasValue)
                {
                    _gymStep = fetchNextStep.Value;
                    ReceiveStep(_gymStep);
                }
            } while (!_gymStepOngoing);
        }

        private void DummyStep()
        {
            PreObservation?.Invoke();
            _agents.ForEach(agent => agent.ProduceObservation());
            _agents.ForEach(agent => agent.DoCollectReward());
            _agents.ForEach(agent => agent.DoStep());
            _agents.FindAll(agent => agent.IsDone() != EnvironmentState.Running)
                .ForEach(doneAgent => doneAgent.DoReset());
            _agents.ForEach(agent => agent.DoInternalAction());
        }

        private IEnumerator DoInitialize(InitializeEnvironment initializeEnvironments, Action<EnvironmentDescription> callback)
        {
            if (_agents.Count != initializeEnvironments.AgentCount)
            {
                Spawner.SpawnAgents(initializeEnvironments.AgentCount);
            }

            yield return new WaitForFixedUpdate();
            Spawner.InitializeEnvAndRegisterAgents();
            _agents.ForEach(agent => agent.DoInitialize());

            _environmentDescription.AgentCount = _agents.Count;

            _initialized = true;
            _gymStepOngoing = false;
            _physicsStepsRemaining = -1;
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
            _physicsStepsRemaining = -1;

            yield return new WaitForFixedUpdate();

            PreObservation?.Invoke();
            callback.Invoke(_agents.Select(agent => agent.ProduceObservation()).ToArray());

            _firstResetComplete = true;
        }


        private void ReceiveStep(Step step)
        {
            _gymStepOngoing = true;
            Time.timeScale = step.TimeScale;
            _physicsStepsRemaining = step.PhysicsStepCount == 0 ? physicsStepsPerGymStep : step.PhysicsStepCount;

            ManageStep(step, true);
        }

        private bool ManageStep(Step step, bool applyAction)
        {
            bool continueProcessing = _physicsStepsRemaining-- > 0;

            if (continueProcessing)
            {
                _agents.ForEach(agent => agent.DoStep());
                if (applyAction)
                {
                    for (int i = 0; i < _agents.Count; i++)
                    {
                        _agents[i].SetAction(step.AgentActions[i]);
                    }
                }
            }

            _gymStepOngoing = continueProcessing;
            return continueProcessing;
        }

        private void OnDestroy()
        {
            _communicator.Dispose();
        }

        private void OnApplicationQuit()
        {
            _communicator.Dispose();
        }

        public void ClearAgents()
        {
            _agents.Clear();
        }
    }
}