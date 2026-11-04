using AtProtoUnity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class RecordPrompt : MonoBehaviour
{
    [SerializeField] AtProtoWriter writer;

    Button _writeButton;
    Button _deleteButton;
    TMP_Text _writeButtonLabel;
    TMP_Text _message;
    AtProtoWriter _writer;
    Color _messageColor;
    bool _busy;

    public AtProtoWriter Writer => writer;

    public void Show(
        GameObject panel,
        Button writeButton,
        TMP_Text message,
        TMP_Text title,
        TMP_Text identity)
    {
        if (!writer || !panel || !writeButton || !message)
            throw new System.ArgumentException("The record prompt needs a writer, panel, button, and status message.");

        _writer = writer;
        _writeButton = writeButton;
        _writeButtonLabel = writeButton.GetComponentInChildren<TMP_Text>();
        _message = message;
        _messageColor = message.color;
        SetWriteButtonLabel("Write Test Record");
        _writeButton.interactable = true;

        if (title)
            title.text = "Write Test Record";

        var card = panel.transform.Find("Card")?.GetComponent<RectTransform>();
        if (card)
        {
            card.anchorMin = new Vector2(0f, 1f);
            card.anchorMax = new Vector2(0f, 1f);
            card.pivot = new Vector2(0f, 1f);
            card.anchoredPosition = new Vector2(24f, -24f);
            card.sizeDelta = new Vector2(560f, 400f);
            CreateDeleteRecordButton(card);
        }

        var overlay = panel.GetComponent<Image>();
        if (overlay)
        {
            overlay.enabled = false;
            overlay.raycastTarget = false;
        }

        var collection = string.IsNullOrWhiteSpace(writer.CollectionNsid)
            ? "an unconfigured collection"
            : writer.CollectionNsid;
        SetStatus(
            "This will " + writer.WriteMode.ToString().ToLowerInvariant()
            + " one record in:\n" + collection);

        var source = identity ? identity : message;
        if (source)
        {
            var controls = Instantiate(source, panel.transform, false);
            controls.name = "ControlsHint";
            controls.text = "MOVE\nWASD / ARROW KEYS";
            controls.fontSize = 18f;
            controls.raycastTarget = false;
            var controlsRect = (RectTransform)controls.transform;
            controlsRect.anchorMin = new Vector2(0f, 1f);
            controlsRect.anchorMax = new Vector2(0f, 1f);
            controlsRect.pivot = new Vector2(0f, 1f);
            controlsRect.anchoredPosition = new Vector2(
                24f,
                card ? card.anchoredPosition.y - card.sizeDelta.y - 12f : -436f);
            controlsRect.sizeDelta = new Vector2(560f, 64f);
        }

        _writeButton.onClick.AddListener(OnWriteClicked);
    }

    void CreateDeleteRecordButton(RectTransform card)
    {
        var buttonObject = Instantiate(_writeButton.gameObject, card, false);
        buttonObject.name = "Delete Record";
        _deleteButton = buttonObject.GetComponent<Button>();
        _deleteButton.onClick.RemoveAllListeners();
        _deleteButton.onClick.AddListener(OnDeleteClicked);
        _deleteButton.interactable = false;

        var buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.anchorMin = new Vector2(1f, 0f);
        buttonRect.anchorMax = new Vector2(1f, 0f);
        buttonRect.pivot = new Vector2(1f, 0f);
        buttonRect.anchoredPosition = new Vector2(-24f, 18f);
        buttonRect.sizeDelta = new Vector2(150f, 44f);

        var layoutElement = buttonObject.GetComponent<LayoutElement>();
        if (layoutElement)
            layoutElement.ignoreLayout = true;

        var label = buttonObject.GetComponentInChildren<TMP_Text>();
        if (label)
        {
            label.text = "Delete Record";
            label.fontSize = 18f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 14f;
            label.fontSizeMax = 18f;
        }

        var colors = _deleteButton.colors;
        colors.normalColor = new Color(0.68f, 0.22f, 0.2f, 1f);
        colors.highlightedColor = new Color(0.78f, 0.28f, 0.24f, 1f);
        colors.pressedColor = new Color(0.52f, 0.15f, 0.14f, 1f);
        colors.disabledColor = new Color(0.42f, 0.42f, 0.42f, 0.5f);
        _deleteButton.colors = colors;
    }

    void OnWriteClicked()
    {
        _ = WriteRecordAsync();
    }

    async Awaitable WriteRecordAsync()
    {
        if (_busy || !_writer)
            return;

        _busy = true;
        _writeButton.interactable = false;
        _deleteButton.interactable = false;
        SetStatus("WRITING TEST RECORD");
        SetWriteButtonLabel("Writing...");
        try
        {
            var result = await _writer.WriteRecordAsync();
            if (!this)
                return;

            if (!result.Succeeded)
            {
                var error = result.Error?.ResponseMessage
                    ?? result.Error?.Message
                    ?? "The record could not be written.";
                SetStatus("WRITE FAILED\n" + error, true);
                SetWriteButtonLabel("Write Test Record");
                _writeButton.interactable = true;
                return;
            }

            Debug.Log("AT Protocol test record written: " + result.Value.Uri, this);
            SetStatus("TEST RECORD WRITTEN\nThe record is in your repository.");
            SetWriteButtonLabel("Record Written");
            var deleteValidation = _writer.GetDeleteValidationMessage();
            _deleteButton.interactable = deleteValidation == null;
            if (deleteValidation != null)
                SetStatus(
                    "TEST RECORD WRITTEN\nThe record is in your repository.\n"
                    + deleteValidation,
                    true);
        }
        catch (System.Exception exception)
        {
            if (!this)
                return;
            SetStatus("WRITE FAILED\n" + exception.Message, true);
            SetWriteButtonLabel("Write Test Record");
            _writeButton.interactable = true;
        }
        finally
        {
            _busy = false;
        }
    }

    void OnDeleteClicked()
    {
        _ = DeleteRecordAsync();
    }

    async Awaitable DeleteRecordAsync()
    {
        if (_busy || !_writer || !_deleteButton || !_deleteButton.interactable)
            return;

        _busy = true;
        _deleteButton.interactable = false;
        _writeButton.interactable = false;
        SetStatus("DELETING TEST RECORD");
        try
        {
            var result = await _writer.DeleteLastWrittenRecordAsync();
            if (!this)
                return;

            if (!result.Succeeded)
            {
                SetStatus("DELETE FAILED\n" + result.Error.Message, true);
                _deleteButton.interactable = _writer.LastWrittenRecord != null
                    && _writer.GetDeleteValidationMessage() == null;
                return;
            }

            SetStatus("TEST RECORD DELETED\nThe record was removed from your repository.");
            SetWriteButtonLabel("Write Test Record");
            _writeButton.interactable = true;
        }
        catch (System.Exception exception)
        {
            if (!this)
                return;

            SetStatus("DELETE FAILED\n" + exception.Message, true);
            var canDelete = _writer.LastWrittenRecord != null
                && _writer.GetDeleteValidationMessage() == null;
            _deleteButton.interactable = canDelete;
            if (!canDelete)
            {
                SetWriteButtonLabel("Write Test Record");
                _writeButton.interactable = true;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    void SetWriteButtonLabel(string value)
    {
        if (_writeButtonLabel)
            _writeButtonLabel.text = value;
    }

    void SetStatus(string value, bool error = false)
    {
        _message.text = value;
        _message.color = error ? new Color(1f, 0.55f, 0.5f, 1f) : _messageColor;
    }
}