using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    // Shows either a repository's collections or one page of records.
    sealed class AtProtoRecordsWindow : EditorWindow
    {
        // Previews are capped so large blobs don't take over the window.
        const float MaxImagePreviewWidth = 360f;
        const float MaxImagePreviewHeight = 240f;

        AtProtoReader reader;
        AtProtoRecordPage displayedPage;
        readonly List<RecordView> records = new();
        Vector2 scrollPosition;
        IDisposable pageReadOperation;
        IDisposable imageLoadOperation;
        bool showingCollections;
        bool loadingPage;
        bool appendLoadedPage;

        public static void ShowRecords(AtProtoReader reader)
        {
            Open(reader, false);
        }

        public static void ShowCollections(AtProtoReader reader)
        {
            Open(reader, true);
        }

        internal static void StopPlayModeOperations()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<AtProtoRecordsWindow>())
            {
                window.StopPageRead();
                window.StopImageLoading();
                window.loadingPage = false;
                window.appendLoadedPage = false;
            }
        }

        static void Open(AtProtoReader reader, bool collections)
        {
            var window = GetWindow<AtProtoRecordsWindow>("AT Protocol Records");
            window.StopPageRead();
            window.StopImageLoading();
            window.DestroyTextures();
            window.reader = reader;
            window.displayedPage = null;
            window.showingCollections = collections;
            window.loadingPage = false;
            window.appendLoadedPage = false;
            window.minSize = new Vector2(560, 400);
            window.Show();
            window.Focus();
        }

        // Textures are native memory, so they must be destroyed explicitly.
        void OnDisable()
        {
            StopPageRead();
            StopImageLoading();
            appendLoadedPage = false;
            DestroyTextures();
        }

        void StopPageRead()
        {
            pageReadOperation?.Dispose();
            pageReadOperation = null;
        }

        void StopImageLoading()
        {
            imageLoadOperation?.Dispose();
            imageLoadOperation = null;
        }

        void OnGUI()
        {
            if (reader == null)
            {
                EditorGUILayout.HelpBox("Select an AtProtoReader with records to view.", MessageType.Info);
                return;
            }

            if (AnyImageLoading())
                EditorGUILayout.HelpBox("Loading image previews...", MessageType.Info);

            if (showingCollections)
            {
                DrawIdentity();
                DrawCollections();
                return;
            }

            var page = reader.LastPage;
            if (page == null)
            {
                if (loadingPage)
                {
                    DrawIdentity();
                    EditorGUILayout.HelpBox("Reading records...", MessageType.Info);
                    return;
                }

                EditorGUILayout.HelpBox("Read records from the AtProtoReader first.", MessageType.Info);
                return;
            }

            if (page != displayedPage)
                LoadPage(page);

            DrawIdentity();
            EditorGUILayout.Space();
            if (loadingPage)
                EditorGUILayout.HelpBox("Loading more records...", MessageType.Info);
            EditorGUILayout.LabelField(
                $"{page.CollectionNsid ?? reader.CollectionNsid}  |  {records.Count} records",
                EditorStyles.boldLabel);

            scrollPosition = EditorGUILayout.BeginScrollView(
                scrollPosition,
                GUILayout.ExpandHeight(true));
            foreach (var record in records)
                DrawRecord(record);
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(loadingPage || !page.HasNextPage))
            {
                if (GUILayout.Button("Load More"))
                    ReadNextPage();
            }
        }

        void DrawCollections()
        {
            var description = reader.LastRepositoryDescription;
            if (description == null)
            {
                EditorGUILayout.HelpBox("Explore the repository first.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Repository Collections ({description.Collections.Count})",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Collections advertised by the account. Choose Read to inspect one.",
                MessageType.Info);

            string selectedCollection = null;
            scrollPosition = EditorGUILayout.BeginScrollView(
                scrollPosition,
                GUILayout.ExpandHeight(true));
            foreach (var collection in description.Collections)
            {
                if (string.IsNullOrWhiteSpace(collection))
                    continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(collection.Trim(), EditorStyles.label);
                if (GUILayout.Button("Read", GUILayout.Width(70)))
                    selectedCollection = collection.Trim();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(selectedCollection))
                ReadCollection(selectedCollection);
        }

        void ReadCollection(string collectionNsid)
        {
            StartPageRead(
                cancellationToken => reader.ReadRecordsAsync(collectionNsid, cancellationToken),
                false);
        }

        void ReadNextPage()
        {
            if (reader.LastPage == null || !reader.LastPage.HasNextPage)
                return;

            StartPageRead(reader.ReadNextRecordsAsync, true);
        }

        void StartPageRead(
            Func<CancellationToken, Awaitable> operationFactory,
            bool appendPage)
        {
            StopPageRead();
            StopImageLoading();
            if (!appendPage)
                displayedPage = null;
            appendLoadedPage = appendPage;
            loadingPage = true;
            showingCollections = false;

            pageReadOperation = AtProtoReaderEditor.StartOperation(
                reader,
                operationFactory,
                () =>
                {
                    pageReadOperation = null;
                    loadingPage = false;
                    if (reader.LastPage == displayedPage)
                        appendLoadedPage = false;
                    Repaint();
                },
                appendPage ? "Loading more records..." : "Reading records...");
        }

        void LoadPage(AtProtoRecordPage page)
        {
            var append = appendLoadedPage;
            appendLoadedPage = false;
            if (!append)
            {
                StopImageLoading();
                DestroyTextures();
                records.Clear();
            }

            displayedPage = page;
            loadingPage = false;

            foreach (var record in page.Records)
                records.Add(new RecordView(record));
        }

        bool AnyImageLoading()
        {
            foreach (var record in records)
            {
                foreach (var blob in record.Blobs)
                {
                    if (blob.Loading)
                        return true;
                }
            }
            return false;
        }

        void DestroyTextures()
        {
            foreach (var record in records)
            {
                foreach (var blob in record.Blobs)
                {
                    if (blob.Texture == null)
                        continue;

                    DestroyImmediate(blob.Texture);
                    blob.Texture = null;
                }
            }
        }

        // Only expanded records load images, one blob at a time.
        async Awaitable LoadImagesAsync(CancellationToken cancellationToken)
        {
            foreach (var record in records)
            {
                if (!record.Expanded)
                    continue;

                foreach (var blob in record.Blobs)
                {
                    if (!blob.IsImage || blob.Loading || blob.Texture != null)
                        continue;

                    blob.Loading = true;
                    Repaint();

                    try
                    {
                        var result = await reader.GetBlobAsync(blob.Cid, cancellationToken);
                        if (result.Succeeded)
                            blob.Texture = result.Value;
                        else
                            blob.Error = result.Error.ToString();
                    }
                    catch (Exception exception) when (!(exception is OperationCanceledException))
                    {
                        blob.Error = exception.Message;
                    }
                    finally
                    {
                        blob.Loading = false;
                        Repaint();
                    }
                }
            }
        }

        void DrawIdentity()
        {
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Handle", reader.Handle);
            EditorGUILayout.SelectableLabel("DID: " + (reader.Did ?? "Not resolved"));
            EditorGUILayout.SelectableLabel("PDS: " + (reader.PdsEndpoint ?? "Not resolved"));
        }

        // Draw one collapsed or expanded record, including metadata and formatted JSON.
        void DrawRecord(RecordView record)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var wasExpanded = record.Expanded;
            record.Expanded = EditorGUILayout.Foldout(
                record.Expanded,
                "rkey: " + record.Rkey,
                true);
            EditorGUILayout.LabelField(record.Collection, EditorStyles.miniLabel);

            if (record.Expanded)
            {
                if (!wasExpanded && record.HasUnloadedImages)
                {
                    StopImageLoading();
                    imageLoadOperation = AtProtoReaderEditor.StartOperation(
                        reader,
                        LoadImagesAsync,
                        () =>
                        {
                            imageLoadOperation = null;
                            Repaint();
                        },
                        "Loading image previews...");
                }

                EditorGUILayout.SelectableLabel("URI: " + record.Record.Uri);
                EditorGUILayout.SelectableLabel("CID: " + record.Record.Cid);

                DrawBlobs(record);
                EditorGUILayout.LabelField("Record Value", EditorStyles.boldLabel);
                var valueJson = record.Value?.ToString(Formatting.Indented)
                    ?? "No record value available.";
                var valueWidth = Mathf.Max(240f, EditorGUIUtility.currentViewWidth - 40f);
                var valueHeight = Mathf.Max(
                    140f,
                    EditorStyles.textArea.CalcHeight(new GUIContent(valueJson), valueWidth));
                EditorGUILayout.TextArea(
                    valueJson,
                    EditorStyles.textArea,
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(valueHeight));
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        // Show blob metadata and, when available, a dimension-aware image preview.
        static void DrawBlobs(RecordView record)
        {
            if (record.Blobs.Count == 0)
                return;

            EditorGUILayout.LabelField("Blobs", EditorStyles.boldLabel);
            foreach (var blob in record.Blobs)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(blob.MimeType + "  |  " + blob.Cid);

                if (!blob.IsImage)
                {
                    EditorGUILayout.LabelField("Binary blob (preview unavailable)", EditorStyles.miniLabel);
                }
                else if (blob.Loading)
                {
                    EditorGUILayout.LabelField("Loading image...", EditorStyles.miniLabel);
                }
                else if (blob.Texture != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawImagePreview(blob.Texture);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
                else if (!string.IsNullOrEmpty(blob.Error))
                {
                    EditorGUILayout.HelpBox(blob.Error, MessageType.Warning);
                }

                EditorGUILayout.EndVertical();
            }
        }

        // Fit the image inside a bounded rectangle while preserving its original aspect ratio.
        static void DrawImagePreview(Texture2D texture)
        {
            var availableWidth = Mathf.Max(1f, EditorGUIUtility.currentViewWidth - 80f);
            var maxWidth = Mathf.Min(MaxImagePreviewWidth, availableWidth);
            var scale = Mathf.Min(
                1f,
                Mathf.Min(
                    maxWidth / Mathf.Max(1, texture.width),
                    MaxImagePreviewHeight / Mathf.Max(1, texture.height)));
            var width = Mathf.Max(1f, texture.width * scale);
            var height = Mathf.Max(1f, texture.height * scale);
            var rect = GUILayoutUtility.GetRect(
                width,
                height,
                GUILayout.Width(width),
                GUILayout.Height(height));
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
        }

        sealed class RecordView
        {
            public readonly AtProtoRecord Record;
            public readonly List<BlobView> Blobs = new();
            public bool Expanded;

            public RecordView(AtProtoRecord record)
            {
                Record = record;
                FindBlobs(record.Value, Blobs);
            }

            public JToken Value => Record?.Value;

            public bool HasUnloadedImages
            {
                get
                {
                    foreach (var blob in Blobs)
                    {
                        if (blob.IsImage && !blob.Loading && blob.Texture == null
                            && string.IsNullOrEmpty(blob.Error))
                            return true;
                    }

                    return false;
                }
            }

            public string Collection
            {
                get => Record?.CollectionNsid ?? "Unknown collection";
            }

            public string Rkey
            {
                get => Record?.RecordKey ?? "Unknown";
            }
        }

        // Editor-only state for one discovered blob and its optional downloaded texture.
        sealed class BlobView
        {
            public readonly string Cid;
            public readonly string MimeType;
            public readonly bool IsImage;
            public bool Loading;
            public Texture2D Texture;
            public string Error;

            public BlobView(string cid, string mimeType)
            {
                Cid = cid;
                MimeType = string.IsNullOrEmpty(mimeType) ? "Unknown blob" : mimeType;
                IsImage = MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Walk nested record JSON because blobs may appear inside arrays or embedded objects.
        static void FindBlobs(JToken token, List<BlobView> blobs)
        {
            if (token is JObject obj && obj["$type"]?.Value<string>() == "blob")
            {
                var cid = obj["ref"]?["$link"]?.Value<string>();
                if (!string.IsNullOrEmpty(cid))
                    blobs.Add(new BlobView(cid, obj["mimeType"]?.Value<string>()));
            }

            if (token is not JContainer container)
                return;

            foreach (var child in container.Children())
                FindBlobs(child, blobs);
        }
    }
}