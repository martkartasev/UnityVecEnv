using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ExternalCommunication;
using Google.Protobuf;
using Scripts.VecEnv.Message;
using UnityEngine;
using EnvironmentDescription = Scripts.VecEnv.Message.EnvironmentDescription;
using Info = Scripts.VecEnv.Message.Info;
using Reset = Scripts.VecEnv.Message.Reset;

namespace Scripts.VecEnv.Networking
{
    public class CommunicatorHttpServer : IExternalCommunication
    {
        private const int DefaultChannel = 50010;
        private static readonly HashSet<string> LoggedBatchedVisualSummaries = new();
        private static readonly HashSet<string> LoggedNonZeroBatchedVisualSummaries = new();
        public static int channel = DefaultChannel;
        private static Lazy<CommunicatorHttpServer> _sLazy = new(() => new CommunicatorHttpServer());
        public static CommunicatorHttpServer Instance => _sLazy.Value;
        public static bool IsInitialized => _sLazy.IsValueCreated;

        public IMessageMapper Mapper = new DefaultMessageMapper();

        private HttpListener _httpListener;
        private Thread _listenerThread;
        private bool _isRunning = true;
        private bool _isDisposed;

        private readonly SemaphoreSlim _stepGate = new(1, 1);
        private readonly ManualResetEventSlim _messageAvailable = new(false);
        private readonly object _messageLock = new();
        private TaskCompletionSource<BatchedResetResults> _resetTcs;
        private TaskCompletionSource<BatchedStepResults> _stepTcs;
        private TaskCompletionSource<ExternalCommunication.EnvironmentDescription> _initializeTcs;

        private ExternalCommunication.Reset _reset;
        private ExternalCommunication.Step _step;
        private InitializeEnvironments _initialize;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            if (_sLazy.IsValueCreated)
            {
                try
                {
                    _sLazy.Value.Dispose();
                }
                catch
                {
                    // Ignore shutdown errors while resetting play mode state.
                }
            }

            channel = DefaultChannel;
            LoggedBatchedVisualSummaries.Clear();
            LoggedNonZeroBatchedVisualSummaries.Clear();
            _sLazy = new Lazy<CommunicatorHttpServer>(() => new CommunicatorHttpServer());
        }


        CommunicatorHttpServer()
        {
            _httpListener = ListenerSetup();

            _listenerThread = new Thread(StartListener);
            _listenerThread.Start();

            Debug.Log($"Communication Server started at http://localhost:{channel.ToString()}/");
        }

        public Reset? FetchReset()
        {
            if (_isDisposed)
            {
                return null;
            }

            lock (_messageLock)
            {
                if (_reset == null) return null;
                var fetchReset = Mapper.MapReset(_reset);
                _reset = null;
                UpdateMessageAvailability_NoLock();
                return fetchReset;
            }
        }


        public Message.Step? FetchNextStep()
        {
            if (_isDisposed)
            {
                return null;
            }

            lock (_messageLock)
            {
                if (_step == null) return null;
                var fetchNextStep = Mapper.MapStep(_step);
                _step = null;
                UpdateMessageAvailability_NoLock();
                return fetchNextStep;
            }
        }

        public InitializeEnvironment? FetchInitialize()
        {
            if (_isDisposed)
            {
                return null;
            }

            lock (_messageLock)
            {
                if (_initialize == null) return null;
                var fetch = Mapper.MapInitialize(_initialize);
                _initialize = null;
                UpdateMessageAvailability_NoLock();
                return fetch;
            }
        }

        public bool WaitForNextMessage(int timeoutMilliseconds)
        {
            if (_isDisposed)
            {
                return false;
            }

            lock (_messageLock)
            {
                if (_reset != null || _step != null || _initialize != null)
                {
                    return true;
                }
            }

            if (timeoutMilliseconds <= 0)
            {
                _messageAvailable.Wait();
                return true;
            }

            return _messageAvailable.Wait(timeoutMilliseconds);
        }

        private void UpdateMessageAvailability_NoLock()
        {
            if (_reset == null && _step == null && _initialize == null)
            {
                _messageAvailable.Reset();
            }
            else
            {
                _messageAvailable.Set();
            }
        }

