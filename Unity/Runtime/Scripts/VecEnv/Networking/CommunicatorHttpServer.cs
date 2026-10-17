using System;
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
                IgnoreTeardownErrors(() => _sLazy.Value.Dispose());
            }

            channel = DefaultChannel;
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

            _stepTcs?.TrySetResult(Mapper.MapStepResults(agentObservations, dones, rewards, infos));
        }

        public void ResetCompleted(AgentObservation[] agentObservations, Info[] infos)
        {
            if (_isDisposed)
            {
                return;
            }

            _resetTcs?.TrySetResult(Mapper.MapResetResults(agentObservations, infos));
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
                IgnoreTeardownErrors(() => context.Response.StatusCode = 500);
            }
            finally
            {
                IgnoreTeardownErrors(() =>
                {
                    if (context.Response.StatusCode != 200) Debug.Log("Closed request with response code " + context.Response.StatusCode);
                    context.Response.Close();
                });
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

        // Teardown paths, where the listener, response or thread may already be gone.
        private static void IgnoreTeardownErrors(System.Action action)
        {
            try
            {
                action();
            }
            catch
            {
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

            IgnoreTeardownErrors(() => _messageAvailable.Set());
            IgnoreTeardownErrors(() => _httpListener?.Stop());
            if (_listenerThread != null && _listenerThread.IsAlive && Thread.CurrentThread != _listenerThread)
            {
                IgnoreTeardownErrors(() => _listenerThread.Join());
            }

            IgnoreTeardownErrors(() => _httpListener?.Close());

            _httpListener = null;
            _listenerThread = null;
            _messageAvailable.Dispose();
            _stepGate.Dispose();
        }
    }
}


