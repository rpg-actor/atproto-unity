using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AtProtoUnity
{
    // Basic (read-only) AT Protocol requests from Handle / DID resolution
    public sealed class AtProtoClient
    {
        // Default resolution service using BSKY plc offerings
        public const string DefaultIdentityEndpoint = "https://public.api.bsky.app";
        public const string DefaultPlcDirectoryEndpoint = "https://plc.directory";

        public string IdentityEndpoint { get; }
        public string PlcDirectoryEndpoint { get; }
        public int TimeoutSeconds { get; set; } = 15;

        public AtProtoClient(
            string identityEndpoint = DefaultIdentityEndpoint,
            string plcDirectoryEndpoint = DefaultPlcDirectoryEndpoint)
        {
            IdentityEndpoint = TrimEndpoint(identityEndpoint, nameof(identityEndpoint));
            PlcDirectoryEndpoint = TrimEndpoint(plcDirectoryEndpoint, nameof(plcDirectoryEndpoint));
        }

        // Resolve a handle to its DID, then follow the DID document to the account's PDS
        public async Awaitable<AtProtoResult<AtProtoIdentity>> ResolveHandleAsync(
            string handle,
            CancellationToken cancellationToken = default)
        {
            var normalizedHandle = NormalizeHandle(handle);
            if (string.IsNullOrEmpty(normalizedHandle))
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("A handle is required."));

            var resolveUrl = BuildXrpcUrl(
                IdentityEndpoint,
                "com.atproto.identity.resolveHandle",
                new Dictionary<string, string> { ["handle"] = normalizedHandle });
            var resolveResponse = await GetAsync(resolveUrl, cancellationToken);

            if (!resolveResponse.Succeeded)
                return AtProtoResult<AtProtoIdentity>.Failure(resolveResponse.Error);

            ResolveHandleResponse resolved;
            try
            {
                resolved = JsonConvert.DeserializeObject<ResolveHandleResponse>(resolveResponse.Value);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("The handle response was not valid JSON.", exception));
            }

            if (resolved == null || string.IsNullOrEmpty(resolved.did))
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("The handle response did not contain a DID."));

            string didDocumentUrl;
            try
            {
                didDocumentUrl = BuildDidDocumentUrl(resolved.did);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError(exception.Message, exception));
            }

            var didDocumentResponse = await GetAsync(didDocumentUrl, cancellationToken);
            if (!didDocumentResponse.Succeeded)
                return AtProtoResult<AtProtoIdentity>.Failure(didDocumentResponse.Error);

            DidDocument didDocument;
            try
            {
                didDocument = JsonConvert.DeserializeObject<DidDocument>(didDocumentResponse.Value);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("The DID document was not valid JSON.", exception));
            }

            var pdsEndpoint = FindPdsEndpoint(didDocument);
            if (string.IsNullOrEmpty(pdsEndpoint))
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("The DID document did not contain an atproto PDS service."));

            return AtProtoResult<AtProtoIdentity>.Success(
                new AtProtoIdentity(normalizedHandle, resolved.did, pdsEndpoint));
        }

        // List a bounded page; pass the returned cursor to fetch the following page.
        public async Awaitable<AtProtoResult<AtProtoRecordPage>> ListRecordsAsync(
            AtProtoIdentity identity,
            string collectionNsid,
            int limit,
            string cursor,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<AtProtoRecordPage>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));

            if (string.IsNullOrWhiteSpace(collectionNsid))
                return AtProtoResult<AtProtoRecordPage>.Failure(
                    new AtProtoRequestError("A record collection NSID is required."));

            var query = new Dictionary<string, string>
            {
                ["repo"] = identity.Did,
                ["collection"] = collectionNsid.Trim(),
                ["limit"] = Math.Max(1, Math.Min(100, limit)).ToString()
            };
            if (!string.IsNullOrWhiteSpace(cursor))
                query["cursor"] = cursor.Trim();

            var url = BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.listRecords",
                query);
            var response = await GetAsync(url, cancellationToken);

            if (!response.Succeeded)
                return AtProtoResult<AtProtoRecordPage>.Failure(response.Error);

            RecordListResponse listResponse;
            try
            {
                listResponse = JsonConvert.DeserializeObject<RecordListResponse>(response.Value);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoRecordPage>.Failure(
                    new AtProtoRequestError("The record response was not valid JSON.", exception));
            }

            var records = new List<AtProtoRecord>();
            if (listResponse?.records != null)
            {
                foreach (var record in listResponse.records)
                {
                    if (record != null)
                        records.Add(new AtProtoRecord(
                            record.uri,
                            record.cid,
                            record.value));
                }
            }

            return AtProtoResult<AtProtoRecordPage>.Success(
                new AtProtoRecordPage(
                    response.Value,
                    collectionNsid.Trim(),
                    listResponse?.cursor,
                    records));
        }

        public async Awaitable<AtProtoResult<AtProtoRecord>> GetRecordAsync(
            AtProtoIdentity identity,
            string collectionNsid,
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));

            if (string.IsNullOrWhiteSpace(collectionNsid))
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("A record collection NSID is required."));

            if (string.IsNullOrWhiteSpace(recordKey))
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("A record key is required."));

            var url = BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.getRecord",
                new Dictionary<string, string>
                {
                    ["repo"] = identity.Did,
                    ["collection"] = collectionNsid.Trim(),
                    ["rkey"] = recordKey.Trim()
                });
            var response = await GetAsync(url, cancellationToken);

            if (!response.Succeeded)
                return AtProtoResult<AtProtoRecord>.Failure(response.Error);

            GetRecordResponse recordResponse;
            try
            {
                recordResponse = JsonConvert.DeserializeObject<GetRecordResponse>(response.Value);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("The record response was not valid JSON.", exception));
            }

            if (recordResponse == null || string.IsNullOrEmpty(recordResponse.uri))
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("The record response did not contain a URI."));

            return AtProtoResult<AtProtoRecord>.Success(
                new AtProtoRecord(
                    recordResponse.uri,
                    recordResponse.cid,
                    recordResponse.value));
        }

        public async Awaitable<AtProtoResult<T>> GetRecordAsync<T>(
            AtProtoIdentity identity,
            string collectionNsid,
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            var result = await GetRecordAsync(
                identity,
                collectionNsid,
                recordKey,
                cancellationToken);
            if (!result.Succeeded)
                return AtProtoResult<T>.Failure(result.Error);

            try
            {
                return AtProtoResult<T>.Success(result.Value.ToObject<T>());
            }
            catch (Exception exception)
            {
                return AtProtoResult<T>.Failure(
                    new AtProtoRequestError("The record value could not be deserialized.", exception));
            }
        }

        // Ask the account PDS which record collections exist in its repository
        public async Awaitable<AtProtoResult<AtProtoRepositoryDescription>> DescribeRepoAsync(
            AtProtoIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));

            var url = BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.describeRepo",
                new Dictionary<string, string> { ["repo"] = identity.Did });
            var response = await GetAsync(url, cancellationToken);

            if (!response.Succeeded)
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(response.Error);

            DescribeRepoResponse description;
            try
            {
                description = JsonConvert.DeserializeObject<DescribeRepoResponse>(response.Value);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(
                    new AtProtoRequestError("The repository description was not valid JSON.", exception));
            }

            if (description == null || string.IsNullOrEmpty(description.did))
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(
                    new AtProtoRequestError("The repository description did not contain a DID."));

            return AtProtoResult<AtProtoRepositoryDescription>.Success(
                new AtProtoRepositoryDescription(
                    description.did,
                    description.handle,
                    description.collections ?? Array.Empty<string>()));
        }

        // The caller owns the returned texture and must destroy it.
        public async Awaitable<AtProtoResult<Texture2D>> GetBlobAsync(
            AtProtoIdentity identity,
            string cid,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<Texture2D>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));

            if (string.IsNullOrWhiteSpace(cid))
                return AtProtoResult<Texture2D>.Failure(
                    new AtProtoRequestError("A blob CID is required."));

            var url = BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.sync.getBlob",
                new Dictionary<string, string>
                {
                    ["did"] = identity.Did,
                    ["cid"] = cid.Trim()
                });

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                await SendRequestAsync(request, cancellationToken);

                if (request.result != UnityWebRequest.Result.Success)
                    return AtProtoResult<Texture2D>.Failure(CreateError(request, url));

                var texture = new Texture2D(2, 2);
                if (!texture.LoadImage(request.downloadHandler.data))
                {
                    DestroyTexture(texture);
                    return AtProtoResult<Texture2D>.Failure(
                        new AtProtoRequestError("The blob response was not a readable image."));
                }

                return AtProtoResult<Texture2D>.Success(texture);
            }
        }

        async Awaitable<AtProtoResult<string>> GetAsync(
            string url,
            CancellationToken cancellationToken)
        {
            // One retry covers brief DNS/socket failures; HTTP error responses are not retried.
            for (var attempt = 0; ; attempt++)
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = TimeoutSeconds;
                    await SendRequestAsync(request, cancellationToken);

                    if (request.result == UnityWebRequest.Result.Success)
                        return AtProtoResult<string>.Success(request.downloadHandler.text);

                    if (request.result != UnityWebRequest.Result.ConnectionError || attempt > 0)
                        return AtProtoResult<string>.Failure(CreateError(request, url));
                }

                await Awaitable.WaitForSecondsAsync(0.5f, cancellationToken);
            }
        }

        static AtProtoRequestError CreateError(UnityWebRequest request, string url)
        {
            var message = request.error ?? "The AT Protocol request failed.";
            if (request.result == UnityWebRequest.Result.ConnectionError
                && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                message += " (" + uri.Host + ")";

            return new AtProtoRequestError(
                message,
                request.responseCode,
                request.downloadHandler?.text);
        }

        static async Awaitable SendRequestAsync(
            UnityWebRequest request,
            CancellationToken cancellationToken)
        {
            // Cancelling the awaitable alone would not stop the transfer, so abort the request too.
            using (cancellationToken.Register(request.Abort))
                await Awaitable.FromAsyncOperation(request.SendWebRequest(), cancellationToken);
        }

        static void DestroyTexture(Texture2D texture)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
        }

        static string FindPdsEndpoint(DidDocument document)
        {
            if (document?.service == null)
                return null;

            foreach (var service in document.service)
            {
                if (service == null)
                    continue;

                var isPds = string.Equals(service.id, "#atproto_pds", StringComparison.Ordinal)
                    || string.Equals(service.type, "AtprotoPersonalDataServer", StringComparison.Ordinal);
                if (isPds && Uri.TryCreate(service.serviceEndpoint, UriKind.Absolute, out var endpoint)
                    && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps))
                    return endpoint.ToString().TrimEnd('/');
            }

            return null;
        }

        // did:web documents are fetched from the DID's own host, so they are only as trustworthy as that host.
        string BuildDidDocumentUrl(string did)
        {
            if (did.StartsWith("did:plc:", StringComparison.OrdinalIgnoreCase))
                return PlcDirectoryEndpoint + "/" + Uri.EscapeDataString(did);

            if (did.StartsWith("did:web:", StringComparison.OrdinalIgnoreCase))
            {
                var methodSpecificId = did.Substring("did:web:".Length);
                var segments = methodSpecificId.Split(':');
                if (segments.Length == 0 || string.IsNullOrEmpty(segments[0]))
                    throw new InvalidOperationException("The did:web identifier has no hostname.");

                var url = "https://" + Uri.UnescapeDataString(segments[0]);
                if (segments.Length == 1)
                    return url + "/.well-known/did.json";

                for (var i = 1; i < segments.Length; i++)
                    url += "/" + Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
                return url + "/did.json";
            }

            throw new InvalidOperationException("Unsupported DID method. Only did:plc and did:web are supported.");
        }

        // Build the standard /xrpc/<method>?key=value URL used by AT Protocol APIs.
        static string BuildXrpcUrl(string endpoint, string method, IReadOnlyDictionary<string, string> query)
        {
            var builder = new StringBuilder(endpoint.TrimEnd('/'));
            builder.Append("/xrpc/").Append(method);

            var first = true;
            foreach (var pair in query)
            {
                builder.Append(first ? '?' : '&');
                first = false;
                builder.Append(Uri.EscapeDataString(pair.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }

            return builder.ToString();
        }

        // Accept the common @handle form while sending the normalized handle to the server.
        static string NormalizeHandle(string handle)
        {
            return string.IsNullOrWhiteSpace(handle)
                ? string.Empty
                : handle.Trim().TrimStart('@').ToLowerInvariant();
        }

        static string TrimEndpoint(string endpoint, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("An endpoint is required.", parameterName);
            return endpoint.Trim().TrimEnd('/');
        }

        // Response shapes for Newtonsoft; only the fields this client reads.
        class ResolveHandleResponse
        {
            public string did;
        }

        class DidDocument
        {
            public DidService[] service;
        }

        class DidService
        {
            public string id;
            public string type;
            public string serviceEndpoint;
        }

        class RecordListResponse
        {
            public string cursor;
            public RecordSummary[] records;
        }

        class GetRecordResponse
        {
            public string uri;
            public string cid;
            public JToken value;
        }

        class DescribeRepoResponse
        {
            public string did;
            public string handle;
            public string[] collections;
        }

        class RecordSummary
        {
            public string uri;
            public string cid;
            public JToken value;
        }
    }

    // The stable identity information discovered from a handle.
    public sealed class AtProtoIdentity
    {
        public string Handle { get; }
        public string Did { get; }
        public string PdsEndpoint { get; }

        public AtProtoIdentity(string handle, string did, string pdsEndpoint)
        {
            Handle = handle;
            Did = did;
            PdsEndpoint = pdsEndpoint;
        }
    }

    // The collection names advertised by one repository.
    public sealed class AtProtoRepositoryDescription
    {
        public string Did { get; }
        public string Handle { get; }
        public IReadOnlyList<string> Collections { get; }

        public AtProtoRepositoryDescription(
            string did,
            string handle,
            IReadOnlyList<string> collections)
        {
            Did = did;
            Handle = handle;
            Collections = collections;
        }
    }

    // One bounded collection page plus the cursor for the following page.
    public sealed class AtProtoRecordPage
    {
        public string RawJson { get; }
        public string CollectionNsid { get; }
        public string Cursor { get; }
        public IReadOnlyList<AtProtoRecord> Records { get; }
        public bool HasNextPage => !string.IsNullOrWhiteSpace(Cursor);

        public AtProtoRecordPage(
            string rawJson,
            string collectionNsid,
            string cursor,
            IReadOnlyList<AtProtoRecord> records)
        {
            RawJson = rawJson;
            CollectionNsid = collectionNsid;
            Cursor = cursor;
            Records = records;
        }
    }

    public sealed class AtProtoRecord
    {
        public string Uri { get; }
        public string Cid { get; }
        public JToken Value { get; }
        public string CollectionNsid => GetUriSegment(Uri, false);
        public string RecordKey => GetUriSegment(Uri, true);

        public AtProtoRecord(string uri, string cid, JToken value)
        {
            Uri = uri;
            Cid = cid;
            Value = value;
        }

        public T ToObject<T>()
        {
            if (Value == null)
                throw new InvalidOperationException("The record has no value.");
            return Value.ToObject<T>();
        }

        static string GetUriSegment(string uri, bool last)
        {
            if (string.IsNullOrEmpty(uri))
                return null;

            var pathStart = uri.IndexOf("at://", StringComparison.OrdinalIgnoreCase) == 0
                ? "at://".Length
                : 0;
            var firstSlash = uri.IndexOf('/', pathStart);
            if (firstSlash < 0)
                return null;

            var lastSlash = uri.LastIndexOf('/');
            if (last)
                return lastSlash > firstSlash && lastSlash < uri.Length - 1
                    ? uri.Substring(lastSlash + 1)
                    : null;

            return lastSlash > firstSlash
                ? uri.Substring(firstSlash + 1, lastSlash - firstSlash - 1)
                : null;
        }
    }

    // A request failure with enough detail for logs and the Inspector.
    public sealed class AtProtoRequestError
    {
        public string Message { get; }
        public long StatusCode { get; }
        public string ResponseBody { get; }

        public AtProtoRequestError(string message, Exception exception = null)
            : this(exception == null ? message : message + " " + exception.Message, 0, null)
        {
        }

        public AtProtoRequestError(string message, long statusCode, string responseBody)
        {
            Message = message;
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        public override string ToString()
        {
            var status = StatusCode > 0 ? " (HTTP " + StatusCode + ")" : string.Empty;
            return Message + status;
        }
    }

    // Holds either a value or an error, never both.
    public sealed class AtProtoResult<T>
    {
        public T Value { get; }
        public AtProtoRequestError Error { get; }
        public bool Succeeded => Error == null;

        private AtProtoResult(T value, AtProtoRequestError error)
        {
            Value = value;
            Error = error;
        }

        public static AtProtoResult<T> Success(T value)
        {
            return new AtProtoResult<T>(value, null);
        }

        public static AtProtoResult<T> Failure(AtProtoRequestError error)
        {
            return new AtProtoResult<T>(default, error);
        }
    }
}