using UnityEngine;

namespace Scripts.VecEnv.Core
{
    [DefaultExecutionOrder(-501)]
    public class GymAgentManager : MonoBehaviour
    {
        public int agentCount;
        private GameObject _agentTemplate;
        private GymEnvironmentTemplate _environmentTemplate;


        public void HandleSceneLoad()
        {
            var agentsInScene = SpawnAgents(agentCount);
            if (agentsInScene > 0)
            {
                InitializeEnvAndRegisterAgents();
            }
            else
            {
                GymVecEnvManager.Instance.SpawnMode = SpawnMode.Disabled;
            }
        }

        public int SpawnAgents(int agents)
        {
            if (TryFindEnvironmentTemplate(out var environmentTemplate))
            {
                return SpawnEnvironments(environmentTemplate, agents);
            }

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
            var externalAgents = FindObjectsByType<GymAgent>(FindObjectsSortMode.None);
            var descriptionAgent = ResolveDescriptionAgent(externalAgents);
            if (descriptionAgent == null) return;

            foreach (var externalAgent in externalAgents)
            {
                manager.RegisterAgent(externalAgent);
            }

            manager.RegisterAgentDescription(descriptionAgent);
        }


        private void RemoveAgents(GymAgent[] agentsInScene, int length)
        {
            for (int i = 0; i < length; i++)
            {
                GymVecEnvManager.Instance.UnregisterAgent(agentsInScene[agentsInScene.Length - 1 - i]);
                Destroy(agentsInScene[agentsInScene.Length - 1 - i].gameObject);
            }
        }

        private void AddAgents(int nr)
        {
            for (int i = 0; i < nr; i++)
            {
                Instantiate(_agentTemplate, _agentTemplate.transform.parent);
            }
        }

        private bool TryFindEnvironmentTemplate(out GymEnvironmentTemplate environmentTemplate)
        {
            var templates = FindObjectsByType<GymEnvironmentTemplate>(FindObjectsSortMode.None);
            environmentTemplate = templates.Length > 0 ? templates[0] : null;
            _environmentTemplate = environmentTemplate;
            return environmentTemplate != null;
        }

        private int SpawnEnvironments(GymEnvironmentTemplate environmentTemplate, int requestedEnvironments)
        {
            var environmentsInScene = FindObjectsByType<GymEnvironmentTemplate>(FindObjectsSortMode.None);
            var targetEnvironments = ResolveEnvironmentCount(environmentTemplate, requestedEnvironments, environmentsInScene.Length);
            if (targetEnvironments <= 0) return environmentsInScene.Length;

            agentCount = targetEnvironments;

            if (environmentsInScene.Length > targetEnvironments)
            {
                RemoveEnvironments(environmentsInScene, environmentsInScene.Length - targetEnvironments);
            }

            if (environmentsInScene.Length < targetEnvironments)
            {
                AddEnvironments(environmentTemplate, targetEnvironments - environmentsInScene.Length, environmentsInScene.Length);
            }

            return targetEnvironments;
        }

        private int ResolveEnvironmentCount(GymEnvironmentTemplate environmentTemplate, int requestedEnvironments, int existingEnvironments)
        {
            if (requestedEnvironments > 0) return requestedEnvironments;
            if (environmentTemplate.defaultEnvCount > 0) return environmentTemplate.defaultEnvCount;
            return existingEnvironments;
        }

        private void RemoveEnvironments(GymEnvironmentTemplate[] environmentsInScene, int length)
        {
            for (int i = 0; i < length; i++)
            {
                var environmentRoot = environmentsInScene[environmentsInScene.Length - 1 - i].ClonePrefab;
                var agentsInEnvironment = environmentRoot.GetComponentsInChildren<GymAgent>(true);
                foreach (var agent in agentsInEnvironment)
                {
                    GymVecEnvManager.Instance.UnregisterAgent(agent);
                }

                Destroy(environmentRoot);
            }
        }

        private void AddEnvironments(GymEnvironmentTemplate environmentTemplate, int nr, int existingCount)
        {
            var templateObject = environmentTemplate.ClonePrefab;
            var templateTransform = templateObject.transform;
            var parent = templateTransform.parent;
            for (int i = 0; i < nr; i++)
            {
                var cloneIndex = existingCount + i;
                var localPosition = templateTransform.localPosition + environmentTemplate.cloneOffset * cloneIndex;
                var worldPosition = parent != null
                    ? parent.TransformPoint(localPosition)
                    : localPosition;

                Instantiate(templateObject, worldPosition, templateTransform.rotation, parent);
            }
        }

        private GymAgent ResolveDescriptionAgent(GymAgent[] externalAgents)
        {
            if (_environmentTemplate == null)
            {
                TryFindEnvironmentTemplate(out _environmentTemplate);
            }

            if (_environmentTemplate != null)
            {
                var templateDescriptionAgent = _environmentTemplate.DescriptionAgent;
                if (templateDescriptionAgent != null) return templateDescriptionAgent;
            }

            if (_agentTemplate == null) _agentTemplate = FindAnyObjectByType<GymAgent>()?.gameObject;
            if (_agentTemplate != null)
            {
                var templateAgent = _agentTemplate.GetComponent<GymAgent>();
                if (templateAgent != null) return templateAgent;
            }

            return externalAgents.Length > 0 ? externalAgents[0] : null;
        }
    }
}
