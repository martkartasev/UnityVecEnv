using UnityEngine;

namespace Scripts.VecEnv.Core
{
    using System;
    using System.Collections.Generic;

    [DefaultExecutionOrder(-501)]
    public class GymAgentManager : MonoBehaviour
    {
        public int agentCount;
        private GameObject _agentTemplate;
        private GymEnvironmentTemplate _environmentTemplate;


        public void HandleSceneLoad()
        {
            if (!TryFindEnvironmentTemplate(out _) && FindOrderedComponents<GymAgent>().Length == 0)
            {
                _agentTemplate = null;
                return;
            }

            var agentsInScene = SpawnAgents(agentCount);
            if (agentsInScene > 0)
            {
                InitializeEnvAndRegisterAgents();
            }
            else if (GymVecEnvManager.IsInitialized)
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

            var agentsInScene = FindOrderedComponents<GymAgent>();
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
            var externalAgents = FindOrderedComponents<GymAgent>();
            var descriptionAgent = ResolveDescriptionAgent(externalAgents);
            if (descriptionAgent == null) return;

            Bootstrap.EnsureVecEnvInitialized();
            var manager = GymVecEnvManager.Instance;

            manager.RegisterAgentsInOrder(externalAgents);
            manager.RegisterAgentDescription(descriptionAgent);
        }


        private void RemoveAgents(GymAgent[] agentsInScene, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (GymVecEnvManager.IsInitialized)
                {
                    GymVecEnvManager.Instance.UnregisterAgent(agentsInScene[agentsInScene.Length - 1 - i]);
                }

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
            var templates = FindOrderedComponents<GymEnvironmentTemplate>();
            environmentTemplate = templates.Length > 0 ? templates[0] : null;
            _environmentTemplate = environmentTemplate;
            return environmentTemplate != null;
        }

        private int SpawnEnvironments(GymEnvironmentTemplate environmentTemplate, int requestedEnvironments)
        {
            var environmentsInScene = FindOrderedComponents<GymEnvironmentTemplate>();
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
                    if (GymVecEnvManager.IsInitialized)
                    {
                        GymVecEnvManager.Instance.UnregisterAgent(agent);
                    }
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
                var localPosition = templateTransform.localPosition + environmentTemplate.cloneOffset * (cloneIndex % environmentTemplate.rowlength) + environmentTemplate.rowOffset * (int)(cloneIndex / environmentTemplate.rowlength);
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

            if (_agentTemplate == null && externalAgents.Length > 0) _agentTemplate = externalAgents[0].gameObject;
            if (_agentTemplate != null)
            {
                var templateAgent = _agentTemplate.GetComponent<GymAgent>();
                if (templateAgent != null) return templateAgent;
            }

            return externalAgents.Length > 0 ? externalAgents[0] : null;
        }

        private static T[] FindOrderedComponents<T>() where T : Component
        {
            var components = FindObjectsByType<T>(FindObjectsSortMode.None);
            Array.Sort(components, CompareComponentsByHierarchy);
            return components;
        }

        private static int CompareComponentsByHierarchy<T>(T left, T right) where T : Component
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            var leftScene = string.IsNullOrEmpty(left.gameObject.scene.path)
                ? left.gameObject.scene.name
                : left.gameObject.scene.path;
            var rightScene = string.IsNullOrEmpty(right.gameObject.scene.path)
                ? right.gameObject.scene.name
                : right.gameObject.scene.path;
            var sceneComparison = string.CompareOrdinal(leftScene, rightScene);
            if (sceneComparison != 0) return sceneComparison;

            var leftPath = GetSiblingIndexPath(left.transform);
            var rightPath = GetSiblingIndexPath(right.transform);
            var commonLength = Math.Min(leftPath.Count, rightPath.Count);
            for (var index = 0; index < commonLength; index++)
            {
                var indexComparison = leftPath[index].CompareTo(rightPath[index]);
                if (indexComparison != 0) return indexComparison;
            }

            var pathLengthComparison = leftPath.Count.CompareTo(rightPath.Count);
            if (pathLengthComparison != 0) return pathLengthComparison;

            var typeComparison = string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
            if (typeComparison != 0) return typeComparison;

            return Array.IndexOf(left.GetComponents<Component>(), left)
                .CompareTo(Array.IndexOf(right.GetComponents<Component>(), right));
        }

        private static List<int> GetSiblingIndexPath(Transform transform)
        {
            var path = new List<int>();
            for (var current = transform; current != null; current = current.parent)
            {
                path.Add(current.GetSiblingIndex());
            }

            path.Reverse();
            return path;
        }
    }
}
