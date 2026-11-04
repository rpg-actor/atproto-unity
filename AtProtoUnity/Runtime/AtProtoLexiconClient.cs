using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    public interface IAtProtoLexiconResolver
    {
        Awaitable<AtProtoResult<AtProtoLexiconDocument>> ResolveAsync(
            string nsid,
            CancellationToken cancellationToken = default);
    }

    public sealed class AtProtoLexiconClient : IAtProtoLexiconResolver
    {
        public const string DefaultDnsOverHttpsEndpoint = "https://cloudflare-dns.com/dns-query";
        const string SchemaCollection = "com.atproto.lexicon.schema";
        const int MaximumDocuments = 64;

        readonly AtProtoClient client;
        readonly string dnsOverHttpsEndpoint;

        public AtProtoLexiconClient(
            string identityEndpoint = AtProtoClient.DefaultIdentityEndpoint,
            string plcDirectoryEndpoint = AtProtoClient.DefaultPlcDirectoryEndpoint,
            string dnsOverHttpsEndpoint = DefaultDnsOverHttpsEndpoint)
        {
            client = new AtProtoClient(identityEndpoint, plcDirectoryEndpoint);
            if (!AtProtoClient.IsHttpsUrl(dnsOverHttpsEndpoint?.Trim()))
                throw new ArgumentException(
                    "The DNS-over-HTTPS endpoint must be an HTTPS URL.",
                    nameof(dnsOverHttpsEndpoint));
            this.dnsOverHttpsEndpoint = dnsOverHttpsEndpoint.Trim().TrimEnd('/');
        }

        public async Awaitable<AtProtoResult<AtProtoLexiconDocument>> ResolveAsync(
            string nsid,
            CancellationToken cancellationToken = default)
        {
            var normalizedNsid = nsid?.Trim();
            if (!IsValidNsid(normalizedNsid))
                return Failure("Enter a valid Lexicon NSID.");

            var authorities = new Dictionary<string, AtProtoIdentity>(StringComparer.OrdinalIgnoreCase);
            var documents = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(normalizedNsid);
            AtProtoRecord rootRecord = null;
            AtProtoIdentity rootIdentity = null;

            try
            {
                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var documentNsid = pending.Dequeue();
                    if (documents.ContainsKey(documentNsid))
                        continue;
                    if (documents.Count >= MaximumDocuments)
                        return Failure("The Lexicon exceeds the limit of " + MaximumDocuments + " referenced documents.");

                    var identity = await ResolveAuthorityAsync(documentNsid, authorities, cancellationToken);
                    if (!identity.Succeeded)
                        return AtProtoResult<AtProtoLexiconDocument>.Failure(identity.Error);
                    var record = await client.GetRecordAsync(
                        identity.Value, SchemaCollection, documentNsid, cancellationToken);
                    if (!record.Succeeded)
                        return Failure("Could not read the published schema '" + documentNsid + "': " + record.Error.Message);
                    if (!(record.Value.Value is JObject json))
                        return Failure("The published schema '" + documentNsid + "' is not an object.");
                    AtProtoLexiconDocument.ValidateDocument(documentNsid, json);
                    documents.Add(documentNsid, json);

                    if (rootRecord == null)
                    {
                        if (json["defs"]?["main"]?.Value<string>("type") != "record")
                            return Failure("'" + normalizedNsid + "' does not define a repository record.");
                        rootRecord = record.Value;
                        rootIdentity = identity.Value;
                    }

                    foreach (var reference in GetReferences(json))
                    {
                        if (reference.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        var referencedNsid = reference.Split('#')[0];
                        if (!IsValidNsid(referencedNsid))
                            return Failure("The schema contains an invalid reference: " + reference);
                        if (!documents.ContainsKey(referencedNsid))
                            pending.Enqueue(referencedNsid);
                    }
                }

                return AtProtoResult<AtProtoLexiconDocument>.Success(
                    new AtProtoLexiconDocument(
                        documents[normalizedNsid],
                        rootIdentity.Did,
                        rootIdentity.PdsEndpoint,
                        rootRecord.Cid,
                        documents));
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                return Failure(exception.Message);
            }
        }

        public async Awaitable<AtProtoResult<IReadOnlyList<string>>> SearchAsync(
            string prefix,
            CancellationToken cancellationToken = default)
        {
            var normalizedPrefix = prefix?.Trim();
            var isRootNamespacePrefix = IsRootNamespacePrefix(normalizedPrefix);
            if (isRootNamespacePrefix && !normalizedPrefix.EndsWith(".", StringComparison.Ordinal))
                normalizedPrefix += ".";
            var probeNsid = normalizedPrefix?.EndsWith(".", StringComparison.Ordinal) == true
                ? normalizedPrefix + "record"
                : normalizedPrefix;
            if (!IsValidNsid(probeNsid))
                return AtProtoResult<IReadOnlyList<string>>.Failure(
                    new AtProtoRequestError("Enter an NSID or a namespace such as app.bsky or app.bsky.feed."));

            try
            {
                var identity = await ResolveAuthorityAsync(
                    probeNsid,
                    new Dictionary<string, AtProtoIdentity>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken);
                if (!identity.Succeeded)
                    return AtProtoResult<IReadOnlyList<string>>.Failure(identity.Error);

                var matches = new List<string>();
                var authority = probeNsid.Substring(0, probeNsid.LastIndexOf('.') + 1);
                string cursor = null;
                for (var pageIndex = 0; pageIndex < 20; pageIndex++)
                {
                    var page = await client.ListRecordsAsync(
                        identity.Value, SchemaCollection, 100, cursor, cancellationToken);
                    if (!page.Succeeded)
                        return AtProtoResult<IReadOnlyList<string>>.Failure(page.Error);
                    foreach (var record in page.Value.Records)
                    {
                        var id = record.Value?.Value<string>("id");
                        if (IsValidNsid(id)
                            && string.Equals(id, record.RecordKey, StringComparison.Ordinal)
                            && id.StartsWith(authority, StringComparison.Ordinal)
                            && (isRootNamespacePrefix || GetAuthorityDomain(id) == GetAuthorityDomain(probeNsid))
                            && id.StartsWith(normalizedPrefix, StringComparison.Ordinal)
                            && record.Value?["defs"]?["main"]?.Value<string>("type") == "record")
                            matches.Add(id);
                    }
                    if (!page.Value.HasNextPage)
                    {
                        matches.Sort(StringComparer.Ordinal);
                        return AtProtoResult<IReadOnlyList<string>>.Success(matches);
                    }
                    cursor = page.Value.Cursor;
                }
                return AtProtoResult<IReadOnlyList<string>>.Failure(
                    new AtProtoRequestError("The schema repository exceeded the search limit. Enter the complete NSID."));
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                return AtProtoResult<IReadOnlyList<string>>.Failure(new AtProtoRequestError(exception.Message));
            }
        }

        public static bool IsNamespaceSearchPrefix(string prefix)
        {
            var normalizedPrefix = prefix?.Trim();
            if (string.IsNullOrEmpty(normalizedPrefix))
                return false;
            var probeNsid = IsRootNamespacePrefix(normalizedPrefix)
                ? normalizedPrefix.EndsWith(".", StringComparison.Ordinal) ? normalizedPrefix + "record" : normalizedPrefix + ".record"
                : normalizedPrefix.EndsWith(".", StringComparison.Ordinal) ? normalizedPrefix + "record" : null;
            return IsValidNsid(probeNsid);
        }

        static bool IsRootNamespacePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return false;
            var rootPrefix = prefix.EndsWith(".", StringComparison.Ordinal)
                ? prefix.Substring(0, prefix.Length - 1)
                : prefix;
            var firstDot = rootPrefix.IndexOf('.');
            return firstDot > 0 && firstDot == rootPrefix.LastIndexOf('.');
        }

        public static bool IsValidNsid(string nsid)
        {
            if (string.IsNullOrEmpty(nsid) || nsid.Length > 317)
                return false;
            var parts = nsid.Split('.');
            if (parts.Length < 3 || !IsAsciiLetter(nsid[0]) || nsid.LastIndexOf('.') > 253)
                return false;
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (part.Length == 0 || part.Length > 63 || part[0] == '-' || part[part.Length - 1] == '-')
                    return false;
                var isName = index == parts.Length - 1;
                if (isName && !IsAsciiLetter(part[0]))
                    return false;
                if (part.Any(character => !IsAsciiLetter(character)
                    && !(character >= '0' && character <= '9') && (isName || character != '-')))
                    return false;
            }
            return true;
        }

        public static string GetAuthorityDomain(string nsid)
        {
            if (!IsValidNsid(nsid))
                throw new ArgumentException("A valid NSID is required.", nameof(nsid));
            var parts = nsid.Split('.');
            var domainParts = new List<string>(parts.Length - 1);
            for (var index = parts.Length - 2; index >= 0; index--)
                domainParts.Add(parts[index]);
            return string.Join(".", domainParts).ToLowerInvariant();
        }

        async Awaitable<AtProtoResult<AtProtoIdentity>> ResolveAuthorityAsync(
            string nsid,
            Dictionary<string, AtProtoIdentity> authorities,
            CancellationToken cancellationToken)
        {
            var domain = GetAuthorityDomain(nsid);
            if (authorities.TryGetValue(domain, out var cachedIdentity))
                return AtProtoResult<AtProtoIdentity>.Success(cachedIdentity);

            var separator = dnsOverHttpsEndpoint.IndexOf('?') >= 0 ? '&' : '?';
            var url = dnsOverHttpsEndpoint + separator
                + "name=" + Uri.EscapeDataString("_lexicon." + domain) + "&type=TXT";
            var response = await client.SendAsync(
                "GET", url, headers: new Dictionary<string, string> { ["Accept"] = "application/dns-json" },
                cancellationToken: cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoIdentity>.Failure(response.Error);
            var did = ReadAuthorityDid(AtProtoLexiconDocument.ReadObject(response.Value.Body));
            if (did == null)
                return AtProtoResult<AtProtoIdentity>.Failure(
                    new AtProtoRequestError("No Lexicon authority is published at _lexicon." + domain + "."));

            var identity = await client.ResolveHandleAsync(did, cancellationToken);
            if (identity.Succeeded)
                authorities.Add(domain, identity.Value);
            return identity;
        }

        public static string ReadAuthorityDid(JObject response)
        {
            if (response?.Value<int?>("Status") != 0)
                throw new FormatException("The Lexicon DNS query failed (DNS status " + response?["Status"] + ").");
            var answers = response?["Answer"] as JArray;
            if (answers == null)
                return null;

            string authorityDid = null;
            foreach (var answer in answers)
            {
                if (answer.Value<int?>("type") != 16)
                    continue;
                var data = answer?["data"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(data))
                    continue;

                data = data.Trim();
                if (data.StartsWith("\"", StringComparison.Ordinal))
                {
                    using (var reader = new JsonTextReader(new StringReader(data)) { SupportMultipleContent = true })
                    {
                        var segments = new List<string>();
                        while (reader.Read())
                        {
                            if (reader.TokenType != JsonToken.String)
                                throw new FormatException("Invalid Lexicon TXT record.");
                            segments.Add((string)reader.Value);
                        }
                        data = string.Concat(segments);
                    }
                }
                if (!data.StartsWith("did=", StringComparison.Ordinal))
                    continue;

                var did = data.Substring("did=".Length);
                if (!did.StartsWith("did:", StringComparison.Ordinal) || did.Any(char.IsWhiteSpace))
                    throw new FormatException("Invalid Lexicon authority DID.");
                if (authorityDid != null && authorityDid != did)
                    throw new FormatException("The Lexicon authority has conflicting DNS TXT records.");
                authorityDid = did;
            }
            return authorityDid;
        }

        static IEnumerable<string> GetReferences(JObject json)
        {
            foreach (var property in json.Descendants().OfType<JProperty>())
            {
                if (property.Name == "ref" && property.Value.Type == JTokenType.String)
                    yield return property.Value.Value<string>();
                if (property.Name == "refs" && property.Value is JArray references)
                {
                    foreach (var reference in references.Values<string>())
                        yield return reference;
                }
            }
        }

        static bool IsAsciiLetter(char character)
        {
            return character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z';
        }

        static AtProtoResult<AtProtoLexiconDocument> Failure(string message)
        {
            return AtProtoResult<AtProtoLexiconDocument>.Failure(
                new AtProtoRequestError(message));
        }
    }
}