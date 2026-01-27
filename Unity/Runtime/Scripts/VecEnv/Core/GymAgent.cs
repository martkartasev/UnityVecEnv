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

        [Header("Agent")] public int gymSteps;

        protected int CurrentStep;
        protected EnvironmentState DoneStatus;
        protected float EpisodeReward;
        protected internal int AgentIndex;
        protected abstract float CollectReward();
        protected abstract void Reset();
        public abstract void SetAction(Action action);
        protected abstract EnvironmentState GymStep();
        protected abstract void CollectObservation(AgentObservation agentObservation);

        protected virtual void Initialize()
        {
        }

        protected virtual Action ProduceDummyAction(Action dummyAction)
        {
            return dummyAction;
        }

        protected internal void DoInitialize()
        {
            if (inferencePolicy != null) _model = new InferenceHelper(inferencePolicy);
            Initialize();
        }

        protected internal AgentObservation ProduceObservation()
        {
            var produceObservation = new AgentObservation(continuousObservations, discreteActions.Count);
            CollectObservation(produceObservation);
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
            var reward = CollectReward();
            EpisodeReward += reward;
            return reward;
        }

        protected internal void DoReset()
        {
            EpisodeReward = 0;
            CurrentStep = 0;
            DoneStatus = EnvironmentState.Running;
            Reset();
        }

        protected internal void AssignIndex(int index)
        {
            AgentIndex = index;
        }

        protected internal void DoInternalAction()
        {
            if (_model != null && inferenceEnabled)
            {
                SetAction(new Action
                {
                    Continuous = _model.DoInference(_latestObservation.Continuous)
                });
            }
            else
            {
                SetAction(ProduceDummyAction(new Action(continuousActions, discreteActions.Count)));
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