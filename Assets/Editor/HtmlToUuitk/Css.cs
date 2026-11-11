using System.Collections.Generic;

namespace HtmlToUuitk
{
    public class CssDeclaration
    {
        public string Property;
        public string Value;
        public string Source;
    }

    public class CssRule
    {
        public readonly List<string> Selectors = new List<string>();
        public readonly List<CssDeclaration> Declarations = new List<CssDeclaration>();
    }

    /// <summary>
    /// Just enough CSS to feed the mapper: rules, selectors and declarations.
    /// At-rules (@media, @font-face, @keyframes) are skipped whole, the same way
    /// the Node converter ignored them.
    /// </summary>
    public static class CssParser
    {
        public static List<CssRule> Parse(string css, string source)
        {
            var rules = new List<CssRule>();
            string text = StripComments(css);
            int i = 0;
            int n = text.Length;

            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) break;

                if (text[i] == '@')
                {
                    i = SkipAtRule(text, i);
                    continue;
                }

                int brace = IndexOfTopLevel(text, i, '{');
                if (brace < 0) break;

                string selectorText = text.Substring(i, brace - i).Trim();
                int end = MatchBrace(text, brace);
                if (end < 0) end = n;

                string body = text.Substring(brace + 1, end - brace - 1);
                i = end + 1;

                if (selectorText.Length == 0 || selectorText.IndexOf('@') >= 0) continue;

                var rule = new CssRule();
                foreach (string selector in selectorText.Split(','))
                {
                    string trimmed = selector.Trim();
                    if (trimmed.Length > 0) rule.Selectors.Add(trimmed);
                }
                if (rule.Selectors.Count == 0) continue;

                rule.Declarations.AddRange(ParseDeclarations(body, source));
                if (rule.Declarations.Count > 0) rules.Add(rule);
            }

            return rules;
        }

        static int SkipAtRule(string text, int start)
        {
            int end = MatchBrace(text, IndexOfTopLevel(text, start, '{'));
            if (end < 0)
            {
                int semi = text.IndexOf(';', start);
                return semi < 0 ? text.Length : semi + 1;
            }
            return end + 1;
        }

        static int IndexOfTopLevel(string text, int start, char target)
        {
            int parens = 0;
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote && text[i - 1] != '\\') quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '(') { parens++; continue; }
                if (c == ')') { parens--; continue; }
                if (parens == 0 && c == target) return i;
            }
            return -1;
        }

        static int MatchBrace(string text, int open)
        {
            if (open < 0) return -1;
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote && text[i - 1] != '\\') quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static IEnumerable<CssDeclaration> ParseDeclarations(string body, string source)
        {
            var declarations = new List<CssDeclaration>();
            int i = 0;
            while (i < body.Length)
            {
                int colon = IndexOfTopLevel(body, i, ':');
                int semi = IndexOfTopLevel(body, i, ';');
                if (semi < 0) semi = body.Length;
                if (colon < 0 || colon > semi)
                {
                    i = semi + 1;
                    continue;
                }

                string property = body.Substring(i, colon - i).Trim().ToLowerInvariant();
                string value = body.Substring(colon + 1, semi - colon - 1).Trim();
                if (property.Length > 0 && value.Length > 0)
                {
                    declarations.Add(new CssDeclaration { Property = property, Value = value, Source = source });
                }
                i = semi + 1;
            }
            return declarations;
        }

        static string StripComments(string css)
        {
            if (css.IndexOf("/*", System.StringComparison.Ordinal) < 0) return css;
            var builder = new System.Text.StringBuilder(css.Length);
            int i = 0;
            while (i < css.Length)
            {
                int start = css.IndexOf("/*", i, System.StringComparison.Ordinal);
                if (start < 0)
                {
                    builder.Append(css, i, css.Length - i);
                    break;
                }
                builder.Append(css, i, start - i);
                int end = css.IndexOf("*/", start + 2, System.StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
            }
            return builder.ToString();
        }
    }
}
