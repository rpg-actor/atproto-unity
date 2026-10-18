using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    // Authenticated DPoP requests built on the same transport as AtProtoClient.
    public sealed class AtProtoAuthenticatedClient
    {
        readonly AtProtoClient transport;
        readonly Func<CancellationToken, Awaitable<AtProtoResult<AtProtoAuthSession>>> refresh;
        readonly TimeSpan refreshSkew;
        AtProtoDpopKeyPair keyPair;
        string dpopNonce;
        AtProtoAuthSession session;

        internal AtProtoAuthenticatedClient(
            AtProtoClient transport,
            AtProtoAuthSession session,
            AtProtoDpopKeyPair keyPair,
            TimeSpan refreshSkew,
            Func<CancellationToken, Awaitable<AtProtoResult<AtProtoAuthSession>>> refresh)
        {
            this.transport = transport;
            this.session = session;
            this.keyPair = keyPair;
            this.refreshSkew = refreshSkew;
            this.refresh = refresh;
        }

        public AtProtoAuthSession Session => session;

        public async Awaitable<AtProtoResult<AtProtoHttpResponse>> SendAsync(
            string method,
            string url,
            byte[] body = null,
            string contentType = null,
            CancellationToken cancellationToken = default)
        {
            if (session == null
                || (session.LoginMethod == AtProtoLoginMethod.OAuth && keyPair == null))
                return AtProtoResult<AtProtoHttpResponse>.Failure(
                    new AtProtoRequestError("An authenticated AT Protocol session is required."));

            var refreshed = await RefreshIfNeededAsync(cancellationToken);
            if (!refreshed.Succeeded)
                return AtProtoResult<AtProtoHttpResponse>.Failure(refreshed.Error);

            var result = await SendSignedAsync(
                method,
                url,
                body,
                contentType,
                cancellationToken);
            if (result.Succeeded || result.Error.StatusCode != 401 || refresh == null)
                return result;

            var refreshResult = await refresh(cancellationToken);
            if (!refreshResult.Succeeded)
                return AtProtoResult<AtProtoHttpResponse>.Failure(refreshResult.Error);

            UpdateSession(refreshResult.Value);
            return await SendSignedAsync(
                method,
                url,
                body,
                contentType,
                cancellationToken);
        }

        public async Awaitable<AtProtoResult<T>> GetJsonAsync<T>(
            string url,
            CancellationToken cancellationToken = default)
        {
            var response = await SendAsync("GET", url, null, null, cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<T>.Failure(response.Error);

            try
            {
                return AtProtoResult<T>.Success(
                    JsonConvert.DeserializeObject<T>(response.Value.Body));
            }
            catch (Exception exception)
            {
                return AtProtoResult<T>.Failure(
                    new AtProtoRequestError("The JSON response was not valid.", exception));
            }
        }

        public async Awaitable<AtProtoResult<AtProtoHttpResponse>> PostJsonAsync(
            string url,
            object body,
            CancellationToken cancellationToken = default)
        {
            return await SendAsync(
                "POST",
                url,
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)),
                "application/json",
                cancellationToken);
        }

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

            var url = AtProtoClient.BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.listRecords",
                query);
            var response = await SendAsync("GET", url, null, null, cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoRecordPage>.Failure(response.Error);

            RecordListResponse listResponse;
            try
            {
                listResponse = JsonConvert.DeserializeObject<RecordListResponse>(response.Value.Body);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoRecordPage>.Failure(
                    new AtProtoRequestError("The record response was not valid JSON.", exception));
            }

            var records = new List<AtProtoRecord>();
            if (listResponse?.Records != null)
            {
                foreach (var record in listResponse.Records)
                {
                    if (record != null)
                        records.Add(new AtProtoRecord(record.Uri, record.Cid, record.Value));
                }
            }

            return AtProtoResult<AtProtoRecordPage>.Success(
                new AtProtoRecordPage(
                    response.Value.Body,
                    collectionNsid.Trim(),
                    listResponse?.Cursor,
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

            var url = AtProtoClient.BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.getRecord",
                new Dictionary<string, string>
                {
                    ["repo"] = identity.Did,
                    ["collection"] = collectionNsid.Trim(),
                    ["rkey"] = recordKey.Trim()
                });
            var response = await GetJsonAsync<GetRecordResponse>(url, cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoRecord>.Failure(response.Error);
            if (response.Value == null || string.IsNullOrEmpty(response.Value.Uri))
                return AtProtoResult<AtProtoRecord>.Failure(
                    new AtProtoRequestError("The record response did not contain a URI."));

            return AtProtoResult<AtProtoRecord>.Success(
                new AtProtoRecord(response.Value.Uri, response.Value.Cid, response.Value.Value));
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

        public async Awaitable<AtProtoResult<AtProtoRepositoryDescription>> DescribeRepoAsync(
            AtProtoIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));

            var url = AtProtoClient.BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.repo.describeRepo",
                new Dictionary<string, string> { ["repo"] = identity.Did });
            var response = await GetJsonAsync<DescribeRepoResponse>(url, cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(response.Error);
            if (response.Value == null || string.IsNullOrEmpty(response.Value.Did))
                return AtProtoResult<AtProtoRepositoryDescription>.Failure(
                    new AtProtoRequestError("The repository description did not contain a DID."));

            return AtProtoResult<AtProtoRepositoryDescription>.Success(
                new AtProtoRepositoryDescription(
                    response.Value.Did,
                    response.Value.Handle,
                    response.Value.Collections ?? Array.Empty<string>()));
        }

        public async Awaitable<AtProtoResult<byte[]>> GetBlobBytesAsync(
            AtProtoIdentity identity,
            string cid,
            CancellationToken cancellationToken = default)
        {
            if (identity == null || string.IsNullOrEmpty(identity.Did))
                return AtProtoResult<byte[]>.Failure(
                    new AtProtoRequestError("A resolved identity is required."));
            if (string.IsNullOrWhiteSpace(cid))
                return AtProtoResult<byte[]>.Failure(
                    new AtProtoRequestError("A blob CID is required."));

            var url = AtProtoClient.BuildXrpcUrl(
                identity.PdsEndpoint,
                "com.atproto.sync.getBlob",
                new Dictionary<string, string>
                {
                    ["did"] = identity.Did,
                    ["cid"] = cid.Trim()
                });
            var response = await SendAsync("GET", url, null, null, cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<byte[]>.Failure(response.Error);
            return AtProtoResult<byte[]>.Success(response.Value.BodyBytes);
        }

        public async Awaitable<AtProtoResult<Texture2D>> GetBlobAsync(
            AtProtoIdentity identity,
            string cid,
            CancellationToken cancellationToken = default)
        {
            var result = await GetBlobBytesAsync(identity, cid, cancellationToken);
            if (!result.Succeeded)
                return AtProtoResult<Texture2D>.Failure(result.Error);

            var texture = new Texture2D(2, 2);
            if (!texture.LoadImage(result.Value))
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(texture);
                else
                    UnityEngine.Object.DestroyImmediate(texture);
                return AtProtoResult<Texture2D>.Failure(
                    new AtProtoRequestError("The blob response was not a readable image."));
            }
            return AtProtoResult<Texture2D>.Success(texture);
        }

        internal void UpdateSession(AtProtoAuthSession value)
        {
            session = value;
        }

        async Awaitable<AtProtoResult<bool>> RefreshIfNeededAsync(
            CancellationToken cancellationToken)
        {
            if (session == null || !session.IsAccessTokenExpired(refreshSkew) || refresh == null)
                return AtProtoResult<bool>.Success(true);

            var result = await refresh(cancellationToken);
            if (!result.Succeeded)
                return AtProtoResult<bool>.Failure(result.Error);
            UpdateSession(result.Value);
            return AtProtoResult<bool>.Success(true);
        }

        async Awaitable<AtProtoResult<AtProtoHttpResponse>> SendSignedAsync(
            string method,
            string url,
            byte[] body,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (session.LoginMethod != AtProtoLoginMethod.OAuth)
            {
                return await transport.SendAsync(
                    method,
                    url,
                    body,
                    contentType,
                    new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer " + session.AccessToken
                    },
                    cancellationToken);
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "DPoP " + session.AccessToken,
                    ["DPoP"] = AtProtoOAuthCrypto.CreateDpopProof(
                        keyPair,
                        method,
                        url,
                        dpopNonce,
                        session.AccessToken)
                };
                var result = await transport.SendAsync(
                    method,
                    url,
                    body,
                    contentType,
                    headers,
                    cancellationToken);
                var nonce = result.Succeeded
                    ? result.Value.GetHeader("dpop-nonce")
                    : result.Error.GetResponseHeader("dpop-nonce");
                if (!string.IsNullOrEmpty(nonce))
                    dpopNonce = nonce;

                if (result.Succeeded || string.IsNullOrEmpty(nonce) || attempt > 0)
                    return result;
            }

            throw new InvalidOperationException("The authenticated request retry loop did not complete.");
        }

        sealed class RecordListResponse
        {
            [JsonProperty("cursor")] public string Cursor { get; set; }
            [JsonProperty("records")] public RecordSummary[] Records { get; set; }
        }

        sealed class RecordSummary
        {
            [JsonProperty("uri")] public string Uri { get; set; }
            [JsonProperty("cid")] public string Cid { get; set; }
            [JsonProperty("value")] public JToken Value { get; set; }
        }

        sealed class GetRecordResponse
        {
            [JsonProperty("uri")] public string Uri { get; set; }
            [JsonProperty("cid")] public string Cid { get; set; }
            [JsonProperty("value")] public JToken Value { get; set; }
        }

        sealed class DescribeRepoResponse
        {
            [JsonProperty("did")] public string Did { get; set; }
            [JsonProperty("handle")] public string Handle { get; set; }
            [JsonProperty("collections")] public string[] Collections { get; set; }
        }
    }
}