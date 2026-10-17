using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RpgActorUnity
{
    internal class Suggestion
    {
        public readonly string Value;
        public readonly string Label;

        public Suggestion(string value, string label = null)
        {
            Value = value;
            Label = label ?? value;
        }
    }

    internal static class AutocompleteField
    {
        private const int MaxSuggestions = 8;
        private const int MaxRecents = 8;

        private static readonly Dictionary<string, List<string>> Recents = new();
        private static readonly Color HighlightColor = new(0.55f, 0.75f, 1f);

        private static string _activeControl;
        private static string _activeFilter;
        private static int _highlight = -1;
        private static bool _accept;
        private static GUIStyle _style;

        private static GUIStyle Style => _style ??= new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft };

        public static string Text(string controlName, GUIContent label, string text)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == controlName)
            {
                switch (e.keyCode)
                {
                    case KeyCode.DownArrow:
                        _highlight++;
                        e.Use();
                        break;
                    case KeyCode.UpArrow:
                        _highlight = Mathf.Max(-1, _highlight - 1);
                        e.Use();
                        break;
                    case KeyCode.Return or KeyCode.KeypadEnter when _highlight >= 0:
                        _accept = true;
                        e.Use();
                        break;
                    case KeyCode.Escape:
                        GUI.FocusControl(null);
                        e.Use();
                        break;
                }
            }

            GUI.SetNextControlName(controlName);
            return label == null ? EditorGUILayout.TextField(text) : EditorGUILayout.TextField(label, text);
        }

        public static string Suggestions(string controlName, string filter, IEnumerable<Suggestion> options, float indent)
        {
            if (GUI.GetNameOfFocusedControl() != controlName) return null;
            if (_activeControl != controlName || _activeFilter != filter)
            {
                _activeControl = controlName;
                _activeFilter = filter;
                _highlight = -1;
            }

            var matches = new List<Suggestion>();
            foreach (var option in options)
            {
                if (filter.Length > 0 && (option.Value == filter || !Matches(option, filter))) continue;
                matches.Add(option);
                if (matches.Count == MaxSuggestions) break;
            }

            _highlight = Mathf.Min(_highlight, matches.Count - 1);
            if (_accept)
            {
                _accept = false;
                if (_highlight >= 0) return Pick(matches[_highlight]);
            }

            for (var i = 0; i < matches.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent + EditorGUI.indentLevel * 15);
                    var color = GUI.backgroundColor;
                    if (i == _highlight) GUI.backgroundColor = HighlightColor;
                    var clicked = GUILayout.Button(matches[i].Label, Style);
                    GUI.backgroundColor = color;
                    if (clicked) return Pick(matches[i]);
                }
            }
            return null;
        }

        public static IEnumerable<Suggestion> RecentThenPopular(string kind, List<Suggestion> popular)
        {
            var recents = RecentsOf(kind);
            foreach (var recent in recents)
                yield return popular?.Find(option => option.Value == recent) ?? new Suggestion(recent);
            if (popular == null) yield break;
            foreach (var option in popular)
                if (!recents.Contains(option.Value))
                    yield return option;
        }

        public static void Remember(string kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var recents = RecentsOf(kind);
            recents.Remove(value);
            recents.Insert(0, value);
            if (recents.Count > MaxRecents)
                recents.RemoveRange(MaxRecents, recents.Count - MaxRecents);
            EditorPrefs.SetString(PrefKey(kind), string.Join("\n", recents));
        }

        private static bool Matches(Suggestion option, string filter) =>
            option.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            option.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Pick(Suggestion option)
        {
            _highlight = -1;
            GUI.FocusControl(null);
            return option.Value;
        }

        private static List<string> RecentsOf(string kind)
        {
            if (!Recents.TryGetValue(kind, out var recents))
            {
                var saved = EditorPrefs.GetString(PrefKey(kind), "");
                Recents[kind] = recents = new List<string>(saved.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            return recents;
        }

        private static string PrefKey(string kind) => $"RpgActorUnity.Recent.{kind}";
    }
}
