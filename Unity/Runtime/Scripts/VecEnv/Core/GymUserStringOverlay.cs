using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scripts.VecEnv.Core
{
    internal sealed class GymUserStringOverlay : MonoBehaviour
    {
        private const float Padding = 10f;
        private const int FontSize = 16;

        private string[] _lines = Array.Empty<string>();
        private GUIStyle _labelStyle;

        public void SetLines(IReadOnlyList<string> lines)
        {
            _lines = new string[lines.Count];
            for (var index = 0; index < lines.Count; index++)
            {
                _lines[index] = lines[index] ?? string.Empty;
            }
        }

        private void OnGUI()
        {
            if (_lines.Length == 0) return;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = FontSize,
                    wordWrap = true
                };
                _labelStyle.normal.textColor = Color.white;
            }

            var previousDepth = GUI.depth;
            GUI.depth = -1000;
            GUILayout.BeginArea(new Rect(
                Padding,
                Padding,
                Mathf.Max(0f, Screen.width - Padding * 2f),
                Mathf.Max(0f, Screen.height - Padding * 2f)));

            foreach (var line in _lines)
            {
                GUILayout.Label(line.Length == 0 ? " " : line, _labelStyle);
            }

            GUILayout.EndArea();
            GUI.depth = previousDepth;
        }
    }
}
