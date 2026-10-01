using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DlbPrecision.Updater
{
    // Release notes are written in GitHub Markdown; the updater shows them as readable plain text.
    internal static class PlainText
    {
        // Notes come from the release page, so input is bounded before any pattern runs: some patterns slow
        // down sharply on long runs of brackets, and the window must never freeze on unusual notes.
        private const int MaximumInput = 20000;
        private const int MaximumLine = 1000;
        private static readonly TimeSpan PatternLimit = TimeSpan.FromMilliseconds(250);
        private static readonly Regex Heading = new Regex(@"^\s{0,3}#{1,6}\s*", RegexOptions.CultureInvariant, PatternLimit);
        private static readonly Regex Bullet = new Regex(@"^(\s*)[-*+]\s+", RegexOptions.CultureInvariant, PatternLimit);
        private static readonly Regex Link = new Regex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, PatternLimit);
        private static readonly Regex Strong = new Regex(@"(\*\*|__)(.+?)\1", RegexOptions.CultureInvariant, PatternLimit);
        private static readonly Regex Code = new Regex(@"`([^`]*)`", RegexOptions.CultureInvariant, PatternLimit);

        public static string FromMarkdown(string? markdown, int maximumLength = 4000)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            string input = markdown!.Length > MaximumInput ? markdown.Substring(0, MaximumInput) : markdown;
            var lines = new List<string>();
            foreach (string raw in input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Length > MaximumLine ? raw.Substring(0, MaximumLine) : raw;
                line = line.TrimEnd();
                try
                {
                    line = Heading.Replace(line, "");
                    line = Bullet.Replace(line, "$1• ");
                    line = Link.Replace(line, "$1");
                    line = Strong.Replace(line, "$2");
                    line = Code.Replace(line, "$1");
                }
                catch (RegexMatchTimeoutException)
                {
                    // Leave this one line unformatted rather than hold up the window.
                }
                if (line.Length == 0 && (lines.Count == 0 || lines[lines.Count - 1].Length == 0)) continue;
                lines.Add(line);
            }
            string text = string.Join("\r\n", lines).Trim();
            return text.Length > maximumLength ? text.Substring(0, maximumLength - 1).TrimEnd() + "…" : text;
        }
    }
}
