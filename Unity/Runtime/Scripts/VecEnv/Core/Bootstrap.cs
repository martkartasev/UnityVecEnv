using System;
using System.Collections.Generic;
using Scripts.VecEnv.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;
using Environment = System.Environment;

namespace Scripts.VecEnv.Core
{
    public static class Bootstrap
    {
        public static AgentAndManagerSpawner AgentAndManagerSpawner { get; set; }
        private static Dictionary<string, string> _args;


        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void BeforeSceneLoad()
        {
            AgentAndManagerSpawner = CreateSpawner();

            if (Application.isEditor) return;
            ParseCommandLine();
        }

        private static void ParseCommandLine()
        {
            Console.TreatControlCAsInput = true;
            _args = GetCommandlineArgs();

            //     if (args.ContainsKey("-nographics") || args.ContainsKey("-headless") || args.ContainsKey("-batchmode")) CommunicationServiceOld.noGraphics = true;

            if (_args.TryGetValue("-channel", out var channel))
            {
                int.TryParse(channel, out var channelValue);
                CommunicatorHttpServer.channel = channelValue;
                Debug.Log($"Channel value: {channelValue}");
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
                AgentAndManagerSpawner.agentCount = agentsValue;
                Debug.Log($"Agents value: {agentsValue}");
            }

            if (_args.TryGetValue("-decisionperiod", out var requestPeriod))
            {
                int.TryParse(requestPeriod, out var requestPeriodValue);
                GymVecEnvManager.Instance.physicsStepsPerGymStep = requestPeriodValue;
                Debug.Log($"Request period value: {requestPeriodValue}");
            }
        }

        private static AgentAndManagerSpawner CreateSpawner()
        {
            var spawnerObject = new GameObject("AgentSpawner");
            spawnerObject.hideFlags = HideFlags.HideAndDontSave;
            spawnerObject.SetActive(false);

            var component = spawnerObject.AddComponent<AgentAndManagerSpawner>();
            UnityEngine.Object.DontDestroyOnLoad(spawnerObject);

            SceneManager.sceneLoaded += OnSceneLoaded;

            return component;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            AgentAndManagerSpawner.gameObject.SetActive(true);
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
                    var value = i < args.Length - 1 ? args[i + 1].ToLower() : null;
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