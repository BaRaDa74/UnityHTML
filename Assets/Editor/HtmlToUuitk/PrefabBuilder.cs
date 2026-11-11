using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HtmlToUuitk
{
    /// <summary>
    /// Turns converted screens into prefabs.
    ///
    /// Unity cannot serialize a VisualElement tree into a prefab, so a screen prefab
    /// is the wiring that matters: a root GameObject with a UIDocument pointing at the
    /// generated .uxml and the shared PanelSettings. The fixture prefab adds the
    /// screens as nested prefab instances plus the MenuScreens controller.
    /// </summary>
    public static class PrefabBuilder
    {
        public const string DefaultPanelSettingsPath = "Assets/UI/MenuPanelSettings.asset";

        public static string EnsurePanelSettings(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (existing != null) return path;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;

            // A runtime panel needs a theme; borrow the project's default if it has one.
            foreach (string guid in AssetDatabase.FindAssets("t:ThemeStyleSheet"))
            {
                string themePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(themePath))
                {
                    settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                    break;
                }
            }

            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            return path;
        }

        /// <summary>One prefab per screen: UIDocument plus the shared PanelSettings.</summary>
        public static string BuildScreenPrefab(string screenName, string uxmlPath, string panelSettingsPath, string prefabDirectory)
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
            if (uxml == null) throw new FileNotFoundException("UXML not imported: " + uxmlPath);

            Directory.CreateDirectory(prefabDirectory);

            var root = new GameObject(screenName + "Screen");
            var document = root.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = uxml;

            string prefabPath = Path.Combine(prefabDirectory, screenName + "Screen.prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);

            return prefabPath;
        }

        /// <summary>
        /// One prefab holding every screen as a nested prefab instance, with the
        /// controller wired so the fixture plays as a menu system.
        /// </summary>
        public static string BuildFixturePrefab(string fixtureName, IList<string> screenPrefabPaths, int indexPosition, int pausePosition, string prefabDirectory)
        {
            Directory.CreateDirectory(prefabDirectory);

            var root = new GameObject(fixtureName);
            var documents = new List<UIDocument>();

            foreach (string prefabPath in screenPrefabPaths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.SetParent(root.transform, false);
                var document = instance.GetComponent<UIDocument>();
                if (document != null) documents.Add(document);
            }

            var controller = root.AddComponent<MenuScreens>();
            if (indexPosition >= 0 && indexPosition < documents.Count) controller.indexScreen = documents[indexPosition];
            if (pausePosition >= 0 && pausePosition < documents.Count) controller.pauseScreen = documents[pausePosition];
            if (documents.Count > 0) controller.settingsScreen = documents[documents.Count - 1];

            string path = Path.Combine(prefabDirectory, fixtureName + ".prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            return path;
        }
    }
}
