using System.Collections.Generic;
using System.Text;

namespace HtmlToUuitk
{
    /// <summary>
    /// Emits a UI Toolkit UXML document from the parsed HTML tree.
    ///
    /// Two things differ from the Node converter on purpose:
    ///   - text is escaped and whitespace-collapsed, so it cannot break the XML;
    ///   - an element with inline children keeps only its own text in the text
    ///     attribute instead of also repeating the children's text, which used to
    ///     render every inline phrase twice.
    /// </summary>
    public class UxmlWriter
    {
        public readonly List<string> Warnings = new List<string>();
        readonly HashSet<string> _reported = new HashSet<string>();

        static readonly HashSet<string> Skipped = new HashSet<string>
        {
            "script", "style", "head", "meta", "link", "title", "base", "template", "noscript",
        };

        static readonly HashSet<string> BlockTags = new HashSet<string>
        {
            "div", "section", "header", "footer", "main", "aside", "nav", "ul", "ol", "li", "form",
            "article", "figure", "figcaption", "fieldset", "hr", "svg", "details", "summary",
        };

        static readonly HashSet<string> LabelTags = new HashSet<string>
        {
            "h1", "h2", "h3", "h4", "h5", "h6", "p", "span", "strong", "em", "small", "code", "pre",
            "label", "time", "a", "b", "i", "u", "blockquote", "cite", "legend", "abbr", "kbd",
            "output", "mark", "sub", "sup",
        };

        public string Build(HtmlNode document, IList<string> stylesheetFiles)
        {
            var xml = new StringBuilder();
            xml.Append("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\" editor-extension-mode=\"False\">\n");
            foreach (string file in stylesheetFiles)
            {
                xml.Append("\t<ui:Style src=\"").Append(file).Append("\" />\n");
            }

            HtmlNode body = Find(document, "body") ?? document;
            foreach (HtmlNode child in body.Children) Write(child, 1, xml);

            xml.Append("</ui:UXML>\n");
            return xml.ToString();
        }

