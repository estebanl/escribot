using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Escribot.Api.Common;

/// <summary>
/// Validates and sanitizes SVG markup using a strict allowlist of safe presentation
/// elements and attributes so generated or uploaded SVGs cannot carry scripts, external
/// resource loads, or XML entity attacks.
/// </summary>
public static partial class SvgSanitizer
{
    public const int MaxRawSvgChars = 512_000;

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XlinkNs = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        // Structure & metadata
        "svg", "g", "defs", "symbol", "title", "desc", "clippath", "mask", "pattern", "marker", "style",
        // Shapes
        "rect", "circle", "ellipse", "line", "polyline", "polygon", "path",
        // Text
        "text", "tspan", "textpath",
        // Gradients
        "lineargradient", "radialgradient", "stop",
        // Safe filter primitives (no feImage)
        "filter", "fedropshadow", "fegaussianblur", "feoffset", "feblend",
        "fecolormatrix", "fecomposite", "feflood", "femerge", "femergenode",
        // Local fragment reuse only (href validated separately)
        "use",
    };

    private static readonly HashSet<string> DrawableElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "rect", "circle", "ellipse", "line", "polyline", "polygon", "path", "text",
    };

    private static readonly HashSet<string> AllowedNamespaces = new(StringComparer.Ordinal)
    {
        string.Empty,
        SvgNs.NamespaceName,
        XlinkNs.NamespaceName,
        XNamespace.Xml.NamespaceName,
    };

    /// <summary>
    /// Parses and sanitizes an SVG string. Returns normalized UTF-8 SVG markup, or null
    /// if the input is not valid SVG or contains no visible drawable elements.
    /// </summary>
    public static string? Sanitize(string? rawSvg)
    {
        if (string.IsNullOrWhiteSpace(rawSvg) || rawSvg.Length > MaxRawSvgChars)
        {
            return null;
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxRawSvgChars,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var stringReader = new StringReader(rawSvg.Trim());
            using var xmlReader = XmlReader.Create(stringReader, settings);
            doc = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!AllowedNamespaces.Contains(root.Name.NamespaceName))
        {
            return null;
        }

        SanitizeElement(root, isRoot: true);

        // Normalize root namespace and ensure viewBox exists for responsive <img> rendering.
        EnsureSvgRootMetadata(root);

        var hasDrawable = root.Descendants()
            .Any(el => DrawableElements.Contains(el.Name.LocalName));
        if (!hasDrawable)
        {
            return null;
        }

        var output = new StringBuilder();
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            Encoding = Encoding.UTF8,
        };
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            root.Save(writer);
        }

        return output.ToString();
    }

    /// <summary>
    /// Extracts the first <c>&lt;svg&gt;...&lt;/svg&gt;</c> block from free-form text (such as
    /// Markdown fences or prose around SVG markup) and sanitizes it.
    /// </summary>
    public static string? ExtractAndSanitize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = SvgBlockPattern().Match(rawText);
        return match.Success ? Sanitize(match.Value) : null;
    }

    private static void SanitizeElement(XElement element, bool isRoot)
    {
        // Strip comments, processing instructions, and disallowed child elements.
        foreach (var node in element.Nodes().ToList())
        {
            switch (node)
            {
                case XComment or XProcessingInstruction:
                    node.Remove();
                    break;
                case XElement child:
                    if (!AllowedElements.Contains(child.Name.LocalName)
                        || !AllowedNamespaces.Contains(child.Name.NamespaceName))
                    {
                        child.Remove();
                        continue;
                    }

                    SanitizeElement(child, isRoot: false);
                    break;
            }
        }

        // Sanitize CSS inside <style> elements.
        if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var cleanedCss = SanitizeCss(element.Value);
            if (string.IsNullOrWhiteSpace(cleanedCss))
            {
                element.Remove();
                return;
            }

            element.Value = cleanedCss;
        }

        // Filter attributes.
        foreach (var attr in element.Attributes().ToList())
        {
            var localName = attr.Name.LocalName;
            var ns = attr.Name.NamespaceName;

            if (attr.IsNamespaceDeclaration)
            {
                var uri = attr.Value.Trim();
                if (!isRoot || (uri != SvgNs.NamespaceName && uri != XlinkNs.NamespaceName))
                {
                    attr.Remove();
                }

                continue;
            }

            // Block any event handler (onload, onclick, onerror, ...).
            if (localName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                attr.Remove();
                continue;
            }

            if (!AllowedNamespaces.Contains(ns))
            {
                attr.Remove();
                continue;
            }

            // href / xlink:href may only point to an internal #fragment.
            if (localName.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                if (!LocalFragmentPattern().IsMatch(attr.Value.Trim()))
                {
                    attr.Remove();
                }

                continue;
            }

            if (localName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = SanitizeCss(attr.Value);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    attr.Remove();
                }
                else
                {
                    attr.Value = cleaned;
                }

                continue;
            }

            if (HasUnsafeValue(attr.Value))
            {
                attr.Remove();
            }
        }

        // A <use> element without a valid local #href is useless; drop it.
        if (element.Name.LocalName.Equals("use", StringComparison.OrdinalIgnoreCase)
            && !element.Attributes().Any(a => a.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)))
        {
            element.Remove();
        }
    }

    private static void EnsureSvgRootMetadata(XElement root)
    {
        if (root.Name.Namespace != SvgNs)
        {
            SetNamespaceRecursive(root, SvgNs);
        }

        if (root.Attribute("xmlns") is null)
        {
            root.Add(new XAttribute("xmlns", SvgNs.NamespaceName));
        }

        if (root.Attribute("viewBox") is null)
        {
            var width = ParseDimension(root.Attribute("width")?.Value) ?? 900;
            var height = ParseDimension(root.Attribute("height")?.Value) ?? 560;
            root.SetAttributeValue("viewBox", $"0 0 {width} {height}");
        }
    }

    private static void SetNamespaceRecursive(XElement element, XNamespace ns)
    {
        if (string.IsNullOrEmpty(element.Name.NamespaceName))
        {
            element.Name = ns + element.Name.LocalName;
        }

        foreach (var child in element.Elements())
        {
            SetNamespaceRecursive(child, ns);
        }
    }

    private static int? ParseDimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = LeadingNumberPattern().Match(value.Trim());
        if (!match.Success || !double.TryParse(match.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        var rounded = (int)Math.Round(parsed);
        return rounded is > 0 and <= 10_000 ? rounded : null;
    }

    private static bool HasUnsafeValue(string value)
    {
        if (UnsafeSchemePattern().IsMatch(value))
        {
            return true;
        }

        // Only allow url(#local-id) references inside fill/stroke/clip-path/filter/marker-*.
        foreach (Match match in CssUrlPattern().Matches(value))
        {
            var target = match.Groups[1].Value.Trim().Trim('\'', '"');
            if (!LocalFragmentPattern().IsMatch(target))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return string.Empty;
        }

        var cleaned = DangerousCssRulePattern().Replace(css, string.Empty);
        cleaned = CssUrlPattern().Replace(cleaned, match =>
        {
            var target = match.Groups[1].Value.Trim().Trim('\'', '"');
            return LocalFragmentPattern().IsMatch(target) ? $"url({target})" : "none";
        });
        cleaned = UnsafeSchemePattern().Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }

    [GeneratedRegex(@"<svg\b[^>]*>[\s\S]*?</svg>", RegexOptions.IgnoreCase)]
    private static partial Regex SvgBlockPattern();

    [GeneratedRegex(@"^#[A-Za-z_][A-Za-z0-9_.:-]*$")]
    private static partial Regex LocalFragmentPattern();

    [GeneratedRegex(@"^\d+(?:\.\d+)?")]
    private static partial Regex LeadingNumberPattern();

    [GeneratedRegex(@"(?:javascript|vbscript|data)\s*:|https?://|expression\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSchemePattern();

    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlPattern();

    [GeneratedRegex(@"@import[^;]*;?|behavior\s*:[^;]*;?|-moz-binding\s*:[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousCssRulePattern();
}
