using RpgActorUnity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class HandlePrompt : MonoBehaviour
{
    private const string LastHandleKey = "Demos.LastHandle";

    public RpgActor Player;
    public GameObject Panel;
    public TMP_InputField Input;
    public Button StartButton;
    public TMP_Text Message;

    public bool Done { get; private set; }

    private void Start()
    {
        Input.text = PlayerPrefs.GetString(LastHandleKey, "");
        Input.onSubmit.AddListener(_ => Submit());
        StartButton.onClick.AddListener(Submit);
        Message.text = "";
        Input.ActivateInputField();
    }

    private void Update()
    {
        if (!Done || Player.Status != RpgActorStatus.Failed) return;
        Done = false;
        Panel.SetActive(true);
        Message.text = Player.Error;
        Input.ActivateInputField();
    }

    private void Submit()
    {
        var handle = Input.text.Trim().TrimStart('@');
        if (handle.Length == 0) return;
        PlayerPrefs.SetString(LastHandleKey, handle);
        Player.Handle = handle;
        Panel.SetActive(false);
        Done = true;
    }
}
