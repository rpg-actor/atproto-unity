using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    // Adds edit-mode actions to the AtProtoReader in the Inspector.
    [CustomEditor(typeof(AtProtoReader))]
    sealed class AtProtoReaderEditor : UnityEditor.Editor
    {
        static readonly Dictionary<int, EditorOperation> Operations = new();

        // Unity redraws this method; it only starts work when a button is pressed.
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var reader = (AtProtoReader)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("AT Protocol Actions", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Resolve Handle"))
                    StartOperation(reader, reader.ResolveHandleAsync, Repaint);
                if (GUILayout.Button("Explore Collections"))
                    StartOperation(
                        reader,
                        reader.ExploreCollectionsAsync,
                        () =>
                        {
                            ShowCollections(reader);
                            Repaint();
                        });
                var collectionLabel = string.IsNullOrWhiteSpace(reader.CollectionNsid)
                    ? "Collection"
                    : reader.CollectionNsid.Trim();
                var collectionColor = EditorGUIUtility.isProSkin ? "#8CC8FF" : "#245A8F";
                var readRecordsContent = new GUIContent(
                    $"Read <color={collectionColor}><b>{collectionLabel}</b></color> Records");
                var readRecordsStyle = new GUIStyle(GUI.skin.button)
                {
                    richText = true
                };
                if (GUILayout.Button(readRecordsContent, readRecordsStyle))
                    StartOperation(
                        reader,
                        reader.ReadRecordsAsync,
                        () =>
                        {
                            ShowRecords(reader);
                            Repaint();
                        });
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(reader.LastPage == null))
            {
                var viewerContent = new GUIContent(
                    "Open Record Viewer",
                    reader.LastPage == null
                        ? "Read records first to populate the viewer."
                        : "Open the most recently read record page.");
                if (GUILayout.Button(viewerContent))
                    ShowRecords(reader);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resolved Identity", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(reader.Did) ? "DID: not resolved" : "DID: " + reader.Did,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(reader.PdsEndpoint)
                    ? "PDS: not resolved"
                    : "PDS: " + reader.PdsEndpoint,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Inspector actions run in edit mode only. "
                    + "In play mode, call AtProtoReader.ResolveHandle or ReadRecords from code.",
                    MessageType.Info);
            }
        }

        // The editor cannot await between GUI frames, so EditorApplication.update polls the request.
        internal static IDisposable StartOperation(
            AtProtoReader reader,
            Func<CancellationToken, Awaitable> operationFactory,
            Action completed = null)
        {
            StopOperation(reader);

            var operation = new EditorOperation(reader, operationFactory, completed);
            Operations[reader.GetInstanceID()] = operation;
            operation.Start();
            return operation;
        }

        static void ShowRecords(AtProtoReader reader)
        {
            // The read action opens this automatically; this path reopens the cached page without a request.
            if (reader.LastPage != null)
                AtProtoRecordsWindow.ShowRecords(reader);
        }

        static void ShowCollections(AtProtoReader reader)
        {
            if (reader.LastRepositoryDescription != null)
                AtProtoRecordsWindow.ShowCollections(reader);
        }

        static void StopOperation(AtProtoReader reader)
        {
            // A reader owns at most one editor request, so a new action cancels the previous one.
            var id = reader.GetInstanceID();
            if (Operations.TryGetValue(id, out var operation))
                operation.Dispose();
        }

        sealed class EditorOperation : IDisposable
        {
            // This object connects one Awaitable to the editor update loop and its completion callback.
            readonly AtProtoReader reader;
            readonly int readerId;
            readonly Func<CancellationToken, Awaitable> operationFactory;
            readonly Action completed;
            CancellationTokenSource cancellation;
            Awaitable operation;
            bool stopped;

            public EditorOperation(
                AtProtoReader reader,
                Func<CancellationToken, Awaitable> operationFactory,
                Action completed)
            {
                this.reader = reader;
                readerId = reader.GetInstanceID();
                this.operationFactory = operationFactory;
                this.completed = completed;
            }

            public void Start()
            {
                cancellation = new CancellationTokenSource();
                try
                {
                    // Create the Awaitable once; repainting the Inspector must not restart the request.
                    operation = operationFactory(cancellation.Token);
                    EditorApplication.update += Tick;
                }
                catch (Exception exception)
                {
                    Dispose();
                    Debug.LogException(exception, reader);
                }
            }

            public void Dispose()
            {
                if (stopped)
                    return;

                // Remove the poller before cancelling so a late completion cannot invoke the callback twice.
                stopped = true;
                EditorApplication.update -= Tick;
                cancellation?.Cancel();
                cancellation?.Dispose();
                cancellation = null;

                if (Operations.TryGetValue(readerId, out var current)
                    && ReferenceEquals(current, this))
                    Operations.Remove(readerId);
            }

            void Tick()
            {
                // EditorApplication.update runs on Unity's main thread, which is required by the callback.
                if (reader == null)
                {
                    Dispose();
                    return;
                }

                if (!operation.IsCompleted)
                    return;

                try
                {
                    operation.GetAwaiter().GetResult();
                    Dispose();
                    completed?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    Dispose();
                }
                catch (Exception exception)
                {
                    Dispose();
                    Debug.LogException(exception, reader);
                }
            }
        }
    }
}