        public void StepCompleted(AgentObservation[] agentObservations, EnvironmentState[] dones, float[] rewards, Info[] infos)
        {
            if (_isDisposed)
            {
                return;
            }

            var customIndices = BuildCustomIndices(infos);
            var finalIndices = BuildFinalIndices(dones);
            var results = new BatchedStepResults
            {
                Observation = BuildBatchedObservation(agentObservations),
                RewardsF32 = FloatArrayToByteString(rewards),
                Dones = StateArrayToByteString(dones, EnvironmentState.Done),
                Truncates = StateArrayToByteString(dones, EnvironmentState.Truncated),
                CustomIndicesI32 = IntArrayToByteString(customIndices),
                Custom = BuildBatchedCustomInfo(infos, customIndices),
                FinalIndicesI32 = IntArrayToByteString(finalIndices),
                FinalInfo = BuildBatchedFinalInfo(agentObservations, infos, finalIndices)
            };

            _stepTcs?.TrySetResult(results);
        }

        public void ResetCompleted(AgentObservation[] agentObservations, Info[] infos)
        {
            if (_isDisposed)
            {
                return;
            }

            var customIndices = BuildCustomIndices(infos);
            var results = new BatchedResetResults
            {
                Observation = BuildBatchedObservation(agentObservations),
                CustomIndicesI32 = IntArrayToByteString(customIndices),
                Custom = BuildBatchedCustomInfo(infos, customIndices)
            };

            _resetTcs?.TrySetResult(results);
        }

        public void InitializeCompleted(EnvironmentDescription initialize1)
        {
            if (_isDisposed)
            {
                return;
            }

            var description = Mapper.MapEnvironmentDescription(initialize1);
            _initializeTcs?.TrySetResult(description);
        }

        private static AgentObservation SelectFinalObservation(AgentObservation currentObservation, Info info)
        {
            if ((info.FinalObservation.Continuous?.Length ?? 0) > 0 || (info.FinalObservation.Discrete?.Length ?? 0) > 0)
            {
                return info.FinalObservation;
            }

            return currentObservation;
        }

        private static int[] BuildCustomIndices(Info[] infos)
        {
            if (infos == null || infos.Length == 0)
            {
                return Array.Empty<int>();
            }

            var indices = new List<int>(infos.Length);
            for (int i = 0; i < infos.Length; i++)
            {
                if (infos[i].custom != null && infos[i].custom.HasAnyValues)
                {
                    indices.Add(i);
                }
            }

            return indices.ToArray();
        }

        private sealed class CustomInfoLayout
        {
            public readonly Dictionary<string, int> FloatKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> FloatKeys = new();
            public readonly Dictionary<string, int> IntKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> IntKeys = new();
            public readonly Dictionary<string, int> BoolKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> BoolKeys = new();
            public readonly Dictionary<string, int> Vector3KeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> Vector3Keys = new();
            public readonly Dictionary<string, int> QuaternionKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> QuaternionKeys = new();
        }

        private static int[] BuildFinalIndices(EnvironmentState[] dones)
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

        private static BatchedCustomInfo BuildBatchedCustomInfo(Info[] infos, int[] indices)
        {
            var batchedCustomInfo = new BatchedCustomInfo();
            if (indices == null || indices.Length == 0)
            {
                return batchedCustomInfo;
            }

            var layout = new CustomInfoLayout();
            var globalKeyTypes = new Dictionary<string, CustomInfoValueType>(StringComparer.Ordinal);
            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                RegisterCustomKeys(custom.FloatValues, CustomInfoValueType.Float, globalKeyTypes, layout.FloatKeyLookup, layout.FloatKeys);
                RegisterCustomKeys(custom.IntValues, CustomInfoValueType.Int, globalKeyTypes, layout.IntKeyLookup, layout.IntKeys);
                RegisterCustomKeys(custom.BoolValues, CustomInfoValueType.Bool, globalKeyTypes, layout.BoolKeyLookup, layout.BoolKeys);
                RegisterCustomKeys(custom.Vector3Values, CustomInfoValueType.Vector3, globalKeyTypes, layout.Vector3KeyLookup, layout.Vector3Keys);
                RegisterCustomKeys(custom.QuaternionValues, CustomInfoValueType.Quaternion, globalKeyTypes, layout.QuaternionKeyLookup, layout.QuaternionKeys);
            }

