using System;
using System.Collections.Generic;
using Scripts.VecEnv.Inference;
using Scripts.VecEnv.Message;
using Scripts.VecEnv.Observation;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Serialization;

namespace Scripts.VecEnv.Core
{
    [DefaultExecutionOrder(-50)]
    public abstract class GymAgent : MonoBehaviour
    {
        [Header("Observation Specification")] public int continuousObservations = 0;
        public List<int> discreteObservations = new();
        [SerializeField] private List<AgentVisualObservationSource> visualObservationSources = new();

        [Header("Action Specification")] public int continuousActions = 0;
        public List<int> discreteActions = new();

        [Header("Inference")] public ModelAsset inferencePolicy;
        protected bool InferenceEnabled;
        private InferenceHelper _model;
        private AgentObservation _latestObservation;
        private AgentAction _latestAction;
        private AgentVisualObservationSource[] _visualObservationSources = Array.Empty<AgentVisualObservationSource>();
        private VisualObservationDescription[] _visualObservationDescriptions = Array.Empty<VisualObservationDescription>();
        private bool _visualObservationSourcesInitialized;

        [Header("Agent")] public int gymSteps;

        protected int CurrentStep;
        protected EnvironmentState DoneStatus;
        protected float EpisodeReward;
        protected float PreviousEpisodeReward;
        private float _latestStepReward;
        private int _gymAgentIndex = -1;

        public int GetGymAgentIndex()
        {
            if (_gymAgentIndex == -1)
            {
                Bootstrap.EnsureVecEnvInitialized();
                GymVecEnvManager.Instance.RegisterAgent(this);
            }

            return _gymAgentIndex;
        }

        protected virtual void Initialize()
        {
        }

        protected abstract void GymReset();
        protected abstract void SetAction(AgentAction agentAction);
        protected abstract void CollectObservation(ref AgentObservation observation);
        protected abstract float CollectReward();
        protected abstract EnvironmentState GymStep();

        protected virtual void CollectInfo(Dictionary<string, float> metadata)
        {
        }


        protected virtual AgentAction ProduceDummyAction(AgentAction dummyAgentAction)
        {
            return dummyAgentAction;
        }

        protected internal void DoInitialize()
        {
            EnsureVisualObservationSourcesInitialized();
            Initialize();
        }


        protected internal Info ProduceInfo()
        {
            var info = new Info();
            info.AgentIndex = GetGymAgentIndex();

            if (DoneStatus != EnvironmentState.Running)
            {
                info.FinalObservation = _latestObservation;
                info.EpisodeReward = _latestStepReward;
                info.EpisodeLength = CurrentStep;
            }

            var dictionary = new Dictionary<string, float>();
            CollectInfo(dictionary);
            info.custom = dictionary;

            return info;
        }

        protected internal AgentObservation ProduceObservation()
        {
            var produceObservation = new AgentObservation(continuousObservations, discreteObservations.Count);
            CollectObservation(ref produceObservation);
            produceObservation.VisualObservations = BuildVisualObservations(forceSynchronousIfMissing: true);
            _latestObservation = produceObservation;
            return produceObservation;
        }

        protected internal EnvironmentState DoGymStep()
        {
            CurrentStep++;
            DoneStatus = GymStep();
            if (DoneStatus == EnvironmentState.Running && CurrentStep >= gymSteps) DoneStatus = EnvironmentState.Truncated;
            return DoneStatus;
        }

        protected internal float DoCollectReward()
        {
            _latestStepReward = CollectReward();
            EpisodeReward += _latestStepReward;
            return _latestStepReward;
        }

        protected internal void DoReset()
        {
            PreviousEpisodeReward = EpisodeReward;
            EpisodeReward = 0;
            _latestStepReward = 0;
            CurrentStep = 0;
            DoneStatus = EnvironmentState.Running;
            GymReset();
        }

        protected internal void DoSetAction(AgentAction agentAction)
        {
            _latestAction = agentAction;
            SetAction(agentAction);
        }

