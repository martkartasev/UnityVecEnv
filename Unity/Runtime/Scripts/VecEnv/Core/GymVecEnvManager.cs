using System;
using System.Collections;
using System.Collections.Generic;
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
    public enum SpawnMode
    {
        Gym,
        Disabled,
    }

    [DefaultExecutionOrder(-500)]
    public class GymVecEnvManager : MonoBehaviour
    {
        private const int MaxAgentRegistrationFrames = 8;

        // Lazy initializer pattern, see https://csharpindepth.com/articles/singleton#lazy
        private static Lazy<GymVecEnvManager> _sLazy = new(CreateGymVecEnvManager);
        public static bool IsInitialized => _sLazy.IsValueCreated;
        public static GymVecEnvManager Instance => _sLazy.Value;

        public SpawnMode SpawnMode = SpawnMode.Gym;

        public int physicsStepsPerGymStep = 10;
        public int timeoutMilliseconds = 3000;

        public event Action PreInitialize;
        public event Action PreObservation;
        public event Action PostObservation;
        public event Action EarlyObservation;
        public event Action PostInitialize;
        public event Action PreStep;
        public event Action PostStep;

        public event Action PreStepReset;
        public event Action PostStepReset;

        private IExternalCommunication _communicator;
        private List<GymAgent> _agents = new();

        private bool _firstResetComplete;
        private bool _connectionInitialized;
        private bool _gymStepOngoing;
        private bool IsShuttingDown;

        private Step _gymStep;
        private EnvironmentDescription _environmentDescription;
        private GymAgent _descriptionAgent;
        private Coroutine _disconnectedStepper;
        private GymUserStringOverlay _userStringOverlay;
        private EnvironmentAutoResetMode _autoResetMode = EnvironmentAutoResetMode.NextStep;
        private readonly Dictionary<string, EnvironmentParameter> _initializationParameters = new(StringComparer.Ordinal);
        public GymAgentManager AgentManager { get; set; }
        public IReadOnlyDictionary<string, EnvironmentParameter> InitializationParameters => _initializationParameters;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _sLazy = new Lazy<GymVecEnvManager>(CreateGymVecEnvManager);
        }

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
            _descriptionAgent = agentTemplate;
            _environmentDescription = new EnvironmentDescription
            {
                ContinuousObservations = agentTemplate.continuousObservations,
                DiscreteObservations = agentTemplate.discreteObservations.ToArray(),
                VisualObservations = agentTemplate.GetVisualObservationDescriptions(),
                ContinuousActions = agentTemplate.continuousActions,
                DiscreteActions = agentTemplate.discreteActions.ToArray(),
                Parameters = Array.Empty<EnvironmentParameter>(),
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

        public void RegisterAgentsInOrder(IEnumerable<GymAgent> orderedAgents)
        {
            _agents = orderedAgents
                .Where(agent => agent != null)
                .Distinct()
                .ToList();

            for (var index = 0; index < _agents.Count; index++)
            {
                _agents[index].AssignIndex(index);
            }
        }

        public void UnregisterAgent(GymAgent externalAgent)
        {
            _agents.Remove(externalAgent);
        }

        public bool TryGetInitializationParameter(string key, out EnvironmentParameter parameter)
        {
            return InitializationParameterUtils.TryGet(_initializationParameters, key, out parameter);
        }

        public string GetInitializationString(string key, string defaultValue = "")
        {
            return InitializationParameterUtils.GetString(_initializationParameters, key, defaultValue);
        }

        public float GetInitializationFloat(string key, float defaultValue = 0f)
        {
            return InitializationParameterUtils.GetFloat(_initializationParameters, key, defaultValue);
        }

        public int GetInitializationInt(string key, int defaultValue = 0)
        {
            return InitializationParameterUtils.GetInt(_initializationParameters, key, defaultValue);
        }

        private void Start()
        {
            _agents.ForEach(agent => agent.DoInitialize());
            PostInitialize?.Invoke();
        }

        public void FixedUpdate()
        {
            if (IsShuttingDown) return;

            if (TryHandleControlMessages())
            {
                return;
            }

            if (!_connectionInitialized)
            {
#if UNITY_EDITOR
                if (_disconnectedStepper == null && _agents.Count > 0) _disconnectedStepper = StartCoroutine(DisconnectedActionStepper());
                if (_agents.Count == 0) AgentManager.InitializeEnvAndRegisterAgents();
#endif
                return;
            }

            if (!_firstResetComplete || _gymStepOngoing)
            {
                return;
            }

            if (!_communicator.WaitForNextMessage(timeoutMilliseconds))
            {
                Debug.Log($"No Step message in {timeoutMilliseconds}ms. Quitting. If needed, increase timeout with GymVecEnvManager.Instance.timeoutMilliseconds. ");
                Shutdown();
                return;
            }

            if (TryHandleControlMessages())
            {
                return;
            }

            var fetchNextStep = _communicator.FetchNextStep();
            if (fetchNextStep.HasValue)
            {
                _gymStep = ReceiveStep(fetchNextStep.Value);
                StartCoroutine(ManageStep(_gymStep,
                    (agentObservations, dones, rewards, infos) => _communicator.StepCompleted(agentObservations, dones, rewards, infos)
                ));
            }
        }

        private bool TryHandleControlMessages()
        {
            var fetchInitialize = _communicator.FetchInitialize();
            if (fetchInitialize.HasValue)
            {
                StartCoroutine(DoInitialize(fetchInitialize.Value, init => _communicator.InitializeCompleted(init)));
                return true;
            }

            var fetchReset = _communicator.FetchReset();
            if (fetchReset.HasValue)
            {
                StartCoroutine(DoReset(fetchReset.Value, (obs, infos) => _communicator.ResetCompleted(obs, infos)));
                return true;
            }

            return false;
        }

        public void Shutdown()
        {
            ClearUserStrings();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
            IsShuttingDown = true;
            DisposeCommunicator();
        }

        private IEnumerator DoInitialize(InitializeEnvironment initializeEnvironments, Action<EnvironmentDescription> callback)
        {
            ClearUserStrings();
            while (!Bootstrap.LoadingDone) yield return new WaitForFixedUpdate();
            _autoResetMode = initializeEnvironments.AutoResetMode;
            InitializationParameterUtils.Replace(_initializationParameters, initializeEnvironments.Parameters);
            PreInitialize?.Invoke();

            var expectedAgentCount = _agents.Count;
            if (SpawnMode == SpawnMode.Gym)
            {
                expectedAgentCount = AgentManager.SpawnAgents(initializeEnvironments.AgentCount);
            }

            _gymStepOngoing = false;
            _firstResetComplete = false;

            yield return StartCoroutine(RegisterAgentsForInitialization(expectedAgentCount));
            _agents.ForEach(agent => agent.DoInitialize());
            PostInitialize?.Invoke();

            RefreshEnvironmentParameters();
            _environmentDescription.AgentCount = _agents.Count;
            _connectionInitialized = true;
            callback?.Invoke(_environmentDescription);
        }

        private void RefreshEnvironmentParameters()
        {
            if (_descriptionAgent == null)
            {
                _environmentDescription.Parameters = Array.Empty<EnvironmentParameter>();
                return;
            }

            _environmentDescription.Parameters = _descriptionAgent.ProduceEnvironmentParameters();
        }

        private IEnumerator DoReset(Reset reset, Action<AgentObservation[], Info[]> callback)
        {
            ClearUserStrings();
            AgentManager.InitializeEnvAndRegisterAgents();
            _gymStepOngoing = false;
            yield return new WaitForFixedUpdate();

            var resetSeeds = BuildResetSeedMap(reset);
            ResetAgents(_agents, resetSeeds);

            PreObservation?.Invoke();
            callback.Invoke(_agents.Select(agent => agent.ProduceObservation()).ToArray(), _agents.Select(agent => agent.ProduceInfo()).ToArray());
            PostObservation?.Invoke();
            
            _firstResetComplete = true;
        }

        private IReadOnlyDictionary<int, int?> BuildResetSeedMap(Reset reset)
        {
            var resetSeeds = new Dictionary<int, int?>();
            foreach (var parameters in reset.ParametersPerAgent ?? Array.Empty<ResetParameters>())
            {
                if (parameters.AgentIndex < 0 || parameters.AgentIndex >= _agents.Count)
                {
                    throw new InvalidOperationException(
                        $"Reset parameter agent index {parameters.AgentIndex} is outside the valid range 0..{_agents.Count - 1}.");
                }

                if (resetSeeds.ContainsKey(parameters.AgentIndex))
                {
                    throw new InvalidOperationException(
                        $"Reset parameters contain duplicate agent index {parameters.AgentIndex}.");
                }

                resetSeeds.Add(parameters.AgentIndex, parameters.Seed);
            }

            return resetSeeds;
        }

        private void ResetAgents(
            IEnumerable<GymAgent> agents,
            IReadOnlyDictionary<int, int?> seedsByAgentIndex = null)
        {
            var resetAgents = agents as IReadOnlyList<GymAgent> ?? agents.ToList();
            if (resetAgents.Count == 0) return;

            PreStepReset?.Invoke();
            foreach (var agent in resetAgents)
            {
                int? seed = null;
                if (seedsByAgentIndex != null)
                {
                    seedsByAgentIndex.TryGetValue(agent.GetGymAgentIndex(), out seed);
                }

                agent.DoReset(seed);
            }

            Physics.SyncTransforms();
            PostStepReset?.Invoke();
        }


        private Step ReceiveStep(Step step)
        {
            UpdateUserStrings(step.UiStrings);
            Time.timeScale = step.TimeScale;

            if (step.PhysicsStepCount == 0) step.PhysicsStepCount = physicsStepsPerGymStep;

            for (int i = 0; i < _agents.Count; i++)
            {
                _agents[i].DoSetAction(step.AgentActions[i]);
            }

            _gymStepOngoing = true;
            return step;
        }

        private void UpdateUserStrings(IReadOnlyList<string> uiStrings)
        {
            if (uiStrings == null || uiStrings.Count == 0)
            {
                ClearUserStrings();
                return;
            }

            if (_userStringOverlay == null)
            {
                _userStringOverlay = gameObject.AddComponent<GymUserStringOverlay>();
            }

            _userStringOverlay.SetLines(uiStrings);
        }

        private void ClearUserStrings()
        {
            if (_userStringOverlay == null) return;

            _userStringOverlay.enabled = false;
            Destroy(_userStringOverlay);
            _userStringOverlay = null;
        }

        private IEnumerator ManageStep(Step step, Action<AgentObservation[], EnvironmentState[], float[], Info[]> completedCallback)
        {
            PreStep?.Invoke();
            for (int i = 0; i < step.PhysicsStepCount; i++)
            {
                if (!_gymStepOngoing) yield break;

                if (i == step.PhysicsStepCount / 2)
                {
                    EarlyObservation?.Invoke();
                    _agents.ForEach(agent => agent.BeginVisualObservationCapture());
                }

                yield return new WaitForFixedUpdate();
            }

            var rewards = _agents.Select(agent => agent.DoCollectReward()).ToArray();
            var dones = _agents.Select(agent => agent.DoGymStep()).ToArray();
            var doneAgents = _agents.FindAll(agent => agent.IsDone() != EnvironmentState.Running).ToList();

            PreObservation?.Invoke();
            var agentObservations = _agents.Select(agent => agent.ProduceObservation()).ToArray();
            var infos = _agents.Select(agent => agent.ProduceInfo()).ToArray();
            PostObservation?.Invoke();

            if (_autoResetMode == EnvironmentAutoResetMode.SameStep && doneAgents.Count > 0)
            {
                ResetAgents(doneAgents);

                PreObservation?.Invoke();
                agentObservations = _agents.Select(agent => agent.ProduceObservation()).ToArray();
                PostObservation?.Invoke();

                completedCallback.Invoke(agentObservations, dones, rewards, infos);
            }
            else
            {
                completedCallback.Invoke(agentObservations, dones, rewards, infos);
                if (doneAgents.Count > 0) ResetAgents(doneAgents);
            }
            
            _gymStepOngoing = false;
            PostStep?.Invoke();
        }

        private IEnumerator DisconnectedActionStepper()
        {
            var enabledAgents = _agents.FindAll(agent => agent.isActiveAndEnabled);
            enabledAgents.ForEach(agent => agent.DoInitialize());
            ResetAgents(enabledAgents);
            enabledAgents.ForEach(agent => agent.ProduceObservation());
            enabledAgents.ForEach(agent => agent.DoInternalAction());

            while (!_gymStepOngoing && !_firstResetComplete && !_connectionInitialized)
            {
                PreStep?.Invoke();
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
                        enabledAgents.ForEach(agent => agent.BeginVisualObservationCapture());
                    }

                    yield return new WaitForFixedUpdate();
                }

                enabledAgents = _agents.FindAll(agent => agent.isActiveAndEnabled);
                enabledAgents.ForEach(agent => agent.DoCollectReward());
                enabledAgents.ForEach(agent => agent.DoGymStep());
                PreObservation?.Invoke();
                enabledAgents.ForEach(agent => agent.ProduceObservation());
                PostObservation?.Invoke();
                var doneAgents = enabledAgents.FindAll(agent => agent.IsDone() != EnvironmentState.Running);
                if (doneAgents.Count > 0) ResetAgents(doneAgents);
                PostStep?.Invoke();
                enabledAgents.ForEach(agent => agent.DoInternalAction());
            }

            _disconnectedStepper = null;
        }

        private void OnDestroy()
        {
            DisposeCommunicator();
        }

        private void OnApplicationQuit()
        {
            ClearUserStrings();
            IsShuttingDown = true;
            DisposeCommunicator();
        }

        public void ClearAgents()
        {
            _agents.Clear();
        }

        private IEnumerator RegisterAgentsForInitialization(int expectedAgentCount)
        {
            for (int i = 0; i < MaxAgentRegistrationFrames; i++)
            {
                AgentManager.InitializeEnvAndRegisterAgents();
                if (expectedAgentCount <= 0 || _agents.Count >= expectedAgentCount)
                {
                    yield break;
                }

                // Give newly duplicated environment roots a frame to run Start/Awake-driven agent spawning.
                yield return null;
            }

            AgentManager.InitializeEnvAndRegisterAgents();
            if (expectedAgentCount > 0 && _agents.Count < expectedAgentCount)
            {
                Debug.LogWarning($"Expected {expectedAgentCount} agents during initialization, but only found {_agents.Count}. " +
                                 "If your environments spawn GymAgents asynchronously, ensure they are created within the first few frames.");
            }
        }

        private void DisposeCommunicator()
        {
            if (_communicator == null)
            {
                return;
            }

            _communicator.Dispose();
            _communicator = null;
        }
    }
}
