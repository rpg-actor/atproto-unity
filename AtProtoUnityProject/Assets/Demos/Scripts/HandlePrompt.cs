using System;
using AtProtoUnity;
using RpgActorUnity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class HandlePrompt : MonoBehaviour
{
    private const string LastHandleKey = "Demos.LastHandle";
    private const string ActionLabel = "Login";

    public RpgActor Player;
    public GameObject Panel;
    public TMP_InputField Input;
    public TMP_InputField SecretInput;
    public Button StartButton;
    public TMP_Text Message;
    public TMP_Text Title;
    public TMP_Text Identity;
    public bool RequireLogin;
    public bool UseLoginComponentMethod = true;
    public AtProtoLoginMethod LoginMethod = AtProtoLoginMethod.OAuth;
    public AtProtoLogin Login;

    public bool Done { get; private set; }

    private TMP_Text _buttonLabel;
    private string _defaultButtonLabel;
    private Color _messageColor;
    private bool _busy;

    private void Start()
    {
        if (!Login)
            Login = GetComponent<AtProtoLogin>();
        if (Login && UseLoginComponentMethod)
            LoginMethod = Login.LoginMethod;

        ConfigureInputs();
        Input.text = PlayerPrefs.GetString(LastHandleKey, "");
        Input.onValueChanged.AddListener(OnHandleChanged);
        Input.onSubmit.AddListener(_ =>
        {
            if (UsesSecret && SecretInput)
                SecretInput.ActivateInputField();
            else
                Submit();
        });
        if (SecretInput && UsesSecret)
            SecretInput.onSubmit.AddListener(_ => Submit());
        StartButton.onClick.AddListener(Submit);
        _buttonLabel = StartButton.GetComponentInChildren<TMP_Text>();
        _defaultButtonLabel = _buttonLabel ? _buttonLabel.text : null;
        _messageColor = Message.color;
        RefreshIdentity(Input.text, false);
        SetStatus(GetReadyMessage());

        if (RequireLogin)
        {
            if (!Login)
            {
                SetStatus("LOGIN UNAVAILABLE\nNo login component is assigned.", true);
                StartButton.interactable = false;
            }
            else
            {
                Login.StateChanged += OnLoginStateChanged;
                Login.SessionChanged += OnSessionChanged;
                if (Login.Session != null && Login.Session.LoginMethod == LoginMethod)
                {
                    Input.text = Login.Session.Handle;
                    RefreshIdentity(Login.Session.Handle, true);
                    SetStatus("ACCOUNT CONFIRMED");
                }
                else
                    SetStatus(GetReadyMessage());
                SetButtonLabel(ActionLabel);
            }
        }

        Input.ActivateInputField();
    }

    private void Submit()
    {
        if (_busy) return;

        var handle = Input.text.Trim().TrimStart('@');
        if (RequireLogin && handle.Length == 0 && Login?.Session != null)
            handle = Login.Session.Handle?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(handle))
        {
            SetStatus("HANDLE REQUIRED\nEnter one to continue.", true);
            FocusLoginInput();
            return;
        }
        _ = SubmitAsync(handle, SecretInput ? SecretInput.text : null);
    }

    private async Awaitable SubmitAsync(string requestedHandle, string secret)
    {
        _busy = true;
        Done = false;
        StartButton.interactable = false;
        var loginPhase = RequireLogin;

        try
        {
            var actorHandle = requestedHandle;
            if (RequireLogin)
            {
                var sameAccount = Login != null && Login.Session != null
                    && Login.Session.LoginMethod == LoginMethod
                    && string.Equals(
                        Login.Session.Handle?.TrimStart('@'),
                        requestedHandle,
                        StringComparison.OrdinalIgnoreCase);
                if (!sameAccount && UsesSecret && string.IsNullOrWhiteSpace(secret))
                    throw new Exception("PASSWORD REQUIRED\nEnter your password.");

                SetStatus(GetProgressMessage(sameAccount));
                SetButtonLabel(GetProgressLabel(sameAccount));
                var loginResult = sameAccount
                    ? AtProtoResult<AtProtoAuthSession>.Success(Login.Session)
                    : await Login.LoginAsync(LoginMethod, requestedHandle, secret);
                if (!loginResult.Succeeded)
                    throw new Exception(loginResult.Error?.GetFriendlyMessage(GetCredentialLabel())
                        ?? "The login could not be completed. Try again.");

                actorHandle = loginResult.Value?.Handle;
                if (string.IsNullOrWhiteSpace(actorHandle))
                    throw new Exception("Login did not return an account handle.");
                loginPhase = false;
            }

            PlayerPrefs.SetString(LastHandleKey, actorHandle);
            Player.Handle = actorHandle;
            Player.Reload();
            SetStatus("LOADING ACTOR\nPreparing your character...");
            SetButtonLabel("Loading...");
            Panel.SetActive(false);
            await Player.Ready();
            if (!this || Player.Handle != actorHandle) return;

            Done = true;
            RefreshIdentity(actorHandle, true);
            SetStatus("ACCOUNT CONFIRMED");
        }
        catch (OperationCanceledException)
        {
            if (!this) return;
            Done = false;
            Panel.SetActive(true);
            SetStatus(
                loginPhase
                    ? "AUTHORIZATION CANCELLED\nStart again when you are ready."
                    : "LOADING CANCELLED",
                true);
            SetButtonLabel(RequireLogin ? ActionLabel : _defaultButtonLabel);
            StartButton.interactable = true;
            FocusLoginInput();
        }
        catch (Exception e)
        {
            if (!this) return;
            Done = false;
            Panel.SetActive(true);
            SetStatus(
                (loginPhase ? "NOT AUTHORIZED" : "ACTOR LOAD FAILED")
                + "\n"
                + e.Message,
                true);
            SetButtonLabel(RequireLogin ? ActionLabel : _defaultButtonLabel);
            StartButton.interactable = true;
            FocusLoginInput();
        }
        finally
        {
            if (SecretInput && UsesSecret)
                SecretInput.text = "";
            _busy = false;
        }
    }

    private void OnLoginStateChanged(AtProtoLoginState state)
    {
        if (!RequireLogin || !this || _busy) return;

        if (state == AtProtoLoginState.Authenticated
            && Login.Session != null
            && Login.Session.LoginMethod == LoginMethod)
        {
            Input.text = Login.Session.Handle;
            RefreshIdentity(Login.Session.Handle, true);
            SetStatus("ACCOUNT CONFIRMED");
        }
        else if (state == AtProtoLoginState.Failed && Login.LastError != null)
        {
            SetStatus(
                "NOT AUTHORIZED\n" + Login.LastError.GetFriendlyMessage(GetCredentialLabel()),
                true);
        }
    }

    private void OnSessionChanged(AtProtoAuthSession session)
    {
        if (session == null || !this || session.LoginMethod != LoginMethod) return;
        Input.text = session.Handle;
        RefreshIdentity(session.Handle, true);
    }

    private void SetButtonLabel(string text)
    {
        if (_buttonLabel)
            _buttonLabel.text = text;
    }

    private bool UsesSecret => LoginMethod != AtProtoLoginMethod.OAuth;

    private void ConfigureInputs()
    {
        if (!Title)
        {
            var titleTransform = Panel ? Panel.transform.Find("Card/Title") : null;
            if (titleTransform)
                Title = titleTransform.GetComponent<TMP_Text>();
        }

        if (Title)
            Title.text = RequireLogin ? "Login with your Atmosphere Account" : "Choose an account";

        if (!Identity && Title)
        {
            var identityObject = Instantiate(Title.gameObject, Title.transform.parent);
            identityObject.name = "Identity";
            identityObject.transform.SetSiblingIndex(1);
            Identity = identityObject.GetComponent<TMP_Text>();
            Identity.fontSize = 20f;
            Identity.fontStyle = FontStyles.Normal;
            Identity.color = new Color(0.64f, 0.7f, 0.8f, 1f);
            Identity.alignment = TMPro.TextAlignmentOptions.Left;
            Identity.enableAutoSizing = false;
            Identity.textWrappingMode = TextWrappingModes.NoWrap;
            Identity.overflowMode = TextOverflowModes.Overflow;
            Identity.lineSpacing = 4f;
            Identity.raycastTarget = false;
            var identityLayout = identityObject.GetComponent<LayoutElement>();
            if (identityLayout)
            {
                identityLayout.minHeight = 64f;
                identityLayout.preferredHeight = 64f;
            }
        }

        if (Identity)
            Identity.gameObject.SetActive(true);

        if (!SecretInput && RequireLogin && UsesSecret && Input)
        {
            var secretObject = Instantiate(Input.gameObject, Input.transform.parent);
            secretObject.name = "Secret";
            secretObject.transform.SetSiblingIndex(Input.transform.GetSiblingIndex() + 1);
            SecretInput = secretObject.GetComponent<TMP_InputField>();
        }

        if (!SecretInput)
            return;

        SecretInput.gameObject.SetActive(RequireLogin && UsesSecret);
        SecretInput.contentType = TMP_InputField.ContentType.Password;
        SecretInput.text = "";
        SetPlaceholder(SecretInput, "password");

        var cardTransform = Panel ? Panel.transform.Find("Card") : null;
        var cardRect = cardTransform ? cardTransform.GetComponent<RectTransform>() : null;
        if (cardRect)
            cardRect.sizeDelta = new Vector2(
                Mathf.Max(cardRect.sizeDelta.x, 640f),
                RequireLogin && UsesSecret ? 500f : 420f);
    }

    private string GetReadyMessage()
    {
        return LoginMethod == AtProtoLoginMethod.OAuth
            ? "READY\nYour browser will open to confirm this account."
            : "READY\nEnter your handle and password to continue.";
    }

    private string GetProgressMessage(bool sameAccount)
    {
        if (sameAccount)
            return "LOADING ACTOR\nPreparing your character...";
        return LoginMethod == AtProtoLoginMethod.OAuth
            ? "OPENING AUTHORIZATION\nContinue in your browser."
            : "VERIFYING CREDENTIAL\nContacting the account service...";
    }

    private string GetProgressLabel(bool sameAccount)
    {
        return sameAccount ? "Loading..." : "Working...";
    }

    private string GetCredentialLabel()
    {
        return LoginMethod == AtProtoLoginMethod.OAuth ? "login confirmation" : "password";
    }

    private void OnHandleChanged(string value)
    {
        if (!_busy)
            RefreshIdentity(value, false);
    }

    private void RefreshIdentity(string value, bool confirmed)
    {
        if (!Identity)
            return;

        var handle = value?.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(handle))
        {
            Identity.text = "<color=#AAB4C6><size=20>ACCOUNT HANDLE</size></color>\n"
                + "<color=#8792A5><size=20>Enter a handle below</size></color>";
            return;
        }

        var label = confirmed ? "AUTHORIZED ACCOUNT" : "ACCOUNT HANDLE";
        var displayHandle = handle
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
        Identity.text = "<color=#AAB4C6><size=20>" + label + "</size></color>\n"
            + "<color=#FFFFFF><size=25><b>@" + displayHandle + "</b></size></color>";
    }

    private void FocusLoginInput()
    {
        var field = UsesSecret && SecretInput ? SecretInput : Input;
        if (field)
            field.ActivateInputField();
    }

    private static void SetPlaceholder(TMP_InputField field, string text)
    {
        if (field && field.placeholder is TMP_Text placeholder)
            placeholder.text = text;
    }

    private void SetStatus(string text, bool error = false)
    {
        Message.text = text;
        Message.color = error ? new Color(1f, 0.55f, 0.5f, 1f) : _messageColor;
    }

    private void OnDestroy()
    {
        if (Login)
        {
            Login.StateChanged -= OnLoginStateChanged;
            Login.SessionChanged -= OnSessionChanged;
        }
    }
}
