using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtProtoUnity
{
    [Serializable]
    public sealed class AtProtoBlobPermission
    {
        [SerializeField] bool upload;
        [SerializeField] List<string> acceptedMimeTypes = new() { "image/*" };

        public bool Upload
        {
            get => upload;
            set => upload = value;
        }

        public IReadOnlyList<string> AcceptedMimeTypes => acceptedMimeTypes;

        public string ToScope()
        {
            if (!Upload)
                return null;

            var accepted = new List<string>();
            if (acceptedMimeTypes != null)
            {
                foreach (var value in acceptedMimeTypes)
                {
                    var mimeType = value?.Trim();
                    if (IsValidMimeType(mimeType) && !accepted.Contains(mimeType))
                        accepted.Add(mimeType);
                }
            }

            if (accepted.Count == 0)
                return null;
            if (accepted.Count == 1)
                return "blob:" + accepted[0];
            return "blob?accept=" + string.Join("&accept=", accepted);
        }

        public static bool IsValidMimeType(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '?', '&', '=' }) < 0
                && value.IndexOf('/') > 0;
        }
    }

    [Serializable]
    public sealed class AtProtoRecordPermission
    {
        [SerializeField] string collection;
        [SerializeField] bool create;
        [SerializeField] bool update;
        [SerializeField] bool delete;

        public string Collection
        {
            get => collection;
            set => collection = value;
        }

        public bool Create
        {
            get => create;
            set => create = value;
        }

        public bool Update
        {
            get => update;
            set => update = value;
        }

        public bool Delete
        {
            get => delete;
            set => delete = value;
        }

        public string ToScope()
        {
            var normalizedCollection = Collection?.Trim();
            if (!IsValidCollection(normalizedCollection))
                return null;

            var actions = new List<string>();
            if (Create) actions.Add("create");
            if (Update) actions.Add("update");
            if (Delete) actions.Add("delete");
            if (actions.Count == 0)
                return null;

            return "repo:" + normalizedCollection
                + "?action=" + string.Join("&action=", actions);
        }

        public static bool IsValidCollection(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 253)
                return false;

            var parts = value.Split('.');
            if (parts.Length < 3)
                return false;

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 63
                    || part[0] == '-' || part[part.Length - 1] == '-')
                    return false;

                foreach (var character in part)
                {
                    if (!(character >= 'a' && character <= 'z')
                        && !(character >= 'A' && character <= 'Z')
                        && !(character >= '0' && character <= '9')
                        && character != '-')
                        return false;
                }
            }
            return true;
        }
    }

    public sealed class AtProtoOAuthScopeDefinition
    {
        public string Token { get; }
        public string Label { get; }

        public AtProtoOAuthScopeDefinition(string token, string label)
        {
            Token = token;
            Label = label;
        }
    }

    public static class AtProtoOAuthScopes
    {
        public const string AtProto = "atproto";

        static readonly AtProtoOAuthScopeDefinition[] knownRecordTypes =
        {
            new AtProtoOAuthScopeDefinition("actor.rpg.generator", "RPG actor generator"),
            new AtProtoOAuthScopeDefinition("actor.rpg.sprite", "RPG actor sprite"),
            new AtProtoOAuthScopeDefinition("equipment.rpg.item", "RPG equipment item"),
            new AtProtoOAuthScopeDefinition("app.bsky.actor.profile", "Profile"),
            new AtProtoOAuthScopeDefinition("app.bsky.feed.post", "Posts"),
            new AtProtoOAuthScopeDefinition("app.bsky.feed.like", "Likes"),
            new AtProtoOAuthScopeDefinition("app.bsky.feed.repost", "Reposts"),
            new AtProtoOAuthScopeDefinition("app.bsky.graph.follow", "Follows"),
            new AtProtoOAuthScopeDefinition("app.bsky.graph.block", "Blocks"),
            new AtProtoOAuthScopeDefinition("app.bsky.graph.list", "Lists"),
            new AtProtoOAuthScopeDefinition("app.bsky.graph.listitem", "List members"),
        };

        public static IReadOnlyList<AtProtoOAuthScopeDefinition> KnownRecordTypes => knownRecordTypes;

        public static string BuildScope(
            IReadOnlyList<AtProtoRecordPermission> recordPermissions,
            AtProtoBlobPermission blobPermission)
        {
            var tokens = new List<string> { AtProto };
            var blobScope = blobPermission?.ToScope();
            if (blobScope != null)
                tokens.Add(blobScope);

            // Rows for the same collection are combined into one repo scope.
            var merged = new List<AtProtoRecordPermission>();
            if (recordPermissions != null)
            {
                foreach (var permission in recordPermissions)
                {
                    var collection = permission?.Collection?.Trim();
                    if (!AtProtoRecordPermission.IsValidCollection(collection))
                        continue;

                    var target = merged.Find(item => item.Collection == collection);
                    if (target == null)
                    {
                        target = new AtProtoRecordPermission { Collection = collection };
                        merged.Add(target);
                    }
                    target.Create |= permission.Create;
                    target.Update |= permission.Update;
                    target.Delete |= permission.Delete;
                }
            }

            foreach (var permission in merged)
            {
                var scope = permission.ToScope();
                if (scope != null)
                    tokens.Add(scope);
            }
            return string.Join(" ", tokens);
        }

        public static bool AllowsRecordAction(
            AtProtoLoginMethod loginMethod,
            string grantedScope,
            string collection,
            string action)
        {
            if (loginMethod != AtProtoLoginMethod.OAuth)
                return true;
            if (string.IsNullOrWhiteSpace(grantedScope)
                || string.IsNullOrWhiteSpace(collection)
                || string.IsNullOrWhiteSpace(action))
                return false;

            foreach (var rawToken in grantedScope.Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (RepoScopeAllows(
                    Uri.UnescapeDataString(rawToken),
                    collection.Trim(),
                    action.Trim()))
                    return true;
            }
            return false;
        }

        // Accepts repo:<nsid>, repo:*, and repo?collection=<nsid>&action=<action>; no action means every action.
        static bool RepoScopeAllows(string token, string collection, string action)
        {
            var collections = new List<string>();
            var actions = new List<string>();
            string query;
            if (token.StartsWith("repo:", StringComparison.Ordinal))
            {
                var rest = token.Substring("repo:".Length);
                var queryStart = rest.IndexOf('?');
                collections.Add(queryStart < 0 ? rest : rest.Substring(0, queryStart));
                query = queryStart < 0 ? null : rest.Substring(queryStart + 1);
            }
            else if (token.StartsWith("repo?", StringComparison.Ordinal))
            {
                query = token.Substring("repo?".Length);
            }
            else
            {
                return false;
            }

            if (query != null)
            {
                foreach (var pair in query.Split('&'))
                {
                    var separator = pair.IndexOf('=');
                    if (separator < 0)
                        continue;
                    var key = pair.Substring(0, separator);
                    var value = pair.Substring(separator + 1);
                    if (key == "collection")
                        collections.Add(value);
                    else if (key == "action")
                        actions.Add(value);
                }
            }

            if (!collections.Exists(item => item == "*" || item == collection))
                return false;
            return actions.Count == 0 || actions.Contains(action);
        }
    }
}