        protected internal void AssignIndex(int index)
        {
            _gymAgentIndex = index;
        }

        protected internal VisualObservationDescription[] GetVisualObservationDescriptions()
        {
            EnsureVisualObservationSourcesInitialized();
            return _visualObservationDescriptions;
        }

        protected internal void BeginVisualObservationCapture()
        {
            EnsureVisualObservationSourcesInitialized();
            foreach (var source in _visualObservationSources)
            {
                source.BeginAsyncCapture();
            }
        }

        protected internal void DoInternalAction()
        {
            if (inferencePolicy != null)
            {
                if (_model == null || _model.PolicyAsset != inferencePolicy)
                {
                    DisposeInferenceHelper();
                    _model = new InferenceHelper(inferencePolicy);
                }

                InferenceEnabled = true;
                DoSetAction(_model.DoInference(_latestObservation, _visualObservationDescriptions));
            }
            else
            {
                DisposeInferenceHelper();
                InferenceEnabled = false;
                DoSetAction(ProduceDummyAction(new AgentAction(continuousActions, discreteActions.Count)));
            }
        }

        private void DisposeInferenceHelper()
        {
            _model?.Dispose();
            _model = null;
        }

        private void EnsureVisualObservationSourcesInitialized()
        {
            if (_visualObservationSourcesInitialized)
            {
                return;
            }

            _visualObservationSources = (visualObservationSources ?? new List<AgentVisualObservationSource>()).ToArray();
            _visualObservationDescriptions = new VisualObservationDescription[_visualObservationSources.Length];
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < _visualObservationSources.Length; i++)
            {
                var source = _visualObservationSources[i];
                if (source == null)
                {
                    throw new InvalidOperationException(
                        $"GymAgent '{name}' has a missing visual observation source reference at index {i}. " +
                        "Remove null entries or assign a valid AgentVisualObservationSource in the Observation Specification.");
                }

                if (source.transform != transform && !source.transform.IsChildOf(transform))
                {
                    throw new InvalidOperationException(
                        $"GymAgent '{name}' references visual observation source '{source.name}' that is not part of the agent hierarchy. " +
                        "Assign only AgentVisualObservationSource components on this agent or its children.");
                }

                source.InitializeSource(this, i);
                var description = source.GetDescription();
                if (!usedNames.Add(description.Name))
                {
                    throw new InvalidOperationException(
                        $"GymAgent '{name}' has duplicate visual observation name '{description.Name}'. " +
                        "Visual observation source names must be unique per agent.");
                }

                _visualObservationDescriptions[i] = description;
            }

            _visualObservationSourcesInitialized = true;
        }

        private AgentVisualObservation[] BuildVisualObservations(bool forceSynchronousIfMissing)
        {
            EnsureVisualObservationSourcesInitialized();
            if (_visualObservationSources.Length == 0)
            {
                return Array.Empty<AgentVisualObservation>();
            }

            var visualObservations = new AgentVisualObservation[_visualObservationSources.Length];
            for (int i = 0; i < _visualObservationSources.Length; i++)
            {
                _visualObservationSources[i].EnsureLatestObservation(forceSynchronousIfMissing);
                visualObservations[i] = _visualObservationSources[i].BuildObservation();
            }

            return visualObservations;
        }

        public EnvironmentState IsDone()
        {
            return DoneStatus;
        }

        public float GetEpisodeReward()
        {
            return EpisodeReward;
        }

        public int GetCurrentStep()
        {
            return CurrentStep;
        }

        private void OnDestroy()
        {
            DisposeInferenceHelper();

            if (_visualObservationSourcesInitialized)
            {
                foreach (var source in _visualObservationSources)
                {
                    source.ShutdownSource();
                }
            }

            if (GymVecEnvManager.IsInitialized)
            {
                GymVecEnvManager.Instance.UnregisterAgent(this);
            }
        }
    }
}
