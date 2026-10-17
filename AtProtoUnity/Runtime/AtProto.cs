using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AtProtoUnity
{
    public static class AtProto
    {
        public static string AppView = "https://public.api.bsky.app";
        public static string PlcDirectory = "https://plc.directory";
        public static int TimeoutSeconds = 30;

        public static async Awaitable<string> ResolveHandle(string handle)
        {
            handle = handle.Trim().TrimStart('@');
            if (handle.StartsWith("did:")) return handle;
            var response = await GetJson<ResolveHandleResponse>($"{AppView}/xrpc/com.atproto.identity.resolveHandle?handle={Escape(handle)}");
            return response.Did;
        }

        public static Awaitable<DidDocument> GetDidDocument(string did) =>
            GetJson<DidDocument>(did.StartsWith("did:web:")
                ? $"https://{Uri.UnescapeDataString(did.Substring(8))}/.well-known/did.json"
                : $"{PlcDirectory}/{did}");

        public static async Awaitable<string> ResolvePds(string did)
        {
            var document = await GetDidDocument(did);
            foreach (var service in document.Service ?? Array.Empty<DidService>())
                if (service.Id == "#atproto_pds" || service.Id == $"{did}#atproto_pds")
                    return service.ServiceEndpoint.TrimEnd('/');
            throw new Exception($"{did} has no PDS.");
        }

        public static async Awaitable<string> ResolveDidHandle(string did)
        {
            var document = await GetDidDocument(did);
            foreach (var alias in document.AlsoKnownAs ?? Array.Empty<string>())
                if (alias.StartsWith("at://"))
                    return alias.Substring(5);
            return did;
        }

        public static async Awaitable<T> GetRecord<T>(string pds, string did, string collection, string rkey)
        {
            var response = await GetJson<Record<T>>($"{pds}/xrpc/com.atproto.repo.getRecord?repo={Escape(did)}&collection={Escape(collection)}&rkey={Escape(rkey)}");
            return response.Value;
        }

        public static async Awaitable<List<T>> ListRecords<T>(string pds, string did, string collection)
        {
            var values = new List<T>();
            string cursor = null;
            do
            {
                var url = $"{pds}/xrpc/com.atproto.repo.listRecords?repo={Escape(did)}&collection={Escape(collection)}&limit=100";
                if (cursor != null) url += $"&cursor={Escape(cursor)}";
                var page = await GetJson<RecordPage<T>>(url);
                foreach (var record in page.Records ?? Array.Empty<Record<T>>())
                    values.Add(record.Value);
                cursor = page.Cursor;
            } while (cursor != null);
            return values;
        }

        public static async Awaitable<List<Profile>> GetFollows(string actor, int maxPages = 10)
        {
            var follows = new List<Profile>();
            string cursor = null;
            for (var page = 0; page < maxPages; page++)
            {
                var url = $"{AppView}/xrpc/app.bsky.graph.getFollows?actor={Escape(actor)}&limit=100";
                if (cursor != null) url += $"&cursor={Escape(cursor)}";
                var response = await GetJson<FollowsPage>(url);
                follows.AddRange(response.Follows ?? Array.Empty<Profile>());
                cursor = response.Cursor;
                if (cursor == null) break;
            }
            return follows;
        }

        public static Awaitable<byte[]> GetBlob(string pds, string did, Blob blob) =>
            GetBytes($"{pds}/xrpc/com.atproto.sync.getBlob?did={Escape(did)}&cid={Escape(blob.Ref.Cid)}");

        public static async Awaitable<T> GetJson<T>(string url, string bearer = null)
        {
            var body = Encoding.UTF8.GetString(await GetBytes(url, bearer));
            try
            {
                return JsonConvert.DeserializeObject<T>(body);
            }
            catch (JsonReaderException)
            {
                throw new Exception($"{new Uri(url).Host} didn't return JSON: {Shorten(body)}");
            }
        }

        public static Awaitable<byte[]> GetBytes(string url, string bearer = null) =>
            Send(UnityWebRequest.Get(url), bearer);

        public static Awaitable<byte[]> PostJson(string url, object body, string bearer = null) =>
            Send(UnityWebRequest.Post(url, JsonConvert.SerializeObject(body), "application/json"), bearer);

        public static string Escape(string value) => Uri.EscapeDataString(value);

        private static async Awaitable<byte[]> Send(UnityWebRequest request, string bearer)
        {
            using (request)
            {
                request.timeout = TimeoutSeconds;
                if (bearer != null)
                    request.SetRequestHeader("Authorization", $"Bearer {bearer}");
                await request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                    throw new Exception($"{request.error}: {ErrorMessage(request.downloadHandler.text)}");
                return request.downloadHandler.data;
            }
        }

        private static string ErrorMessage(string body)
        {
            try
            {
                var json = JObject.Parse(body);
                return (string)json["message"] ?? (string)json["error"] ?? body;
            }
            catch (JsonException)
            {
                return Shorten(body);
            }
        }

        private static string Shorten(string text) => text.Length > 200 ? text.Substring(0, 200) + "..." : text;

        private class ResolveHandleResponse
        {
            public string Did;
        }

        private class Record<T>
        {
            public T Value;
        }

        private class RecordPage<T>
        {
            public Record<T>[] Records;
            public string Cursor;
        }

        private class FollowsPage
        {
            public Profile[] Follows;
            public string Cursor;
        }
    }

    public class Profile
    {
        public string Did;
        public string Handle;
        public string DisplayName;
    }

    public class DidDocument
    {
        public string[] AlsoKnownAs;
        public DidService[] Service;
    }

    public class DidService
    {
        public string Id;
        public string ServiceEndpoint;
    }

    public class Blob
    {
        public Link Ref;
        public string MimeType;
        public int Size;
    }

    public class Link
    {
        [JsonProperty("$link")] public string Cid;
    }
}
