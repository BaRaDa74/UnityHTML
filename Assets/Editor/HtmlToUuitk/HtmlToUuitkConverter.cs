using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace HtmlToUuitk
{
    public class ConversionResult
    {
        public string Screen;
        public string Source;
        public string OutputDirectory;
        public string UxmlPath;
        public readonly List<string> UssPaths = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int UxmlBytes;
        public int UnresolvedVariables;
        public string Error;
    }

    /// <summary>
    /// Runs the conversion entirely inside the editor: HTML plus its stylesheets in,
    /// a UXML document and its USS files out, next to each other so the relative
    /// &lt;Style src&gt; references resolve.
    /// </summary>
    public static class HtmlToUuitkConverter
    {
        /// <summary>Font family name to project URL, read from the fixture's config.json.</summary>
        public static Dictionary<string, string> LoadFonts(string configPath)
        {
            var fonts = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return fonts;

            string json = File.ReadAllText(configPath);
            foreach (Match match in Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\\{\\s*\"path\"\\s*:\\s*\"([^\"]+)\"\\s*\\}"))
            {
                fonts[match.Groups[1].Value] = match.Groups[2].Value;
            }
            return fonts;
        }

        /// <summary>Stylesheets the page links, in document order; falls back to every .css beside it.</summary>
        public static List<string> FindStylesheets(string htmlPath)
        {
            string directory = Path.GetDirectoryName(htmlPath) ?? ".";
            var sheets = new List<string>();
            string html = File.ReadAllText(htmlPath);

            foreach (Match match in Regex.Matches(html, "<link[^>]+href\\s*=\\s*\"([^\"]+\\.css)\"[^>]*>", RegexOptions.IgnoreCase))
            {
                string resolved = Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value));
                if (File.Exists(resolved) && !sheets.Contains(resolved)) sheets.Add(resolved);
            }

            if (sheets.Count > 0) return sheets;

            string[] files = Directory.GetFiles(directory, "*.css");
            System.Array.Sort(files);
            sheets.AddRange(files);
            return sheets;
        }

        public static ConversionResult ConvertScreen(string htmlPath, IEnumerable<string> cssPaths, string outputRoot, Dictionary<string, string> fonts)
        {
            var result = new ConversionResult
            {
                Source = htmlPath,
                Screen = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(htmlPath).Replace('-', ' ')),
            };

            try
            {
                string folder = Path.Combine(outputRoot, result.Screen);
                Directory.CreateDirectory(folder);
                result.OutputDirectory = folder;

                HtmlNode document = HtmlParser.Parse(File.ReadAllText(htmlPath));

                var sheets = new List<string>(cssPaths);
                var parsed = new List<KeyValuePair<string, List<CssRule>>>();
                var mapper = new UssMapper(fonts);

                foreach (string sheet in sheets)
                {
                    List<CssRule> rules = CssParser.Parse(File.ReadAllText(sheet), sheet);
                    parsed.Add(new KeyValuePair<string, List<CssRule>>(sheet, rules));
                }
                // Variables must be known before any declaration is mapped.
                foreach (KeyValuePair<string, List<CssRule>> entry in parsed) mapper.AddVariables(entry.Value);

                var styleNames = new List<string>();
                foreach (KeyValuePair<string, List<CssRule>> entry in parsed)
                {
                    string name = Path.GetFileNameWithoutExtension(entry.Key) + ".uss";
                    string target = Path.Combine(folder, name);
                    File.WriteAllText(target, mapper.MapSheet(entry.Value));
                    styleNames.Add(name);
                    result.UssPaths.Add(target);
                }

                var writer = new UxmlWriter();
                string uxml = writer.Build(document, styleNames);
                string uxmlPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(htmlPath) + ".uxml");
                File.WriteAllText(uxmlPath, uxml);
                result.UxmlPath = uxmlPath;
                result.UxmlBytes = uxml.Length;

                result.Warnings.AddRange(writer.Warnings);
                result.Warnings.AddRange(mapper.Warnings);
                result.UnresolvedVariables = mapper.UnresolvedVariables;
            }
            catch (System.Exception error)
            {
                result.Error = error.Message;
            }

            return result;
        }

        /// <summary>Converts every .html in a folder. Each page is its own screen.</summary>
        public static List<ConversionResult> ConvertFolder(string folder, string outputRoot, Dictionary<string, string> fonts)
        {
            var results = new List<ConversionResult>();
            if (!Directory.Exists(folder)) return results;

            string[] pages = Directory.GetFiles(folder, "*.html");
            System.Array.Sort(pages);
            foreach (string page in pages) results.Add(ConvertScreen(page, FindStylesheets(page), outputRoot, fonts));
            return results;
        }

        /// <summary>Locates config.json beside the source, then the project-wide one.</summary>
        public static string FindConfig(string sourcePath)
        {
            string directory = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
            string beside = Path.Combine(directory ?? ".", "config.json");
            if (File.Exists(beside)) return beside;

            foreach (string candidate in AssetDatabase.FindAssets("config t:TextAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(candidate);
                if (Path.GetFileName(path) == "config.json") return Path.GetFullPath(path);
            }
            return null;
        }
    }
}
