using Newtonsoft.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    [CustomEditor(typeof(AtProtoRecordComposer))]
    sealed class AtProtoRecordComposerEditor : UnityEditor.Editor
    {
        SerializedProperty baseJson;
        SerializedProperty fields;
        SerializedProperty groupList;
        SerializedProperty addCreatedAt;
        SerializedProperty createdAtPath;
        SerializedProperty addUpdatedAt;
        SerializedProperty updatedAtPath;
        bool showStartingJson;
        bool showOutputJson;
        readonly Dictionary<string, bool> expandedGroups = new();
        string newGroupPath = string.Empty;
        string renamingGroup;
        string renameBuffer = string.Empty;
        string subgroupParent;
        string subgroupBuffer = string.Empty;
        float startingJsonHeight = 150f;
        float outputJsonHeight = 190f;

        void OnEnable()
        {
            baseJson = serializedObject.FindProperty("baseJson");
            fields = serializedObject.FindProperty("fields");
            groupList = serializedObject.FindProperty("groups");
            addCreatedAt = serializedObject.FindProperty("addCreatedAt");
            createdAtPath = serializedObject.FindProperty("createdAtPath");
            addUpdatedAt = serializedObject.FindProperty("addUpdatedAt");
            updatedAtPath = serializedObject.FindProperty("updatedAtPath");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            showStartingJson = EditorGUILayout.Foldout(
                showStartingJson,
                "Starting JSON",
                true);
            if (showStartingJson)
            {
                baseJson.stringValue = AtProtoJsonEditorUtility.DrawTextArea(
                    baseJson.stringValue,
                    ref startingJsonHeight);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Fields", EditorStyles.boldLabel);
            DrawFieldTable();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Timestamps", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(addCreatedAt, new GUIContent("createdAt"));
            if (addCreatedAt.boolValue)
                EditorGUILayout.PropertyField(createdAtPath, new GUIContent("Path"));
            EditorGUILayout.PropertyField(addUpdatedAt, new GUIContent("updatedAt"));
            if (addUpdatedAt.boolValue)
                EditorGUILayout.PropertyField(updatedAtPath, new GUIContent("Path"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4f);
            showOutputJson = EditorGUILayout.Foldout(
                showOutputJson,
                "Output JSON",
                true);
            if (showOutputJson)
                DrawOutputJson((AtProtoRecordComposer)target);

            if (GUILayout.Button("Clear runtime values", EditorStyles.miniButton))
                ((AtProtoRecordComposer)target).ClearRuntimeValues();
        }

        void DrawFieldTable()
        {
            if (fields == null)
                return;

            var groups = BuildGroups();
            var baseIndent = EditorGUI.indentLevel;
            foreach (var group in groups)
            {
                if (IsHidden(group.Path))
                    continue;

                var depth = GetDepth(group.Path);
                if (depth == 0 && group.Path.Length > 0)
                    DrawDivider();

                EditorGUI.indentLevel = baseIndent + depth;
                if (DrawGroupHeader(group, groups))
                    DrawGroupBody(group);
            }
            EditorGUI.indentLevel = baseIndent;

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("New group", GUILayout.Width(70f));
                newGroupPath = EditorGUILayout.TextField(
                    newGroupPath,
                    GUILayout.MinWidth(90f));
                using (new EditorGUI.DisabledScope(!IsValidGroupPath(newGroupPath)))
                {
                    if (GUILayout.Button("+ Add group", EditorStyles.miniButton, GUILayout.Width(100f)))
                    {
                        AddGroup(newGroupPath.Trim());
                        newGroupPath = string.Empty;
                    }
                }
            }
        }

        static void DrawDivider()
        {
            GUILayout.Space(6f);
            var line = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(line, new Color(0.5f, 0.5f, 0.5f, 0.6f));
            GUILayout.Space(6f);
        }

        void DrawGroupBody(FieldGroup group)
        {
            EditorGUI.indentLevel++;
            if (group.Indices.Count > 0)
            {
                DrawColumnHeaders();
                EditorGUILayout.Space(2f);
            }
            foreach (var index in group.Indices)
                DrawFieldRow(index, group.Path);
            EditorGUI.indentLevel--;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f + EditorGUI.indentLevel * 15f);
                if (GUILayout.Button(
                    string.IsNullOrEmpty(group.Path)
                        ? "+ Add root field"
                        : "+ Add field to " + group.Path,
                    EditorStyles.miniButton))
                {
                    AddField(group.Path);
                }
                if (GUILayout.Button("+ Subgroup", EditorStyles.miniButton, GUILayout.Width(80f)))
                {
                    subgroupParent = group.Path;
                    subgroupBuffer = string.Empty;
                    GUI.FocusControl(null);
                }
            }

            if (subgroupParent == group.Path)
                DrawSubgroupRow(group.Path);
        }

        static void DrawColumnHeaders()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Field", EditorStyles.miniLabel, GUILayout.Width(120f));
                GUILayout.Label("Type", EditorStyles.miniLabel, GUILayout.Width(105f));
                GUILayout.Label("Value", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                GUILayout.Space(62f);
            }
        }

        void DrawSubgroupRow(string parentPath)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                subgroupBuffer = EditorGUILayout.TextField(subgroupBuffer);
                using (new EditorGUI.DisabledScope(!IsValidGroupPath(subgroupBuffer)))
                {
                    if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(48f)))
                    {
                        var name = subgroupBuffer.Trim();
                        subgroupParent = null;
                        AddGroup(parentPath.Length == 0 ? name : parentPath + "." + name);
                        serializedObject.ApplyModifiedProperties();
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(52f)))
                    subgroupParent = null;
            }
        }

        void AddGroup(string path)
        {
            var names = ReadGroupList();
            if (!names.Contains(path))
                names.Add(path);
            WriteGroupList(names);
            expandedGroups[path] = true;
        }

        List<string> ReadGroupList()
        {
            var names = new List<string>();
            for (var index = 0; index < groupList.arraySize; index++)
                names.Add(groupList.GetArrayElementAtIndex(index).stringValue);
            return names;
        }

        void WriteGroupList(List<string> names)
        {
            if (names.SequenceEqual(ReadGroupList()))
                return;

            groupList.arraySize = names.Count;
            for (var index = 0; index < names.Count; index++)
                groupList.GetArrayElementAtIndex(index).stringValue = names[index];
        }

        // An empty target drops the group names; nested names move under the target.
        void RemapGroups(string sourcePath, string targetPath)
        {
            var sourcePrefix = sourcePath + ".";
            var result = new List<string>();
            foreach (var name in ReadGroupList())
            {
                string mapped;
                if (name == sourcePath)
                    mapped = targetPath;
                else if (IsUnder(name, sourcePath))
                    mapped = targetPath.Length == 0 ? name.Substring(sourcePrefix.Length) : targetPath + "." + name.Substring(sourcePrefix.Length);
                else
                    mapped = name;

                if (mapped.Length > 0 && !result.Contains(mapped))
                    result.Add(mapped);
            }
            WriteGroupList(result);
        }

        void AddField(string groupPath)
        {
            var path = string.IsNullOrWhiteSpace(groupPath)
                ? string.Empty
                : groupPath.Trim().TrimEnd('.') + ".";
            var index = fields.arraySize;
            fields.InsertArrayElementAtIndex(index);
            var element = fields.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("path").stringValue = path;
            element.FindPropertyRelative("valueType").enumValueIndex = 0;
            element.FindPropertyRelative("value").stringValue = string.Empty;
            element.FindPropertyRelative("bindingSource").objectReferenceValue = null;
            element.FindPropertyRelative("bindingMember").stringValue = string.Empty;
            element.FindPropertyRelative("bound").boolValue = false;
            expandedGroups[GetParentPath(path)] = true;
        }

        void DrawFieldRow(int index, string groupPath)
        {
            var element = fields.GetArrayElementAtIndex(index);
            var path = element.FindPropertyRelative("path");
            var valueType = element.FindPropertyRelative("valueType");
            var value = element.FindPropertyRelative("value");
            var source = element.FindPropertyRelative("bindingSource");
            var member = element.FindPropertyRelative("bindingMember");
            var bound = element.FindPropertyRelative("bound");
            var prefix = string.IsNullOrEmpty(groupPath) ? string.Empty : groupPath + ".";

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var name = path.stringValue.StartsWith(prefix, System.StringComparison.Ordinal)
                    ? path.stringValue.Substring(prefix.Length)
                    : path.stringValue;
                EditorGUI.BeginChangeCheck();
                var edited = EditorGUILayout.TextField(name, GUILayout.Width(120f));
                if (EditorGUI.EndChangeCheck())
                    path.stringValue = prefix + edited.Replace(".", string.Empty);

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(
                    valueType,
                    GUIContent.none,
                    GUILayout.Width(105f));
                if (EditorGUI.EndChangeCheck())
                {
                    value.stringValue = NormalizeLiteral(
                        value.stringValue,
                        (AtProtoRecordValueType)valueType.enumValueIndex);
                }

                var type = (AtProtoRecordValueType)valueType.enumValueIndex;
                if (type == AtProtoRecordValueType.UtcTimestamp)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.TextField("now", GUILayout.ExpandWidth(true));
                }
                else if (bound.boolValue)
                {
                    DrawBinding(source, member);
                }
                else
                {
                    DrawLiteralValue(value, type);
                }

                using (new EditorGUI.DisabledScope(
                    type == AtProtoRecordValueType.UtcTimestamp))
                {
                    var previousColor = GUI.backgroundColor;
                    if (bound.boolValue)
                        GUI.backgroundColor = new Color(0.35f, 0.7f, 1f);
                    bound.boolValue = GUILayout.Toggle(
                        bound.boolValue,
                        new GUIContent("Link", "Read this value from a script or component"),
                        EditorStyles.miniButton,
                        GUILayout.Width(40f));
                    GUI.backgroundColor = previousColor;
                }

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    fields.DeleteArrayElementAtIndex(index);
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }
        }

        static void DrawBinding(SerializedProperty source, SerializedProperty member)
        {
            EditorGUILayout.PropertyField(
                source,
                GUIContent.none,
                GUILayout.MinWidth(0f),
                GUILayout.ExpandWidth(true));

            var members = AtProtoRecordBinding.GetMembers(source.objectReferenceValue);
            var options = new string[members.Count + 1];
            options[0] = source.objectReferenceValue == null ? "(set object first)" : "(select member)";
            for (var index = 0; index < members.Count; index++)
                options[index + 1] = members[index].Replace('.', '/');

            var selected = members.IndexOf(member.stringValue) + 1;
            using (new EditorGUI.DisabledScope(members.Count == 0))
            {
                var chosen = EditorGUILayout.Popup(
                    selected,
                    options,
                    GUILayout.MinWidth(0f),
                    GUILayout.ExpandWidth(true));
                if (chosen != selected)
                    member.stringValue = chosen == 0 ? string.Empty : members[chosen - 1];
            }
        }

        static void DrawLiteralValue(SerializedProperty value, AtProtoRecordValueType type)
        {
            var text = value.stringValue ?? string.Empty;
            EditorGUI.BeginChangeCheck();
            string edited;
            switch (type)
            {
                case AtProtoRecordValueType.Boolean:
                    var flag = bool.TryParse(text, out var parsedFlag) && parsedFlag;
                    edited = EditorGUILayout.ToggleLeft(flag ? "true" : "false", flag)
                        ? "true"
                        : "false";
                    break;
                case AtProtoRecordValueType.Integer:
                    long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer);
                    edited = EditorGUILayout.LongField(integer)
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case AtProtoRecordValueType.Number:
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    edited = EditorGUILayout.DoubleField(number)
                        .ToString("R", CultureInfo.InvariantCulture);
                    break;
                case AtProtoRecordValueType.Json:
                case AtProtoRecordValueType.JsonObject:
                case AtProtoRecordValueType.JsonArray:
                    var previousColor = GUI.backgroundColor;
                    if (!AtProtoRecordValueConverter.IsValid(text, type))
                        GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                    edited = EditorGUILayout.TextField(text, GUILayout.ExpandWidth(true));
                    GUI.backgroundColor = previousColor;
                    break;
                default:
                    edited = EditorGUILayout.TextField(text, GUILayout.ExpandWidth(true));
                    break;
            }

            if (EditorGUI.EndChangeCheck())
                value.stringValue = edited;
        }

        static string NormalizeLiteral(string literal, AtProtoRecordValueType type)
        {
            if (AtProtoRecordValueConverter.IsValid(literal, type))
                return literal ?? string.Empty;

            switch (type)
            {
                case AtProtoRecordValueType.Boolean:
                    return "false";
                case AtProtoRecordValueType.Integer:
                case AtProtoRecordValueType.Number:
                    return "0";
                case AtProtoRecordValueType.Json:
                    return "null";
                case AtProtoRecordValueType.JsonObject:
                    return "{}";
                case AtProtoRecordValueType.JsonArray:
                    return "[]";
                default:
                    return string.Empty;
            }
        }

        bool DrawGroupHeader(FieldGroup group, List<FieldGroup> groups)
        {
            var isRoot = string.IsNullOrEmpty(group.Path);
            var expanded = !expandedGroups.TryGetValue(group.Path, out var stored) || stored;
            if (!isRoot && renamingGroup == group.Path)
            {
                DrawRenameRow(group.Path);
                return expanded;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                expanded = EditorGUILayout.Foldout(
                    expanded,
                    new GUIContent(isRoot ? "Root" : group.Path.Substring(group.Path.LastIndexOf('.') + 1), group.Path),
                    true);
                if (!isRoot)
                {
                    DrawMoveButtons(group, groups);
                    if (GUILayout.Button("Rename", EditorStyles.miniButton, GUILayout.Width(56f)))
                    {
                        renamingGroup = group.Path;
                        renameBuffer = group.Path;
                        GUI.FocusControl(null);
                    }
                    if (GUILayout.Button("Move to\u2026", EditorStyles.miniButton, GUILayout.Width(68f)))
                        ShowGroupMenu(group.Path, groups, true);
                    if (GUILayout.Button("Merge to\u2026", EditorStyles.miniButton, GUILayout.Width(72f)))
                        ShowGroupMenu(group.Path, groups, false);
                    if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(48f))
                        && DeleteGroup(group.Path))
                    {
                        serializedObject.ApplyModifiedProperties();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            expandedGroups[group.Path] = expanded;
            return expanded;
        }

        void ShowGroupMenu(string sourcePath, List<FieldGroup> groups, bool asSubgroup)
        {
            var name = sourcePath.Substring(sourcePath.LastIndexOf('.') + 1);
            var menu = new GenericMenu();
            foreach (var target in groups)
            {
                if (target.Path == sourcePath || IsUnder(target.Path, sourcePath))
                    continue;

                var destination = !asSubgroup
                    ? target.Path
                    : target.Path.Length == 0 ? name : target.Path + "." + name;
                if (destination == sourcePath)
                    continue;

                menu.AddItem(
                    new GUIContent(target.Path.Length == 0 ? "Root" : target.Path),
                    false,
                    () =>
                    {
                        serializedObject.Update();
                        if (MoveGroup(sourcePath, destination))
                            serializedObject.ApplyModifiedProperties();
                        Repaint();
                    });
            }
            menu.ShowAsContext();
        }

        void DrawMoveButtons(FieldGroup group, List<FieldGroup> groups)
        {
            var parent = GetParentPath(group.Path);
            var siblings = groups.FindAll(
                item => item.Path.Length > 0 && GetParentPath(item.Path) == parent);
            var position = siblings.IndexOf(group);

            if (position > 0
                && GUILayout.Button("\u25B2", EditorStyles.miniButton, GUILayout.Width(22f)))
            {
                MoveGroupBlock(group.Path, siblings[position - 1].Path, false);
                serializedObject.ApplyModifiedProperties();
                GUIUtility.ExitGUI();
            }
            if (position < siblings.Count - 1
                && GUILayout.Button("\u25BC", EditorStyles.miniButton, GUILayout.Width(22f)))
            {
                MoveGroupBlock(group.Path, siblings[position + 1].Path, true);
                serializedObject.ApplyModifiedProperties();
                GUIUtility.ExitGUI();
            }
        }

        // Moves the group's fields, including nested groups, next to the target group's block.
        void MoveGroupBlock(string sourcePath, string targetPath, bool after)
        {
            var paths = new List<string>();
            for (var index = 0; index < fields.arraySize; index++)
                paths.Add(GetFieldPath(index));

            var moving = new List<int>();
            var remaining = new List<int>();
            for (var index = 0; index < paths.Count; index++)
            {
                if (IsUnder(paths[index], sourcePath))
                    moving.Add(index);
                else
                    remaining.Add(index);
            }

            var order = new List<int>(remaining);
            order.InsertRange(
                FindBlockInsert(remaining.Select(index => paths[index]).ToList(), targetPath, after, false),
                moving);

            var names = ReadGroupList();
            var movingNames = names.FindAll(name => name == sourcePath || IsUnder(name, sourcePath));
            names.RemoveAll(movingNames.Contains);
            names.InsertRange(FindBlockInsert(names, targetPath, after, true), movingNames);
            WriteGroupList(names);

            var layout = new List<int>();
            for (var index = 0; index < fields.arraySize; index++)
                layout.Add(index);
            for (var position = 0; position < order.Count; position++)
            {
                var from = layout.IndexOf(order[position]);
                if (from == position)
                    continue;

                fields.MoveArrayElement(from, position);
                layout.RemoveAt(from);
                layout.Insert(position, order[position]);
            }
        }

        static int FindBlockInsert(List<string> paths, string targetPath, bool after, bool includeTarget)
        {
            var insertAt = paths.Count;
            for (var position = 0; position < paths.Count; position++)
            {
                if (!(includeTarget && paths[position] == targetPath) && !IsUnder(paths[position], targetPath))
                    continue;

                if (!after)
                    return position;
                insertAt = position + 1;
            }
            return insertAt;
        }

        static bool IsUnder(string path, string groupPath)
        {
            return path.StartsWith(groupPath + ".", System.StringComparison.Ordinal);
        }

        void DrawRenameRow(string groupPath)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                renameBuffer = EditorGUILayout.TextField(renameBuffer);
                using (new EditorGUI.DisabledScope(!IsValidGroupPath(renameBuffer)))
                {
                    if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(48f)))
                    {
                        var target = renameBuffer.Trim();
                        renamingGroup = null;
                        if (target != groupPath && MoveGroup(groupPath, target))
                        {
                            serializedObject.ApplyModifiedProperties();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(52f)))
                    renamingGroup = null;
            }
        }

        static bool IsValidGroupPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            foreach (var segment in path.Trim().Split('.'))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    return false;
            }
            return true;
        }

        bool DeleteGroup(string groupPath)
        {
            var doomed = new List<int>();
            for (var index = 0; index < fields.arraySize; index++)
            {
                if (IsUnder(GetFieldPath(index), groupPath))
                    doomed.Add(index);
            }

            if (!EditorUtility.DisplayDialog(
                "Delete group",
                "Delete '" + groupPath + "' and all " + doomed.Count
                + " field(s) inside it, including nested groups?",
                "Delete",
                "Cancel"))
                return false;

            for (var index = doomed.Count - 1; index >= 0; index--)
                fields.DeleteArrayElementAtIndex(doomed[index]);
            RemapGroups(groupPath, string.Empty);
            return true;
        }

        // An empty target moves the group's fields to the root.
        bool MoveGroup(string groupPath, string targetPath)
        {
            var oldPrefix = groupPath + ".";
            var newPrefix = targetPath.Length == 0 ? string.Empty : targetPath + ".";
            var moved = new Dictionary<int, string>();
            var taken = new HashSet<string>();
            for (var index = 0; index < fields.arraySize; index++)
            {
                var path = GetFieldPath(index);
                if (IsUnder(path, groupPath))
                    moved[index] = newPrefix + path.Substring(oldPrefix.Length);
                else
                    taken.Add(path);
            }

            foreach (var entry in moved)
            {
                if (entry.Value.Length > 0
                    && !entry.Value.EndsWith(".", System.StringComparison.Ordinal)
                    && taken.Contains(entry.Value))
                {
                    EditorUtility.DisplayDialog(
                        "Field already exists",
                        "'" + entry.Value + "' already exists. Rename or delete it first.",
                        "OK");
                    return false;
                }
            }

            foreach (var entry in moved)
            {
                fields.GetArrayElementAtIndex(entry.Key)
                    .FindPropertyRelative("path")
                    .stringValue = entry.Value;
            }
            expandedGroups[targetPath] = true;
            RemapGroups(groupPath, targetPath);
            return true;
        }

        string GetFieldPath(int index)
        {
            return fields.GetArrayElementAtIndex(index).FindPropertyRelative("path").stringValue ?? string.Empty;
        }

        List<FieldGroup> BuildGroups()
        {
            var byPath = new Dictionary<string, FieldGroup>();
            var all = new List<FieldGroup>();
            FieldGroup Ensure(string path)
            {
                if (byPath.TryGetValue(path, out var existing))
                    return existing;

                if (path.Length > 0)
                    Ensure(GetParentPath(path));
                var created = new FieldGroup(path);
                byPath[path] = created;
                all.Add(created);
                return created;
            }

            Ensure(string.Empty);
            foreach (var name in ReadGroupList())
            {
                if (IsValidGroupPath(name))
                    Ensure(name);
            }
            for (var index = 0; index < fields.arraySize; index++)
                Ensure(GetParentPath(GetFieldPath(index))).Indices.Add(index);

            var ordered = new List<FieldGroup>();
            void Visit(FieldGroup group)
            {
                ordered.Add(group);
                foreach (var child in all)
                {
                    if (child.Path.Length > 0 && GetParentPath(child.Path) == group.Path)
                        Visit(child);
                }
            }

            Visit(byPath[string.Empty]);
            WriteGroupList(ordered.Skip(1).Select(group => group.Path).ToList());
            return ordered;
        }

        static int GetDepth(string path)
        {
            var depth = 0;
            foreach (var character in path)
            {
                if (character == '.')
                    depth++;
            }
            return path.Length == 0 ? 0 : depth;
        }

        bool IsHidden(string path)
        {
            for (var parent = GetParentPath(path); parent.Length > 0; parent = GetParentPath(parent))
            {
                if (expandedGroups.TryGetValue(parent, out var expanded) && !expanded)
                    return true;
            }
            return false;
        }

        static string GetParentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var separator = path.LastIndexOf('.');
            return separator < 0 ? string.Empty : path.Substring(0, separator);
        }

        sealed class FieldGroup
        {
            public readonly string Path;
            public readonly List<int> Indices = new();

            public FieldGroup(string path)
            {
                Path = path;
            }
        }

        void DrawOutputJson(AtProtoRecordComposer composer)
        {
            try
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    AtProtoJsonEditorUtility.DrawTextArea(
                        composer.BuildJson(Formatting.Indented),
                        ref outputJsonHeight);
                }
            }
            catch (System.Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Warning);
            }
        }
    }
}