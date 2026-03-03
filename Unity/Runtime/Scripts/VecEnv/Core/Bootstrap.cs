using System;
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
        public static AgentSpawner AgentSpawner { get; set; }
        private static Dictionary<string, string> _args;


        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void BeforeSceneLoad()
        {
            AgentSpawner = CreateOrFetchSpawner();

            if (Application.isEditor) return;
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

        private static void ParseCommandLine()
        {
            Console.TreatControlCAsInput = true;
            _args = GetCommandlineArgs();

            if (_args.TryGetValue("-channel", out var channel))
            {
                int.TryParse(channel, out var channelValue);
                CommunicatorHttpServer.channel = channelValue;
                Debug.Log($"Channel value: {channelValue}");
            }

            if (_args.TryGetValue("-timeout", out var timeout))
            {
                int.TryParse(timeout, out var timeoutValue);
                GymVecEnvManager.Instance.timeoutMilliseconds = timeoutValue;
                Debug.Log($"Timeout value: {timeoutValue}");
            }

            if (_args.TryGetValue("-timescale", out var timeScale))
            {
                float.TryParse(timeScale, out var timeScaleValue);
                Time.timeScale = timeScaleValue;
                Debug.Log($"Time scale value: {timeScaleValue}");
            }

            if (_args.TryGetValue("-agentCount", out var agents))
            {
                int.TryParse(agents, out var agentsValue);
                AgentSpawner.agentCount = agentsValue;
                Debug.Log($"Agents value: {agentsValue}");
            }

            if (_args.TryGetValue("-decisionperiod", out var requestPeriod))
            {
                int.TryParse(requestPeriod, out var requestPeriodValue);
                GymVecEnvManager.Instance.physicsStepsPerGymStep = requestPeriodValue;
                Debug.Log($"Request period value: {requestPeriodValue}");
            }

            if (_args.TryGetValue("-scene", out var scene))
            {
                SceneToLoad = scene;
                Debug.Log($"Scene value: {scene}");
            }
        }

        private static AgentSpawner CreateOrFetchSpawner()
        {
            if (AgentSpawner != null) return AgentSpawner;

            var spawnerObject = new GameObject("AgentSpawner");
            spawnerObject.hideFlags = HideFlags.HideAndDontSave;
            spawnerObject.SetActive(false);

            var component = spawnerObject.AddComponent<AgentSpawner>();
            UnityEngine.Object.DontDestroyOnLoad(spawnerObject);

            SceneManager.sceneLoaded += OnSceneLoaded;

            return component;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            LoadingDone = SceneToLoad == null || SceneManager.GetActiveScene().name == SceneToLoad;
            AgentSpawner.HandleSceneLoad();
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