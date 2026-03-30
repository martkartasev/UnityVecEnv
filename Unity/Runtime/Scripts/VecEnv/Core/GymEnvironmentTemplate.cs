using UnityEngine;

namespace Scripts.VecEnv.Core
{
    [DisallowMultipleComponent]
    public sealed class GymEnvironmentTemplate : MonoBehaviour
    {
        [SerializeField] private Transform cloneRoot;
        [SerializeField] private GymAgent descriptionAgent;
        public Vector3 cloneOffset;
        public int defaultEnvCount = 1;
        public int rowlength = 20;
        public Vector3 rowOffset = new Vector3(0, 0, 120);

        public GameObject ClonePrefab => cloneRoot != null && transform.IsChildOf(cloneRoot)
            ? cloneRoot.gameObject
            : gameObject;

        public GymAgent DescriptionAgent => descriptionAgent != null
            ? descriptionAgent
            : ClonePrefab.GetComponentInChildren<GymAgent>(true);
    }
}