using System;
using System.Collections.Generic;
using System.Linq;
using AtProtoUnity;
using RpgActorUnity;
using UnityEngine;

public class Cast : MonoBehaviour
{
    public HandlePrompt Prompt;
    public RpgActor[] Extras;

    private static ActorEntry[] _registry;
    private readonly Queue<string> _candidates = new();
    private string _castFor;

    private void Update()
    {
        if (Prompt.Done && _castFor != Prompt.Player.Handle)
        {
            _castFor = Prompt.Player.Handle;
            _ = FindCandidates(Prompt.Player);
        }

        foreach (var extra in Extras)
            if (_candidates.Count > 0 && (extra.Status == RpgActorStatus.Failed || string.IsNullOrEmpty(extra.Handle)))
                extra.Handle = _candidates.Dequeue();
    }

    private async Awaitable FindCandidates(RpgActor player)
    {
        _candidates.Clear();
        foreach (var extra in Extras)
            extra.Handle = "";

        try
        {
            await player.Ready();
            var follows = await AtProto.GetFollows(player.Did);
            var followed = new HashSet<string>(follows.Select(profile => profile.Did));

            try
            {
                _registry ??= await RpgActorApi.GetActors();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"rpg.actor is unavailable, casting from follows only: {e.Message}");
            }

            var handles = _registry == null
                ? follows.Select(profile => profile.Handle)
                : _registry
                    .Where(actor => actor.Sprite != null && actor.Handle != null && actor.Did != player.Did)
                    .OrderByDescending(actor => followed.Contains(actor.Did))
                    .ThenByDescending(actor => actor.FollowersCount)
                    .Select(actor => actor.Handle);

            foreach (var handle in handles)
                _candidates.Enqueue(handle);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Couldn't cast extras: {e.Message}");
        }
    }
}
