using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    [CustomEditor(typeof(AtProtoWriter))]
    sealed class AtProtoWriterEditor : UnityEditor.Editor
    {
        SerializedProperty login;
        SerializedProperty writeMode;
        SerializedProperty writeOnLogin;
        SerializedProperty requirePdsSchemaValidation;
        SerializedProperty composer;
        SerializedProperty jsonFile;
        SerializedProperty collectionNsid;
        SerializedProperty recordKey;
        SerializedProperty expectedRecordCid;
        SerializedProperty rawJson;
        bool showInlineJson = true;
        bool showResolvedJson;
        float inlineJsonHeight = 190f;
        float resolvedJsonHeight = 190f;
        string resultMessage;
        MessageType resultType;

        void OnEnable()
        {
            login = serializedObject.FindProperty("login");
            writeMode = serializedObject.FindProperty("writeMode");
            writeOnLogin = serializedObject.FindProperty("writeOnLogin");
            requirePdsSchemaValidation = serializedObject.FindProperty("requirePdsSchemaValidation");
            composer = serializedObject.FindProperty("composer");
            jsonFile = serializedObject.FindProperty("jsonFile");
            collectionNsid = serializedObject.FindProperty("collectionNsid");
            recordKey = serializedObject.FindProperty("recordKey");
            expectedRecordCid = serializedObject.FindProperty("expectedRecordCid");
            rawJson = serializedObject.FindProperty("rawJson");
        }

        internal static void OpenFromReader(
            AtProtoReader reader,
            string collectionNsid,
            AtProtoRecord record)
        {
            if (reader == null)
                return;

            var writer = reader.GetComponent<AtProtoWriter>();
            if (writer == null)
            {
                writer = Undo.AddComponent<AtProtoWriter>(reader.gameObject);
                if (writer == null)
                    return;
            }

            Undo.RecordObject(
                writer,
                record == null
                    ? "Prepare AT Protocol record creation"
                    : "Prepare AT Protocol record update");
            writer.Login = reader.Login;
            writer.Composer = null;
            writer.JsonFile = null;
            if (record == null)
                writer.PrepareCreate(collectionNsid);
            else
                writer.LoadRecord(record);
            EditorUtility.SetDirty(writer);

            Selection.activeObject = writer;
            EditorGUIUtility.PingObject(writer);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawConnectionSection();
            DrawTargetSection();
            DrawRecordInputSection();

            serializedObject.ApplyModifiedProperties();

            var writer = (AtProtoWriter)target;
            DrawResolvedJsonSection(writer);
            DrawStatusAndActions(writer);
            DrawLastWrittenRecord(writer);
        }

        void DrawConnectionSection()
        {
            EditorGUILayout.PropertyField(login, new GUIContent("Login"));
            EditorGUILayout.PropertyField(writeMode, new GUIContent("Mode"));
            EditorGUILayout.PropertyField(
                writeOnLogin,
                new GUIContent("Write on login"));
        }

        void DrawTargetSection()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            var schema = composer.objectReferenceValue as AtProtoSchemaComposer;
            if (schema != null)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField("Collection", schema.Nsid);
                EditorGUILayout.PropertyField(
                    requirePdsSchemaValidation,
                    new GUIContent(
                        "Require PDS validation",
                        "Sends validate: true and fails if the PDS does not recognize this lexicon. The schema composer still validates locally when this is off."));
            }
            else
                EditorGUILayout.PropertyField(collectionNsid, new GUIContent("Collection"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(recordKey, new GUIContent("Key"));
                if (EditorGUILayout.DropdownButton(
                    new GUIContent("Key options"),
                    FocusType.Passive,
                    GUILayout.Width(100f)))
                    ShowKeyOptions((AtProtoWriter)target, composer.objectReferenceValue as AtProtoSchemaComposer);
            }
            EditorGUILayout.PropertyField(
                expectedRecordCid,
                new GUIContent(
                    "Expected CID",
                    "A CID identifies record content for update concurrency checks. It is not a record key."));
            if (schema != null && schema.HasSchema)
                EditorGUILayout.HelpBox(
                    "Generate stable TID stores a key for repeat Update/Upsert. Server-generated TID switches Mode to Create. Self and NSID are only available when allowed by this schema. CID is a revision identifier, not a key.",
                    MessageType.Info);
        }

        static void ShowKeyOptions(AtProtoWriter writer, AtProtoSchemaComposer schema)
        {
            var hasSchema = schema != null && schema.HasSchema;
            var schemaKey = hasSchema ? schema.SchemaRecordKey : null;
            var supportsTid = schemaKey == "tid" || schemaKey == "any";
            var supportsSelf = schemaKey == "any" || schemaKey == "literal:self";
            var supportsNsid = schemaKey == "any" || schemaKey == "nsid";
            var menu = new GenericMenu();

            if (supportsTid)
            {
                menu.AddItem(
                    new GUIContent("TID/Server-generated on Create"),
                    false,
                    () => ApplyKeyPreset(writer, AtProtoWriteMode.Create, string.Empty, "Use server-generated TID"));
                menu.AddItem(
                    new GUIContent("TID/Generate stable key"),
                    false,
                    () => ApplyKeyPreset(writer, writer.WriteMode, AtProtoLexiconDocument.GenerateTid(), "Generate TID key"));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("TID/Requires a tid or any schema"));
                menu.AddDisabledItem(new GUIContent("TID/Generate stable key (requires tid or any schema)"));
            }

            if (supportsSelf)
                menu.AddItem(new GUIContent("Self/Use self"), false, () => ApplyKeyPreset(writer, writer.WriteMode, "self", "Use self record key"));
            else
                menu.AddDisabledItem(new GUIContent("Self/Not valid for this schema key type"));

            if (supportsNsid)
                menu.AddItem(new GUIContent("NSID/Use collection NSID"), false, () => ApplyKeyPreset(writer, writer.WriteMode, schema.Nsid, "Use collection NSID as record key"));
            else
                menu.AddDisabledItem(new GUIContent("NSID/Not valid for this schema key type"));

            if (hasSchema && schemaKey.StartsWith("literal:", StringComparison.Ordinal))
            {
                var literal = schemaKey.Substring("literal:".Length);
                menu.AddItem(new GUIContent("Schema/Use literal: " + literal), false, () => ApplyKeyPreset(writer, writer.WriteMode, literal, "Use schema record key"));
            }
            else
                menu.AddDisabledItem(new GUIContent("Schema/No literal key"));

            menu.AddDisabledItem(new GUIContent("CID/Revision identifier, not a record key"));
            menu.ShowAsContext();
        }

        static void ApplyKeyPreset(
            AtProtoWriter writer,
            AtProtoWriteMode mode,
            string key,
            string undoName)
        {
            Undo.RecordObject(writer, undoName);
            writer.WriteMode = mode;
            writer.RecordKey = key;
            EditorUtility.SetDirty(writer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(writer);
        }

        void DrawRecordInputSection()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Record data", EditorStyles.boldLabel);
            DrawExclusiveField(composer, "Composer", jsonFile);
            DrawExclusiveField(jsonFile, "JSON file", composer);
            EditorGUILayout.LabelField("Writing: " + GetSourceLabel(), EditorStyles.miniLabel);

            showInlineJson = EditorGUILayout.Foldout(
                showInlineJson,
                "Inline JSON",
                true);
            if (!showInlineJson)
                return;

            using (new EditorGUI.DisabledScope(
                composer.objectReferenceValue != null || jsonFile.objectReferenceValue != null))
            {
                rawJson.stringValue = AtProtoJsonEditorUtility.DrawTextArea(
                    rawJson.stringValue,
                    ref inlineJsonHeight);
            }
        }

        static void DrawExclusiveField(
            SerializedProperty property,
            string label,
            SerializedProperty other)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(other.objectReferenceValue != null))
                    EditorGUILayout.PropertyField(property, new GUIContent(label));

                using (new EditorGUI.DisabledScope(property.objectReferenceValue == null))
                {
                    if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22f)))
                        property.objectReferenceValue = null;
                }
            }
        }

        void DrawResolvedJsonSection(AtProtoWriter writer)
        {
            EditorGUILayout.Space(6f);
            showResolvedJson = EditorGUILayout.Foldout(
                showResolvedJson,
                "Output JSON",
                true);
            if (!showResolvedJson)
                return;

            try
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    AtProtoJsonEditorUtility.DrawTextArea(
                        writer.BuildRecordJson(),
                        ref resolvedJsonHeight);
                }
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Warning);
            }
        }

        string GetSourceLabel()
        {
            if (composer.objectReferenceValue != null)
                return "Composer";
            if (jsonFile.objectReferenceValue != null)
                return "JSON file";
            return "Inline JSON";
        }

        void DrawStatusAndActions(AtProtoWriter writer)
        {
            EditorGUILayout.Space(6f);
            var validation = writer.GetValidationMessage(writer.WriteMode);
            var status = AtProtoReaderEditor.GetOperationStatus(writer);
            if (status != null)
                EditorGUILayout.HelpBox(status, MessageType.Info);
            else if (validation != null && !IsLoginValidation(validation))
                EditorGUILayout.HelpBox(validation, MessageType.Warning);
            else
                EditorGUILayout.LabelField(GetAuthSummary(writer), EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(resultMessage))
                EditorGUILayout.HelpBox(resultMessage, resultType);

            using (new EditorGUI.DisabledScope(status != null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawWriteButton(writer, AtProtoWriteMode.Create, "Create");
                    DrawWriteButton(writer, AtProtoWriteMode.Update, "Update");
                    DrawWriteButton(writer, AtProtoWriteMode.Upsert, "Upsert");
                }
            }
        }

        void DrawWriteButton(AtProtoWriter writer, AtProtoWriteMode mode, string label)
        {
            using (new EditorGUI.DisabledScope(writer.GetValidationMessage(mode) != null))
            {
                if (GUILayout.Button(label))
                    StartWrite(writer, mode);
            }
        }

        void DrawLastWrittenRecord(AtProtoWriter writer)
        {
            if (writer.LastWrittenRecord == null)
                return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Last write", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(writer.LastWrittenRecord.Uri);
            EditorGUILayout.SelectableLabel("CID: " + writer.LastWrittenRecord.Cid);

            var validation = writer.GetDeleteValidationMessage();
            var status = AtProtoReaderEditor.GetOperationStatus(writer);
            using (new EditorGUI.DisabledScope(status != null || validation != null))
            {
                if (GUILayout.Button(new GUIContent("Delete record", validation)))
                    StartDelete(writer);
            }
        }

        static string GetAuthSummary(AtProtoWriter writer)
        {
            if (writer.Login == null)
                return "Assign an AtProtoLogin component to enable writing.";
            if (writer.Login.Session == null)
                return "The assigned login is signed out.";
            if (writer.Login.Session.LoginMethod == AtProtoLoginMethod.Password)
                return "Password session: record writes are unrestricted by OAuth scopes.";
            return "OAuth session: writes are checked against the granted session scope.";
        }

        static bool IsLoginValidation(string validation)
        {
            return validation == AtProtoWriter.LoginMissingMessage
                || validation == AtProtoWriter.LoginRequiredMessage;
        }

        void StartWrite(AtProtoWriter writer, AtProtoWriteMode mode)
        {
            resultMessage = null;
            AtProtoReaderEditor.StartOperation(
                writer,
                cancellationToken => RunWriteAsync(writer, mode, cancellationToken),
                Repaint,
                GetProgressMessage(mode));
        }

        void StartDelete(AtProtoWriter writer)
        {
            var record = writer.LastWrittenRecord;
            if (record == null
                || !EditorUtility.DisplayDialog(
                    "Delete AT Protocol record?",
                    "Permanently delete " + record.Uri + "? The deletion will only succeed if its CID is still " + record.Cid + ".",
                    "Delete record",
                    "Cancel"))
                return;

            resultMessage = null;
            AtProtoReaderEditor.StartOperation(
                writer,
                cancellationToken => RunDeleteAsync(writer, cancellationToken),
                Repaint,
                "Deleting record...");
        }

        async Awaitable RunDeleteAsync(
            AtProtoWriter writer,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await writer.DeleteLastWrittenRecordAsync(cancellationToken);
                if (result.Succeeded)
                {
                    resultMessage = "Record deleted.";
                    resultType = MessageType.Info;
                }
                else
                {
                    resultMessage = result.Error.ToString();
                    resultType = MessageType.Error;
                    Debug.LogError("[AtProto] " + resultMessage, writer);
                }
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                resultMessage = exception.Message;
                resultType = MessageType.Error;
                Debug.LogException(exception, writer);
            }
        }

        async Awaitable RunWriteAsync(
            AtProtoWriter writer,
            AtProtoWriteMode mode,
            CancellationToken cancellationToken)
        {
            try
            {
                AtProtoResult<AtProtoRecord> result;
                if (mode == AtProtoWriteMode.Create)
                    result = await writer.CreateRecordAsync(cancellationToken);
                else if (mode == AtProtoWriteMode.Update)
                    result = await writer.UpdateRecordAsync(cancellationToken);
                else
                    result = await writer.UpsertRecordAsync(cancellationToken);
                SetResult(writer, result, mode.ToString().ToLowerInvariant());
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                resultMessage = exception.Message;
                resultType = MessageType.Error;
                Debug.LogException(exception, writer);
            }
        }

        static string GetProgressMessage(AtProtoWriteMode mode)
        {
            if (mode == AtProtoWriteMode.Create)
                return "Creating record...";
            if (mode == AtProtoWriteMode.Update)
                return "Updating record...";
            return "Upserting record...";
        }

        void SetResult(
            AtProtoWriter writer,
            AtProtoResult<AtProtoRecord> result,
            string verb)
        {
            if (result.Succeeded)
            {
                resultMessage = "Record " + verb + ": " + result.Value.Uri;
                resultType = MessageType.Info;
            }
            else
            {
                resultMessage = result.Error.ToString();
                resultType = MessageType.Error;
                Debug.LogError("[AtProto] " + resultMessage, writer);
            }
        }
    }
}