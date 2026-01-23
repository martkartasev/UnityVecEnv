using System;
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
using Observations = ExternalCommunication.Observations;
using Reset = Scripts.VecEnv.Message.Reset;
using Step = ExternalCommunication.Step;

namespace Scripts.VecEnv.Networking
{
    public class CommunicatorHttpServer : IExternalCommunication
    {
        public static int channel = 50010;
        private static Lazy<CommunicatorHttpServer> _sLazy = new(() => new CommunicatorHttpServer());
        public static CommunicatorHttpServer Instance => _sLazy.Value;
        public static bool IsInitialized => _sLazy.IsValueCreated;

        public IMessageMapper Mapper = new DefaultMessageMapper();

        private HttpListener httpListener;
        private Thread listenerThread;
        private bool isRunning = true;

        private TaskCompletionSource<Observations> _resetTcs;
        private TaskCompletionSource<StepResults> _stepTcs;
        private TaskCompletionSource<ExternalCommunication.EnvironmentDescription> _initializeTcs;

        private ExternalCommunication.Reset reset;
        private Step step;
        private InitializeEnvironments initialize;


        CommunicatorHttpServer()
        {
            httpListener = ListenerSetup();

            listenerThread = new Thread(StartListener);
            listenerThread.Start();

            Debug.Log($"Communication Server started at http://localhost:{channel.ToString()}/");
        }

        public Reset? FetchReset()
        {
            if (reset == null) return null;
            var fetchReset = Mapper.MapReset(reset);
            reset = null;
            return fetchReset;
        }


        public Message.Step? FetchNextStep()
        {
            if (step == null) return null;
            var fetchNextStep = Mapper.MapStep(step);
            step = null;
            return fetchNextStep;
        }

        public InitializeEnvironment? FetchInitialize()
        {
            if (initialize == null) return null;
            var fetch = Mapper.MapInitialize(initialize);
            initialize = null;
            return fetch;
        }


        public void StepCompleted(AgentObservation[] agentObservations, EnvironmentState[] dones, float[] rewards, Info info)
        {
            var results = new StepResults();
            for (int i = 0; i < agentObservations.Length; i++)
            {
                var result = new StepResult();
                result.Observation = Mapper.MapObservationToExternal(agentObservations[i]);
                result.Done = dones[i] == EnvironmentState.Done;
                result.Truncated = dones[i] == EnvironmentState.Truncated;
                result.Reward = rewards[i];
                results.StepResults_.Add(result);
            }

            results.Infos = Mapper.MapInfo(info);
            _stepTcs?.TrySetResult(results);
        }

        public void ResetCompleted(AgentObservation[] agentObservations)
        {
            var observations = BuildObservations(agentObservations);
            _resetTcs?.TrySetResult(observations);
        }

        public void InitializeCompleted(EnvironmentDescription initialize1)
        {
            var description = Mapper.MapEnvironmentDescription(initialize1);
            _initializeTcs?.TrySetResult(description);
        }

        private Observations BuildObservations(AgentObservation[] agentObservations)
        {
            var observations = new Observations();
            foreach (var agentObservation in agentObservations)
            {
                observations.Observations_.Add(Mapper.MapObservationToExternal(agentObservation));
            }

            return observations;
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
                catch (SocketException e)
                {
                    l.Prefixes.Clear();
                    channel += 1;
                }
            }

            return l;
        }


        private void StartListener()
        {
            while (isRunning)
            {
                HttpListenerContext context = null;
                try
                {
                    context = httpListener.GetContext(); // blocks
                    _ = Task.Run(() => HandleContextAsync(context));
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

            initialize = incoming;
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
            
            WriteBytesToOutputStream(context, description.ToByteArray());
        }

        private async Task HandleResetAsync(HttpListenerContext context)
        {
            var incoming = ExternalCommunication.Reset.Parser.ParseFrom(context.Request.InputStream);

            reset = incoming;
            _resetTcs?.TrySetCanceled();
            _resetTcs = new TaskCompletionSource<Observations>(TaskCreationOptions.RunContinuationsAsynchronously);
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

            WriteBytesToOutputStream(context, obs.ToByteArray());
        }

        private async Task HandleStepAsync(HttpListenerContext context)
        {
            var incoming = Step.Parser.ParseFrom(context.Request.InputStream);

            step = incoming;
            _stepTcs?.TrySetCanceled();
            _stepTcs = new TaskCompletionSource<StepResults>(TaskCreationOptions.RunContinuationsAsynchronously);
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

            WriteBytesToOutputStream(context, sr.ToByteArray());
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


        private static async void WriteBytesToOutputStream(HttpListenerContext context, byte[] bytes)
        {
            context.Response.ContentLength64 = bytes.Length;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.StatusCode = 200;

            var output = context.Response.OutputStream;
            await output.WriteAsync(bytes, 0, bytes.Length);
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
            isRunning = false;
            httpListener.Stop();
            listenerThread.Join();
        }
    }
}