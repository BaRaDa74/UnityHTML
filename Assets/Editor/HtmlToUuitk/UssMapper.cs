using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HtmlToUuitk
{
    /// <summary>
    /// Turns CSS rules into USS. This is the same contract the Node converter had:
    /// selectors are copied through unchanged, and declarations are expanded or
    /// renamed into what UI Toolkit actually understands. Anything UI Toolkit has
    /// no equivalent for is dropped and reported.
    /// </summary>
    public class UssMapper
    {
        readonly Dictionary<string, string> _fonts;
        readonly Dictionary<string, string> _variables = new Dictionary<string, string>();
        readonly HashSet<string> _reported = new HashSet<string>();

        public readonly List<string> Warnings = new List<string>();
        public int UnresolvedVariables;
        public float RootFontSize = 16f;

        public UssMapper(Dictionary<string, string> fonts)
        {
            _fonts = fonts ?? new Dictionary<string, string>();
        }

        /// <summary>Collects :root custom properties so var() can be substituted later.</summary>
        public void AddVariables(IEnumerable<CssRule> rules)
        {
            foreach (CssRule rule in rules)
            {
                bool isRoot = false;
                foreach (string selector in rule.Selectors)
                {
                    if (selector.Trim() == ":root") isRoot = true;
                }
                if (!isRoot) continue;

                foreach (CssDeclaration declaration in rule.Declarations)
                {
                    if (!declaration.Property.StartsWith("--")) continue;
                    _variables[declaration.Property] = NormalizeColor(declaration.Value.Trim());
                }
            }
        }

        public string MapSheet(IEnumerable<CssRule> rules)
        {
            var output = new StringBuilder();
            foreach (CssRule rule in rules)
            {
                var declarations = new List<string>();
                foreach (CssDeclaration declaration in rule.Declarations)
                {
                    if (declaration.Property.StartsWith("--")) continue;
                    foreach (KeyValuePair<string, string> mapped in MapDeclaration(declaration))
                    {
                        declarations.Add("    " + mapped.Key + ": " + mapped.Value + ";");
                    }
                }
                if (declarations.Count == 0) continue;

                output.Append(string.Join(", ", rule.Selectors)).Append(" {\n");
                foreach (string line in declarations) output.Append(line).Append('\n');
                output.Append("}\n\n");
            }
            return output.ToString();
        }

        public List<KeyValuePair<string, string>> MapDeclaration(CssDeclaration declaration)
        {
            var result = new List<KeyValuePair<string, string>>();
            string property = declaration.Property;
            string value = ResolveVariables(declaration.Value);

            switch (property)
            {
                case "display":
                {
                    string mapped = value == "none" ? "none" : "flex";
                    Add(result, "display", mapped);
                    break;
                }

                case "flex-direction":
                case "justify-content":
                case "align-items":
                case "align-self":
                case "align-content":
                case "flex-wrap":
                case "flex-grow":
                case "flex-shrink":
                case "flex-basis":
                case "row-gap":
                case "column-gap":
                case "opacity":
                case "white-space":
                    Add(result, property, value);
                    break;

                case "gap":
                {
                    List<string> parts = SplitTokens(value);
                    string row = parts.Count > 0 ? Length(parts[0]) : null;
                    string column = parts.Count > 1 ? Length(parts[1]) : row;
                    Add(result, "row-gap", row);
                    Add(result, "column-gap", column);
                    break;
                }

                case "flex":
                {
                    List<string> parts = SplitTokens(value);
                    if (parts.Count == 1 && IsNumber(parts[0]))
                    {
                        Add(result, "flex-grow", parts[0]);
                        Add(result, "flex-shrink", "1");
                    }
                    else if (parts.Count >= 2)
                    {
                        Add(result, "flex-grow", parts[0]);
                        Add(result, "flex-shrink", parts[1]);
                        Add(result, "flex-basis", parts.Count > 2 ? Length(parts[2]) : "auto");
                    }
                    break;
                }

                case "padding":
                case "margin":
                {
                    string[] sides = ExpandSides(value);
                    if (sides != null)
                    {
                        Add(result, property + "-top", Length(sides[0]));
                        Add(result, property + "-right", Length(sides[1]));
                        Add(result, property + "-bottom", Length(sides[2]));
                        Add(result, property + "-left", Length(sides[3]));
                    }
                    break;
                }

                case "padding-top":
                case "padding-right":
                case "padding-bottom":
                case "padding-left":
                case "margin-top":
                case "margin-right":
                case "margin-bottom":
                case "margin-left":
                case "width":
                case "height":
                case "min-width":
                case "min-height":
                case "max-width":
                case "max-height":
                case "top":
                case "right":
                case "bottom":
                case "left":
                case "letter-spacing":
                case "font-size":
                    Add(result, property, Length(value));
                    break;

                case "border":
                case "border-top":
                case "border-right":
                case "border-bottom":
                case "border-left":
                {
                    string side = property == "border" ? "" : property.Substring("border-".Length) + "-";
                    if (value.Contains("none"))
                    {
                        Add(result, "border-" + side + "width", "0");
                        break;
                    }
                    foreach (string token in SplitTokens(value))
                    {
                        string colour = TryColor(token);
                        if (colour != null) Add(result, "border-" + side + "color", colour);
                        else
                        {
                            string length = Length(token);
                            if (length != null && !IsKeyword(token)) Add(result, "border-" + side + "width", length);
                        }
                    }
                    break;
                }

                case "border-width":
                case "border-color":
                    Add(result, property, property == "border-color" ? TryColor(value) ?? value : Length(value));
                    break;

                case "border-radius":
                {
                    string[] corners = ExpandSides(value);
                    if (corners != null)
                    {
                        Add(result, "border-top-left-radius", Length(corners[0]));
                        Add(result, "border-top-right-radius", Length(corners[1]));
                        Add(result, "border-bottom-right-radius", Length(corners[2]));
                        Add(result, "border-bottom-left-radius", Length(corners[3]));
                    }
                    break;
                }

                case "background":
                    MapBackground(result, value);
                    break;

                case "background-color":
                    Add(result, "background-color", TryColor(value) ?? value);
                    break;

                case "color":
                    Add(result, "color", TryColor(value) ?? value);
                    break;

                case "font":
                    MapFontShorthand(result, value);
                    break;

                case "font-family":
                {
                    string url = FontUrl(value);
                    if (url != null) Add(result, "-unity-font", url);
                    else Warn("font-family has no mapped asset", value);
                    break;
                }

                case "font-weight":
                    Add(result, "-unity-font-style", WeightToStyle(value));
                    break;

                case "font-style":
                    Add(result, "-unity-font-style", value == "italic" ? "italic" : "normal");
                    break;

                case "text-align":
                    Add(result, "-unity-text-align", TextAlign(value));
                    break;

                case "overflow":
                case "overflow-x":
                case "overflow-y":
                    if (property == "overflow")
                    {
                        // UI Toolkit has no scrolling overflow; it clips instead.
                        Add(result, "overflow", value == "visible" ? "visible" : "hidden");
                    }
                    break;

                case "position":
                    if (value != "static") Add(result, "position", value);
                    break;

                case "box-shadow":
                    MapBoxShadow(result, value);
                    break;

                case "visibility":
                case "box-sizing":
                case "text-transform":
                case "line-height":
                case "cursor":
                case "transition":
                case "transform":
                case "content":
                case "outline":
                case "z-index":
                case "background-image":
                case "text-decoration":
                case "list-style":
                case "list-style-type":
                    break; // no USS equivalent

                default:
                    Warn("property has no USS equivalent: " + property, value);
                    break;
            }

            return result;
        }

        void MapBackground(List<KeyValuePair<string, string>> result, string value)
        {
            foreach (string token in SplitTokens(value))
            {
                string colour = TryColor(token);
                if (colour != null) { Add(result, "background-color", colour); continue; }
                if (token == "no-repeat") Add(result, "background-repeat", "no-repeat");
                else if (Regex.IsMatch(token, "^(repeat|space|round)(-x|-y)?$")) Add(result, "background-repeat", token);
            }
        }

        void MapFontShorthand(List<KeyValuePair<string, string>> result, string value)
        {
            List<string> tokens = SplitTokens(value);
            string size = null;
            string weight = null;
            var family = new List<string>();

            foreach (string token in tokens)
            {
                if (size == null && Length(token) != null && !IsNumber(token) && Regex.IsMatch(token, @"[\d.]+(px|rem|em|pt|%)"))
                {
                    size = Length(token);
                    continue;
                }
                if (size == null && IsNumber(token) && int.Parse(token) >= 100) { weight = token; continue; }
                if (token == "italic" || token == "normal") continue;
                family.Add(token);
            }

            if (family.Count > 0)
            {
                string url = FontUrl(string.Join(" ", family));
                if (url != null) Add(result, "-unity-font", url);
            }
            if (size != null) Add(result, "font-size", size);
            if (weight != null) Add(result, "-unity-font-style", WeightToStyle(weight));
        }

        void MapBoxShadow(List<KeyValuePair<string, string>> result, string value)
        {
            if (value == "none") return;

            var lengths = new List<string>();
            string colour = null;
            foreach (string token in SplitTokens(value))
            {
                string c = TryColor(token);
                if (c != null) { colour = c; continue; }
                string length = Length(token);
                if (length != null) lengths.Add(length);
            }

            // UI Toolkit has no spread, so a fourth length is dropped.
            if (lengths.Count >= 2)
            {
                Add(result, "--unity-shadow-offset-x", lengths[0]);
                Add(result, "--unity-shadow-offset-y", lengths[1]);
                Add(result, "--unity-shadow-blur-radius", lengths.Count > 2 ? lengths[2] : "0");
            }
            if (colour != null) Add(result, "--unity-shadow-color", colour);
        }

        public string ResolveVariables(string text)
        {
            if (text.IndexOf("var(", System.StringComparison.Ordinal) < 0) return NormalizeColor(text);
            string resolved = Substitute(text, null, new HashSet<string>());
            return NormalizeColor(resolved);
        }

        string Substitute(string text, string fallback, HashSet<string> stack)
        {
            var output = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("var(", i, System.StringComparison.Ordinal);
                if (start < 0)
                {
                    output.Append(text, i, text.Length - i);
                    break;
                }
                output.Append(text, i, start - i);

                int depth = 0;
                int end = start + 4;
                for (; end < text.Length; end++)
                {
                    if (text[end] == '(') depth++;
                    else if (text[end] == ')' && depth-- == 0) { end++; break; }
                }

                string inner = text.Substring(start + 4, System.Math.Max(0, end - 1 - (start + 4)));
                int comma = TopLevelComma(inner);
                string name = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
                string fb = comma < 0 ? null : inner.Substring(comma + 1).Trim();

                string value = Lookup(name, fb, stack);
                if (value == null)
                {
                    UnresolvedVariables++;
                    output.Append(text, start, end - start);
                }
                else output.Append(value);

                i = end;
            }
            return output.ToString();
        }

        string Lookup(string name, string fallback, HashSet<string> stack)
        {
            string raw;
            if (_variables.TryGetValue(name, out raw) && !stack.Contains(name))
            {
                var next = new HashSet<string>(stack) { name };
                string resolved = Substitute(raw, null, next);
                if (resolved.IndexOf("var(", System.StringComparison.Ordinal) < 0) return resolved.Trim();
            }
            if (fallback != null) return NormalizeColor(Substitute(fallback, null, stack).Trim());
            return null;
        }

        static int TopLevelComma(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                else if (text[i] == ',' && depth == 0) return i;
            }
            return -1;
        }

        string FontUrl(string family)
        {
            string first = family.Split(',')[0].Trim().Trim('"', '\'');
            string path;
            if (_fonts.TryGetValue(first, out path)) return "url(\"" + path + "\")";

            foreach (KeyValuePair<string, string> entry in _fonts)
            {
                if (string.Equals(entry.Key, first, System.StringComparison.OrdinalIgnoreCase)) return "url(\"" + entry.Value + "\")";
            }
            return null;
        }

        static string WeightToStyle(string value)
        {
            int weight;
            if (int.TryParse(value, out weight)) return weight >= 600 ? "bold" : "normal";
            return value == "bold" || value == "bolder" ? "bold" : "normal";
        }

        static string TextAlign(string value)
        {
            switch (value)
            {
                case "left": return "middle-left";
                case "right": return "middle-right";
                case "center": return "middle-center";
                default: return "middle-left";
            }
        }

        public string Length(string token)
        {
            if (token == null) return null;
            token = token.Trim();
            if (token.Length == 0) return null;

            if (token == "0") return "0";
            if (token == "auto") return "auto";

            Match match = Regex.Match(token, @"^(-?[\d.]+)(px|rem|em|pt|%|vh|vw)$");
            if (!match.Success) return null;

            float number = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (match.Groups[2].Value)
            {
                case "px": return Trim(number) + "px";
                case "rem":
                case "em": return Trim(number * RootFontSize) + "px";
                case "pt": return Trim(number * 96f / 72f) + "px";
                case "%": return Trim(number) + "%";
                // A UI Toolkit panel is always the whole screen, so the viewport
                // units the source CSS uses resolve to a percentage of it.
                case "vh":
                case "vw": return Trim(number) + "%";
                default: return null;
            }
        }

        static string Trim(float value)
        {
            return Mathf_IsWhole(value) ? ((int)value).ToString(CultureInfo.InvariantCulture)
                                        : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static bool Mathf_IsWhole(float value) { return System.Math.Abs(value - (int)value) < 0.0001f; }

        static bool IsNumber(string token)
        {
            float v;
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static bool IsKeyword(string token)
        {
            return Regex.IsMatch(token, "^[a-z-]+$");
        }

        public static string TryColor(string token)
        {
            if (token == null) return null;
            token = token.Trim();
            if (token.Length == 0) return null;

            if (token[0] == '#')
            {
                string hex = token.Substring(1);
                if (hex.Length == 3) hex = "" + hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
                if (hex.Length == 8)
                {
                    // USS has no 8-digit hex: fold alpha into rgba().
                    int r = System.Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = System.Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = System.Convert.ToInt32(hex.Substring(4, 2), 16);
                    float a = System.Convert.ToInt32(hex.Substring(6, 2), 16) / 255f;
                    return "rgba(" + r + ", " + g + ", " + b + ", " + Trim(a) + ")";
                }
                if (hex.Length != 6) return null;
                return "#" + hex.ToLowerInvariant();
            }

            Match rgb = Regex.Match(token, @"^rgba?\(([^)]*)\)$");
            if (rgb.Success) return NormalizeColor(token);

            if (NamedColors.Contains(token)) return token;
            return null;
        }

        // USS understands these without translation; anything else spelled as a
        // bare word in a colour slot is a mistake worth surfacing as no colour.
        static readonly HashSet<string> NamedColors = new HashSet<string>
        {
            "transparent", "white", "black", "red", "green", "blue", "yellow", "orange",
            "purple", "gray", "grey", "silver", "teal", "navy", "maroon", "olive", "lime",
        };

        /// <summary>Modern space/slash colour syntax becomes rgb()/rgba(); USS rejects the former.</summary>
        public static string NormalizeColor(string value)
        {
            if (value == null) return null;
            string result = Regex.Replace(value,
                @"rgba?\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*(?:/\s*([\d.]+%?)\s*)?\)",
                m => m.Groups[4].Success
                    ? "rgba(" + m.Groups[1].Value + ", " + m.Groups[2].Value + ", " + m.Groups[3].Value + ", " + m.Groups[4].Value + ")"
                    : "rgb(" + m.Groups[1].Value + ", " + m.Groups[2].Value + ", " + m.Groups[3].Value + ")");
            result = Regex.Replace(result, @"rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)", "rgb($1, $2, $3)");
            return result;
        }

        public static List<string> SplitTokens(string value)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            foreach (char c in value)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Length = 0; }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        static string[] ExpandSides(string value)
        {
            List<string> parts = SplitTokens(value);
            if (parts.Count == 0 || parts.Count > 4) return null;
            switch (parts.Count)
            {
                case 1: return new[] { parts[0], parts[0], parts[0], parts[0] };
                case 2: return new[] { parts[0], parts[1], parts[0], parts[1] };
                case 3: return new[] { parts[0], parts[1], parts[2], parts[1] };
                default: return new[] { parts[0], parts[1], parts[2], parts[3] };
            }
        }

        static void Add(List<KeyValuePair<string, string>> target, string property, string value)
        {
            if (value == null || value.Length == 0) return;
            target.Add(new KeyValuePair<string, string>(property, value));
        }

        void Warn(string message, string value)
        {
            string key = message + " (" + value + ")";
            if (!_reported.Add(key)) return;
            Warnings.Add(key);
        }
    }
}
