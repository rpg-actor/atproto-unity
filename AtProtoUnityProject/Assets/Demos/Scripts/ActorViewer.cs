using System;
using System.Linq;
using RpgActorUnity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

public class ActorViewer : MonoBehaviour
{
    public RpgActor Actor;
    public TMP_InputField HandleInput;
    public Button RandomButton;
    public Button[] DirectionButtons;
    public Toggle WalkToggle;
    public TMP_Text Status;

    private static ActorEntry[] _registry;
    private string _notice;

    private void Start()
    {
        HandleInput.onSubmit.AddListener(text => Load(text.Trim().TrimStart('@')));
        RandomButton.onClick.AddListener(() => _ = PickRandom());
        for (var i = 0; i < DirectionButtons.Length; i++)
        {
            var direction = (RpgActorDirection)i;
            DirectionButtons[i].onClick.AddListener(() => Actor.Direction = direction);
        }
        WalkToggle.SetIsOnWithoutNotify(Actor.IsWalking);
        WalkToggle.onValueChanged.AddListener(walking => Actor.IsWalking = walking);
    }

    private void Update()
    {
        if (!HandleInput.isFocused && HandleInput.text != Actor.Handle)
            HandleInput.text = Actor.Handle;

        Status.text = _notice ?? Actor.Status switch
        {
            RpgActorStatus.Loading => "Loading...",
            RpgActorStatus.Ready => $"@{Actor.Handle}",
            RpgActorStatus.Failed => Actor.Error,
            _ => "Enter a handle or press Random.",
        };
    }

    private void Load(string handle)
    {
        _notice = null;
        Actor.Handle = handle;
        Actor.Reload();
    }

    private async Awaitable PickRandom()
    {
        _notice = "Finding someone...";
        RandomButton.interactable = false;
        try
        {
            _registry ??= await RpgActorApi.GetActors();
            var actors = _registry.Where(actor => actor.Sprite != null && actor.Handle != null).ToList();
            if (actors.Count == 0)
                _notice = "No actors found.";
            else
                Load(actors[Random.Range(0, actors.Count)].Handle);
        }
        catch (Exception e)
        {
            _notice = $"rpg.actor is unavailable right now. ({e.Message})";
        }
        finally
        {
            RandomButton.interactable = true;
        }
    }
}