            var floatValues = new float[indices.Length * layout.FloatKeys.Count];
            var floatPresent = new byte[floatValues.Length];
            var intValues = new int[indices.Length * layout.IntKeys.Count];
            var intPresent = new byte[intValues.Length];
            var boolValues = new byte[indices.Length * layout.BoolKeys.Count];
            var boolPresent = new byte[boolValues.Length];
            var vector3Values = new float[indices.Length * layout.Vector3Keys.Count * 3];
            var vector3Present = new byte[indices.Length * layout.Vector3Keys.Count];
            var quaternionValues = new float[indices.Length * layout.QuaternionKeys.Count * 4];
            var quaternionPresent = new byte[indices.Length * layout.QuaternionKeys.Count];

            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                PopulateFloatCustomSection(custom, row, layout.FloatKeyLookup, layout.FloatKeys.Count, floatValues, floatPresent);
                PopulateIntCustomSection(custom, row, layout.IntKeyLookup, layout.IntKeys.Count, intValues, intPresent);
                PopulateBoolCustomSection(custom, row, layout.BoolKeyLookup, layout.BoolKeys.Count, boolValues, boolPresent);
                PopulateVector3CustomSection(custom, row, layout.Vector3KeyLookup, layout.Vector3Keys.Count, vector3Values, vector3Present);
                PopulateQuaternionCustomSection(custom, row, layout.QuaternionKeyLookup, layout.QuaternionKeys.Count, quaternionValues, quaternionPresent);
            }

            batchedCustomInfo.Keys.Add(layout.FloatKeys);
            batchedCustomInfo.ValuesF32 = FloatArrayToByteString(floatValues);
            batchedCustomInfo.Present = ByteArrayToByteString(floatPresent);
            batchedCustomInfo.KeysI32.Add(layout.IntKeys);
            batchedCustomInfo.ValuesI32 = IntArrayToByteString(intValues);
            batchedCustomInfo.PresentI32 = ByteArrayToByteString(intPresent);
            batchedCustomInfo.KeysBool.Add(layout.BoolKeys);
            batchedCustomInfo.ValuesBool = ByteArrayToByteString(boolValues);
            batchedCustomInfo.PresentBool = ByteArrayToByteString(boolPresent);
            batchedCustomInfo.KeysVector3.Add(layout.Vector3Keys);
            batchedCustomInfo.ValuesVector3F32 = FloatArrayToByteString(vector3Values);
            batchedCustomInfo.PresentVector3 = ByteArrayToByteString(vector3Present);
            batchedCustomInfo.KeysQuaternion.Add(layout.QuaternionKeys);
            batchedCustomInfo.ValuesQuaternionF32 = FloatArrayToByteString(quaternionValues);
            batchedCustomInfo.PresentQuaternion = ByteArrayToByteString(quaternionPresent);
            return batchedCustomInfo;
        }

        private static void RegisterCustomKeys<T>(
            IReadOnlyDictionary<string, T> values,
            CustomInfoValueType valueType,
            Dictionary<string, CustomInfoValueType> globalKeyTypes,
            Dictionary<string, int> keyLookup,
            List<string> orderedKeys)
        {
            foreach (var key in values.Keys)
            {
                if (globalKeyTypes.TryGetValue(key, out var existingType) && existingType != valueType)
                {
                    throw new InvalidOperationException(
                        $"Custom info key '{key}' was emitted as both {existingType} and {valueType}.");
                }

                globalKeyTypes[key] = valueType;
                if (keyLookup.ContainsKey(key))
                {
                    continue;
                }

                keyLookup[key] = orderedKeys.Count;
                orderedKeys.Add(key);
            }
        }

        private static void PopulateFloatCustomSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.FloatValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value;
                present[offset] = 1;
            }
        }

        private static void PopulateIntCustomSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            int[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.IntValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value;
                present[offset] = 1;
            }
        }

        private static void PopulateBoolCustomSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            byte[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.BoolValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value ? (byte)1 : (byte)0;
                present[offset] = 1;
            }
        }

        private static void PopulateVector3CustomSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.Vector3Values)
            {
                var keyOffset = row * keyCount + keyLookup[entry.Key];
                var valueOffset = keyOffset * 3;
                values[valueOffset] = entry.Value.x;
                values[valueOffset + 1] = entry.Value.y;
                values[valueOffset + 2] = entry.Value.z;
                present[keyOffset] = 1;
            }
        }

        private static void PopulateQuaternionCustomSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.QuaternionValues)
            {
                var keyOffset = row * keyCount + keyLookup[entry.Key];
                var valueOffset = keyOffset * 4;
                values[valueOffset] = entry.Value.x;
                values[valueOffset + 1] = entry.Value.y;
                values[valueOffset + 2] = entry.Value.z;
                values[valueOffset + 3] = entry.Value.w;
                present[keyOffset] = 1;
            }
        }

        private static BatchedFinalInfo BuildBatchedFinalInfo(AgentObservation[] agentObservations, Info[] infos, int[] indices)
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

            batchedFinalInfo.FinalObservation = BuildBatchedObservation(finalObservations);
            return batchedFinalInfo;
        }

        private static BatchedObservation BuildBatchedObservation(AgentObservation[] agentObservations)
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
            batchedObservation.ContinuousF32 = FloatArrayToByteString(continuousValues);
            batchedObservation.DiscreteI32 = IntArrayToByteString(discreteValues);
            foreach (var visualObservation in BuildBatchedVisualObservations(agentObservations))
            {
                batchedObservation.Visual.Add(visualObservation);
            }

            return batchedObservation;
        }

        private static BatchedVisualObservation[] BuildBatchedVisualObservations(AgentObservation[] agentObservations)
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

        private static ByteString FloatArrayToByteString(float[] values)
        {
            if (values == null || values.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return ByteArrayToByteString(bytes);
        }

        private static ByteString IntArrayToByteString(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return ByteArrayToByteString(bytes);
        }

        private static ByteString StateArrayToByteString(EnvironmentState[] states, EnvironmentState activeState)
        {
            if (states == null || states.Length == 0)
            {
                return ByteString.Empty;
            }

            var bytes = new byte[states.Length];
            for (int i = 0; i < states.Length; i++)
            {
                bytes[i] = states[i] == activeState ? (byte)1 : (byte)0;
            }

            return ByteArrayToByteString(bytes);
        }

        private static ByteString ByteArrayToByteString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return ByteString.Empty;
            }

            return UnsafeByteOperations.UnsafeWrap(new ReadOnlyMemory<byte>(bytes));
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

            var shouldLog = LoggedBatchedVisualSummaries.Add(key);
            if (hasNonZero)
            {
                shouldLog |= LoggedNonZeroBatchedVisualSummaries.Add($"{key}:nonzero");
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

        private HttpListener ListenerSetup()
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://localhost:{channel}/step/");
                listener.Prefixes.Add($"http://127.0.0.1:{channel}/step/");

                listener.Prefixes.Add($"http://localhost:{channel}/reset/");
                listener.Prefixes.Add($"http://127.0.0.1:{channel}/reset/");

                listener.Prefixes.Add($"http://localhost:{channel}/initialize/");
                listener.Prefixes.Add($"http://127.0.0.1:{channel}/initialize/");

                listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
                listener.Start();
                return listener;
            }
            catch
            {
                listener.Close();
                throw;
            }
        }


        private void StartListener()
        {
            while (_isRunning)
            {
                HttpListenerContext context = null;
                try
                {
                    context = _httpListener.GetContext();
                    HandleContextAsync(context).GetAwaiter().GetResult();
                }
                catch (HttpListenerException)
                {
                    break; // Stop() called
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    try
                    {
                        context?.Response.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    return;
                }

                var path = context.Request.Url.AbsolutePath;

                if (path.Contains("/initialize", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInitializeAsync(context);
                    return;
                }

                if (path.Contains("/reset", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleResetAsync(context);
                    return;
                }

                if (path.Contains("/step", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleStepAsync(context);
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                try
                {
                    context.Response.StatusCode = 500;
                }
                catch
                {
                    // ignored
                }
            }
            finally
            {
                try
                {
                    if (context.Response.StatusCode != 200) Debug.Log("Closed request with response code " + context.Response.StatusCode);
                    context.Response.Close();
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task HandleInitializeAsync(HttpListenerContext context)
        {
            var incoming = InitializeEnvironments.Parser.ParseFrom(context.Request.InputStream);

            lock (_messageLock)
            {
                _initialize = incoming;
                UpdateMessageAvailability_NoLock();
            }
            _initializeTcs?.TrySetCanceled();
            _initializeTcs = new TaskCompletionSource<ExternalCommunication.EnvironmentDescription>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcs = _initializeTcs;

            var description = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(30), onTimeout: () =>
            {
                Debug.LogWarning("No initializet result produced before timeout");
                return null;
            });

            if (description == null)
            {
                ReturnError(context, 500, "No initial initialization result");
                return;
            }

            await WriteMessageToOutputStream(context, description);
        }

        private async Task HandleResetAsync(HttpListenerContext context)
        {
            var incoming = ExternalCommunication.Reset.Parser.ParseFrom(context.Request.InputStream);

            lock (_messageLock)
            {
                _reset = incoming;
                UpdateMessageAvailability_NoLock();
            }
            _resetTcs?.TrySetCanceled();
            _resetTcs = new TaskCompletionSource<BatchedResetResults>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcs = _resetTcs;

            var obs = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(30), onTimeout: () =>
            {
                Debug.LogWarning("No reset result produced before timeout");
                return null;
            });

            if (obs == null)
            {
                ReturnError(context, 500, "No reset result produced before timeout");
                return;
            }

            await WriteMessageToOutputStream(context, obs);
        }

        private async Task HandleStepAsync(HttpListenerContext context)
        {
            await _stepGate.WaitAsync();
            try
            {
                var incoming = ExternalCommunication.Step.Parser.ParseFrom(context.Request.InputStream);

                lock (_messageLock)
                {
                    _step = incoming;
                    UpdateMessageAvailability_NoLock();
                }
                _stepTcs?.TrySetCanceled();
                _stepTcs = new TaskCompletionSource<BatchedStepResults>(TaskCreationOptions.RunContinuationsAsynchronously);
                var tcs = _stepTcs;

                var sr = await WaitWithTimeout(tcs.Task, TimeSpan.FromSeconds(30), onTimeout: () =>
                {
                    Debug.LogWarning("No step result produced before timeout");
                    return null;
                });

                if (sr == null)
                {
                    ReturnError(context, 500, "No step result produced before timeout");
                    return;
                }

                await WriteMessageToOutputStream(context, sr);
            }
            finally
            {
                if (!_isDisposed)
                {
                    try
                    {
                        _stepGate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, Func<T> onTimeout)
        {
            var done = await Task.WhenAny(task, Task.Delay(timeout));
            if (done == task)
            {
                // Task may be canceled/faulted; let it propagate to help debugging
                return await task;
            }

            return onTimeout();
        }


        private static async Task WriteMessageToOutputStream(HttpListenerContext context, IMessage message)
        {
            context.Response.ContentLength64 = message.CalculateSize();
            context.Response.ContentType = "application/x-protobuf";
            context.Response.StatusCode = 200;

            var output = context.Response.OutputStream;
            message.WriteTo(output);
            await context.Response.OutputStream.FlushAsync();
        }


        private static void ReturnError(
            HttpListenerContext context,
            int status,
            string message = null)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain";

            if (!string.IsNullOrEmpty(message))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isRunning = false;

            lock (_messageLock)
            {
                _reset = null;
                _step = null;
                _initialize = null;
                UpdateMessageAvailability_NoLock();
            }

            _initializeTcs?.TrySetCanceled();
            _resetTcs?.TrySetCanceled();
            _stepTcs?.TrySetCanceled();

            try
            {
                _messageAvailable.Set();
            }
            catch
            {
                // ignored
            }

            try
            {
                _httpListener?.Stop();
            }
            catch
            {
                // ignored
            }

            if (_listenerThread != null && _listenerThread.IsAlive && Thread.CurrentThread != _listenerThread)
            {
                try
                {
                    _listenerThread.Join();
                }
                catch
                {
                    // ignored
                }
            }

            try
            {
                _httpListener?.Close();
            }
            catch
            {
                // ignored
            }

            _httpListener = null;
            _listenerThread = null;
            _messageAvailable.Dispose();
            _stepGate.Dispose();
        }
    }
}


