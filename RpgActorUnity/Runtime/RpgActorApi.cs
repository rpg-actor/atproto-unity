using System;
using System.Collections.Generic;
using AtProtoUnity;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RpgActorUnity
{
    public static class RpgActorApi
    {
        public static string Url = "https://rpg.actor/api";
        public static AtProtoClient Client { get; } = new AtProtoClient { TimeoutSeconds = 30 };

        public static Awaitable<byte[]> GetNormalizedSheet(string did) =>
            Require(Client.GetBytesAsync($"{Url}/sprite/normalized?did={Uri.EscapeDataString(did)}"));

        public static async Awaitable<AtProtoIdentity> ResolveHandle(string handle) =>
            await Require(Client.ResolveHandleAsync(handle));

        public static async Awaitable<ActorEntry[]> GetActors()
        {
            var response = await Require(Client.GetJsonAsync<ActorsResponse>($"{Url}/actors/full"));
            return response.Actors ?? Array.Empty<ActorEntry>();
        }

        public static async Awaitable<List<EquipmentItem>> GetEquipment(string did)
        {
            var identity = await ResolveDid(did);
            return await Require(Client.ListAllRecordsAsync<EquipmentItem>(identity, "equipment.rpg.item"));
        }

        public static async Awaitable<JArray> GetGeneratorLayers(string did)
        {
            var identity = await ResolveDid(did);
            var generator = await Require(Client.GetRecordAsync<JObject>(identity, "actor.rpg.generator", "self"));
            return generator?["layers"] as JArray ?? throw new Exception($"{did} has no generator layers.");
        }

        public static async Awaitable<Dictionary<string, List<string>>> GetGeneratorAssets()
        {
            var manifest = await Require(Client.GetJsonAsync<JObject>($"{Url}/generator/manifest"));
            var assets = new Dictionary<string, List<string>>();
            AddAssets(manifest, assets);
            return assets;
        }

        public static Awaitable<CreatorItemList> GetCreatorItems(string apiKey) =>
            Require(Client.GetJsonAsync<CreatorItemList>($"{Url}/creator/items", apiKey));

        public static Awaitable<byte[]> Compose(string did, JArray layers = null, Dictionary<string, string> layerOverrides = null)
        {
            var body = new JObject { ["did"] = did };
            if (layers != null) body["layers"] = layers;
            if (layerOverrides is { Count: > 0 }) body["layerOverrides"] = JObject.FromObject(layerOverrides);
            return Require(Client.PostJsonAsync($"{Url}/generator/compose", body));
        }

        public static Awaitable<List<AtProtoProfile>> GetFollows(string actor, int maxPages = 10) =>
            Require(Client.GetFollowsAsync(actor, maxPages));

        private static async Awaitable<AtProtoIdentity> ResolveDid(string did)
        {
            var pds = await Require(Client.ResolvePdsAsync(did));
            return new AtProtoIdentity(null, did.Trim(), pds);
        }

        internal static async Awaitable<T> Require<T>(Awaitable<AtProtoResult<T>> request)
        {
            var result = await request;
            if (!result.Succeeded)
                throw new Exception(result.Error?.ToString() ?? "The AT Protocol request failed.");
            return result.Value;
        }

        private static void AddAssets(JObject group, Dictionary<string, List<string>> assets)
        {
            foreach (var (key, value) in group)
            {
                if (key == "newlyAdded") continue;
                if (value is JObject child)
                {
                    AddAssets(child, assets);
                }
                else if (value is JArray list && list.Count > 0)
                {
                    var layer = key == "hands" ? "clothHands" : key;
                    assets[layer] = new List<string>();
                    foreach (var asset in list)
                        if (asset is JObject entry && entry["name"] != null)
                            assets[layer].Add((string)entry["name"]);
                }
            }
        }

        private class ActorsResponse
        {
            public ActorEntry[] Actors;
        }
    }

    public class ActorEntry
    {
        public string Did;
        public string Handle;
        public string DisplayName;
        public int FollowersCount;
        public JObject Sprite;
        public GeneratorLayerEntry[] GeneratorLayers;
    }

    public class GeneratorLayerEntry
    {
        public string Id;
        public string AssetName;
    }

    public class EquipmentItem
    {
        public string Item;
        public string Title;
        public string Kind;
        public string Category;
        public string Description;
        public string Provider;
        public AtProtoBlob Icon;
    }

    public class CreatorItemList
    {
        public string Provider;
        public int Count;
        public CreatorItem[] Items;
    }

    public class CreatorItem
    {
        public string Item;
        public string Title;
        public string Kind;
        public string Category;
    }
}
