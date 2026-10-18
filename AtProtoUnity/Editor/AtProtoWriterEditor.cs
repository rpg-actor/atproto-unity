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
            EditorGUILayout.PropertyField(collectionNsid, new GUIContent("Collection"));
            EditorGUILayout.PropertyField(recordKey, new GUIContent("Key"));
            EditorGUILayout.PropertyField(
                expectedRecordCid,
                new GUIContent("Expected CID"));
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

        static void DrawLastWrittenRecord(AtProtoWriter writer)
        {
            if (writer.LastWrittenRecord == null)
                return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Last write", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(writer.LastWrittenRecord.Uri);
            EditorGUILayout.SelectableLabel("CID: " + writer.LastWrittenRecord.Cid);
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