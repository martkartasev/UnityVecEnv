using Scripts.VecEnv.Message;
using Unity.InferenceEngine;
using UnityEngine;

namespace Scripts.VecEnv.Core
{
    public abstract class GymAgent : ExternalAgent
    {
        [Header("Inference")] 
        public ModelAsset inferencePolicy;
        public bool inferenceEnabled;
        [Header("Agent")] public int maxSteps;

        protected int CurrentStep;
        protected EnvironmentState DoneStatus;
        protected float EpisodeReward;
        protected internal int AgentIndex;
        protected abstract float CollectReward();

        public EnvironmentState DoStep()
        {
            CurrentStep++;
            DoneStatus = Step();
            if (DoneStatus == EnvironmentState.Running && CurrentStep >= maxSteps) DoneStatus = EnvironmentState.Truncated;
            return DoneStatus;
        }

        public float DoCollectReward()
        {
            var reward = CollectReward();
            EpisodeReward += reward;
            return reward;
        }

        public void DoReset()
        {
            EpisodeReward = 0;
            CurrentStep = 0;
            DoneStatus = EnvironmentState.Running;
            Reset();
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

        protected internal void AssignIndex(int index)
        {
            AgentIndex = index;
        }

        protected internal void DummyAction()
        {
            SetAction(ProduceDummyAction(new Action(continuousActions, discreteActions.Count)));
        }
    }
}