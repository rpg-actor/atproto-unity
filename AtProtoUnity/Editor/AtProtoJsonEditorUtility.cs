using UnityEditor;
using UnityEngine;

namespace AtProtoUnity.Editor
{
    static class AtProtoJsonEditorUtility
    {
        const float ResizeHandleHeight = 8f;
        const float MinimumHeight = 64f;
        const float MaximumHeight = 720f;

        public static string DrawTextArea(
            string value,
            ref float height)
        {
            height = Mathf.Clamp(height, MinimumHeight, MaximumHeight);
            var rect = EditorGUILayout.GetControlRect(
                false,
                height,
                GUILayout.ExpandWidth(true));
            var textRect = new Rect(
                rect.x,
                rect.y,
                rect.width,
                rect.height - ResizeHandleHeight);
            var result = EditorGUI.TextArea(textRect, value ?? string.Empty, EditorStyles.textArea);

            var handleRect = new Rect(
                rect.x,
                rect.yMax - ResizeHandleHeight,
                rect.width,
                ResizeHandleHeight);
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeVertical);

            var controlId = GUIUtility.GetControlID(FocusType.Passive);
            var currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown
                && currentEvent.button == 0
                && handleRect.Contains(currentEvent.mousePosition))
            {
                GUIUtility.hotControl = controlId;
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.MouseDrag
                && GUIUtility.hotControl == controlId)
            {
                height = Mathf.Clamp(
                    currentEvent.mousePosition.y - rect.y,
                    MinimumHeight,
                    MaximumHeight);
                currentEvent.Use();
                GUI.changed = true;
            }
            else if (currentEvent.type == EventType.MouseUp
                && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                currentEvent.Use();
            }

            return result;
        }
    }
}