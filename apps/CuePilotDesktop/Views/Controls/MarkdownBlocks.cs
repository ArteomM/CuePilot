using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views.Controls;

public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Code,
    Rule,
    Note,
    TableRows,
    Record
}

/// <summary>
/// One block of a Markdown document. Big tables are split into chunks of rows (<see cref="MarkdownBlockKind.TableRows"/>)
/// that share column weights, and tables too wide to read as a grid become one record per row.
/// </summary>
public sealed class MarkdownBlock
{
    public MarkdownBlockKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public int Level { get; init; }
    public string Marker { get; init; } = string.Empty;
    public string[] Header { get; init; } = [];
    public IReadOnlyList<string[]> Rows { get; init; } = [];
    public double[] ColumnWeights { get; init; } = [];
    public bool ShowHeader { get; init; }
    public bool IsLast { get; init; }
}

/// <summary>
/// Parses Markdown into blocks without touching the UI framework, so long documents can be parsed off the UI thread
/// (shared with the Avalonia app; inline formatting lives in MarkdownInlines.cs).
/// </summary>
public static class MarkdownBlocks
{
    public const int TableChunkRows = 25;

    /// <summary>Tables with more columns are shown as one record per row: a grid that narrow is unreadable.</summary>
    public const int MaxGridColumns = 6;

    private static readonly Regex OrderedItem = new(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletItem = new(@"^(\s*)[-*•+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleLine = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    public static List<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        string[] lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Code, Text = string.Join("\n", code) });
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                string[] header = SplitRow(trimmed);
                var rows = new List<string[]>();
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    rows.Add(SplitRow(lines[i].Trim()));
                }

                i--;
                AddTable(blocks, header, rows);
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                int level = trimmed.TakeWhile(c => c == '#').Count();
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Heading, Level = Math.Min(level, 6), Text = trimmed[level..].Trim() });
                continue;
            }

            if (RuleLine.IsMatch(trimmed))
            {
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Rule });
                continue;
            }

            if (trimmed.Length > 2 && trimmed[0] == '_' && trimmed[^1] == '_' && trimmed[1] != '_')
            {
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Note, Text = trimmed[1..^1] });
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Quote, Text = trimmed.TrimStart('>').Trim() });
                continue;
            }

            Match ordered = OrderedItem.Match(line);
            Match bullet = BulletItem.Match(line);
            if (ordered.Success || bullet.Success)
            {
                string indent = ordered.Success ? ordered.Groups[1].Value : bullet.Groups[1].Value;
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.ListItem,
                    Level = Math.Min(indent.Replace("\t", "  ").Length / 2, 4),
                    Marker = ordered.Success ? ordered.Groups[2].Value + "." : "•",
                    Text = (ordered.Success ? ordered.Groups[3].Value : bullet.Groups[2].Value).Trim()
                });
                continue;
            }

            // Consecutive plain lines belong to one paragraph.
            var paragraph = new List<string> { trimmed };
            while (i + 1 < lines.Length && IsPlainContinuation(lines[i + 1]))
            {
                i++;
                paragraph.Add(lines[i].Trim());
            }

            blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph, Text = string.Join("\n", paragraph) });
        }

        return blocks;
    }

    private static void AddTable(List<MarkdownBlock> blocks, string[] header, List<string[]> rows)
    {
        int width = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        header = Pad(header, width);
        rows = rows.Select(r => Pad(r, width)).ToList();
        if (width > MaxGridColumns)
        {
            foreach (string[] row in rows)
            {
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Record, Header = header, Rows = [row] });
            }

            return;
        }

        // Column widths follow the longest text of each column, so chunks of one table line up.
        double[] weights = Enumerable.Range(0, width)
            .Select(c => (double)Math.Clamp(rows.Append(header).Max(r => r[c].Length), 4, 48))
            .ToArray();
        if (rows.Count == 0)
        {
            blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.TableRows, Header = header, ColumnWeights = weights, ShowHeader = true, IsLast = true });
            return;
        }

        for (int start = 0; start < rows.Count; start += TableChunkRows)
        {
            blocks.Add(new MarkdownBlock
            {
                Kind = MarkdownBlockKind.TableRows,
                Header = header,
                Rows = rows.Skip(start).Take(TableChunkRows).ToList(),
                ColumnWeights = weights,
                ShowHeader = start == 0,
                IsLast = start + TableChunkRows >= rows.Count
            });
        }
    }

    private static string[] Pad(string[] row, int width) =>
        row.Length >= width ? row : row.Concat(Enumerable.Repeat(string.Empty, width - row.Length)).ToArray();

    private static bool IsPlainContinuation(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0 &&
            !trimmed.StartsWith('#') &&
            !trimmed.StartsWith('|') &&
            !trimmed.StartsWith('>') &&
            !trimmed.StartsWith("```", StringComparison.Ordinal) &&
            !RuleLine.IsMatch(trimmed) &&
            !OrderedItem.IsMatch(line) &&
            !BulletItem.IsMatch(line);
    }

    private static string[] SplitRow(string row)
    {
        string inner = row.Trim();
        if (inner.StartsWith('|'))
        {
            inner = inner[1..];
        }

        if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal))
        {
            inner = inner[..^1];
        }

        return Regex.Split(inner, @"(?<!\\)\|").Select(c => c.Replace("\\|", "|").Trim()).ToArray();
    }
}

/// <summary>
/// [text](url) links in rendered Markdown. Parsed documents and AI answers can carry any link, so a click opens only
/// web pages and attached audio or video; any other file is shown in its folder instead of being started.
/// </summary>
public static class MarkdownLinks
{
    /// <summary>Group 1: the text, group 2: the address (http, https or file).</summary>
    public const string Pattern = @"\[([^\]\n]+)\]\((https?://[^)\s]+|file:///[^)\s]+)\)";

    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        try
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                Start(uri.AbsoluteUri);
            }
            else if (uri.IsFile && File.Exists(uri.LocalPath) && MediaTranscriptionService.IsMedia(uri.LocalPath))
            {
                Start(uri.LocalPath);
            }
            else if (uri.IsFile && Path.GetDirectoryName(uri.LocalPath) is { } folder && Directory.Exists(folder))
            {
                Start(folder);
            }
        }
        catch (Exception)
        {
            // No program for the file: the link just does nothing.
        }
    }

    private static void Start(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
