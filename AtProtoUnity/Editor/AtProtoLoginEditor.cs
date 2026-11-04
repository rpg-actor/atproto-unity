using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtProtoUnity.Editor
{
    [CustomEditor(typeof(AtProtoLogin))]
    sealed class AtProtoLoginEditor : UnityEditor.Editor
    {
        const int MaxPermissionSearchResults = 8;
        SerializedProperty loginMethod;
        SerializedProperty handle;
        SerializedProperty clientId;
        SerializedProperty oauthRedirectMode;
        SerializedProperty webGlClientMetadataUrl;
        SerializedProperty webGlRedirectUri;
        SerializedProperty resource;
        SerializedProperty recordPermissions;
        SerializedProperty blobPermission;
        SerializedProperty identityEndpoint;
        SerializedProperty plcDirectoryEndpoint;
        IReadOnlyList<string> permissionSearchResults = Array.Empty<string>();
        string permissionSearchMessage;
        string permissionSearchPrefix;
        int permissionSearchIndex = -1;

        void OnEnable()
        {
            loginMethod = serializedObject.FindProperty("loginMethod");
            handle = serializedObject.FindProperty("handle");
            clientId = serializedObject.FindProperty("clientId");
            oauthRedirectMode = serializedObject.FindProperty("oauthRedirectMode");
            webGlClientMetadataUrl = serializedObject.FindProperty("webGlClientMetadataUrl");
            webGlRedirectUri = serializedObject.FindProperty("webGlRedirectUri");
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
                EditorGUILayout.LabelField("OAuth Callback", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(oauthRedirectMode, new GUIContent("Mode"));
                var webGlBuild = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
                var webGlCallback = oauthRedirectMode.enumValueIndex == (int)AtProtoOAuthRedirectMode.WebGlBrowser
                    || oauthRedirectMode.enumValueIndex == (int)AtProtoOAuthRedirectMode.Auto && webGlBuild;
                EditorGUILayout.HelpBox(
                    "Auto uses the browser callback in WebGL players and loopback on desktop. Editor Play mode always uses the desktop callback.",
                    MessageType.Info);
                if (webGlBuild && oauthRedirectMode.enumValueIndex == (int)AtProtoOAuthRedirectMode.DesktopLoopback)
                    EditorGUILayout.HelpBox("Desktop loopback cannot run in a WebGL player. Select Auto or WebGL browser.", MessageType.Warning);
                else if (!webGlBuild && oauthRedirectMode.enumValueIndex == (int)AtProtoOAuthRedirectMode.WebGlBrowser)
                    EditorGUILayout.HelpBox("The WebGL callback can only run in a WebGL player. Select Auto or desktop loopback.", MessageType.Warning);
                if (webGlCallback)
                {
                    if (GUILayout.Button("Configure Local OAuth Test"))
                    {
                        webGlClientMetadataUrl.stringValue = "http://localhost";
                        webGlRedirectUri.stringValue = string.Empty;
                        oauthRedirectMode.enumValueIndex = (int)AtProtoOAuthRedirectMode.Auto;
                    }
                    EditorGUILayout.PropertyField(webGlClientMetadataUrl, new GUIContent("Client metadata URL"));
                    EditorGUILayout.PropertyField(webGlRedirectUri, new GUIContent("Redirect URI override"));
                    EditorGUILayout.HelpBox(
                        "For a local test, click Configure Local OAuth Test. In Player Settings > WebGL > Publishing Settings, set Compression Format to Disabled, then use Build (not Build And Run). From the output folder run: python -m http.server 8000 --bind 127.0.0.1. Open http://127.0.0.1:8000/, enter a handle, and press Login. Allow the popup. Published builds need hosted HTTPS client metadata.",
                        MessageType.Info);
                    if (!AtProtoOAuthWebGlUrlValidation.IsClientMetadataUrl(webGlClientMetadataUrl.stringValue)
                        || (!string.IsNullOrWhiteSpace(webGlRedirectUri.stringValue)
                            && !AtProtoOAuthWebGlUrlValidation.IsRedirectUri(webGlRedirectUri.stringValue)))
                        EditorGUILayout.HelpBox(
                            "Use an HTTPS client metadata URL and HTTPS redirect URI, or http://localhost with a loopback IP callback for local development.",
                            MessageType.Warning);
                }
                else
                    EditorGUILayout.PropertyField(clientId, new GUIContent("Desktop client ID"));
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

            var searchStatus = AtProtoReaderEditor.GetOperationStatus(target);
            if (searchStatus != null)
                EditorGUILayout.HelpBox(searchStatus, MessageType.Info);
            if (!string.IsNullOrEmpty(permissionSearchMessage))
                EditorGUILayout.HelpBox(permissionSearchMessage, MessageType.Warning);

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
                    using (new EditorGUI.DisabledScope(
                        !AtProtoLexiconClient.IsNamespaceSearchPrefix(collection.stringValue.Trim())
                        || AtProtoReaderEditor.GetOperationStatus(target) != null))
                    {
                        if (GUILayout.Button("Search", EditorStyles.miniButton, GUILayout.Width(52f)))
                            StartPermissionSearch(index, collection.stringValue);
                    }
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(64f)))
                        return true;
                }

                var picked = AtProtoRecordTypeAutocomplete.Suggestions(
                    "AtProtoRecordPermission." + index,
                    collection.stringValue);
                if (picked != null)
                    collection.stringValue = picked;

                if (permissionSearchIndex == index
                    && string.Equals(permissionSearchPrefix, collection.stringValue.Trim(), StringComparison.Ordinal))
                {
                    for (var resultIndex = 0;
                        resultIndex < Mathf.Min(permissionSearchResults.Count, MaxPermissionSearchResults);
                        resultIndex++)
                    {
                        var suggestion = permissionSearchResults[resultIndex];
                        if (GUILayout.Button(suggestion, EditorStyles.linkLabel))
                        {
                            collection.stringValue = suggestion;
                            GUI.FocusControl(null);
                            permissionSearchResults = Array.Empty<string>();
                            permissionSearchMessage = null;
                            break;
                        }
                    }
                    if (permissionSearchResults.Count > MaxPermissionSearchResults)
                        EditorGUILayout.LabelField(
                            "Showing the first " + MaxPermissionSearchResults + " results. Refine the namespace to narrow the search.",
                            EditorStyles.wordWrappedMiniLabel);
                }

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

        void StartPermissionSearch(int index, string prefix)
        {
            permissionSearchIndex = index;
            permissionSearchPrefix = prefix.Trim();
            permissionSearchResults = Array.Empty<string>();
            permissionSearchMessage = null;
            var client = new AtProtoLexiconClient(
                identityEndpoint.stringValue,
                plcDirectoryEndpoint.stringValue);
            AtProtoReaderEditor.StartOperation(
                target,
                cancellationToken => SearchPermissionTypesAsync(
                    index,
                    permissionSearchPrefix,
                    client,
                    cancellationToken),
                Repaint,
                "Searching published record schemas...");
        }

        async Awaitable SearchPermissionTypesAsync(
            int index,
            string prefix,
            AtProtoLexiconClient client,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var result = await client.SearchAsync(prefix, timeout.Token);
                    cancellationToken.ThrowIfCancellationRequested();
                    permissionSearchIndex = index;
                    permissionSearchPrefix = prefix;
                    permissionSearchResults = result.Succeeded
                        ? result.Value
                        : Array.Empty<string>();
                    permissionSearchMessage = result.Succeeded
                        ? result.Value.Count == 0
                            ? "No published record schemas match this namespace. You can still enter an NSID manually."
                            : null
                        : result.Error?.Message ?? "The published schema search failed.";
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                permissionSearchMessage = "Schema search timed out. Refine the namespace or enter an NSID manually.";
            }
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

    sealed class AtProtoOAuthWebGlBuildValidator : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null)
                return;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var writer in root.GetComponentsInChildren<AtProtoWriter>(true))
                    ValidateWriteOnLogin(writer);

                if (report.summary.platform != BuildTarget.WebGL)
                    continue;

                foreach (var login in root.GetComponentsInChildren<AtProtoLogin>(true))
                {
                    if (login.LoginMethod != AtProtoLoginMethod.OAuth)
                        continue;
                    if (login.OAuthRedirectMode == AtProtoOAuthRedirectMode.DesktopLoopback)
                        throw new BuildFailedException(
                            "AT Protocol OAuth on WebGL cannot use the desktop loopback callback. Set callback mode to Auto or WebGlBrowser on '"
                            + login.name + "'.");
                    if (!AtProtoOAuthWebGlUrlValidation.IsClientMetadataUrl(login.WebGlClientMetadataUrl)
                        || (!string.IsNullOrWhiteSpace(login.WebGlRedirectUri)
                            && !AtProtoOAuthWebGlUrlValidation.IsRedirectUri(login.WebGlRedirectUri)))
                        throw new BuildFailedException(
                            "AT Protocol OAuth on WebGL requires a valid client metadata URL and optional redirect URI override on '"
                            + login.name + "'. Configure them in the AtProtoLogin Inspector.");
                }
            }
        }

        static void ValidateWriteOnLogin(AtProtoWriter writer)
        {
            if (!writer.WriteOnLogin
                || writer.Login == null
                || writer.Login.LoginMethod != AtProtoLoginMethod.OAuth)
                return;

            var collection = writer.CollectionNsid?.Trim();
            if (!AtProtoLexiconClient.IsValidNsid(collection))
                throw new BuildFailedException(
                    "AtProtoWriter '" + writer.name + "' has Write on login enabled but no valid collection NSID. Configure its composer or collection before building.");

            if (writer.Composer is AtProtoSchemaComposer schema && schema.HasSchema)
            {
                var keyError = schema.Document.GetRecordKeyValidationMessage(
                    writer.RecordKey,
                    writer.WriteMode);
                if (keyError != null)
                    throw new BuildFailedException(
                        "AtProtoWriter '" + writer.name + "' cannot write on login: " + keyError);
            }

            var requiredActions = writer.WriteMode == AtProtoWriteMode.Create
                ? new[] { "create" }
                : writer.WriteMode == AtProtoWriteMode.Update
                    ? new[] { "update" }
                    : new[] { "create", "update" };
            foreach (var action in requiredActions)
            {
                if (!AtProtoOAuthScopes.AllowsRecordAction(
                    writer.Login.LoginMethod,
                    writer.Login.Scope,
                    collection,
                    action))
                    throw new BuildFailedException(
                        "AtProtoWriter '" + writer.name + "' writes on login using "
                        + writer.WriteMode + " for '" + collection + "', but its AtProtoLogin does not request '"
                        + action + "' permission for that collection. Add the permission and sign in again after building.");
            }
        }

    }

    static class AtProtoOAuthWebGlUrlValidation
    {
        public static bool IsClientMetadataUrl(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return true;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && uri.IsDefaultPort && uri.AbsolutePath == "/"
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && string.IsNullOrEmpty(uri.UserInfo);
        }

        public static bool IsRedirectUri(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return true;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && System.Net.IPAddress.TryParse(uri.Host, out var address)
                && System.Net.IPAddress.IsLoopback(address);
        }
    }

    sealed class AtProtoOAuthWebGlCallbackPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AtProtoLogin).Assembly);
            var source = package == null
                ? null
                : Path.Combine(package.resolvedPath, "WebGL", "AtProtoOAuthCallback.html");
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                throw new BuildFailedException("The AT Protocol WebGL OAuth callback page is missing from the package.");

            Directory.CreateDirectory(report.summary.outputPath);
            File.Copy(source, Path.Combine(report.summary.outputPath, "AtProtoOAuthCallback.html"), true);
        }
    }
}