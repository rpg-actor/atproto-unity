using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    public enum AtProtoWriteMode
    {
        Create,
        Update,
        Upsert
    }

    // Composes and writes repository records through an authenticated login session.
    public sealed class AtProtoWriter : MonoBehaviour
    {
        [SerializeField] AtProtoLogin login;
        [SerializeField] AtProtoWriteMode writeMode = AtProtoWriteMode.Create;
        [SerializeField] bool writeOnLogin;
        [SerializeField] MonoBehaviour composer;
        [SerializeField] TextAsset jsonFile;
        [SerializeField] string collectionNsid;
        [SerializeField] string recordKey;
        [SerializeField] string expectedRecordCid;
        [SerializeField, TextArea(8, 30)] string rawJson = "{}";

        public AtProtoLogin Login
        {
            get => login;
            set
            {
                if (ReferenceEquals(login, value))
                    return;

                UnsubscribeFromLogin();
                login = value;
                SubscribeToLogin();
            }
        }

        public AtProtoWriteMode WriteMode
        {
            get => writeMode;
            set => writeMode = value;
        }

        public bool WriteOnLogin
        {
            get => writeOnLogin;
            set
            {
                writeOnLogin = value;
                if (value && login?.Session != null)
                    BeginAutomaticWrite(login.Session);
                else if (!value)
                    automaticWriteCancellation?.Cancel();
            }
        }

        // Any component implementing IAtProtoRecordJsonSource, such as AtProtoRecordComposer.
        public MonoBehaviour Composer
        {
            get => composer;
            set => composer = value;
        }

        public TextAsset JsonFile
        {
            get => jsonFile;
            set => jsonFile = value;
        }

        public string CollectionNsid
        {
            get => collectionNsid;
            set => collectionNsid = value;
        }

        public string RecordKey
        {
            get => recordKey;
            set => recordKey = value;
        }

        public string ExpectedRecordCid
        {
            get => expectedRecordCid;
            set => expectedRecordCid = value;
        }

        public string RawJson
        {
            get => rawJson;
            set => rawJson = value;
        }

        public string BuildRecordJson()
        {
            var result = ResolveRecordJson();
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error.Message);
            return result.Value;
        }

        public AtProtoRecord LastWrittenRecord { get; private set; }

        public event Action<AtProtoRecord> RecordWritten;
        public event Action<AtProtoRequestError> WriteFailed;

        CancellationTokenSource automaticWriteCancellation;
        string automaticWriteSessionDid;
        bool automaticWriteRunning;

        string TargetLabel(string collection, string key)
        {
            var did = login?.Identity?.Did ?? "<unknown-did>";
            var targetCollection = string.IsNullOrWhiteSpace(collection)
                ? "<missing-collection>"
                : collection.Trim();
            var targetKey = string.IsNullOrEmpty(key) ? "<server-generated>" : key;
            return "at://" + did + "/" + targetCollection + "/" + targetKey;
        }

        void OnEnable()
        {
            SubscribeToLogin();
            if (writeOnLogin && login?.Session != null)
                BeginAutomaticWrite(login.Session);
        }

        void OnDisable()
        {
            UnsubscribeFromLogin();
            automaticWriteCancellation?.Cancel();
            automaticWriteCancellation?.Dispose();
            automaticWriteCancellation = null;
            automaticWriteRunning = false;
        }

        public void WriteRecord()
        {
            if (login?.Session == null)
            {
                Debug.LogWarning(
                    "[AtProto] Write requested, but no authenticated login session is available.",
                    this);
                WriteFailed?.Invoke(new AtProtoRequestError(
                    "An authenticated AtProtoLogin session is required."));
                return;
            }

            BeginAutomaticWrite(login.Session, true);
        }

        void SubscribeToLogin()
        {
            if (login == null)
                return;

            login.SessionChanged -= OnLoginSessionChanged;
            login.SessionChanged += OnLoginSessionChanged;
        }

        void UnsubscribeFromLogin()
        {
            if (login != null)
                login.SessionChanged -= OnLoginSessionChanged;
        }

        void OnLoginSessionChanged(AtProtoAuthSession session)
        {
            if (session == null)
            {
                automaticWriteSessionDid = null;
                automaticWriteCancellation?.Cancel();
                return;
            }

            if (writeOnLogin)
                BeginAutomaticWrite(session);
        }

        void BeginAutomaticWrite(AtProtoAuthSession session, bool force = false)
        {
            if (session == null)
                return;
            if (automaticWriteRunning)
            {
                Debug.Log(
                    "[AtProto] Login write skipped because another write is already running.",
                    this);
                return;
            }
            if (!force && string.Equals(
                automaticWriteSessionDid,
                session.Did,
                StringComparison.Ordinal))
            {
                Debug.Log(
                    "[AtProto] Login write skipped because this session has already triggered a write.",
                    this);
                return;
            }

            automaticWriteSessionDid = session.Did;
            automaticWriteRunning = true;
            automaticWriteCancellation?.Cancel();
            automaticWriteCancellation?.Dispose();
            automaticWriteCancellation = new CancellationTokenSource();
            Debug.Log(
                "[AtProto] Login write starting: " + writeMode + " "
                + TargetLabel(collectionNsid, recordKey),
                this);
            _ = RunAutomaticWriteAsync(session, automaticWriteCancellation.Token);
        }

        async Awaitable RunAutomaticWriteAsync(
            AtProtoAuthSession session,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!ReferenceEquals(login?.Session, session))
                {
                    Debug.LogWarning(
                        "[AtProto] Login write stopped because the active session changed.",
                        this);
                    return;
                }

                var result = await WriteRecordAsync(cancellationToken);
                if (!result.Succeeded)
                {
                    automaticWriteSessionDid = null;
                    WriteFailed?.Invoke(result.Error);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                automaticWriteSessionDid = null;
                WriteFailed?.Invoke(new AtProtoRequestError(
                    "Automatic record write failed.",
                    exception));
            }
            finally
            {
                automaticWriteRunning = false;
            }
        }

        public void PrepareCreate(string requestedCollectionNsid)
        {
            collectionNsid = requestedCollectionNsid;
            recordKey = string.Empty;
            expectedRecordCid = string.Empty;
            rawJson = "{}";
            LastWrittenRecord = null;
        }

        public void LoadRecord(AtProtoRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            collectionNsid = record.CollectionNsid;
            recordKey = record.RecordKey;
            expectedRecordCid = record.Cid;
            rawJson = record.Value?.ToString(Formatting.Indented) ?? "{}";
            LastWrittenRecord = record;
        }

        public const string LoginMissingMessage = "Assign an AtProtoLogin component.";
        public const string LoginRequiredMessage = "Log in before writing a record.";

        public string GetValidationMessage(AtProtoWriteMode mode)
        {
            if (login == null)
                return LoginMissingMessage;
            if (login.Session == null || login.AuthenticatedClient == null)
                return LoginRequiredMessage;
            if (!AtProtoRecordPermission.IsValidCollection(collectionNsid?.Trim()))
                return "Enter a valid collection NSID.";
            if (mode != AtProtoWriteMode.Create && !IsValidRecordKey(recordKey))
                return "A valid record key is required for " + mode.ToString().ToLowerInvariant() + ".";
            if (mode == AtProtoWriteMode.Create
                && !string.IsNullOrWhiteSpace(recordKey)
                && !IsValidRecordKey(recordKey))
                return "The optional record key is invalid.";

            var sourceResult = ResolveRecordJson();
            if (!sourceResult.Succeeded)
                return sourceResult.Error.Message;

            var jsonResult = ParseRecordValue(sourceResult.Value);
            if (!jsonResult.Succeeded)
                return jsonResult.Error.Message;

            var allowsCreate = AtProtoOAuthScopes.AllowsRecordAction(
                login.Session.LoginMethod,
                login.Session.Scope,
                collectionNsid,
                "create");
            var allowsUpdate = AtProtoOAuthScopes.AllowsRecordAction(
                login.Session.LoginMethod,
                login.Session.Scope,
                collectionNsid,
                "update");
            if ((mode == AtProtoWriteMode.Create && !allowsCreate)
                || (mode == AtProtoWriteMode.Update && !allowsUpdate)
                || (mode == AtProtoWriteMode.Upsert && (!allowsCreate || !allowsUpdate)))
            {
                var action = mode == AtProtoWriteMode.Upsert
                    ? "create and update"
                    : mode.ToString().ToLowerInvariant();
                return "The current login scope does not allow " + action
                    + " for " + collectionNsid.Trim() + ".";
            }

            return null;
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> CreateRecordAsync(
            CancellationToken cancellationToken = default)
        {
            return WriteConfiguredAsync(AtProtoWriteMode.Create, cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> UpdateRecordAsync(
            CancellationToken cancellationToken = default)
        {
            return WriteConfiguredAsync(AtProtoWriteMode.Update, cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> UpsertRecordAsync(
            CancellationToken cancellationToken = default)
        {
            return WriteConfiguredAsync(AtProtoWriteMode.Upsert, cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> WriteRecordAsync(
            CancellationToken cancellationToken = default)
        {
            return WriteConfiguredAsync(writeMode, cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> CreateRecordAsync(
            string requestedCollectionNsid,
            string requestedJson,
            string requestedRecordKey = null,
            CancellationToken cancellationToken = default)
        {
            return WriteJsonAsync(
                AtProtoWriteMode.Create,
                requestedCollectionNsid,
                requestedRecordKey,
                null,
                requestedJson,
                cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> UpdateRecordAsync(
            string requestedCollectionNsid,
            string requestedRecordKey,
            string requestedJson,
            string requestedExpectedRecordCid = null,
            CancellationToken cancellationToken = default)
        {
            return WriteJsonAsync(
                AtProtoWriteMode.Update,
                requestedCollectionNsid,
                requestedRecordKey,
                requestedExpectedRecordCid,
                requestedJson,
                cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoRecord>> UpsertRecordAsync(
            string requestedCollectionNsid,
            string requestedRecordKey,
            string requestedJson,
            CancellationToken cancellationToken = default)
        {
            return WriteJsonAsync(
                AtProtoWriteMode.Upsert,
                requestedCollectionNsid,
                requestedRecordKey,
                null,
                requestedJson,
                cancellationToken);
        }

        async Awaitable<AtProtoResult<AtProtoRecord>> WriteConfiguredAsync(
            AtProtoWriteMode mode,
            CancellationToken cancellationToken)
        {
            var sourceResult = ResolveRecordJson();
            if (!sourceResult.Succeeded)
            {
                Debug.LogError(
                    "[AtProto] " + mode + " could not build its record payload: "
                    + (sourceResult.Error?.ToString() ?? "Unknown source error."),
                    this);
                return AtProtoResult<AtProtoRecord>.Failure(sourceResult.Error);
            }

            return await WriteJsonAsync(
                mode,
                collectionNsid,
                recordKey,
                expectedRecordCid,
                sourceResult.Value,
                cancellationToken);
        }

        AtProtoResult<string> ResolveRecordJson()
        {
            if (composer != null)
            {
                if (!(composer is IAtProtoRecordJsonSource source))
                    return AtProtoResult<string>.Failure(
                        new AtProtoRequestError(
                            "The assigned composer must implement IAtProtoRecordJsonSource."));

                try
                {
                    return AtProtoResult<string>.Success(source.BuildRecordJson());
                }
                catch (Exception exception)
                {
                    return AtProtoResult<string>.Failure(
                        new AtProtoRequestError("The composer could not build a record.", exception));
                }
            }

            if (jsonFile != null)
                return AtProtoResult<string>.Success(jsonFile.text);

            return AtProtoResult<string>.Success(rawJson);
        }

        async Awaitable<AtProtoResult<AtProtoRecord>> WriteJsonAsync(
            AtProtoWriteMode mode,
            string requestedCollectionNsid,
            string requestedRecordKey,
            string requestedExpectedRecordCid,
            string requestedJson,
            CancellationToken cancellationToken)
        {
            Debug.Log(
                "[AtProto] " + mode + " attempt: "
                + TargetLabel(requestedCollectionNsid, requestedRecordKey)
                + "\nPayload: " + (requestedJson ?? "<null>"),
                this);

            var result = await WriteJsonCoreAsync(
                mode,
                requestedCollectionNsid,
                requestedRecordKey,
                requestedExpectedRecordCid,
                requestedJson,
                cancellationToken);
            if (result.Succeeded)
            {
                Debug.Log(
                    "[AtProto] " + mode + " succeeded: "
                    + result.Value.Uri + " (CID " + result.Value.Cid + ")",
                    this);
            }
            else
            {
                Debug.LogError(
                    "[AtProto] " + mode + " failed: "
                    + (result.Error?.ToString() ?? "Unknown write error."),
                    this);
            }
            return result;
        }

        async Awaitable<AtProtoResult<AtProtoRecord>> WriteJsonCoreAsync(
            AtProtoWriteMode mode,
            string requestedCollectionNsid,
            string requestedRecordKey,
            string requestedExpectedRecordCid,
            string requestedJson,
            CancellationToken cancellationToken)
        {
            if (login == null)
                return Failure("An AtProtoLogin component is required.");
            if (login.Session == null || login.AuthenticatedClient == null)
                return Failure("An authenticated AtProtoLogin session is required.");

            var collection = requestedCollectionNsid?.Trim();
            if (!AtProtoRecordPermission.IsValidCollection(collection))
                return Failure("A valid collection NSID is required.");

            var key = requestedRecordKey?.Trim();
            if (mode != AtProtoWriteMode.Create && !IsValidRecordKey(key))
                return Failure("A valid record key is required for " + mode.ToString().ToLowerInvariant() + ".");
            if (mode == AtProtoWriteMode.Create
                && !string.IsNullOrEmpty(key)
                && !IsValidRecordKey(key))
                return Failure("The record key is invalid.");

            var jsonResult = ParseRecordValue(requestedJson);
            if (!jsonResult.Succeeded)
                return AtProtoResult<AtProtoRecord>.Failure(jsonResult.Error);

            if (mode == AtProtoWriteMode.Upsert)
                return await UpsertJsonAsync(
                    collection,
                    key,
                    requestedJson,
                    cancellationToken);

            var action = mode == AtProtoWriteMode.Update ? "update" : "create";
            if (!AtProtoOAuthScopes.AllowsRecordAction(
                login.Session.LoginMethod,
                login.Session.Scope,
                collection,
                action))
            {
                return Failure(
                    "The current login scope does not allow " + action
                    + " for " + collection + ".");
            }

            var identity = login.Identity;
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return Failure("The authenticated session has no repository DID.");
            if (!AtProtoClient.IsHttpsUrl(identity.PdsEndpoint))
                return Failure("The authenticated session has no valid HTTPS PDS endpoint.");

            var request = new JObject
            {
                ["repo"] = identity.Did,
                ["collection"] = collection,
                ["record"] = jsonResult.Value
            };
            if (mode == AtProtoWriteMode.Create)
            {
                if (!string.IsNullOrEmpty(key))
                    request["rkey"] = key;
            }
            else
            {
                request["rkey"] = key;
                if (!string.IsNullOrWhiteSpace(requestedExpectedRecordCid))
                    request["swapRecord"] = requestedExpectedRecordCid.Trim();
            }

            var method = mode == AtProtoWriteMode.Update
                ? "com.atproto.repo.putRecord"
                : "com.atproto.repo.createRecord";
            var url = AtProtoClient.BuildXrpcUrl(
                identity.PdsEndpoint,
                method,
                new Dictionary<string, string>());
            var response = await login.AuthenticatedClient.PostJsonAsync(
                url,
                request,
                cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoRecord>.Failure(response.Error);

            AtProtoRecord written;
            try
            {
                if (response.Value == null)
                    return Failure("The PDS write response was empty.");

                var result = JObject.Parse(response.Value.Body ?? string.Empty);
                var uri = result.Value<string>("uri");
                var cid = result.Value<string>("cid");
                if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(cid))
                    return Failure("The PDS response did not contain a record URI and CID.");
                written = new AtProtoRecord(uri, cid, jsonResult.Value);
                if (!string.Equals(written.CollectionNsid, collection, StringComparison.Ordinal)
                    || (mode == AtProtoWriteMode.Update
                        && !string.Equals(written.RecordKey, key, StringComparison.Ordinal)))
                    return Failure("The PDS response did not match the requested record.");
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("The PDS write response was not valid JSON.", exception));
            }

            LastWrittenRecord = written;
            collectionNsid = written.CollectionNsid;
            recordKey = written.RecordKey;
            expectedRecordCid = written.Cid;
            if (composer == null && jsonFile == null)
                rawJson = written.Value?.ToString(Formatting.Indented) ?? requestedJson;
            RecordWritten?.Invoke(written);
            return AtProtoResult<AtProtoRecord>.Success(written);
        }

        async Awaitable<AtProtoResult<AtProtoRecord>> UpsertJsonAsync(
            string collection,
            string key,
            string requestedJson,
            CancellationToken cancellationToken)
        {
            var identity = login.Identity;
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return Failure("The authenticated session has no repository DID.");

            Debug.Log(
                "[AtProto] Upsert lookup: " + TargetLabel(collection, key),
                this);
            var existing = await login.AuthenticatedClient.GetRecordAsync(
                identity,
                collection,
                key,
                cancellationToken);
            if (existing.Succeeded)
            {
                Debug.Log(
                    "[AtProto] Upsert found an existing record with CID "
                    + existing.Value.Cid + "; updating it.",
                    this);
                return await WriteJsonAsync(
                    AtProtoWriteMode.Update,
                    collection,
                    key,
                    existing.Value.Cid,
                    requestedJson,
                    cancellationToken);
            }
            if (!IsMissingRecord(existing.Error))
            {
                Debug.LogError(
                    "[AtProto] Upsert lookup failed: "
                    + (existing.Error?.ToString() ?? "Unknown lookup error."),
                    this);
                return AtProtoResult<AtProtoRecord>.Failure(existing.Error);
            }

            Debug.Log(
                "[AtProto] Upsert did not find the record; creating it.",
                this);
            return await WriteJsonAsync(
                AtProtoWriteMode.Create,
                collection,
                key,
                null,
                requestedJson,
                cancellationToken);
        }

        static AtProtoResult<JObject> ParseRecordValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return AtProtoResult<JObject>.Failure(
                    new AtProtoRequestError("Record JSON is required."));

            try
            {
                var token = JToken.Parse(value);
                if (!(token is JObject record))
                    return AtProtoResult<JObject>.Failure(
                        new AtProtoRequestError("A record value must be a JSON object."));
                return AtProtoResult<JObject>.Success(record);
            }
            catch (Exception exception)
            {
                return AtProtoResult<JObject>.Failure(
                    new AtProtoRequestError("Record JSON is invalid.", exception));
            }
        }

        static bool IsValidRecordKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
                return false;

            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character)
                    || character == '/' || character == '?' || character == '#')
                    return false;
            }
            return true;
        }

        static bool IsMissingRecord(AtProtoRequestError error)
        {
            return error != null
                && (error.StatusCode == 404
                    || string.Equals(
                        error.ErrorCode,
                        "RecordNotFound",
                        StringComparison.OrdinalIgnoreCase));
        }

        static AtProtoResult<AtProtoRecord> Failure(string message)
        {
            return AtProtoResult<AtProtoRecord>.Failure(new AtProtoRequestError(message));
        }
    }
}