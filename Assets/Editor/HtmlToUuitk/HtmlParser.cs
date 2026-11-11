using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HtmlToUuitk
{
    /// <summary>One node of a parsed HTML document: an element or a run of text.</summary>
    public class HtmlNode
    {
        public string Tag;
        public string Text;
        public HtmlNode Parent;
        public readonly Dictionary<string, string> Attributes = new Dictionary<string, string>();
        public readonly List<HtmlNode> Children = new List<HtmlNode>();

        public bool IsText { get { return Tag == null; } }

        public string Get(string name, string fallback = null)
        {
            string value;
            return Attributes.TryGetValue(name, out value) ? value : fallback;
        }

        public bool Has(string name) { return Attributes.ContainsKey(name); }
    }

    /// <summary>
    /// Small tolerant HTML parser: enough for hand-written pages and exported fixtures.
    /// No implied tags, no foster parenting, no template content — it builds the tree the
    /// markup literally describes, which is what a converter wants.
    /// </summary>
    public static class HtmlParser
    {
        static readonly HashSet<string> VoidElements = new HashSet<string>
        {
            "meta", "link", "input", "br", "hr", "img", "source", "area", "base", "col", "embed", "param", "track", "wbr",
        };

        static readonly HashSet<string> RawTextElements = new HashSet<string> { "script", "style" };

        public static HtmlNode Parse(string html)
        {
            var root = new HtmlNode { Tag = "#document" };
            var stack = new Stack<HtmlNode>();
            stack.Push(root);

            var text = new StringBuilder();
            int i = 0;
            int n = html.Length;

            while (i < n)
            {
                char c = html[i];

                if (c != '<')
                {
                    text.Append(c);
                    i++;
                    continue;
                }

                if (StartsWith(html, i, "<!--"))
                {
                    FlushText(stack, text);
                    int end = html.IndexOf("-->", i + 4, System.StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                    continue;
                }

                if (StartsWith(html, i, "<!") || StartsWith(html, i, "<?"))
                {
                    FlushText(stack, text);
                    int end = html.IndexOf('>', i);
                    i = end < 0 ? n : end + 1;
                    continue;
                }

                if (StartsWith(html, i, "</"))
                {
                    FlushText(stack, text);
                    int end = html.IndexOf('>', i);
                    if (end < 0) { i = n; break; }
                    string closing = html.Substring(i + 2, end - i - 2).Trim().ToLowerInvariant();
                    i = end + 1;

                    // Pop to the matching element; ignore a stray close tag.
                    if (stack.Count <= 1) continue;
                    HtmlNode match = null;
                    foreach (HtmlNode open in stack)
                    {
                        if (open.Tag == closing) { match = open; break; }
                    }
                    if (match == null) continue;
                    while (stack.Count > 1)
                    {
                        HtmlNode popped = stack.Pop();
                        if (popped == match) break;
                    }
                    continue;
                }

                FlushText(stack, text);

                int cursor = i + 1;
                var name = new StringBuilder();
                while (cursor < n && !char.IsWhiteSpace(html[cursor]) && html[cursor] != '>' && html[cursor] != '/')
                {
                    name.Append(html[cursor]);
                    cursor++;
                }

                var node = new HtmlNode { Tag = name.ToString().ToLowerInvariant() };
                bool selfClosing = false;

                // Attributes.
                while (cursor < n)
                {
                    while (cursor < n && char.IsWhiteSpace(html[cursor])) cursor++;
                    if (cursor >= n) break;
                    if (html[cursor] == '>') { cursor++; break; }
                    if (html[cursor] == '/') { selfClosing = true; cursor++; continue; }

                    var attrName = new StringBuilder();
                    while (cursor < n && !char.IsWhiteSpace(html[cursor]) && html[cursor] != '=' && html[cursor] != '>' && html[cursor] != '/')
                    {
                        attrName.Append(html[cursor]);
                        cursor++;
                    }
                    while (cursor < n && char.IsWhiteSpace(html[cursor])) cursor++;

                    string value = "";
                    if (cursor < n && html[cursor] == '=')
                    {
                        cursor++;
                        while (cursor < n && char.IsWhiteSpace(html[cursor])) cursor++;
                        if (cursor < n && (html[cursor] == '"' || html[cursor] == '\''))
                        {
                            char quote = html[cursor++];
                            var val = new StringBuilder();
                            while (cursor < n && html[cursor] != quote) val.Append(html[cursor++]);
                            if (cursor < n) cursor++;
                            value = val.ToString();
                        }
                        else
                        {
                            var val = new StringBuilder();
                            while (cursor < n && !char.IsWhiteSpace(html[cursor]) && html[cursor] != '>') val.Append(html[cursor++]);
                            value = val.ToString();
                        }
                    }

                    string key = attrName.ToString().ToLowerInvariant();
                    if (key.Length > 0) node.Attributes[key] = Decode(value);
                }

                i = cursor;
                node.Parent = stack.Peek();
                stack.Peek().Children.Add(node);

                if (VoidElements.Contains(node.Tag) || selfClosing) continue;

                if (RawTextElements.Contains(node.Tag))
                {
                    string closing = "</" + node.Tag;
                    int end = html.IndexOf(closing, i, System.StringComparison.OrdinalIgnoreCase);
                    node.Text = end < 0 ? html.Substring(i) : html.Substring(i, end - i);
                    int after = end < 0 ? n : html.IndexOf('>', end);
                    i = after < 0 ? n : after + 1;
                    continue;
                }

                stack.Push(node);
            }

            FlushText(stack, text);
            return root;
        }

        static void FlushText(Stack<HtmlNode> stack, StringBuilder buffer)
        {
            if (buffer.Length == 0) return;
            string raw = buffer.ToString();
            buffer.Length = 0;

            string collapsed = Regex.Replace(Decode(raw), @"\s+", " ");
            if (collapsed.Trim().Length == 0) return;

            stack.Peek().Children.Add(new HtmlNode { Text = collapsed.Trim() });
        }

        static bool StartsWith(string source, int index, string value)
        {
            return index + value.Length <= source.Length &&
                   string.Compare(source, index, value, 0, value.Length, System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        static string Decode(string value)
        {
            if (value.IndexOf('&') < 0) return value;
            string result = value
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&apos;", "'")
                .Replace("&nbsp;", " ")
                .Replace("&mdash;", "—")
                .Replace("&ndash;", "–")
                .Replace("&middot;", "·")
                .Replace("&copy;", "©")
                .Replace("&rarr;", "→")
                .Replace("&larr;", "←");
            result = Regex.Replace(result, @"&#(\d+);", m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)));
            return result.Replace("&amp;", "&");
        }
    }
}
