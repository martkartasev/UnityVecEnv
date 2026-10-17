using System;
using System.Collections.Generic;
using ExternalCommunication;
using Google.Protobuf;
using UnityEngine;

namespace Scripts.VecEnv.Message
{
    public static class BatchedObservationEncoder
    {
        private static readonly HashSet<string> LoggedVisualSummaries = new();
        private static readonly HashSet<string> LoggedNonZeroVisualSummaries = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            LoggedVisualSummaries.Clear();
            LoggedNonZeroVisualSummaries.Clear();
        }

        public static BatchedObservation Encode(AgentObservation[] agentObservations)
        {
            var batchedObservation = new BatchedObservation();
            if (agentObservations == null || agentObservations.Length == 0)
            {
                return batchedObservation;
            }

            var continuousSize = agentObservations[0].Continuous?.Length ?? 0;
            var discreteSize = agentObservations[0].Discrete?.Length ?? 0;
            var continuousValues = new float[agentObservations.Length * continuousSize];
            var discreteValues = new int[agentObservations.Length * discreteSize];

            for (int i = 0; i < agentObservations.Length; i++)
            {
                var continuous = agentObservations[i].Continuous ?? Array.Empty<float>();
                var discrete = agentObservations[i].Discrete ?? Array.Empty<int>();
                if (continuous.Length != continuousSize)
                {
                    throw new InvalidOperationException($"Observation {i} has {continuous.Length} continuous values, expected {continuousSize}.");
                }

                if (discrete.Length != discreteSize)
                {
                    throw new InvalidOperationException($"Observation {i} has {discrete.Length} discrete values, expected {discreteSize}.");
                }

                if (continuousSize > 0)
                {
                    Array.Copy(continuous, 0, continuousValues, i * continuousSize, continuousSize);
                }

                if (discreteSize > 0)
                {
                    Array.Copy(discrete, 0, discreteValues, i * discreteSize, discreteSize);
                }
            }

            batchedObservation.NumEnvs = agentObservations.Length;
            batchedObservation.ContinuousSize = continuousSize;
            batchedObservation.DiscreteSize = discreteSize;
            batchedObservation.ContinuousF32 = ByteStringUtils.FloatArrayToByteString(continuousValues);
            batchedObservation.DiscreteI32 = ByteStringUtils.IntArrayToByteString(discreteValues);
            foreach (var visualObservation in EncodeVisualObservations(agentObservations))
            {
                batchedObservation.Visual.Add(visualObservation);
            }

            return batchedObservation;
        }

        public static int[] BuildFinalIndices(EnvironmentState[] dones)
        {
            if (dones == null || dones.Length == 0)
            {
                return Array.Empty<int>();
            }

            var indices = new List<int>(dones.Length);
            for (int i = 0; i < dones.Length; i++)
            {
                if (dones[i] == EnvironmentState.Done || dones[i] == EnvironmentState.Truncated)
                {
                    indices.Add(i);
                }
            }

            return indices.ToArray();
        }

        public static BatchedFinalInfo EncodeFinalInfo(AgentObservation[] agentObservations, Info[] infos, int[] indices)
        {
            var batchedFinalInfo = new BatchedFinalInfo();
            if (indices == null || indices.Length == 0)
            {
                return batchedFinalInfo;
            }

            var finalObservations = new AgentObservation[indices.Length];
            for (int row = 0; row < indices.Length; row++)
            {
                var index = indices[row];
                finalObservations[row] = SelectFinalObservation(agentObservations[index], infos[index]);
            }

            batchedFinalInfo.FinalObservation = Encode(finalObservations);
            return batchedFinalInfo;
        }

        private static AgentObservation SelectFinalObservation(AgentObservation currentObservation, Info info)
        {
            if ((info.FinalObservation.Continuous?.Length ?? 0) > 0 || (info.FinalObservation.Discrete?.Length ?? 0) > 0)
            {
                return info.FinalObservation;
            }

            return currentObservation;
        }

        private static BatchedVisualObservation[] EncodeVisualObservations(AgentObservation[] agentObservations)
        {
            var firstVisuals = agentObservations[0].VisualObservations ?? Array.Empty<AgentVisualObservation>();
            if (firstVisuals.Length == 0)
            {
                return Array.Empty<BatchedVisualObservation>();
            }

            var batchedVisuals = new BatchedVisualObservation[firstVisuals.Length];
            for (int visualIndex = 0; visualIndex < firstVisuals.Length; visualIndex++)
            {
                var firstVisual = firstVisuals[visualIndex];
                var name = firstVisual.Name ?? string.Empty;
                var frameSize = firstVisual.Data?.Length ?? 0;
                var batchedData = new byte[agentObservations.Length * frameSize];

                for (int envIndex = 0; envIndex < agentObservations.Length; envIndex++)
                {
                    var visuals = agentObservations[envIndex].VisualObservations ?? Array.Empty<AgentVisualObservation>();
                    if (visuals.Length != firstVisuals.Length)
                    {
                        throw new InvalidOperationException(
                            $"Observation {envIndex} has {visuals.Length} visual observations, expected {firstVisuals.Length}.");
                    }

                    if (!string.Equals(visuals[visualIndex].Name ?? string.Empty, name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Observation {envIndex} visual observation {visualIndex} is named '{visuals[visualIndex].Name}', expected '{name}'.");
                    }

                    var data = visuals[visualIndex].Data ?? Array.Empty<byte>();
                    if (data.Length != frameSize)
                    {
                        throw new InvalidOperationException(
                            $"Observation {envIndex} visual observation '{name}' has {data.Length} bytes, expected {frameSize}.");
                    }

                    if (frameSize > 0)
                    {
                        Buffer.BlockCopy(data, 0, batchedData, envIndex * frameSize, frameSize);
                    }
                }

                batchedVisuals[visualIndex] = new BatchedVisualObservation
                {
                    Name = name,
                    Data = ByteString.CopyFrom(batchedData)
                };
                if (firstVisual.DebugLoggingEnabled)
                {
                    LogVisualSummaryOnce("server-batch", name, batchedData);
                }
            }

            return batchedVisuals;
        }

        private static void LogVisualSummaryOnce(string stage, string observationName, byte[] data)
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

            var shouldLog = LoggedVisualSummaries.Add(key);
            if (hasNonZero)
            {
                shouldLog |= LoggedNonZeroVisualSummaries.Add($"{key}:nonzero");
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
