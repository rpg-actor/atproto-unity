using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    [CustomEditor(typeof(AtProtoLogin))]
    sealed class AtProtoLoginEditor : UnityEditor.Editor
    {
        SerializedProperty loginMethod;
        SerializedProperty handle;
        SerializedProperty clientId;
        SerializedProperty resource;
        SerializedProperty recordPermissions;
        SerializedProperty blobPermission;
        SerializedProperty identityEndpoint;
        SerializedProperty plcDirectoryEndpoint;

        void OnEnable()
        {
            loginMethod = serializedObject.FindProperty("loginMethod");
            handle = serializedObject.FindProperty("handle");
            clientId = serializedObject.FindProperty("clientId");
            resource = serializedObject.FindProperty("resource");
            recordPermissions = serializedObject.FindProperty("recordPermissions");
            blobPermission = serializedObject.FindProperty("blobPermission");
            identityEndpoint = serializedObject.FindProperty("identityEndpoint");
            plcDirectoryEndpoint = serializedObject.FindProperty("plcDirectoryEndpoint");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Authentication", EditorStyles.boldLabel);
            loginMethod.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Login method"),
                loginMethod.enumValueIndex,
                new[] { "OAuth", "Password" });
            var method = loginMethod.enumValueIndex;
            EditorGUILayout.PropertyField(handle, new GUIContent("Default handle"));

            DrawResolutionEndpoints();

            if (method == 0)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("OAuth Client", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(clientId, new GUIContent("Client ID"));
                EditorGUILayout.PropertyField(resource, new GUIContent("Resource"));

                DrawRecordPermissions();
                DrawBlobPermission();
            }

            serializedObject.ApplyModifiedProperties();

            if (method == 0)
            {
                var login = (AtProtoLogin)target;
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Generated OAuth scope", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(
                    login.Scope,
                    EditorStyles.helpBox,
                    GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2f));
            }
        }

        void DrawResolutionEndpoints()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Resolution Endpoints", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(identityEndpoint, new GUIContent("Identity endpoint"));
            EditorGUILayout.PropertyField(plcDirectoryEndpoint, new GUIContent("PLC directory endpoint"));
        }

        void DrawRecordPermissions()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Record Permissions", EditorStyles.boldLabel);

            if (recordPermissions == null)
                return;

            for (var i = 0; i < recordPermissions.arraySize; i++)
            {
                var element = recordPermissions.GetArrayElementAtIndex(i);
                if (DrawPermission(element, i))
                {
                    recordPermissions.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }

            if (GUILayout.Button("Add record permission", EditorStyles.miniButton))
                AddPermission();

            DrawPermissionWarnings();
            if (recordPermissions.arraySize == 0)
                EditorGUILayout.HelpBox(
                    "No record-writing access requested. Add a record type when the game needs to write data.",
                    MessageType.None);
        }

        void DrawPermissionWarnings()
        {
            if (recordPermissions == null)
                return;

            for (var i = 0; i < recordPermissions.arraySize; i++)
            {
                var element = recordPermissions.GetArrayElementAtIndex(i);
                var collection = element.FindPropertyRelative("collection").stringValue.Trim();
                var hasAction = element.FindPropertyRelative("create").boolValue
                    || element.FindPropertyRelative("update").boolValue
                    || element.FindPropertyRelative("delete").boolValue;

                if (string.IsNullOrEmpty(collection))
                    EditorGUILayout.HelpBox(
                        "Record permission " + (i + 1) + " needs a collection NSID.",
                        MessageType.Warning);
                else if (!AtProtoRecordPermission.IsValidCollection(collection))
                    EditorGUILayout.HelpBox(
                        "Record permission " + (i + 1) + " has an invalid collection NSID.",
                        MessageType.Warning);
                else if (!hasAction)
                    EditorGUILayout.HelpBox(
                        "Record permission " + (i + 1) + " has no selected actions and will be skipped.",
                        MessageType.Warning);
            }
        }

        bool DrawPermission(SerializedProperty element, int index)
        {
            var collection = element.FindPropertyRelative("collection");
            var create = element.FindPropertyRelative("create");
            var update = element.FindPropertyRelative("update");
            var delete = element.FindPropertyRelative("delete");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var controlName = "AtProtoRecordPermission." + index;
                    var value = AtProtoRecordTypeAutocomplete.Text(
                        controlName,
                        new GUIContent("Record type"),
                        collection.stringValue);
                    collection.stringValue = value;
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(64f)))
                        return true;
                }

                var picked = AtProtoRecordTypeAutocomplete.Suggestions(
                    "AtProtoRecordPermission." + index,
                    collection.stringValue);
                if (picked != null)
                    collection.stringValue = picked;

                EditorGUILayout.LabelField("Allowed actions", EditorStyles.miniLabel);
                var actionRect = EditorGUILayout.GetControlRect(
                    false,
                    EditorGUIUtility.singleLineHeight);
                var actionWidth = actionRect.width / 3f;
                create.boolValue = EditorGUI.ToggleLeft(
                    new Rect(actionRect.x, actionRect.y, actionWidth, actionRect.height),
                    "Create",
                    create.boolValue);
                update.boolValue = EditorGUI.ToggleLeft(
                    new Rect(actionRect.x + actionWidth, actionRect.y, actionWidth, actionRect.height),
                    "Update",
                    update.boolValue);
                delete.boolValue = EditorGUI.ToggleLeft(
                    new Rect(actionRect.x + actionWidth * 2f, actionRect.y, actionWidth, actionRect.height),
                    "Delete",
                    delete.boolValue);
            }
            return false;
        }

        void DrawBlobPermission()
        {
            if (blobPermission == null)
                return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Blob access", EditorStyles.miniBoldLabel);
            var upload = blobPermission.FindPropertyRelative("upload");
            EditorGUILayout.PropertyField(upload, new GUIContent("Upload blobs"));

            if (!upload.boolValue)
                return;

            var acceptedMimeTypes = blobPermission.FindPropertyRelative("acceptedMimeTypes");
            EditorGUILayout.LabelField("Accepted MIME types", EditorStyles.miniLabel);
            for (var i = 0; i < acceptedMimeTypes.arraySize; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var type = acceptedMimeTypes.GetArrayElementAtIndex(i);
                    type.stringValue = EditorGUILayout.TextField(type.stringValue);
                    if (!string.IsNullOrWhiteSpace(type.stringValue)
                        && !AtProtoBlobPermission.IsValidMimeType(type.stringValue.Trim()))
                    {
                        EditorGUILayout.HelpBox("Invalid MIME type", MessageType.Warning);
                    }
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(64f)))
                    {
                        acceptedMimeTypes.DeleteArrayElementAtIndex(i);
                        serializedObject.ApplyModifiedProperties();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (GUILayout.Button("Add MIME type", EditorStyles.miniButton))
            {
                var index = acceptedMimeTypes.arraySize;
                acceptedMimeTypes.InsertArrayElementAtIndex(index);
                acceptedMimeTypes.GetArrayElementAtIndex(index).stringValue = string.Empty;
            }

            if (acceptedMimeTypes.arraySize == 0)
                EditorGUILayout.HelpBox(
                    "Add at least one MIME type before requesting blob upload access.",
                    MessageType.Warning);
        }

        void AddPermission()
        {
            var index = recordPermissions.arraySize;
            recordPermissions.InsertArrayElementAtIndex(index);
            var element = recordPermissions.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("collection").stringValue = string.Empty;
            element.FindPropertyRelative("create").boolValue = false;
            element.FindPropertyRelative("update").boolValue = false;
            element.FindPropertyRelative("delete").boolValue = false;
        }

        static class AtProtoRecordTypeAutocomplete
        {
            const int MaxSuggestions = 8;
            const int MaxRecents = 8;
            const string RecentKey = "AtProtoUnity.Recent.RecordType";

            sealed class Suggestion
            {
                public readonly string Value;
                public readonly string Label;

                public Suggestion(string value, string label)
                {
                    Value = value;
                    Label = label;
                }
            }

            static string activeControl;
            static string activeFilter;
            static int highlighted = -1;
            static bool accept;
            static GUIStyle suggestionStyle;

            static GUIStyle SuggestionStyle => suggestionStyle ??= new GUIStyle(EditorStyles.miniButton)
            {
                alignment = TextAnchor.MiddleLeft
            };

            public static string Text(string controlName, GUIContent label, string text)
            {
                var e = Event.current;
                if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == controlName)
                {
                    switch (e.keyCode)
                    {
                        case KeyCode.DownArrow:
                            highlighted++;
                            e.Use();
                            break;
                        case KeyCode.UpArrow:
                            highlighted = Mathf.Max(-1, highlighted - 1);
                            e.Use();
                            break;
                        case KeyCode.Return or KeyCode.KeypadEnter when highlighted >= 0:
                            accept = true;
                            e.Use();
                            break;
                        case KeyCode.Escape:
                            GUI.FocusControl(null);
                            highlighted = -1;
                            e.Use();
                            break;
                    }
                }

                GUI.SetNextControlName(controlName);
                return EditorGUILayout.TextField(label, text);
            }

            public static string Suggestions(string controlName, string filter)
            {
                if (GUI.GetNameOfFocusedControl() != controlName)
                    return null;

                if (activeControl != controlName || activeFilter != filter)
                {
                    activeControl = controlName;
                    activeFilter = filter;
                    highlighted = -1;
                }

                var matches = GetMatches(filter);
                highlighted = Mathf.Min(highlighted, matches.Count - 1);
                if (accept)
                {
                    accept = false;
                    if (highlighted >= 0)
                        return Pick(matches[highlighted]);
                }

                for (var i = 0; i < matches.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(EditorGUIUtility.labelWidth);
                        var color = GUI.backgroundColor;
                        if (i == highlighted)
                            GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                        var clicked = GUILayout.Button(matches[i].Label, SuggestionStyle);
                        GUI.backgroundColor = color;
                        if (clicked)
                            return Pick(matches[i]);
                    }
                }
                return null;
            }

            static List<Suggestion> GetMatches(string filter)
            {
                filter ??= string.Empty;

                var matches = new List<Suggestion>();
                var recentValues = GetRecentValues();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var recent in recentValues)
                {
                    var definition = FindDefinition(recent);
                    var suggestion = definition == null
                        ? new Suggestion(recent, "Recent  (" + recent + ")")
                        : ToSuggestion(definition);
                    AddMatch(matches, seen, suggestion, filter);
                }

                foreach (var definition in AtProtoOAuthScopes.KnownRecordTypes)
                    AddMatch(matches, seen, ToSuggestion(definition), filter);

                if (matches.Count > MaxSuggestions)
                    matches.RemoveRange(MaxSuggestions, matches.Count - MaxSuggestions);
                return matches;
            }

            static void AddMatch(
                List<Suggestion> matches,
                HashSet<string> seen,
                Suggestion suggestion,
                string filter)
            {
                if (filter.Length > 0 && suggestion.Value == filter)
                    return;
                if (filter.Length > 0
                    && suggestion.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && suggestion.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                if (seen.Add(suggestion.Value))
                    matches.Add(suggestion);
            }

            static Suggestion ToSuggestion(AtProtoOAuthScopeDefinition definition) =>
                new(definition.Token, definition.Label + "  (" + definition.Token + ")");

            static AtProtoOAuthScopeDefinition FindDefinition(string token)
            {
                foreach (var definition in AtProtoOAuthScopes.KnownRecordTypes)
                    if (definition.Token == token)
                        return definition;
                return null;
            }

            static List<string> GetRecentValues()
            {
                var saved = EditorPrefs.GetString(RecentKey, string.Empty);
                return new List<string>(saved.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }

            static string Pick(Suggestion suggestion)
            {
                Remember(suggestion.Value);
                highlighted = -1;
                GUI.FocusControl(null);
                return suggestion.Value;
            }

            static void Remember(string value)
            {
                var recentValues = GetRecentValues();
                recentValues.Remove(value);
                recentValues.Insert(0, value);
                if (recentValues.Count > MaxRecents)
                    recentValues.RemoveRange(MaxRecents, recentValues.Count - MaxRecents);
                EditorPrefs.SetString(RecentKey, string.Join("\n", recentValues));
            }
        }
    }
}