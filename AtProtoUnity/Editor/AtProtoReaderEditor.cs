using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    // Adds actions to the AtProtoReader in the Inspector.
    [InitializeOnLoad]
    [CustomEditor(typeof(AtProtoReader))]
    sealed class AtProtoReaderEditor : UnityEditor.Editor
    {
        static readonly Dictionary<int, EditorOperation> Operations = new();

        static AtProtoReaderEditor()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                    StopAllOperations();
            };
        }

        // Unity redraws this method; it only starts work when a button is pressed.
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var reader = (AtProtoReader)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("AT Protocol Actions", EditorStyles.boldLabel);
            var operationStatus = GetOperationStatus(reader);
            if (operationStatus != null)
                EditorGUILayout.HelpBox(operationStatus, MessageType.Info);

            using (new EditorGUI.DisabledScope(operationStatus != null))
            {
                if (GUILayout.Button("Resolve Handle"))
                    StartOperation(
                        reader,
                        reader.ResolveHandleAsync,
                        Repaint,
                        "Resolving handle...");
                if (GUILayout.Button("Explore Collections"))
                    StartOperation(
                        reader,
                        reader.ExploreCollectionsAsync,
                        () =>
                        {
                            ShowCollections(reader);
                            Repaint();
                        },
                        "Exploring repository collections...");
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
                        },
                        "Reading records...");

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
        }

        // The editor cannot await between GUI frames, so EditorApplication.update polls the request.
        internal static IDisposable StartOperation(
            UnityEngine.Object owner,
            Func<CancellationToken, Awaitable> operationFactory,
            Action completed = null,
            string status = "Working...")
        {
            StopOperation(owner);

            var operation = new EditorOperation(owner, operationFactory, completed, status);
            Operations[owner.GetInstanceID()] = operation;
            operation.Start();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return operation;
        }

        internal static string GetOperationStatus(UnityEngine.Object owner)
        {
            if (owner == null || !Operations.TryGetValue(owner.GetInstanceID(), out var operation))
                return null;
            return operation.Status;
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

        static void StopOperation(UnityEngine.Object owner)
        {
            // Each component owns at most one editor request, so a new action cancels the previous one.
            if (owner == null)
                return;

            var id = owner.GetInstanceID();
            if (Operations.TryGetValue(id, out var operation))
                operation.Dispose();
        }

        static void StopAllOperations()
        {
            AtProtoRecordsWindow.StopPlayModeOperations();
            foreach (var operation in new List<EditorOperation>(Operations.Values))
                operation.Dispose();
        }

        sealed class EditorOperation : IDisposable
        {
            // This object connects one Awaitable to the editor update loop and its completion callback.
            readonly UnityEngine.Object owner;
            readonly int ownerId;
            readonly Func<CancellationToken, Awaitable> operationFactory;
            readonly Action completed;
            public string Status { get; }
            CancellationTokenSource cancellation;
            Awaitable operation;
            bool stopped;

            public EditorOperation(
                UnityEngine.Object owner,
                Func<CancellationToken, Awaitable> operationFactory,
                Action completed,
                string status)
            {
                this.owner = owner;
                ownerId = owner.GetInstanceID();
                this.operationFactory = operationFactory;
                this.completed = completed;
                Status = status;
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
                    Debug.LogException(exception, owner);
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

                if (Operations.TryGetValue(ownerId, out var current)
                    && ReferenceEquals(current, this))
                    Operations.Remove(ownerId);

                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            void Tick()
            {
                // EditorApplication.update runs on Unity's main thread, which is required by the callback.
                if (owner == null)
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
                    Debug.LogException(exception, owner);
                }
            }
        }
    }
}