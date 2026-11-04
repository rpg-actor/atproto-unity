using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    [AddComponentMenu("AT Protocol/Record Composer (NSID)")]
    public class AtProtoSchemaComposer : MonoBehaviour, IAtProtoRecordJsonSource
    {
        [SerializeField] string nsid = string.Empty;
        [SerializeField] string identityEndpoint = AtProtoClient.DefaultIdentityEndpoint;
        [SerializeField] string plcDirectoryEndpoint = AtProtoClient.DefaultPlcDirectoryEndpoint;
        [SerializeField] string dnsOverHttpsEndpoint = AtProtoLexiconClient.DefaultDnsOverHttpsEndpoint;
        [SerializeField, HideInInspector] string schemaNsid;
        [SerializeField, HideInInspector] string schemaSourceDid;
        [SerializeField, HideInInspector] string schemaSourcePdsEndpoint;
        [SerializeField, HideInInspector] string schemaRecordCid;
        [SerializeField, HideInInspector] string schemaJson;
        [SerializeField, HideInInspector] string dependenciesJson = "{}";
        [SerializeField, HideInInspector] string recordJson;
        [SerializeField, HideInInspector] List<AtProtoSchemaRecordField> fields = new();

        AtProtoLexiconDocument document;
        string cachedSchemaJson;
        string cachedDependenciesJson;
        int resolutionVersion;

        public string Nsid
        {
            get => nsid;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                if (nsid == normalized)
                    return;
                nsid = normalized;
                resolutionVersion++;
            }
        }

        public string IdentityEndpoint { get => identityEndpoint; set => identityEndpoint = value; }
        public string PlcDirectoryEndpoint { get => plcDirectoryEndpoint; set => plcDirectoryEndpoint = value; }
        public string DnsOverHttpsEndpoint { get => dnsOverHttpsEndpoint; set => dnsOverHttpsEndpoint = value; }
        public string SchemaNsid => schemaNsid;
        public string SchemaSourceDid => schemaSourceDid;
        public string SchemaSourcePdsEndpoint => schemaSourcePdsEndpoint;
        public string SchemaRecordCid => schemaRecordCid;
        public string SchemaRecordKey => HasSchema ? Document.RecordKey : null;
        public string SchemaDescription => HasSchema ? Document.Description : null;
        public string SchemaJson => schemaJson;
        public string LastSchemaError { get; private set; }
        public bool HasSchema => !string.IsNullOrWhiteSpace(schemaJson)
            && string.Equals(schemaNsid, nsid?.Trim(), StringComparison.Ordinal);

        public AtProtoLexiconDocument Document
        {
            get
            {
                if (!HasSchema)
                    throw new InvalidOperationException("A resolved schema is required for this NSID.");
                if (document == null || cachedSchemaJson != schemaJson || cachedDependenciesJson != dependenciesJson)
                {
                    var dependencies = AtProtoLexiconDocument.ReadObject(dependenciesJson ?? "{}");
                    document = new AtProtoLexiconDocument(
                        AtProtoLexiconDocument.ReadObject(schemaJson),
                        schemaSourceDid, schemaSourcePdsEndpoint, schemaRecordCid,
                        dependencies.Properties().ToDictionary(property => property.Name, property => (JObject)property.Value));
                    cachedSchemaJson = schemaJson;
                    cachedDependenciesJson = dependenciesJson;
                }
                return document;
            }
        }

        public AtProtoLexiconClient CreateLexiconClient()
        {
            return new AtProtoLexiconClient(identityEndpoint, plcDirectoryEndpoint, dnsOverHttpsEndpoint);
        }

        public Awaitable<AtProtoResult<AtProtoLexiconDocument>> ResolveSchemaAsync(
            CancellationToken cancellationToken = default)
        {
            return ResolveSchemaAsync(CreateLexiconClient(), cancellationToken);
        }

        public async Awaitable<AtProtoResult<AtProtoLexiconDocument>> ResolveSchemaAsync(
            IAtProtoLexiconResolver resolver,
            CancellationToken cancellationToken = default)
        {
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));
            var requestedNsid = nsid?.Trim();
            var requestVersion = ++resolutionVersion;
            LastSchemaError = null;
            try
            {
                var result = await resolver.ResolveAsync(requestedNsid, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (requestVersion != resolutionVersion || !string.Equals(nsid?.Trim(), requestedNsid, StringComparison.Ordinal))
                    return AtProtoResult<AtProtoLexiconDocument>.Failure(new AtProtoRequestError("The NSID changed while the schema was loading."));
                if (result.Succeeded)
                    LoadSchema(result.Value);
                else
                    LastSchemaError = result.Error.Message;
                return result;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                if (requestVersion == resolutionVersion)
                    LastSchemaError = exception.Message;
                return AtProtoResult<AtProtoLexiconDocument>.Failure(new AtProtoRequestError(exception.Message));
            }
        }

        public void LoadSchema(AtProtoLexiconDocument resolvedDocument)
        {
            if (resolvedDocument == null)
                throw new ArgumentNullException(nameof(resolvedDocument));
            var preserveValues = string.IsNullOrEmpty(schemaNsid) || schemaNsid == resolvedDocument.Nsid;
            var draft = resolvedDocument.CreateRecord(preserveValues ? ReadDraft() : null);
            nsid = resolvedDocument.Nsid;
            schemaNsid = resolvedDocument.Nsid;
            schemaSourceDid = resolvedDocument.SourceDid;
            schemaSourcePdsEndpoint = resolvedDocument.SourcePdsEndpoint;
            schemaRecordCid = resolvedDocument.RecordCid;
            schemaJson = resolvedDocument.Json.ToString(Formatting.None);
            dependenciesJson = resolvedDocument.ExportDependencies().ToString(Formatting.None);
            recordJson = draft.ToString(Formatting.None);
            fields.Clear();
            document = null;
            LastSchemaError = null;
        }

        public JObject GetRecordValue()
        {
            var draft = ReadDraft();
            if (HasSchema)
                draft["$type"] = schemaNsid;
            return draft;
        }

        public void SetRecordValue(JObject value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            recordJson = value.ToString(Formatting.None);
        }

        public void SetValue(string path, object value)
        {
            var draft = GetRecordValue();
            SetPath(draft, path, value is JToken token ? token.DeepClone() : value == null ? JValue.CreateNull() : JToken.FromObject(value));
            SetRecordValue(draft);
        }

        public IReadOnlyList<string> GetValidationMessages()
        {
            return Document.ValidateRecord(GetRecordValue());
        }

        public JObject BuildValue()
        {
            var draft = GetRecordValue();
            var errors = Document.ValidateRecord(draft);
            if (errors.Count > 0)
                throw new FormatException(string.Join("\n", errors));
            return draft;
        }

        public string BuildRecordJson()
        {
            return BuildValue().ToString(Formatting.None);
        }

        JObject ReadDraft()
        {
            if (!string.IsNullOrWhiteSpace(recordJson))
                return AtProtoLexiconDocument.ReadObject(recordJson);
            var result = new JObject();
            foreach (var field in fields ?? Enumerable.Empty<AtProtoSchemaRecordField>())
            {
                if (field == null || string.IsNullOrEmpty(field.Path) || !field.Required && !field.Included)
                    continue;
                var type = field.FieldType == AtProtoSchemaFieldType.Boolean ? AtProtoRecordValueType.Boolean
                    : field.FieldType == AtProtoSchemaFieldType.Integer ? AtProtoRecordValueType.Integer
                    : field.FieldType == AtProtoSchemaFieldType.JsonObject ? AtProtoRecordValueType.JsonObject
                    : field.FieldType == AtProtoSchemaFieldType.JsonArray ? AtProtoRecordValueType.JsonArray
                    : field.FieldType == AtProtoSchemaFieldType.Json ? AtProtoRecordValueType.Json
                    : AtProtoRecordValueType.String;
                SetPath(result, field.Path, AtProtoRecordValueConverter.ToJson(field.Value ?? string.Empty, type, field.Path));
            }
            return result;
        }

        static void SetPath(JObject root, string path, JToken value)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A field path is required.", nameof(path));
            var parts = path.Split('.');
            if (parts.Any(string.IsNullOrWhiteSpace) || parts[0] == "$type")
                throw new ArgumentException("The field path is invalid or reserved.", nameof(path));
            var current = root;
            foreach (var part in parts.Take(parts.Length - 1))
            {
                if (current[part] == null)
                    current[part] = new JObject();
                if (!(current[part] is JObject child))
                    throw new ArgumentException("The field path crosses a non-object value.", nameof(path));
                current = child;
            }
            current[parts[parts.Length - 1]] = value;
        }
    }
}