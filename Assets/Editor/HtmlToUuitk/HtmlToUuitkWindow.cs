using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HtmlToUuitk
{
    /// <summary>
    /// Tools ▸ HTML to UI Toolkit: converts a page or a folder of pages into UXML/USS
    /// assets, and optionally builds a prefab per screen plus one fixture prefab with
    /// the navigation controller attached.
    /// </summary>
    public class HtmlToUuitkWindow : EditorWindow
    {
        const string SourcePrefKey = "HtmlToUuitk.Source";
        const string OutputPrefKey = "HtmlToUuitk.Output";
        const string PrefabPrefKey = "HtmlToUuitk.Prefabs";

        string _source = "html2uuitk-test";
        string _output = "Assets/UI";
        string _prefabDirectory = "Assets/UI/Prefabs";

        List<ConversionResult> _results = new List<ConversionResult>();
        List<string> _prefabPaths = new List<string>();
        Vector2 _scroll;
        string _summary = "Pick a folder or a single .html file.";

        [MenuItem("Tools/HTML to UI Toolkit")]
        public static void Open()
        {
            var window = GetWindow<HtmlToUuitkWindow>("HTML → UI Toolkit");
            window.minSize = new Vector2(520, 420);
        }

        void OnEnable()
        {
            _source = EditorPrefs.GetString(SourcePrefKey, _source);
            _output = EditorPrefs.GetString(OutputPrefKey, _output);
            _prefabDirectory = EditorPrefs.GetString(PrefabPrefKey, _prefabDirectory);
        }

        void OnDisable()
        {
            EditorPrefs.SetString(SourcePrefKey, _source);
            EditorPrefs.SetString(OutputPrefKey, _output);
            EditorPrefs.SetString(PrefabPrefKey, _prefabDirectory);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _source = EditorGUILayout.TextField(_source);
                if (GUILayout.Button("Folder…", GUILayout.Width(80)))
                {
                    string picked = EditorUtility.OpenFolderPanel("HTML folder", Absolute(_source), "");
                    if (!string.IsNullOrEmpty(picked)) _source = MakeRelative(picked);
                }
                if (GUILayout.Button("File…", GUILayout.Width(60)))
                {
                    string picked = EditorUtility.OpenFilePanel("HTML file", Absolute(_source), "html");
                    if (!string.IsNullOrEmpty(picked)) _source = MakeRelative(picked);
                }
            }

            _output = EditorGUILayout.TextField("Output folder", _output);
            _prefabDirectory = EditorGUILayout.TextField("Prefab folder", _prefabDirectory);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Convert", GUILayout.Height(26))) Run(false);
                if (GUILayout.Button("Convert + Create Prefabs", GUILayout.Height(26))) Run(true);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_summary, EditorStyles.wordWrappedLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ConversionResult result in _results) DrawResult(result);
            if (_prefabPaths.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Prefabs", EditorStyles.boldLabel);
                foreach (string path in _prefabPaths) EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawResult(ConversionResult result)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(result.Screen + "  ←  " + Path.GetFileName(result.Source), EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(result.Error))
            {
                EditorGUILayout.HelpBox(result.Error, MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField(result.UxmlPath + "  (" + result.UxmlBytes + " bytes)", EditorStyles.miniLabel);
            foreach (string uss in result.UssPaths) EditorGUILayout.LabelField("  " + Path.GetFileName(uss), EditorStyles.miniLabel);

            if (result.UnresolvedVariables > 0)
            {
                EditorGUILayout.HelpBox(result.UnresolvedVariables + " var() reference(s) had no value and were left as written.", MessageType.Warning);
            }
            if (result.Warnings.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", result.Warnings.ToArray()), MessageType.Warning);
            }
        }

        void Run(bool createPrefabs)
        {
            _results.Clear();
            _prefabPaths.Clear();

            string source = Absolute(_source);
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                _summary = "Nothing at " + source;
                return;
            }

            Dictionary<string, string> fonts = HtmlToUuitkConverter.LoadFonts(HtmlToUuitkConverter.FindConfig(source));

            if (Directory.Exists(source))
            {
                _results = HtmlToUuitkConverter.ConvertFolder(source, _output, fonts);
            }
            else
            {
                _results.Add(HtmlToUuitkConverter.ConvertScreen(source, HtmlToUuitkConverter.FindStylesheets(source), _output, fonts));
            }

            AssetDatabase.Refresh();

            int failures = 0;
            int warnings = 0;
            foreach (ConversionResult result in _results)
            {
                if (!string.IsNullOrEmpty(result.Error)) failures++;
                warnings += result.Warnings.Count;
            }

            _summary = _results.Count + " screen(s) converted, " + failures + " failed, " + warnings + " warning(s).";

            if (createPrefabs && failures == 0)
            {
                try
                {
                    BuildPrefabs(source);
                }
                catch (System.Exception error)
                {
                    _summary += "\nPrefab step failed: " + error.Message;
                }
            }
        }

        void BuildPrefabs(string source)
        {
            string panelPath = PrefabBuilder.EnsurePanelSettings(PrefabBuilder.DefaultPanelSettingsPath);
            var screenPrefabs = new List<string>();

            foreach (ConversionResult result in _results)
            {
                if (result.UxmlPath == null) continue;
                screenPrefabs.Add(PrefabBuilder.BuildScreenPrefab(result.Screen, result.UxmlPath, panelPath, _prefabDirectory));
            }

            if (screenPrefabs.Count == 0) return;

            string fixtureName = Directory.Exists(source)
                ? new DirectoryInfo(source).Name
                : Path.GetFileNameWithoutExtension(source);

            int indexPosition = PositionOf("Index");
            int pausePosition = PositionOf("Pause");

            _prefabPaths.AddRange(screenPrefabs);
            _prefabPaths.Add(PrefabBuilder.BuildFixturePrefab(fixtureName, screenPrefabs, indexPosition, pausePosition, _prefabDirectory));
            AssetDatabase.SaveAssets();
        }

        int PositionOf(string screenName)
        {
            for (int i = 0; i < _results.Count; i++)
            {
                if (_results[i].Screen == screenName) return i;
            }
            return -1;
        }

        static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        static string Absolute(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ProjectRoot, path));
        }

        static string MakeRelative(string absolutePath)
        {
            string root = ProjectRoot;
            string normalized = absolutePath.Replace('\\', '/');
            string normalizedRoot = root.Replace('\\', '/');
            if (normalized.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(normalizedRoot.Length).TrimStart('/');
            }
            return absolutePath;
        }
    }
}
