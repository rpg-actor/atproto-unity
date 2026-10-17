using System;
using UnityEditor;
using UnityEngine;

namespace RpgActorUnity
{
    public static class RpgActorPreferences
    {
        private const string SettingsPath = "Preferences/RPG Actor";
        private const string ApiKeyPref = "RpgActorUnity.ApiKey";

        private static string _testResult;

        public static string ApiKey
        {
            get => EditorPrefs.GetString(ApiKeyPref, "");
            set => EditorPrefs.SetString(ApiKeyPref, value);
        }

        public static bool HasApiKey => ApiKey.Length > 0;

        [SettingsProvider]
        private static SettingsProvider CreateProvider() => new(SettingsPath, SettingsScope.User)
        {
            guiHandler = searchContext =>
            {
                ApiKey = EditorGUILayout.PasswordField("API Key", ApiKey);
                EditorGUILayout.HelpBox("Stored in EditorPrefs on this machine. Never saved to the project.", MessageType.None);
                using (new EditorGUI.DisabledScope(!HasApiKey))
                    if (GUILayout.Button("Test", GUILayout.Width(80)))
                        _ = Test();
                if (_testResult != null)
                    EditorGUILayout.LabelField(_testResult);
            }
        };

        private static async Awaitable Test()
        {
            _testResult = "Testing...";
            try
            {
                var items = await RpgActorApi.GetCreatorItems(ApiKey);
                _testResult = $"OK: {items.Count} creator items.";
            }
            catch (Exception e)
            {
                _testResult = e.Message;
            }
            SettingsService.RepaintAllSettingsWindow();
        }
    }
}
