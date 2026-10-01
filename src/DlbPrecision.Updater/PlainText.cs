using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DlbPrecision.Updater
{
    // Release notes are written in GitHub Markdown; the updater shows them as readable plain text.
    internal static class PlainText
    {
        private static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s*", RegexOptions.CultureInvariant);
        private static readonly Regex Bullet = new Regex(@"^(\s*)[-*+]\s+", RegexOptions.CultureInvariant);
        private static readonly Regex Link = new Regex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
        private static readonly Regex Strong = new Regex(@"(\*\*|__)(.+?)\1", RegexOptions.CultureInvariant);
        private static readonly Regex Code = new Regex(@"`([^`]*)`", RegexOptions.CultureInvariant);

        public static string FromMarkdown(string? markdown, int maximumLength = 4000)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            var lines = new List<string>();
            foreach (string raw in markdown!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = Heading.Replace(raw.TrimEnd(), "");
                line = Bullet.Replace(line, "$1• ");
                line = Link.Replace(line, "$1");
                line = Strong.Replace(line, "$2");
                line = Code.Replace(line, "$1");
                if (line.Length == 0 && (lines.Count == 0 || lines[lines.Count - 1].Length == 0)) continue;
                lines.Add(line);
            }
            string text = string.Join("\r\n", lines).Trim();
            return text.Length > maximumLength ? text.Substring(0, maximumLength - 1).TrimEnd() + "…" : text;
        }
    }
}
