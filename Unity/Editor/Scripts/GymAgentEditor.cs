using Scripts.VecEnv.Core;
using Scripts.VecEnv.Message;

namespace Editor.Scripts
{
    using System.Reflection;
    using System.Text;
    using UnityEditor;
    using UnityEngine;

    [CustomEditor(typeof(GymAgent), true)]
    public class GymAgentEditor : Editor
    {
        bool _showStats = true;
        const float RowH = 18f;


        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            DrawDebugData();

            serializedObject.ApplyModifiedProperties();
        }

        void DrawDebugData()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _showStats = EditorGUILayout.Foldout(_showStats, "Debug Data", true);
                if (!_showStats) return;
                var agent = (GymAgent)target;
                using (new EditorGUI.DisabledScope(false))
                {
                    var step = (int)typeof(GymAgent).GetField("CurrentStep", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);
                    DrawRow("Current Step", step.ToString());
                    
                    EditorGUILayout.Space(4);
                    DrawHeaderRow("Rewards", "");
                    var episodeReward = (float)typeof(GymAgent).GetField("EpisodeReward", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);
                    var prevEpisodeReward = (float)typeof(GymAgent).GetField("PreviousEpisodeReward", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);
                    var latestStepReward = (float)typeof(GymAgent).GetField("_latestStepReward", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);

                    DrawRow("Previous", prevEpisodeReward.ToString("0.####"));
                    DrawRow("Episode", episodeReward.ToString("0.####"));
                    DrawRow("Current", latestStepReward.ToString("0.####"));

                    var latestObs = (AgentObservation)typeof(GymAgent).GetField("_latestObservation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);
                    if (agent.continuousObservations > 0 || latestObs.Continuous?.Length > 0)
                    {
                        EditorGUILayout.Space(8);
                        DrawHeaderRow("Observations", "");
                        DrawHeaderRow("Index", "Continuous");
                        for (int i = 0; i < Mathf.Max(agent.continuousObservations, latestObs.Continuous?.Length ?? 0); i++)
                        {
                            if (latestObs.Continuous?.Length > i)
                            {
                                DrawRow(i.ToString(), latestObs.Continuous[i].ToString("0.####"));
                            }
                            else
                            {
                                DrawRow(i.ToString(), 0.ToString("0.####"));
                            }
                        }
                    }

                    var latestAction = (AgentAction)typeof(GymAgent).GetField("_latestAction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent);
                    if (agent.continuousActions > 0 || latestAction.Continuous?.Length > 0)
                    {
                        EditorGUILayout.Space(8);
                        DrawHeaderRow("Actions", "");
                        DrawHeaderRow("Index", "Continuous");
                        for (int i = 0; i < Mathf.Max(agent.continuousActions, latestAction.Continuous?.Length ?? 0); i++)
                        {
                            if (latestAction.Continuous?.Length > i)
                            {
                                DrawRow(i.ToString(), latestAction.Continuous[i].ToString("0.####"));
                            }
                            else
                            {
                                DrawRow(i.ToString(), 0.ToString("0.####"));
                            }
                        }
                    }
                }
            }
        }


        void DrawHeaderRow(string a, string b, float indexColWidth = 80f, float valueColWidth = 180f)
        {
            var r = EditorGUILayout.GetControlRect(false, RowH);
            var left = new Rect(r.x, r.y, indexColWidth, r.height);
            var right = new Rect(r.x + indexColWidth, r.y, valueColWidth, r.height);

            EditorGUI.LabelField(left, a, EditorStyles.boldLabel);
            EditorGUI.LabelField(right, b, EditorStyles.boldLabel);

            // A thin line
            var line = new Rect(r.x, r.yMax + 2, r.width, 1);
            EditorGUI.DrawRect(line, new Color(0, 0, 0, 0.2f));
            EditorGUILayout.Space(4);
        }

        void DrawRow(string index, string value, float indexColWidth = 80f, float valueColWidth = 180f)
        {
            var r = EditorGUILayout.GetControlRect(false, RowH);
            var left = new Rect(r.x, r.y, indexColWidth, r.height);
            var right = new Rect(r.x + indexColWidth, r.y, valueColWidth, r.height);

            EditorGUI.LabelField(left, index);
            EditorGUI.SelectableLabel(right, value); // selectable is nice for quick copying too
        }
    }
}