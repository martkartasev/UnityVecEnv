using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Scripts.VecEnv.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;
using Environment = System.Environment;

namespace Scripts.VecEnv.Core
{
    public static class Bootstrap
    {
        public static bool LoadingDone = false;
        public static string SceneToLoad = null;
        public static GymAgentManager GymAgentManager { get; set; }
        private static Dictionary<string, string> _args;
        private static bool _sceneCallbacksRegistered;
        private static int? _configuredTimeoutMilliseconds;
        private static int? _configuredPhysicsStepsPerGymStep;
        private static int? _configuredAgentCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;

            LoadingDone = false;
            SceneToLoad = null;
            GymAgentManager = null;
            _args = null;
            _sceneCallbacksRegistered = false;
            _configuredTimeoutMilliseconds = null;
            _configuredPhysicsStepsPerGymStep = null;
            _configuredAgentCount = null;
        }


        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void BeforeSceneLoad()
        {
            EnsureSceneCallbacksRegistered();

            if (Application.isEditor)
            {
                LoadingDone = true;
                return;
            }

            ParseCommandLine();

            if (SceneToLoad != null && SceneManager.GetSceneByBuildIndex(0).name != SceneToLoad && !LoadingDone)
            {
                Debug.Log($"Loading scene {SceneToLoad}");
                SceneManager.LoadScene(SceneToLoad);
            }
            else
            {
                LoadingDone = true;
            }
        }

        public static void EnsureVecEnvInitialized()
        {
            EnsureSceneCallbacksRegistered();

            GymAgentManager = CreateOrFetchSpawner();
            ApplyConfiguredValues(GymAgentManager);

            var manager = GymVecEnvManager.Instance;
            manager.AgentManager = GymAgentManager;
            ApplyConfiguredValues(manager);
        }

        private static void ParseCommandLine()
        {
            _args = GetCommandlineArgs();

            if (_args.TryGetValue("-channel", out var channel))
            {
                int.TryParse(channel, out var channelValue);
                CommunicatorHttpServer.channel = channelValue;
                Debug.Log($"Channel value: {channelValue}");
            }

            if (_args.TryGetValue("-timeout", out var timeout))
            {
                if (int.TryParse(timeout, out var timeoutValue))
                {
                    _configuredTimeoutMilliseconds = timeoutValue;
                    Debug.Log($"Timeout value: {timeoutValue}");
                }
            }

            if (_args.TryGetValue("-timescale", out var timeScale))
            {
                float.TryParse(timeScale, out var timeScaleValue);
                Time.timeScale = timeScaleValue;
                Debug.Log($"Time scale value: {timeScaleValue}");
            }

            if (_args.TryGetValue("-agentcount", out var agents))
            {
                if (int.TryParse(agents, out var agentsValue))
                {
                    _configuredAgentCount = agentsValue;
                    Debug.Log($"Agents value: {agentsValue}");
                }
            }

            if (_args.TryGetValue("-decisionperiod", out var requestPeriod))
            {
                if (int.TryParse(requestPeriod, out var requestPeriodValue))
                {
                    _configuredPhysicsStepsPerGymStep = requestPeriodValue;
                    Debug.Log($"Request period value: {requestPeriodValue}");
                }
            }

            if (_args.TryGetValue("-scene", out var scene))
            {
                SceneToLoad = scene;
                Debug.Log($"Scene value: {scene}");
            }
        }

        private static void EnsureSceneCallbacksRegistered()
        {
            if (_sceneCallbacksRegistered) return;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _sceneCallbacksRegistered = true;
        }

        private static GymAgentManager CreateOrFetchSpawner()
        {
            if (GymAgentManager != null) return GymAgentManager;

            var spawnerObject = new GameObject("AgentSpawner");
            spawnerObject.hideFlags = HideFlags.HideAndDontSave;
            spawnerObject.SetActive(false);

            var component = spawnerObject.AddComponent<GymAgentManager>();
            UnityEngine.Object.DontDestroyOnLoad(spawnerObject);

            return component;
        }

        private static void ApplyConfiguredValues(GymAgentManager gymAgentManager)
        {
            if (_configuredAgentCount.HasValue)
            {
                gymAgentManager.agentCount = _configuredAgentCount.Value;
            }
        }

        private static void ApplyConfiguredValues(GymVecEnvManager manager)
        {
            if (_configuredTimeoutMilliseconds.HasValue)
            {
                manager.timeoutMilliseconds = _configuredTimeoutMilliseconds.Value;
            }

            if (_configuredPhysicsStepsPerGymStep.HasValue)
            {
                manager.physicsStepsPerGymStep = _configuredPhysicsStepsPerGymStep.Value;
            }
        }

        private static bool SceneContainsGymContent(Scene scene)
        {
            return scene.IsValid() &&
                   (UnityEngine.Object.FindObjectsByType<GymEnvironmentTemplate>(FindObjectsSortMode.None).Length > 0 ||
                    UnityEngine.Object.FindObjectsByType<GymAgent>(FindObjectsSortMode.None).Length > 0);
        }

        private static void OnSceneUnloaded(Scene arg0)
        {
            if (GymVecEnvManager.IsInitialized)
            {
                GymVecEnvManager.Instance.ClearAgents();
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            LoadingDone = SceneToLoad == null || SceneManager.GetActiveScene().name == SceneToLoad;
            if (!SceneContainsGymContent(scene))
            {
                return;
            }

            EnsureVecEnvInitialized();
            GymAgentManager.HandleSceneLoad();
        }

        public static Dictionary<string, string> GetCommandlineArgs()
        {
            var argDictionary = new Dictionary<string, string>();

            var args = Environment.GetCommandLineArgs();

            for (var i = 0; i < args.Length; ++i)
            {
                var arg = args[i].ToLower();
                if (arg.StartsWith("-"))
                {
                    var value = i < args.Length - 1 ? args[i + 1] : null;
                    value = value?.StartsWith("-") ?? false ? null : value;

                    argDictionary.Add(arg, value);
                }
            }

            foreach (var kvp in argDictionary)
                //textBox3.Text += ("Key = {0}, Value = {1}", kvp.Key, kvp.Value);
                Debug.Log($"Key = {kvp.Key}, Value = {kvp.Value}");

            return argDictionary;
        }
    }
}
