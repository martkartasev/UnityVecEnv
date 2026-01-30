using System.Collections.Generic;
using Scripts.VecEnv.Inference;
using Scripts.VecEnv.Message;
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

        [Header("Action Specification")] public int continuousActions = 0;
        public List<int> discreteActions = new();

        [Header("Inference")] public ModelAsset inferencePolicy;
        public bool inferenceEnabled;
        private InferenceHelper _model;
        private AgentObservation _latestObservation;
        private AgentAction _latestAction;

        [Header("Agent")] public int gymSteps;

        protected int CurrentStep;
        protected EnvironmentState DoneStatus;
        protected float EpisodeReward;
        protected float PreviousEpisodeReward;
        private float _latestStepReward;
        protected internal int GymAgentIndex;

        public int GetGymAgentIndex()
        {
            return GymAgentIndex;
        }

        protected abstract float CollectReward();
        protected abstract void GymReset();
        protected abstract void SetAction(AgentAction agentAction);
        protected abstract EnvironmentState GymStep();
        protected abstract void CollectObservation(ref AgentObservation observation);

        protected virtual void Initialize()
        {
        }

        protected virtual AgentAction ProduceDummyAction(AgentAction dummyAgentAction)
        {
            return dummyAgentAction;
        }

        protected internal void DoInitialize()
        {
            if (inferencePolicy != null) _model = new InferenceHelper(inferencePolicy);
            Initialize();
        }

        protected internal AgentObservation ProduceObservation()
        {
            var produceObservation = new AgentObservation(continuousObservations, discreteActions.Count);
            CollectObservation(ref produceObservation);
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
            GymAgentIndex = index;
        }

        protected internal void DoInternalAction()
        {
            if (_model != null && inferenceEnabled)
            {
                DoSetAction(new AgentAction
                {
                    Continuous = _model.DoInference(_latestObservation.Continuous)
                });
            }
            else
            {
                DoSetAction(ProduceDummyAction(new AgentAction(continuousActions, discreteActions.Count)));
            }
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
    }
}