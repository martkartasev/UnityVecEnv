using System.Collections.Generic;
using Scripts.VecEnv.Message;
using UnityEngine;
using Action = Scripts.VecEnv.Message.Action;

namespace Scripts.VecEnv.Core
{
    [DefaultExecutionOrder(-50)]
    public abstract class ExternalAgent : MonoBehaviour
    {
        
        
        [Header("Observation Specification")] public int continuousObservations = 0;
        public List<int> discreteObservations = new();


        [Header("Action Specification")] public int continuousActions = 0;
        public List<int> discreteActions = new();
        protected abstract void Reset();
        public abstract void SetAction(Action action);
        protected abstract EnvironmentState Step();
        protected abstract void CollectObservation(AgentObservation agentObservation);

        public virtual Action ProduceDummyAction(Action dummyAction)
        {
            return dummyAction;
        }
 
        public AgentObservation ProduceObservation()
        {
            var produceObservation = new AgentObservation(continuousObservations, discreteActions.Count);
            CollectObservation(produceObservation);
            return produceObservation;
        }
    }
}