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

        public static Awaitable<byte[]> GetNormalizedSheet(string did) =>
            AtProto.GetBytes($"{Url}/sprite/normalized?did={AtProto.Escape(did)}");

        public static async Awaitable<ActorEntry[]> GetActors()
        {
            var response = await AtProto.GetJson<ActorsResponse>($"{Url}/actors/full");
            return response.Actors ?? Array.Empty<ActorEntry>();
        }

        public static async Awaitable<List<EquipmentItem>> GetEquipment(string did)
        {
            var pds = await AtProto.ResolvePds(did);
            return await AtProto.ListRecords<EquipmentItem>(pds, did, "equipment.rpg.item");
        }

        public static async Awaitable<JArray> GetGeneratorLayers(string did)
        {
            var pds = await AtProto.ResolvePds(did);
            var generator = await AtProto.GetRecord<JObject>(pds, did, "actor.rpg.generator", "self");
            return generator?["layers"] as JArray ?? throw new Exception($"{did} has no generator layers.");
        }

        public static async Awaitable<Dictionary<string, List<string>>> GetGeneratorAssets()
        {
            var manifest = await AtProto.GetJson<JObject>($"{Url}/generator/manifest");
            var assets = new Dictionary<string, List<string>>();
            AddAssets(manifest, assets);
            return assets;
        }

        public static Awaitable<CreatorItemList> GetCreatorItems(string apiKey) =>
            AtProto.GetJson<CreatorItemList>($"{Url}/creator/items", apiKey);

        public static Awaitable<byte[]> Compose(string did, JArray layers = null, Dictionary<string, string> layerOverrides = null)
        {
            var body = new JObject { ["did"] = did };
            if (layers != null) body["layers"] = layers;
            if (layerOverrides is { Count: > 0 }) body["layerOverrides"] = JObject.FromObject(layerOverrides);
            return AtProto.PostJson($"{Url}/generator/compose", body);
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
        public Blob Icon;
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
