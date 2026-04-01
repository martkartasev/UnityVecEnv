using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
        public static int channel = 50010;
        private static Lazy<CommunicatorHttpServer> _sLazy = new(() => new CommunicatorHttpServer());
        public static CommunicatorHttpServer Instance => _sLazy.Value;
        public static bool IsInitialized => _sLazy.IsValueCreated;

        public IMessageMapper Mapper = new DefaultMessageMapper();

        private HttpListener _httpListener;
        private Thread _listenerThread;
        private bool _isRunning = true;

        private readonly SemaphoreSlim _stepGate = new(1, 1);
        private readonly ManualResetEventSlim _messageAvailable = new(false);
        private readonly object _messageLock = new();
        private TaskCompletionSource<BatchedResetResults> _resetTcs;
        private TaskCompletionSource<BatchedStepResults> _stepTcs;
        private TaskCompletionSource<ExternalCommunication.EnvironmentDescription> _initializeTcs;

        private ExternalCommunication.Reset _reset;
        private ExternalCommunication.Step _step;
        private InitializeEnvironments _initialize;


        CommunicatorHttpServer()
        {
            _httpListener = ListenerSetup();

            _listenerThread = new Thread(StartListener);
            _listenerThread.Start();

            Debug.Log($"Communication Server started at http://localhost:{channel.ToString()}/");
        }

        public Reset? FetchReset()
        {
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
                if (infos[i].custom != null && infos[i].custom.Count > 0)
                {
                    indices.Add(i);
                }
            }

            return indices.ToArray();
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

            var keyLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            var orderedKeys = new List<string>();
            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                foreach (var entry in custom)
                {
                    if (keyLookup.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    keyLookup[entry.Key] = orderedKeys.Count;
                    orderedKeys.Add(entry.Key);
                }
            }

            if (orderedKeys.Count == 0)
            {
                return batchedCustomInfo;
            }

            var values = new float[indices.Length * orderedKeys.Count];
            var present = new byte[values.Length];
            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                foreach (var entry in custom)
                {
                    var offset = row * orderedKeys.Count + keyLookup[entry.Key];
                    values[offset] = entry.Value;
                    present[offset] = 1;
                }
            }

            batchedCustomInfo.Keys.Add(orderedKeys);
            batchedCustomInfo.ValuesF32 = FloatArrayToByteString(values);
            batchedCustomInfo.Present = ByteString.CopyFrom(present);
            return batchedCustomInfo;
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

        private HttpListener ListenerSetup()
        {
            var l = new HttpListener();
            while (!l.IsListening)
            {
                try
                {
                    l.Prefixes.Add($"http://localhost:{channel}/step/");
                    l.Prefixes.Add($"http://127.0.0.1:{channel}/step/");

                    l.Prefixes.Add($"http://localhost:{channel}/reset/");
                    l.Prefixes.Add($"http://127.0.0.1:{channel}/reset/");

                    l.Prefixes.Add($"http://localhost:{channel}/initialize/");
                    l.Prefixes.Add($"http://127.0.0.1:{channel}/initialize/");

                    l.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
                    l.Start();
                }
                catch (SocketException)
                {
                    l.Prefixes.Clear();
                    channel += 1;
                }
            }

            return l;
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
                _stepGate.Release();
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
            _isRunning = false; // TODO: Need to fix handling
            _httpListener.Stop();
            _listenerThread.Join();
            _messageAvailable.Dispose();
        }
    }
}


