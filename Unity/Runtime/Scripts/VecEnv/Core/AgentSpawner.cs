using UnityEngine;

namespace Scripts.VecEnv.Core
{
    [DefaultExecutionOrder(-501)]
    public class AgentSpawner : MonoBehaviour
    {
        public int agentCount;
        private GameObject _agentTemplate;


        public void HandleSceneLoad()
        {
            var agentsInScene = SpawnAgents(agentCount);
            if (agentsInScene > 0) InitializeEnvAndRegisterAgents();
        }

        public int SpawnAgents(int agents)
        {
            var agentsInScene = FindObjectsByType<GymAgent>(FindObjectsSortMode.None);
            if (agentsInScene.Length > 0)
            {
                _agentTemplate = agentsInScene[0].gameObject;
            }

            if (agents <= 0) return agentsInScene.Length;

            agentCount = agents;

            if (agentsInScene.Length > agents)
            {
                RemoveAgents(agentsInScene, agentsInScene.Length - agents);
            }

            if (agentsInScene.Length < agents)
            {
                AddAgents(agents - agentsInScene.Length);
            }

            return agentCount;
        }


        public void InitializeEnvAndRegisterAgents()
        {
            var manager = GymVecEnvManager.Instance;
            manager.Spawner = this;
            manager.ClearAgents();

            var externalAgents = FindObjectsByType<GymAgent>(FindObjectsSortMode.None);
            foreach (var externalAgent in externalAgents)
            {
                manager.RegisterAgent(externalAgent);
            }

            manager.RegisterAgentDescription(_agentTemplate.GetComponent<GymAgent>());
        }


        private void RemoveAgents(GymAgent[] agentsInScene, int length)
        {
            for (int i = 0; i < length; i++)
                Destroy(agentsInScene[agentsInScene.Length - 1 - i].gameObject);
        }

        private void AddAgents(int nr)
        {
            for (int i = 0; i < nr; i++)
            {
                Instantiate(_agentTemplate, _agentTemplate.transform.parent);
            }
        }
    }
}