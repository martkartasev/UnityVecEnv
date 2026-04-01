using System;
using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;
using UnityEngine;

namespace Scripts.VecEnv.Observation
{
    public abstract class AgentVisualObservationSource : MonoBehaviour
    {
        private static readonly System.Collections.Generic.HashSet<string> LoggedBuildObservationSummaries = new();
        private static readonly System.Collections.Generic.HashSet<string> LoggedNonZeroBuildObservationSummaries = new();
        [SerializeField] private string observationName = "visual";

        protected GymAgent Agent { get; private set; }
        public string ObservationName { get; private set; }
        public string DebugDisplayName => string.IsNullOrWhiteSpace(ObservationName)
            ? (string.IsNullOrWhiteSpace(observationName) ? GetType().Name : observationName.Trim())
            : ObservationName;
        public virtual Texture DebugPreviewTexture => null;
        public virtual string DebugPreviewDetails => null;

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
            var sourceBytes = GetLatestObservationBytes() ?? Array.Empty<byte>();
            var snapshot = new byte[sourceBytes.Length];
            if (sourceBytes.Length > 0)
            {
                Buffer.BlockCopy(sourceBytes, 0, snapshot, 0, sourceBytes.Length);
            }

            LogObservationSummaryOnce("source-snapshot", ObservationName, snapshot);

            return new AgentVisualObservation
            {
                Name = ObservationName,
                Data = snapshot
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

        private static void LogObservationSummaryOnce(string stage, string observationName, byte[] data)
        {
            var key = $"{stage}:{observationName}";
            var hasNonZero = false;
            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != 0)
                    {
                        hasNonZero = true;
                        break;
                    }
                }
            }

            var shouldLog = LoggedBuildObservationSummaries.Add(key);
            if (hasNonZero)
            {
                shouldLog |= LoggedNonZeroBuildObservationSummaries.Add($"{key}:nonzero");
            }

            if (!shouldLog)
            {
                return;
            }

            if (data == null || data.Length == 0)
            {
                Debug.Log($"Visual observation '{observationName}' {stage}: len=0");
                return;
            }

            byte min = byte.MaxValue;
            byte max = byte.MinValue;
            for (int i = 0; i < data.Length; i++)
            {
                var value = data[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            var sampleCount = Math.Min(8, data.Length);
            var sample = string.Join(",", data.AsSpan(0, sampleCount).ToArray());
            Debug.Log(
                $"Visual observation '{observationName}' {stage}: len={data.Length} min={min} max={max} sample=[{sample}]");
        }
    }
}