        static HtmlNode Find(HtmlNode node, string tag)
        {
            if (node.Tag == tag) return node;
            foreach (HtmlNode child in node.Children)
            {
                HtmlNode found = Find(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        void Write(HtmlNode node, int depth, StringBuilder xml)
        {
            string indent = new string('\t', depth);

            if (node.IsText)
            {
                if (node.Text.Trim().Length == 0) return;
                xml.Append(indent).Append("<ui:Label text=\"").Append(Escape(node.Text)).Append("\" />\n");
                return;
            }

            if (Skipped.Contains(node.Tag)) return;

            var attributes = new List<KeyValuePair<string, string>>();
            var classes = new List<string>();
            var children = new List<HtmlNode>();

            string element;
            bool labelLike = false;
            bool textOnly = false; // element carries its text in one attribute

            switch (node.Tag)
            {
                case "button":
                    element = "ui:Button";
                    break;

                case "select":
                {
                    element = "ui:DropdownField";
                    var options = new List<string>();
                    foreach (HtmlNode child in node.Children)
                    {
                        if (child.IsText) continue;
                        if (child.Tag == "option")
                        {
                            string optionText = InnerText(child);
                            bool safe = optionText.Length > 0 && !optionText.Contains(",") && !optionText.Contains("\"") && !optionText.Contains("<");
                            if (safe) options.Add(optionText);
                            else Warn("option dropped from choices (needs escaping): " + optionText);
                        }
                        else if (child.Tag == "optgroup")
                        {
                            foreach (HtmlNode option in child.Children)
                            {
                                if (option.Tag != "option") continue;
                                string optionText = InnerText(option);
                                if (optionText.Length > 0 && !optionText.Contains(",") && !optionText.Contains("\"")) options.Add(optionText);
                            }
                        }
                    }
                    if (options.Count > 0) attributes.Add(Pair("choices", string.Join(",", options.ToArray())));
                    else Warn("select without usable options: " + Describe(node));
                    textOnly = true;
                    break;
                }

                case "progress":
                    element = "ui:ProgressBar";
                    textOnly = true;
                    break;

                case "input":
                {
                    string type = node.Get("type", "text");
                    switch (type)
                    {
                        case "range": element = "ui:Slider"; break;
                        case "number": element = "ui:IntegerField"; break;
                        case "checkbox": element = "ui:Toggle"; break;
                        case "radio": element = "ui:RadioButton"; break;
                        case "submit":
                        case "button":
                        case "reset": element = "ui:Button"; break;
                        default: element = "ui:TextField"; break;
                    }
                    textOnly = true;
                    break;
                }

                case "img":
                    element = "ui:VisualElement";
                    Warn("img has no sprite to point at: " + Describe(node));
                    textOnly = true;
                    break;

                default:
                    if (LabelTags.Contains(node.Tag))
                    {
                        element = "ui:Label";
                        labelLike = true;
                    }
                    else
                    {
                        if (!BlockTags.Contains(node.Tag) && node.Tag != "body" && node.Tag != "html")
                        {
                            Warn("unknown element mapped to a container: <" + node.Tag + ">");
                        }
                        element = "ui:VisualElement";
                    }
                    break;
            }

            // Class list, including the data-key marker the runtime controller reads.
            string classAttribute = node.Get("class");
            if (!string.IsNullOrEmpty(classAttribute))
            {
                foreach (string cls in classAttribute.Split(' '))
                {
                    if (cls.Trim().Length > 0) classes.Add(cls.Trim());
                }
            }

            string dataKey = node.Get("data-key");
            if (!string.IsNullOrEmpty(dataKey)) classes.Add("data-key:" + dataKey);
            if (classes.Count > 0) attributes.Insert(0, Pair("class", string.Join(" ", classes.ToArray())));

            string id = node.Get("id");
            if (!string.IsNullOrEmpty(id)) attributes.Add(Pair("id", id));

            if (node.Tag == "input")
            {
                string type = node.Get("type", "text");
                if (type == "range")
                {
                    string min = node.Get("min");
                    string max = node.Get("max");
                    if (min != null) attributes.Add(Pair("low-value", min));
                    if (max != null) attributes.Add(Pair("high-value", max));
                }
                // `name` arrives through the passthrough list; adding it here too
                // emits a duplicate attribute and Unity rejects the whole document.
                if (node.Has("checked")) attributes.Add(Pair("checked", ""));
            }

            foreach (string name in PassthroughAttributes)
            {
                if (!node.Has(name)) continue;
                if (name == "type" && node.Tag != "input" && node.Tag != "button") continue;
                string value = node.Get(name);
                if (value == null) continue;
                attributes.Add(Pair(name, value));
            }

            // Mixed content ("Chapter 3 — <strong>The Foundry</strong> …") cannot live in
            // one text attribute without losing order, so those children are emitted in
            // sequence instead: text runs and inline elements each become a Label.
            bool hasInline = false;
            foreach (HtmlNode child in node.Children)
            {
                if (child.IsText) continue;
                if (IsInlineSkip(child)) continue;
                hasInline = true;
            }

            bool textInChildren = labelLike && hasInline;
            string text = null;
            if ((labelLike || textOnly || node.Tag == "button") && !textInChildren)
            {
                var direct = new StringBuilder();
                foreach (HtmlNode child in node.Children)
                {
                    if (child.IsText)
                    {
                        if (!textOnly && direct.Length > 0) direct.Append(' ');
                        if (!textOnly) direct.Append(child.Text);
                    }
                    else if (child.Tag == "br" && !textOnly)
                    {
                        direct.Append(' ');
                    }
                }
                text = direct.ToString().Trim();
            }

            foreach (HtmlNode child in node.Children)
            {
                if (child.IsText)
                {
                    bool keep = textInChildren || (!labelLike && !textOnly && node.Tag != "button");
                    if (keep && child.Text.Trim().Length > 0) children.Add(child);
                    continue;
                }
                if (IsInlineSkip(child)) continue;
                children.Add(child);
            }

            if (textOnly) children.Clear();

            // Inline runs are siblings, so the label keeps them on one flowing line
            // instead of stacking each phrase vertically.
            if (textInChildren) attributes.Add(Pair("style", "flex-direction: row; flex-wrap: wrap;"));

            var line = new StringBuilder();
            line.Append(indent).Append('<').Append(element);
            if (!string.IsNullOrEmpty(text)) line.Append(" text=\"").Append(Escape(text)).Append('"');
            foreach (KeyValuePair<string, string> attribute in attributes)
            {
                line.Append(' ').Append(attribute.Key).Append("=\"").Append(Escape(attribute.Value)).Append('"');
            }

            if (children.Count == 0)
            {
                line.Append(" />\n");
                xml.Append(line);
                return;
            }

            line.Append(">\n");
            xml.Append(line);
            foreach (HtmlNode child in children) Write(child, depth + 1, xml);
            xml.Append(indent).Append("</").Append(element).Append(">\n");
        }

        static bool IsInlineSkip(HtmlNode node)
        {
            return node.Tag == "option" || node.Tag == "optgroup" || node.Tag == "br" || Skipped.Contains(node.Tag);
        }

        static readonly string[] PassthroughAttributes =
        {
            "for", "href", "name", "type", "value", "min", "max", "placeholder", "datetime",
            "aria-label", "title", "alt", "role",
        };

        static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        static string InnerText(HtmlNode node)
        {
            var text = new StringBuilder();
            foreach (HtmlNode child in node.Children)
            {
                if (child.IsText) text.Append(child.Text);
                else text.Append(InnerText(child));
            }
            return text.ToString().Trim();
        }

        static string Describe(HtmlNode node)
        {
            string id = node.Get("id");
            string cls = node.Get("class");
            return "<" + node.Tag + (id != null ? " id=\"" + id + "\"" : "") + (cls != null ? " class=\"" + cls + "\"" : "") + ">";
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("\n", " ")
                .Replace("\r", "")
                .Replace("\t", " ");
        }

        void Warn(string message)
        {
            if (_reported.Add(message)) Warnings.Add(message);
        }
    }
}
