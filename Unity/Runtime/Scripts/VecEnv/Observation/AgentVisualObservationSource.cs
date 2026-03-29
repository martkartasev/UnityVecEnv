using System;
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;
using UnityEngine;

namespace Scripts.VecEnv.Observation
{
    public abstract class AgentVisualObservationSource : MonoBehaviour
    {
        [SerializeField] private string observationName = "visual";

        protected GymAgent Agent { get; private set; }
        public string ObservationName { get; private set; }

        internal void InitializeSource(GymAgent agent, int index)
        {
            Agent = agent;
            ObservationName = string.IsNullOrWhiteSpace(observationName)
                ? $"{GetType().Name}_{index}"
                : observationName.Trim();
            OnInitialize();
        }

        internal void ShutdownSource()
        {
            OnShutdown();
        }

        internal void BeginAsyncCapture()
        {
            OnBeginAsyncCapture();
        }

        internal void UpdateCapture()
        {
            OnUpdateCapture();
        }

        internal void EnsureLatestObservation(bool forceSynchronousIfMissing)
        {
            UpdateCapture();
            if (!HasLatestObservation() && forceSynchronousIfMissing)
            {
                OnCaptureBlocking();
                UpdateCapture();
            }
        }

        internal AgentVisualObservation BuildObservation()
        {
            return new AgentVisualObservation
            {
                Name = ObservationName,
                Data = GetLatestObservationBytes() ?? Array.Empty<byte>()
            };
        }

        internal VisualObservationDescription GetDescription()
        {
            var description = CreateDescription();
            description.Name = ObservationName;
            return description;
        }

        protected abstract VisualObservationDescription CreateDescription();
        protected abstract bool HasLatestObservation();
        protected abstract byte[] GetLatestObservationBytes();
        protected virtual void OnInitialize() { }
        protected virtual void OnShutdown() { }
        protected abstract void OnBeginAsyncCapture();
        protected abstract void OnUpdateCapture();
        protected abstract void OnCaptureBlocking();
    }
}
