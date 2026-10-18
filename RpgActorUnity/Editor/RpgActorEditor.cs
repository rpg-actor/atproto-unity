using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtProtoUnity;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace RpgActorUnity
{
    [CustomEditor(typeof(RpgActor)), CanEditMultipleObjects]
    public class RpgActorEditor : Editor
    {
        private const string HandleControl = "RpgActorHandle";
        private const float FramePreviewSize = 144;
        private const float SheetPreviewHeight = 192;

        private static readonly RemoteValue<ActorEntry[]> Registry = new(RpgActorApi.GetActors);
        private static readonly RemoteValue<Dictionary<string, List<string>>> AssetCatalog = new(RpgActorApi.GetGeneratorAssets);
        private static List<Suggestion> _popularHandles;
        private static Dictionary<string, List<Suggestion>> _popularAssets;
        private static bool _assetsRankedByUsage;

        private SerializedProperty _handle;
        private SerializedProperty _source;
        private SerializedProperty _sheet;
        private SerializedProperty _direction;
        private SerializedProperty _isWalking;
        private SerializedProperty _frameDuration;

        private string _handleText;
        private bool _handleFocused;

        private bool _showEquipment;
        private string _equipmentDid;
        private List<EquipmentItem> _equipment;
        private string _equipmentError;

        private bool _showTryOn;
        private string _tryOnDid;
        private List<TryOnRow> _tryOnRows;
        private string _tryOnError;
        private bool _composing;
        private string _handleDid;
        private string _handleDidFor;

        private RpgActor Actor => (RpgActor)target;
        private bool SingleActor => targets.Length == 1;

        private void OnEnable()
        {
            if (!target) return;
            _handle = serializedObject.FindProperty(nameof(RpgActor.Handle));
            _source = serializedObject.FindProperty(nameof(RpgActor.Source));
            _sheet = serializedObject.FindProperty(nameof(RpgActor.Sheet));
            _direction = serializedObject.FindProperty(nameof(RpgActor.Direction));
            _isWalking = serializedObject.FindProperty(nameof(RpgActor.IsWalking));
            _frameDuration = serializedObject.FindProperty(nameof(RpgActor.FrameDuration));
        }

        private void OnDisable()
        {
            if (!target || !_handleFocused) return;
            serializedObject.Update();
            CommitHandle(_handleText);
            serializedObject.ApplyModifiedProperties();
        }

        public override bool RequiresConstantRepaint() => target && Actor.NeedsUpdate;

        public override void OnInspectorGUI()
        {
            if (!target) return;
            serializedObject.Update();

            DrawHandle();
            if (SingleActor) DrawStatus();
            EditorGUILayout.PropertyField(_source);
            if (_source.hasMultipleDifferentValues || _source.enumValueIndex == (int)RpgActorSource.Texture)
                EditorGUILayout.PropertyField(_sheet);
            if (ShowsTryOnPreview())
            {
                EditorGUILayout.HelpBox("Showing an unsaved Try On preview, so the handle is ignored.", MessageType.Info);
                if (GUILayout.Button("Back to Server", EditorStyles.miniButton, GUILayout.Width(100)))
                    ResetToServer();
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_direction);
            EditorGUILayout.PropertyField(_isWalking);
            EditorGUILayout.PropertyField(_frameDuration);
            EditorGUILayout.Space();

            if (SingleActor)
            {
                DrawPreview();
                EditorGUILayout.Space();
                DrawEquipment();
                DrawTryOn();
            }
            else
            {
                EditorGUILayout.HelpBox("Select a single actor to see its preview, equipment and Try On.", MessageType.None);
            }

            EditorGUILayout.Space();
            DrawActions();
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawHandle()
        {
            var shown = _handle.hasMultipleDifferentValues ? "" : _handle.stringValue;
            if (!_handleFocused)
                _handleText = shown;

            var e = Event.current;
            var editing = GUI.GetNameOfFocusedControl() == HandleControl;
            var submitted = editing && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            var cancelled = editing && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape;

            var handles = PopularHandles();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.showMixedValue = _handle.hasMultipleDifferentValues;
                _handleText = AutocompleteField.Text(HandleControl, new GUIContent("Handle"), _handleText);
                EditorGUI.showMixedValue = false;

                using (new EditorGUI.DisabledScope(handles is not { Count: > 0 }))
                {
                    if (GUILayout.Button("Random", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        _handleText = handles[UnityEngine.Random.Range(0, handles.Count)].Value;
                        GUI.FocusControl(null);
                    }
                }
            }

            var filter = _handleText == shown ? "" : _handleText;
            var options = AutocompleteField.RecentThenPopular("Handle", handles);
            var picked = AutocompleteField.Suggestions(HandleControl, filter, options, EditorGUIUtility.labelWidth);
            if (picked != null) _handleText = picked;

            var focused = GUI.GetNameOfFocusedControl() == HandleControl;
            if (cancelled)
            {
                _handleText = shown;
                focused = false;
            }
            else if (picked != null || submitted || !focused)
            {
                CommitHandle(_handleText);
                if (submitted) GUI.FocusControl(null);
                focused = false;
            }
            _handleFocused = focused;
        }

        private void CommitHandle(string text)
        {
            var handle = text.Trim();
            var shown = _handle.hasMultipleDifferentValues ? "" : _handle.stringValue;
            if (handle == shown) return;
            _handle.stringValue = handle;
            if (ShowsTryOnPreview())
                ResetToServer();
            AutocompleteField.Remember("Handle", handle);
        }

        private void ResetToServer()
        {
            _source.enumValueIndex = (int)RpgActorSource.Server;
            _sheet.objectReferenceValue = null;
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField("Status", Actor.Status.ToString());
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("DID", Actor.Did ?? "-");
                using (new EditorGUI.DisabledScope(Actor.Did == null))
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(60)))
                        EditorGUIUtility.systemCopyBuffer = Actor.Did;
            }
            if (Actor.Status == RpgActorStatus.Failed)
                EditorGUILayout.HelpBox(Actor.Error, MessageType.Error);
        }

        private void DrawPreview()
        {
            var sprite = Actor.CurrentSprite;
            if (!sprite || !sprite.texture)
            {
                var hint = Actor.Status == RpgActorStatus.Loading ? "Loading..." : "Enter a handle or assign a sheet.";
                EditorGUILayout.HelpBox(hint, MessageType.None);
                return;
            }

            var texture = sprite.texture;
            var frame = sprite.rect;
            var available = EditorGUIUtility.currentViewWidth - 40;
            var frameSize = Mathf.Min(FramePreviewSize, available / 2 - 4);
            var frameScale = frameSize / Mathf.Max(frame.width, frame.height);
            var sheetScale = Mathf.Min(SheetPreviewHeight / texture.height, (available - frameSize - 8) / texture.width);

            using (new EditorGUILayout.HorizontalScope())
            {
                var frameRect = GUILayoutUtility.GetRect(frame.width * frameScale, frame.height * frameScale, GUILayout.ExpandWidth(false));
                var uv = new Rect(frame.x / texture.width, frame.y / texture.height, frame.width / texture.width, frame.height / texture.height);
                GUI.DrawTextureWithTexCoords(frameRect, texture, uv);

                GUILayout.Space(8);
                var sheetRect = GUILayoutUtility.GetRect(texture.width * sheetScale, texture.height * sheetScale, GUILayout.ExpandWidth(false));
                GUI.DrawTexture(sheetRect, texture);
                var highlight = new Rect(sheetRect.x + frame.x * sheetScale, sheetRect.yMax - frame.yMax * sheetScale,
                    frame.width * sheetScale, frame.height * sheetScale);
                Handles.DrawSolidRectangleWithOutline(highlight, Color.clear, Color.yellow);

                var e = Event.current;
                if (e.type == EventType.MouseDown && sheetRect.Contains(e.mousePosition))
                {
                    var row = (int)((e.mousePosition.y - sheetRect.y) / (frame.height * sheetScale));
                    if (row < 4 && row < Actor.Rows)
                    {
                        _direction.enumValueIndex = row;
                        e.Use();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (RpgActorDirection direction in Enum.GetValues(typeof(RpgActorDirection)))
                    if (GUILayout.Button(direction.ToString(), EditorStyles.miniButton, GUILayout.Width(48)))
                        _direction.enumValueIndex = (int)direction;
            }
        }

        private void DrawEquipment()
        {
            _showEquipment = EditorGUILayout.Foldout(_showEquipment, "Equipment", true);
            if (!_showEquipment) return;

            using (new EditorGUI.IndentLevelScope())
            {
                if (Actor.Did == null)
                {
                    EditorGUILayout.LabelField("Load an actor to see its equipment.");
                    return;
                }

                if (_equipmentDid != Actor.Did)
                {
                    _equipmentDid = Actor.Did;
                    _equipment = null;
                    _equipmentError = null;
                    _ = LoadEquipment(Actor.Did);
                }

                if (_equipmentError != null)
                    EditorGUILayout.HelpBox(_equipmentError, MessageType.Error);
                else if (_equipment == null)
                    EditorGUILayout.LabelField("Loading...");
                else if (_equipment.Count == 0)
                    EditorGUILayout.LabelField("No equipment.");
                else
                    foreach (var item in _equipment)
                        EditorGUILayout.LabelField(item.Title, item.Category);

                if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(60)))
                    _equipmentDid = null;
            }
        }

        private void DrawTryOn()
        {
            _showTryOn = EditorGUILayout.Foldout(_showTryOn, "Try On", true);
            if (!_showTryOn) return;

            var assets = AssetCatalog.Get();
            using (new EditorGUI.IndentLevelScope())
            {
                if (Actor.Did == null && !string.IsNullOrWhiteSpace(Actor.Handle) && _handleDidFor != Actor.Handle)
                    _ = ResolveHandleDid(Actor.Handle);
                var did = Actor.Did ?? (_handleDidFor == Actor.Handle ? _handleDid : null);
                if (did != null && _tryOnDid != did)
                {
                    _tryOnDid = did;
                    _tryOnRows = null;
                    _tryOnError = null;
                    _ = LoadTryOnRows(did);
                }

                if (_tryOnError != null)
                {
                    EditorGUILayout.HelpBox(_tryOnError, MessageType.Error);
                    if (_tryOnRows == null && GUILayout.Button("Retry", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        _tryOnError = null;
                        _tryOnDid = _handleDid = _handleDidFor = null;
                    }
                }
                if (_tryOnRows == null)
                {
                    if (_tryOnError == null)
                        EditorGUILayout.LabelField(did == null && _handleDidFor == null ? "Load an actor to try things on." : "Loading...");
                    return;
                }

                AddCatalogRows(assets);

                var layerWidth = EditorGUIUtility.labelWidth + 60;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Layer", EditorStyles.miniBoldLabel, GUILayout.Width(layerWidth));
                    EditorGUILayout.LabelField("Override asset", EditorStyles.miniBoldLabel);
                }

                for (var i = 0; i < _tryOnRows.Count; i++)
                {
                    var row = _tryOnRows[i];
                    var control = $"RpgActorTryOn{i}";
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (row.Layer == null)
                            EditorGUILayout.LabelField(row.Id, GUILayout.Width(layerWidth));
                        else
                            row.Keep = EditorGUILayout.ToggleLeft($"{row.Id}  {row.Title}", row.Keep, GUILayout.Width(layerWidth));
                        row.Override = AutocompleteField.Text(control, null, row.Override);
                    }
                    var options = AutocompleteField.RecentThenPopular($"Asset.{row.Id}", PopularAssets(row.Id));
                    var picked = AutocompleteField.Suggestions(control, row.Override, options, layerWidth + 4);
                    if (picked != null) row.Override = picked;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_composing))
                        if (GUILayout.Button(_composing ? "Composing..." : "Compose", EditorStyles.miniButton, GUILayout.Width(100)))
                            _ = ComposeTryOn();
                    if (GUILayout.Button("Back to Server", EditorStyles.miniButton, GUILayout.Width(100)))
                        SetSheet(Actor, RpgActorSource.Server, null);
                }
            }
        }

        private void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload"))
                    foreach (RpgActor actor in targets)
                        actor.Reload();

                var savedAlready = Actor.Source == RpgActorSource.Texture && AssetDatabase.Contains(Actor.Sheet);
                var standardSheet = Actor.Columns == 3 && Actor.Rows == 4;
                using (new EditorGUI.DisabledScope(!SingleActor || Actor.Status != RpgActorStatus.Ready || savedAlready || !standardSheet))
                {
                    if (GUILayout.Button("Save Sheet as Asset..."))
                    {
                        var path = EditorUtility.SaveFilePanelInProject("Save Sheet", Actor.Handle, "png", "Save this actor's sheet as a texture asset.");
                        if (path.Length > 0)
                            SaveSheet(Actor, path);
                    }
                }
            }
        }

        private bool ShowsTryOnPreview() =>
            !_source.hasMultipleDifferentValues && _source.enumValueIndex == (int)RpgActorSource.Texture &&
            _sheet.objectReferenceValue && !AssetDatabase.Contains(_sheet.objectReferenceValue);

        private void AddCatalogRows(Dictionary<string, List<string>> assets)
        {
            if (assets == null) return;
            foreach (var id in assets.Keys)
                if (!_tryOnRows.Exists(row => row.Id == id))
                    _tryOnRows.Add(new TryOnRow { Id = id });
        }

        private async Awaitable LoadEquipment(string did)
        {
            try
            {
                var equipment = await RpgActorApi.GetEquipment(did);
                if (_equipmentDid == did) _equipment = equipment;
            }
            catch (Exception e)
            {
                if (_equipmentDid == did) _equipmentError = e.Message;
            }
            Repaint();
        }

        private async Awaitable LoadTryOnRows(string did)
        {
            try
            {
                var layers = await RpgActorApi.GetGeneratorLayers(did);
                if (_tryOnDid != did) return;
                _tryOnRows = new List<TryOnRow>();
                foreach (var layer in layers)
                {
                    _tryOnRows.Add(new TryOnRow
                    {
                        Id = (string)layer["id"],
                        Title = (string)layer["title"],
                        Layer = layer,
                        Keep = layer["disabled"]?.Value<bool>() != true,
                    });
                }
            }
            catch (Exception e)
            {
                if (_tryOnDid == did) _tryOnError = e.Message;
            }
            Repaint();
        }

        private async Awaitable ResolveHandleDid(string handle)
        {
            _handleDidFor = handle;
            _handleDid = null;
            try
            {
                var identity = await RpgActorApi.ResolveHandle(handle);
                if (_handleDidFor == handle) _handleDid = identity.Did;
            }
            catch (Exception e)
            {
                if (_handleDidFor == handle) _tryOnError = e.Message;
            }
            Repaint();
        }

        private async Awaitable ComposeTryOn()
        {
            _composing = true;
            _tryOnError = null;
            try
            {
                var layers = new JArray();
                var removedLayer = false;
                var overrides = new Dictionary<string, string>();
                foreach (var row in _tryOnRows)
                {
                    if (row.Layer != null && row.Keep) layers.Add(row.Layer);
                    if (row.Layer != null && !row.Keep) removedLayer = true;
                    if (row.Override.Length > 0)
                    {
                        overrides[row.Id] = row.Override;
                        AutocompleteField.Remember($"Asset.{row.Id}", row.Override);
                    }
                }
                if (removedLayer && overrides.Count > 0)
                    throw new Exception("rpg.actor can't remove layers and override assets in the same preview. Clear one of them.");

                var actor = Actor;
                var did = _tryOnDid;
                var texture = RpgActor.LoadTexture(await RpgActorApi.Compose(did, removedLayer ? layers : null, overrides));
                if (!actor || _tryOnDid != did)
                {
                    DestroyImmediate(texture);
                    return;
                }

                var previous = actor.Sheet;
                SetSheet(actor, RpgActorSource.Texture, texture);
                if (previous && !AssetDatabase.Contains(previous))
                    DestroyImmediate(previous);
            }
            catch (Exception e)
            {
                _tryOnError = e.Message;
            }
            finally
            {
                _composing = false;
                Repaint();
            }
        }

        private static List<Suggestion> PopularHandles()
        {
            var registry = Registry.Get();
            if (registry == null || _popularHandles != null) return _popularHandles;

            _popularHandles = registry
                .Where(actor => actor.Sprite != null && actor.Handle != null)
                .OrderByDescending(actor => actor.FollowersCount)
                .Select(actor => new Suggestion(actor.Handle,
                    string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.Handle : $"{actor.Handle}  ·  {actor.DisplayName}"))
                .ToList();
            return _popularHandles;
        }

        private static List<Suggestion> PopularAssets(string layer)
        {
            var catalog = AssetCatalog.Get();
            if (catalog == null) return null;

            var registry = Registry.Get();
            if (_popularAssets == null || (registry != null && !_assetsRankedByUsage))
            {
                var wearers = new Dictionary<string, int>();
                foreach (var actor in registry ?? Array.Empty<ActorEntry>())
                foreach (var worn in actor.GeneratorLayers ?? Array.Empty<GeneratorLayerEntry>())
                    wearers[$"{worn.Id}/{worn.AssetName}"] = wearers.GetValueOrDefault($"{worn.Id}/{worn.AssetName}") + 1;

                _popularAssets = new Dictionary<string, List<Suggestion>>();
                foreach (var (id, assets) in catalog)
                {
                    _popularAssets[id] = assets
                        .Select(asset => (asset, count: wearers.GetValueOrDefault($"{id}/{asset}")))
                        .OrderByDescending(entry => entry.count)
                        .Select(entry => new Suggestion(entry.asset, entry.count > 0 ? $"{entry.asset}  ×{entry.count}" : entry.asset))
                        .ToList();
                }
                _assetsRankedByUsage = registry != null;
            }
            return _popularAssets.GetValueOrDefault(layer);
        }

        private static void SetSheet(RpgActor actor, RpgActorSource source, Texture2D sheet)
        {
            var serialized = new SerializedObject(actor);
            serialized.FindProperty(nameof(RpgActor.Source)).enumValueIndex = (int)source;
            serialized.FindProperty(nameof(RpgActor.Sheet)).objectReferenceValue = sheet;
            serialized.ApplyModifiedProperties();
        }

        public static Texture2D SaveSheet(RpgActor actor, string path)
        {
            File.WriteAllBytes(path, actor.CurrentSprite.texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            var sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            SetSheet(actor, RpgActorSource.Texture, sheet);
            return sheet;
        }

        private class TryOnRow
        {
            public string Id;
            public string Title;
            public JToken Layer;
            public bool Keep = true;
            public string Override = "";
        }

        private class RemoteValue<T> where T : class
        {
            private readonly Func<Awaitable<T>> _load;
            private T _value;
            private bool _loading;
            private double _retryAt;

            public RemoteValue(Func<Awaitable<T>> load) => _load = load;

            public T Get()
            {
                if (_value == null && !_loading && EditorApplication.timeSinceStartup >= _retryAt)
                    _ = Load();
                return _value;
            }

            private async Awaitable Load()
            {
                _loading = true;
                try
                {
                    _value = await _load();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"rpg.actor: {e.Message}");
                    _retryAt = EditorApplication.timeSinceStartup + 60;
                }
                _loading = false;
                InternalEditorUtility.RepaintAllViews();
            }
        }
    }
}
