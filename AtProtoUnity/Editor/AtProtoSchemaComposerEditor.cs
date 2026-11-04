using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    [CustomEditor(typeof(AtProtoSchemaComposer), true)]
    sealed class AtProtoSchemaComposerEditor : UnityEditor.Editor
    {
        const double ResolveDelay = 0.6;
        static readonly string[] FreeTypes = { "String", "Boolean", "Integer", "Object", "Array", "Null" };
        readonly Dictionary<string, bool> expanded = new();
        readonly Dictionary<string, string> newNames = new();
        SerializedProperty nsid;
        SerializedProperty identityEndpoint;
        SerializedProperty plcDirectoryEndpoint;
        SerializedProperty dnsOverHttpsEndpoint;
        IDisposable operation;
        IReadOnlyList<string> suggestions;
        string observedRequest;
        string attemptedRequest;
        string message;
        string status;
        double resolveAt;
        int generation;
        bool requestSearch;
        bool forceResolve;
        bool showDetails;
        bool showOutput;
        float outputHeight = 180f;
        MessageType messageType;

        AtProtoSchemaComposer Composer => (AtProtoSchemaComposer)target;

        void OnEnable()
        {
            nsid = serializedObject.FindProperty("nsid");
            identityEndpoint = serializedObject.FindProperty("identityEndpoint");
            plcDirectoryEndpoint = serializedObject.FindProperty("plcDirectoryEndpoint");
            dnsOverHttpsEndpoint = serializedObject.FindProperty("dnsOverHttpsEndpoint");
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            CancelOperation();
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                CancelOperation();
                attemptedRequest = null;
            }
        }

        void CancelOperation()
        {
            generation++;
            operation?.Dispose();
            operation = null;
            status = null;
        }

        string GetRequestKey()
        {
            return Composer.Nsid?.Trim() + "\n" + Composer.IdentityEndpoint
                + "\n" + Composer.PlcDirectoryEndpoint + "\n" + Composer.DnsOverHttpsEndpoint;
        }

        void Tick()
        {
            if (target == null || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            var request = GetRequestKey();
            if (observedRequest != request)
            {
                CancelOperation();
                observedRequest = request;
                attemptedRequest = null;
                suggestions = null;
                message = null;
                requestSearch = false;
                forceResolve = false;
                resolveAt = EditorApplication.timeSinceStartup + ResolveDelay;
                Repaint();
            }
            if (operation != null || EditorApplication.timeSinceStartup < resolveAt)
                return;
            if (requestSearch)
            {
                requestSearch = false;
                attemptedRequest = request;
                StartRequest(true);
            }
            else if (IsSearchableNamespace(Composer.Nsid?.Trim()) && attemptedRequest != request)
            {
                attemptedRequest = request;
                StartRequest(true);
            }
            else if (AtProtoLexiconClient.IsValidNsid(Composer.Nsid?.Trim())
                && (forceResolve || !Composer.HasSchema && attemptedRequest != request))
            {
                forceResolve = false;
                attemptedRequest = request;
                StartRequest(false);
            }
        }

        void StartRequest(bool search)
        {
            var composer = Composer;
            var requestedNsid = composer.Nsid.Trim();
            var requestGeneration = generation;
            message = null;
            status = search ? "Searching the published namespace..." : "Loading the published record schema...";
            operation = AtProtoReaderEditor.StartOperation(
                composer,
                token => RunRequestAsync(composer, requestedNsid, requestGeneration, search, token),
                () =>
                {
                    operation = null;
                    status = null;
                    Repaint();
                },
                status);
        }

        async Awaitable RunRequestAsync(
            AtProtoSchemaComposer composer,
            string requestedNsid,
            int requestGeneration,
            bool search,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var client = composer.CreateLexiconClient();
                    if (search)
                    {
                        var result = await client.SearchAsync(requestedNsid, timeout.Token);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrent(composer, requestedNsid, requestGeneration))
                            return;
                        if (result.Succeeded)
                        {
                            suggestions = result.Value;
                            if (suggestions.Count == 0)
                                SetMessage("No matching record schemas are published in this namespace.", MessageType.Info);
                        }
                        else
                            SetMessage(result.Error.Message, MessageType.Error);
                    }
                    else
                    {
                        var result = await client.ResolveAsync(requestedNsid, timeout.Token);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrent(composer, requestedNsid, requestGeneration))
                            return;
                        if (result.Succeeded)
                        {
                            Undo.RecordObject(composer, "Load AT Protocol schema");
                            composer.LoadSchema(result.Value);
                            MarkChanged(composer);
                            serializedObject.Update();
                        }
                        else
                            SetMessage(result.Error.Message, MessageType.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (IsCurrent(composer, requestedNsid, requestGeneration))
                    SetMessage("Schema lookup timed out. Retry or check the network settings.", MessageType.Error);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                if (IsCurrent(composer, requestedNsid, requestGeneration))
                    SetMessage(exception.Message, MessageType.Error);
            }
        }

        bool IsCurrent(AtProtoSchemaComposer composer, string requestedNsid, int requestGeneration)
        {
            return this != null && target != null && generation == requestGeneration
                && string.Equals(composer.Nsid?.Trim(), requestedNsid, StringComparison.Ordinal);
        }

        void SetMessage(string text, MessageType type)
        {
            message = text;
            messageType = type;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(nsid, new GUIContent("NSID", "A record NSID, or a namespace prefix such as app.bsky or app.bsky.feed."));
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(nsid.stringValue) || operation != null))
                {
                    if (GUILayout.Button("Search", EditorStyles.miniButton, GUILayout.Width(48f)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        QueueRequest(true);
                    }
                }
                using (new EditorGUI.DisabledScope(!AtProtoLexiconClient.IsValidNsid(nsid.stringValue?.Trim()) || operation != null))
                {
                    if (IconButton("Refresh", "Reload the schema from AT Protocol"))
                    {
                        serializedObject.ApplyModifiedProperties();
                        QueueRequest(false);
                    }
                }
            }
            serializedObject.ApplyModifiedProperties();

            if (status != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(55f)))
                    {
                        CancelOperation();
                        attemptedRequest = GetRequestKey();
                    }
                }
            }
            if (!string.IsNullOrEmpty(message))
                EditorGUILayout.HelpBox(message, messageType);
            if (suggestions != null)
            {
                foreach (var suggestion in suggestions)
                {
                    if (GUILayout.Button(suggestion, EditorStyles.linkLabel))
                    {
                        nsid.stringValue = suggestion;
                        serializedObject.ApplyModifiedProperties();
                        suggestions = null;
                        break;
                    }
                }
            }

            if (Composer.HasSchema)
            {
                try
                {
                    DrawRecord();
                    DrawWriterAction();
                }
                catch (Exception exception) when (!(exception is ExitGUIException))
                {
                    EditorGUILayout.HelpBox(exception.Message, MessageType.Error);
                }
            }
            DrawDetails();
        }

        void QueueRequest(bool search)
        {
            CancelOperation();
            observedRequest = GetRequestKey();
            attemptedRequest = null;
            requestSearch = search;
            forceResolve = !search;
            resolveAt = EditorApplication.timeSinceStartup;
            message = null;
        }

        static bool IsSearchableNamespace(string value)
        {
            return AtProtoLexiconClient.IsNamespaceSearchPrefix(value);
        }

        void DrawRecord()
        {
            var composer = Composer;
            var draft = composer.GetRecordValue();
            var previousJson = draft.ToString(Formatting.None);
            EditorGUILayout.Space(4f);
            DrawObject(draft, composer.Document.RecordDefinition, composer.Nsid.Trim(), string.Empty, 0);
            if (draft.ToString(Formatting.None) != previousJson)
            {
                Undo.RecordObject(composer, "Edit AT Protocol record");
                composer.SetRecordValue(draft);
                MarkChanged(composer);
            }
            var errors = composer.Document.ValidateRecord(draft);
            if (errors.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n", errors.Take(5)), MessageType.Warning);
            showOutput = EditorGUILayout.Foldout(showOutput, "Output JSON", true);
            if (showOutput)
            {
                using (new EditorGUI.DisabledScope(true))
                    AtProtoJsonEditorUtility.DrawTextArea(draft.ToString(Formatting.Indented), ref outputHeight);
            }
        }

        void DrawObject(JObject value, JObject schema, string ownerNsid, string path, int depth)
        {
            var required = (schema["required"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
            var nullable = (schema["nullable"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
            var properties = ((JObject)schema["properties"]).Properties()
                .OrderByDescending(property => required.Contains(property.Name)).ToArray();
            foreach (var property in properties)
            {
                var name = property.Name;
                var isRequired = required.Contains(name);
                var propertySchema = (JObject)property.Value;
                var included = value.Property(name) != null;
                if (!isRequired && !included)
                    continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(name + (isRequired ? " *" : string.Empty), propertySchema.Value<string>("description")),
                        EditorStyles.boldLabel);
                    if (!isRequired && IconButton("Toolbar Minus", "Remove optional field"))
                    {
                        value.Remove(name);
                        continue;
                    }
                    if (included && nullable.Contains(name))
                    {
                        var isNull = value[name]?.Type == JTokenType.Null;
                        var nextNull = EditorGUILayout.ToggleLeft("Null", isNull, GUILayout.Width(50f));
                        if (isNull != nextNull)
                            value[name] = nextNull ? JValue.CreateNull() : Composer.Document.CreateValue(propertySchema, ownerNsid);
                    }
                }
                if (!included || value[name]?.Type == JTokenType.Null)
                    continue;
                var fieldPath = path.Length == 0 ? name : path + "." + name;
                using (new EditorGUI.IndentLevelScope())
                    value[name] = DrawValue(value[name], propertySchema, ownerNsid, fieldPath, depth + 1);
            }

            var available = properties.Where(property => !required.Contains(property.Name)
                && value.Property(property.Name) == null).ToArray();
            if (available.Length > 0)
            {
                var options = new string[available.Length + 1];
                options[0] = "+ Add optional field...";
                for (var index = 0; index < available.Length; index++)
                    options[index + 1] = available[index].Name;
                using (new EditorGUI.IndentLevelScope())
                {
                    var selected = EditorGUILayout.Popup("", 0, options);
                    if (selected > 0)
                    {
                        var property = available[selected - 1];
                        value[property.Name] = Composer.Document.CreateValue((JObject)property.Value, ownerNsid);
                    }
                }
            }
        }

        JToken DrawValue(JToken value, JObject schema, string ownerNsid, string path, int depth)
        {
            if (depth > AtProtoLexiconDocument.MaximumDepth)
            {
                EditorGUILayout.HelpBox("Maximum field nesting reached.", MessageType.Warning);
                return value;
            }
            schema = Composer.Document.ResolveSchema(schema, ownerNsid, out ownerNsid);
            var type = schema.Value<string>("type");
            using (new EditorGUI.DisabledScope(schema["const"] != null))
            {
                if (schema["enum"] is JArray choices && choices.Count > 0)
                {
                    var labels = choices.Select(choice => choice.ToString()).ToArray();
                    var selected = choices.Select((choice, index) => JToken.DeepEquals(choice, value) ? index : -1).Max();
                    var next = EditorGUILayout.Popup(selected, labels);
                    return next >= 0 ? choices[next].DeepClone() : value;
                }
                switch (type)
                {
                    case "string":
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var text = EditorGUILayout.TextField(value.Value<string>() ?? string.Empty);
                            if (schema.Value<string>("format") == "datetime" && IconButton("Refresh", "Set the current UTC time"))
                                text = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                            if (schema["knownValues"] is JArray knownValues)
                            {
                                var pick = EditorGUILayout.Popup(-1, knownValues.Values<string>().ToArray(), GUILayout.Width(24f));
                                if (pick >= 0)
                                    text = knownValues[pick].Value<string>();
                            }
                            return new JValue(text);
                        }
                    case "boolean":
                        return new JValue(EditorGUILayout.Toggle(value.Type == JTokenType.Boolean && value.Value<bool>()));
                    case "integer":
                        return new JValue(EditorGUILayout.LongField(value.Type == JTokenType.Integer ? value.Value<long>() : 0));
                    case "record":
                        DrawObject((JObject)value, (JObject)schema["record"], ownerNsid, path, depth);
                        return value;
                    case "object":
                        if (Foldout(path, "Fields"))
                            DrawObject((JObject)value, schema, ownerNsid, path, depth);
                        return value;
                    case "array":
                        DrawArray((JArray)value, schema, ownerNsid, path, depth);
                        return value;
                    case "union":
                        return DrawUnion((JObject)value, schema, ownerNsid, path, depth);
                    case "blob":
                        var blob = (JObject)value;
                        var reference = blob["ref"] as JObject ?? new JObject();
                        reference["$link"] = EditorGUILayout.TextField("Blob CID", reference.Value<string>("$link") ?? string.Empty);
                        blob["ref"] = reference;
                        blob["$type"] = "blob";
                        blob["mimeType"] = EditorGUILayout.TextField("MIME type", blob.Value<string>("mimeType") ?? string.Empty);
                        blob["size"] = EditorGUILayout.LongField("Size (bytes)", blob.Value<long?>("size") ?? 0);
                        return blob;
                    case "cid-link":
                        ((JObject)value)["$link"] = EditorGUILayout.TextField("CID", value.Value<string>("$link") ?? string.Empty);
                        return value;
                    case "bytes":
                        ((JObject)value)["$bytes"] = EditorGUILayout.TextField("Base64", value.Value<string>("$bytes") ?? string.Empty);
                        return value;
                    case "unknown":
                        DrawFreeObject((JObject)value, path, depth);
                        return value;
                    default:
                        EditorGUILayout.HelpBox("Unsupported field type: " + type, MessageType.Warning);
                        return value;
                }
            }
        }

        void DrawArray(JArray value, JObject schema, string ownerNsid, string path, int depth)
        {
            if (!Foldout(path, "Items (" + value.Count + ")"))
                return;
            for (var index = 0; index < value.Count; index++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Element " + index, EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(index == 0))
                    {
                        if (GUILayout.Button("Up", EditorStyles.miniButton, GUILayout.Width(30f)))
                        {
                            var item = value[index];
                            value.RemoveAt(index);
                            value.Insert(index - 1, item);
                            break;
                        }
                    }
                    using (new EditorGUI.DisabledScope(value.Count <= (schema.Value<int?>("minLength") ?? 0)))
                    {
                        if (IconButton("Toolbar Minus", "Remove array item"))
                        {
                            value.RemoveAt(index);
                            break;
                        }
                    }
                }
                using (new EditorGUI.IndentLevelScope())
                    value[index] = DrawValue(value[index], (JObject)schema["items"], ownerNsid, path + "[" + index + "]", depth + 1);
            }
            using (new EditorGUI.DisabledScope(value.Count >= (schema.Value<int?>("maxLength") ?? int.MaxValue)))
            {
                if (GUILayout.Button("Add item", EditorStyles.miniButton))
                    value.Add(Composer.Document.CreateValue((JObject)schema["items"], ownerNsid));
            }
        }

        JToken DrawUnion(JObject value, JObject schema, string ownerNsid, string path, int depth)
        {
            var references = (schema["refs"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
            var types = references.Select(reference => AtProtoLexiconDocument.GetTypeReference(reference, ownerNsid)).ToArray();
            var selected = Array.IndexOf(types, value.Value<string>("$type"));
            var next = EditorGUILayout.Popup("Variant", selected, types);
            if (next >= 0 && next != selected
                && (value.Count <= 1 || EditorUtility.DisplayDialog("Change Variant", "Replace the current variant's values?", "Replace", "Cancel")))
            {
                var replacement = (JObject)Composer.Document.CreateValue(
                    new JObject { ["type"] = "ref", ["ref"] = references[next] }, ownerNsid);
                replacement["$type"] = types[next];
                value = replacement;
                selected = next;
            }
            if (selected >= 0)
            {
                var variantSchema = Composer.Document.ResolveSchema(
                    new JObject { ["type"] = "ref", ["ref"] = references[selected] }, ownerNsid, out var variantOwner);
                DrawValue(value, variantSchema, variantOwner, path, depth + 1);
            }
            else
                DrawFreeObject(value, path, depth);
            return value;
        }

        void DrawFreeObject(JObject value, string path, int depth)
        {
            foreach (var property in value.Properties().ToArray())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(property.Name, EditorStyles.miniLabel);
                    if (IconButton("Toolbar Minus", "Remove field"))
                    {
                        value.Remove(property.Name);
                        continue;
                    }
                }
                value[property.Name] = DrawFreeValue(property.Value, path + "." + property.Name, depth + 1);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                newNames.TryGetValue(path, out var name);
                name = EditorGUILayout.TextField(name ?? string.Empty);
                newNames[path] = name;
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(name) || value.Property(name) != null))
                {
                    if (IconButton("Toolbar Plus", "Add field"))
                    {
                        value[name] = string.Empty;
                        newNames[path] = string.Empty;
                    }
                }
            }
        }

        JToken DrawFreeValue(JToken value, string path, int depth)
        {
            if (depth > AtProtoLexiconDocument.MaximumDepth)
                return value;
            var selected = value.Type == JTokenType.String ? 0 : value.Type == JTokenType.Boolean ? 1
                : value.Type == JTokenType.Integer ? 2 : value is JObject ? 3 : value is JArray ? 4 : 5;
            var next = EditorGUILayout.Popup("Type", selected, FreeTypes);
            if (next != selected)
                value = next == 0 ? new JValue(string.Empty) : next == 1 ? new JValue(false)
                    : next == 2 ? new JValue(0L) : next == 3 ? new JObject() : next == 4 ? new JArray() : JValue.CreateNull();
            if (value is JObject obj)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawFreeObject(obj, path, depth);
            }
            else if (value is JArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Element " + index, EditorStyles.miniLabel);
                        if (IconButton("Toolbar Minus", "Remove item"))
                        {
                            array.RemoveAt(index);
                            break;
                        }
                    }
                    array[index] = DrawFreeValue(array[index], path + "[" + index + "]", depth + 1);
                }
                if (GUILayout.Button("Add item", EditorStyles.miniButton))
                    array.Add(string.Empty);
            }
            else if (value.Type == JTokenType.String)
                value = new JValue(EditorGUILayout.TextField(value.Value<string>()));
            else if (value.Type == JTokenType.Boolean)
                value = new JValue(EditorGUILayout.Toggle(value.Value<bool>()));
            else if (value.Type == JTokenType.Integer)
                value = new JValue(EditorGUILayout.LongField(value.Value<long>()));
            return value;
        }

        bool Foldout(string path, string label)
        {
            var isExpanded = !expanded.TryGetValue(path, out var previous) || previous;
            expanded[path] = EditorGUILayout.Foldout(isExpanded, label, true);
            return expanded[path];
        }

        void DrawDetails()
        {
            showDetails = EditorGUILayout.Foldout(showDetails, "Schema and Network", true);
            if (!showDetails)
                return;
            if (Composer.HasSchema)
            {
                EditorGUILayout.LabelField("Record key", Composer.SchemaRecordKey);
                EditorGUILayout.SelectableLabel("DID: " + Composer.SchemaSourceDid, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(32f));
                EditorGUILayout.SelectableLabel("CID: " + Composer.SchemaRecordCid, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(32f));
            }
            serializedObject.Update();
            EditorGUILayout.PropertyField(identityEndpoint);
            EditorGUILayout.PropertyField(plcDirectoryEndpoint);
            EditorGUILayout.PropertyField(dnsOverHttpsEndpoint, new GUIContent("DNS-over-HTTPS"));
            serializedObject.ApplyModifiedProperties();
        }

        void DrawWriterAction()
        {
            var writer = Composer.GetComponent<AtProtoWriter>();
            if (writer != null && writer.Composer == Composer)
            {
                EditorGUILayout.LabelField("Writer connected", EditorStyles.miniLabel);
                return;
            }
            if (GUILayout.Button(writer == null ? "Add Writer" : "Use in Writer"))
                ConnectWriter(Composer);
        }

        internal static AtProtoWriter ConnectWriter(AtProtoSchemaComposer composer)
        {
            var writer = composer.GetComponent<AtProtoWriter>() ?? Undo.AddComponent<AtProtoWriter>(composer.gameObject);
            Undo.RecordObject(writer, "Connect record composer");
            writer.Composer = composer;
            writer.JsonFile = null;
            writer.CollectionNsid = composer.Nsid.Trim();
            if (writer.Login == null)
                writer.Login = composer.GetComponent<AtProtoLogin>();
            if (composer.HasSchema && composer.SchemaRecordKey.StartsWith("literal:", StringComparison.Ordinal)
                && string.IsNullOrEmpty(writer.RecordKey))
                writer.RecordKey = composer.SchemaRecordKey.Substring("literal:".Length);
            MarkChanged(writer);
            return writer;
        }

        internal static void MarkChanged(UnityEngine.Object targetObject)
        {
            EditorUtility.SetDirty(targetObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(targetObject);
            if (targetObject is Component component && component.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }

        static bool IconButton(string icon, string tooltip)
        {
            var content = new GUIContent(EditorGUIUtility.IconContent(icon)) { tooltip = tooltip };
            return GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(24f));
        }

        [MenuItem("GameObject/AT Protocol/Record Composer", false, 10)]
        static void CreateComposer(MenuCommand command)
        {
            var gameObject = new GameObject("Record Composer");
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create record composer");
            Undo.AddComponent<AtProtoSchemaComposer>(gameObject);
            Selection.activeGameObject = gameObject;
        }
    }
}