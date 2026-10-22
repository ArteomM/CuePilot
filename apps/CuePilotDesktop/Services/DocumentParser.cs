using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExcelDataReader;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace CuePilotDesktop.Services;

/// <summary>
/// Turns attached documents into Markdown for the topic knowledge base.
/// Text formats are read locally; pictures (and scanned PDF pages) go to the vision model through <c>recognizeImage</c>.
/// </summary>
public static class DocumentParser
{
    public static readonly string[] SupportedExtensions = [".txt", ".md", ".docx", ".doc", ".xlsx", ".xls", ".pdf", ".jpg", ".jpeg", ".png"];

    /// <summary>Filter for the open-file dialog.</summary>
    public const string FileDialogFilter = "*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.txt;*.md;*.jpg;*.jpeg;*.png|*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.txt;*.md;*.jpg;*.jpeg;*.png";

    private const long MaxFileBytes = 60L * 1024 * 1024;
    private const int MaxCharsPerFile = 250_000;
    private const int MaxRowsPerSheet = 5000;
    private const int MaxScannedPdfPages = 8;
    private const int MaxFolderFiles = 400;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
    private static readonly string[] SkippedFolders = ["node_modules", "bin", "obj", ".git", ".vs", "$recycle.bin"];

    static DocumentParser()
    {
        // ExcelDataReader needs the legacy code pages for .xls files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>Supported files of a folder and its subfolders, skipping hidden, system and build folders.</summary>
    public static IEnumerable<string> EnumerateFolder(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        int yielded = 0;
        while (pending.Count > 0 && yielded < MaxFolderFiles)
        {
            string current = pending.Pop();
            string[] files;
            string[] subfolders;
            try
            {
                files = Directory.GetFiles(current);
                subfolders = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = System.IO.Path.GetFileName(file);
                if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith('.') || !IsSupported(file))
                {
                    continue;
                }

                yielded++;
                yield return file;
                if (yielded >= MaxFolderFiles)
                {
                    yield break;
                }
            }

            foreach (string subfolder in subfolders.OrderByDescending(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = System.IO.Path.GetFileName(subfolder);
                if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(subfolder) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                pending.Push(subfolder);
            }
        }
    }

    /// <summary>Markdown body of one file (without a title heading).</summary>
    public static async Task<string> ParseFileAsync(
        string path,
        Func<string, CancellationToken, Task<string>> recognizeImage,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(path);
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidOperationException($"{info.Name}: > {MaxFileBytes / 1024 / 1024} MB");
        }

        string extension = info.Extension.ToLowerInvariant();
        string text = extension switch
        {
            ".txt" or ".md" => ReadText(path),
            ".docx" => ReadDocx(path),
            ".doc" => ReadLegacyDoc(path),
            ".xlsx" or ".xls" => ReadWorkbook(path),
            ".pdf" => await ReadPdfAsync(path, recognizeImage, cancellationToken),
            ".jpg" or ".jpeg" or ".png" => await recognizeImage(path, cancellationToken),
            _ => throw new NotSupportedException(extension)
        };

        text = NormalizeWhitespace(text);
        if (text.Length > MaxCharsPerFile)
        {
            text = text[..MaxCharsPerFile] + "\n\n…";
        }

        return text;
    }

    private static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            // Strict UTF-8 first; old Russian text files are often Windows-1251.
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Paragraphs, headings, lists and tables of a Word document.</summary>
    private static string ReadDocx(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml");
        XDocument document;
        using (Stream stream = entry.Open())
        {
            document = XDocument.Load(stream);
        }

        XElement body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("w:body");
        var builder = new StringBuilder();
        foreach (XElement block in body.Elements())
        {
            if (block.Name == W + "p")
            {
                AppendDocxParagraph(builder, block);
            }
            else if (block.Name == W + "tbl")
            {
                AppendDocxTable(builder, block);
            }
        }

        return builder.ToString();
    }

    private static void AppendDocxParagraph(StringBuilder builder, XElement paragraph)
    {
        string text = DocxText(paragraph).Trim();
        if (text.Length == 0)
        {
            return;
        }

        XElement? properties = paragraph.Element(W + "pPr");
        string style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? string.Empty;
        bool isList = properties?.Element(W + "numPr") != null || style.Contains("List", StringComparison.OrdinalIgnoreCase);
        Match heading = Regex.Match(style, @"(?:Heading|Заголовок|berschrift)\s*(\d)", RegexOptions.IgnoreCase);
        if (heading.Success && int.TryParse(heading.Groups[1].Value, out int level))
        {
            builder.Append(new string('#', Math.Clamp(level + 1, 2, 6))).Append(' ').AppendLine(text).AppendLine();
        }
        else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("## ").AppendLine(text).AppendLine();
        }
        else if (isList)
        {
            builder.Append("- ").AppendLine(text);
        }
        else
        {
            builder.AppendLine(text).AppendLine();
        }
    }

    private static string DocxText(XElement element)
    {
        var builder = new StringBuilder();
        foreach (XElement node in element.Descendants())
        {
            if (node.Name == W + "t")
            {
                builder.Append(node.Value);
            }
            else if (node.Name == W + "tab")
            {
                builder.Append('\t');
            }
            else if (node.Name == W + "br" || node.Name == W + "cr")
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static void AppendDocxTable(StringBuilder builder, XElement table)
    {
        var rows = table.Elements(W + "tr")
            .Select(row => row.Elements(W + "tc").Select(cell => DocxText(cell).Trim()).ToList())
            .Where(row => row.Count > 0)
            .ToList();
        AppendMarkdownTable(builder, rows);
    }

    /// <summary>
    /// Word 97–2003: the text from the document's piece table; Microsoft Word (when installed) and
    /// the readable text runs of the file are fallbacks for unusual files.
    /// </summary>
    private static string ReadLegacyDoc(string path)
    {
        try
        {
            string text = ReadWord97Text(path);
            if (text.Trim().Length > 0)
            {
                return FormatWordText(text);
            }
        }
        catch (Exception)
        {
            // Encrypted, very old (Word 6/95) or damaged file: try the other ways.
        }

        string? viaWord = TryReadWithWord(path);
        if (!string.IsNullOrWhiteSpace(viaWord))
        {
            return viaWord;
        }

        byte[] bytes = File.ReadAllBytes(path);
        string unicode = ExtractRuns(Encoding.Unicode.GetString(bytes));
        string ansi = ExtractRuns(Encoding.GetEncoding(1251).GetString(bytes));
        string best = unicode.Length >= ansi.Length ? unicode : ansi;
        if (best.Length < 40)
        {
            throw new InvalidDataException("Не удалось извлечь текст из .doc. Сохраните файл как .docx.");
        }

        return best;
    }

    /// <summary>Reads the text pieces listed in the CLX of a Word 97+ binary document ([MS-DOC] 2.4.1).</summary>
    private static string ReadWord97Text(string path)
    {
        using OpenMcdf.RootStorage root = OpenMcdf.RootStorage.OpenRead(path);
        byte[] wordDocument = ReadAll(root.OpenStream("WordDocument"));
        if (BitConverter.ToUInt16(wordDocument, 0) != 0xA5EC)
        {
            throw new InvalidDataException("Not a Word 97+ document.");
        }

        ushort flags = BitConverter.ToUInt16(wordDocument, 0x0A);
        if ((flags & 0x0100) != 0)
        {
            throw new InvalidDataException("Encrypted document.");
        }

        string tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        byte[] table = ReadAll(root.OpenStream(tableName));

        int position = 32;
        int csw = BitConverter.ToUInt16(wordDocument, position);
        position += 2 + csw * 2;
        int cslw = BitConverter.ToUInt16(wordDocument, position);
        position += 2 + cslw * 4;
        position += 2; // cbRgFcLcb
        int fcClx = BitConverter.ToInt32(wordDocument, position + 33 * 8);
        int lcbClx = BitConverter.ToInt32(wordDocument, position + 33 * 8 + 4);
        if (lcbClx <= 0 || fcClx < 0 || fcClx + lcbClx > table.Length)
        {
            throw new InvalidDataException("No piece table.");
        }

        int clx = fcClx;
        while (table[clx] == 0x01)
        {
            clx += 3 + BitConverter.ToInt16(table, clx + 1);
        }

        if (table[clx] != 0x02)
        {
            throw new InvalidDataException("Bad piece table.");
        }

        int lcb = BitConverter.ToInt32(table, clx + 1);
        int plc = clx + 5;
        int pieces = (lcb - 4) / 12;
        var builder = new StringBuilder();
        Encoding ansi = Encoding.GetEncoding(1252);
        for (int i = 0; i < pieces; i++)
        {
            int cpStart = BitConverter.ToInt32(table, plc + i * 4);
            int cpEnd = BitConverter.ToInt32(table, plc + (i + 1) * 4);
            int pcd = plc + (pieces + 1) * 4 + i * 8;
            uint fc = BitConverter.ToUInt32(table, pcd + 2);
            int count = cpEnd - cpStart;
            if (count <= 0)
            {
                continue;
            }

            bool compressed = (fc & 0x40000000) != 0;
            int offset = (int)(fc & 0x3FFFFFFF);
            if (compressed)
            {
                offset /= 2;
                count = Math.Min(count, wordDocument.Length - offset);
                builder.Append(ansi.GetString(wordDocument, offset, Math.Max(0, count)));
            }
            else
            {
                count = Math.Min(count * 2, wordDocument.Length - offset);
                builder.Append(Encoding.Unicode.GetString(wordDocument, offset, Math.Max(0, count)));
            }
        }

        return CleanWordText(builder.ToString());
    }

    /// <summary>Paragraphs of a Word 97 text become Markdown paragraphs; rows of table cells become a table.</summary>
    private static string FormatWordText(string text)
    {
        var builder = new StringBuilder();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Contains('\t'))
            {
                // A cell ends with a cell mark and a row with one more, so rows are separated by two tabs.
                List<List<string>> rows = line.Split("\t\t", StringSplitOptions.RemoveEmptyEntries)
                    .Select(row => row.Split('\t').Select(cell => cell.Trim()).ToList())
                    .Where(row => row.Any(cell => cell.Length > 0))
                    .ToList();
                if (rows.Count > 0 && rows.Max(r => r.Count) >= 2)
                {
                    AppendMarkdownTable(builder, rows);
                    continue;
                }
            }

            builder.AppendLine(line.Trim()).AppendLine();
        }

        return builder.ToString();
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    /// <summary>Drops field codes and control characters of Word text; paragraph and cell marks become line breaks.</summary>
    private static string CleanWordText(string text)
    {
        var builder = new StringBuilder(text.Length);
        int fieldDepth = 0;
        foreach (char c in text)
        {
            switch (c)
            {
                case '\u0013':
                    fieldDepth++;
                    continue;
                case '\u0014':
                    fieldDepth = Math.Max(0, fieldDepth - 1);
                    continue;
                case '\u0015':
                    continue;
            }

            if (fieldDepth > 0)
            {
                continue;
            }

            builder.Append(c switch
            {
                '\r' or '\v' or '\f' => '\n',
                '\a' => '\t',
                _ when char.IsControl(c) && c != '\t' && c != '\n' => ' ',
                _ => c
            });
        }

        return builder.ToString();
    }

    private static string? TryReadWithWord(string path)
    {
        Type? wordType = Type.GetTypeFromProgID("Word.Application");
        if (wordType == null)
        {
            return null;
        }

        dynamic? word = null;
        dynamic? document = null;
        try
        {
            word = Activator.CreateInstance(wordType);
            if (word == null)
            {
                return null;
            }

            word.Visible = false;
            word.DisplayAlerts = 0;
            document = word.Documents.Open(path, false, true, false);
            string text = (string)document.Content.Text;
            return text.Replace('\r', '\n').Replace('\a', ' ');
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                document?.Close(false);
            }
            catch (Exception)
            {
            }

            try
            {
                word?.Quit(false);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Keeps runs of letters, digits and punctuation that look like real sentences.</summary>
    private static string ExtractRuns(string decoded)
    {
        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(decoded, @"[\p{L}\p{N}][\p{L}\p{N}\p{P}\p{Zs}\t«»№%+=<>/\\-]{18,}"))
        {
            string run = match.Value.Trim();
            int letters = run.Count(char.IsLetter);
            if (letters >= run.Length / 2)
            {
                builder.AppendLine(run);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Every sheet as a Markdown table under its name. Title lines above the real header row become text,
    /// empty rows and columns are dropped, and when the workbook is too big every sheet keeps a fair share of rows.
    /// </summary>
    private static string ReadWorkbook(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream);
        DataSet data = reader.AsDataSet();
        var sheets = new List<(string Name, List<string> Titles, List<List<string>> Rows, int Dropped)>();
        foreach (DataTable sheet in data.Tables)
        {
            var rows = new List<List<string>>();
            int dropped = 0;
            foreach (DataRow row in sheet.Rows)
            {
                List<string> cells = row.ItemArray.Select(FormatCell).ToList();
                if (!cells.Any(c => c.Length > 0))
                {
                    continue;
                }

                if (rows.Count >= MaxRowsPerSheet)
                {
                    dropped++;
                    continue;
                }

                rows.Add(cells);
            }

            if (rows.Count == 0)
            {
                continue;
            }

            // The header is the first row that fills most of the columns; lines above it are the sheet's title.
            int widest = rows.Take(15).Max(r => r.Count(c => c.Length > 0));
            int headerIndex = rows.FindIndex(r => r.Count(c => c.Length > 0) >= Math.Max(2, (int)Math.Ceiling(widest * 0.6)));
            if (headerIndex < 0 || headerIndex > 14)
            {
                headerIndex = 0;
            }

            List<string> titles = rows.Take(headerIndex).Select(r => string.Join(" · ", r.Where(c => c.Length > 0))).ToList();
            rows = rows.Skip(headerIndex).ToList();
            int width = rows.Max(r => r.Count);
            var usedColumns = Enumerable.Range(0, width).Where(c => rows.Any(r => c < r.Count && r[c].Length > 0)).ToList();
            rows = rows.Select(r => usedColumns.Select(c => c < r.Count ? r[c] : string.Empty).ToList()).ToList();
            sheets.Add((sheet.TableName, titles, rows, dropped));
        }

        // A long first sheet must not push the others out of the size limit.
        int total = sheets.Sum(s => s.Rows.Sum(r => r.Sum(c => c.Length + 3)));
        int budget = MaxCharsPerFile - 2000;
        var builder = new StringBuilder();
        foreach ((string name, List<string> titles, List<List<string>> rows, int dropped) in sheets)
        {
            int sheetChars = rows.Sum(r => r.Sum(c => c.Length + 3));
            int share = total <= budget ? int.MaxValue : Math.Max(4000, (int)((long)budget * sheetChars / Math.Max(1, total)));
            var kept = new List<List<string>>();
            int used = 0;
            foreach (List<string> row in rows)
            {
                int length = row.Sum(c => c.Length + 3);
                if (kept.Count > 1 && used + length > share)
                {
                    break;
                }

                kept.Add(row);
                used += length;
            }

            builder.Append("## ").AppendLine(name).AppendLine();
            foreach (string title in titles)
            {
                builder.Append("**").Append(title.Replace("*", string.Empty)).AppendLine("**").AppendLine();
            }

            AppendMarkdownTable(builder, kept);
            int missing = rows.Count - kept.Count + dropped;
            if (missing > 0)
            {
                builder.Append("_… ещё ").Append(missing).AppendLine(" строк не вошло: полностью они есть в исходном файле_").AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string FormatCell(object? value) => value switch
    {
        null or DBNull => string.Empty,
        DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("dd.MM.yyyy") : date.ToString("dd.MM.yyyy HH:mm"),
        double number => number.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString()?.Trim() ?? string.Empty
    };

    private static void AppendMarkdownTable(StringBuilder builder, List<List<string>> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (builder.Length > 1 && !(builder[^1] == '\n' && builder[^2] == '\n'))
        {
            builder.AppendLine();
        }

        int width = rows.Max(r => r.Count);
        if (width == 1)
        {
            foreach (List<string> row in rows)
            {
                builder.AppendLine(row[0]);
            }

            builder.AppendLine();
            return;
        }

        static string Cell(string text) => text.Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ');
        for (int i = 0; i < rows.Count; i++)
        {
            List<string> row = rows[i];
            builder.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, width).Select(c => Cell(c < row.Count ? row[c] : string.Empty)))).AppendLine(" |");
            if (i == 0)
            {
                builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).AppendLine();
            }
        }

        builder.AppendLine();
    }

    /// <summary>Text layer page by page; pages without text (scans) are recognized from their images, in page order.</summary>
    private static async Task<string> ReadPdfAsync(
        string path,
        Func<string, CancellationToken, Task<string>> recognizeImage,
        CancellationToken cancellationToken)
    {
        List<(int Number, string? Markdown, byte[]? Image)> pages = ReadPdfPages(path, out bool numbered);
        var builder = new StringBuilder();
        foreach ((int number, string? markdown, byte[]? image) in pages)
        {
            if (image == null)
            {
                builder.Append(markdown);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string imagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cuepilot-pdf-{Guid.NewGuid():N}.{(IsPng(image) ? "png" : "jpg")}");
            await File.WriteAllBytesAsync(imagePath, image, cancellationToken);
            try
            {
                string recognized = await recognizeImage(imagePath, cancellationToken);
                builder.Append(numbered ? $"_стр. {number}, скан_\n\n" : string.Empty).AppendLine(recognized.Trim()).AppendLine();
            }
            finally
            {
                TryDelete(imagePath);
            }
        }

        if (builder.ToString().Trim().Length == 0)
        {
            throw new InvalidDataException("В PDF нет текста и распознаваемых страниц.");
        }

        return builder.ToString();
    }

    /// <summary>Markdown of every page with a text layer, or the page image of a scan to be recognized.</summary>
    private static List<(int Number, string? Markdown, byte[]? Image)> ReadPdfPages(string path, out bool numbered)
    {
        var result = new List<(int Number, string? Markdown, byte[]? Image)>();
        using PdfDocument document = PdfDocument.Open(path);
        var pages = new List<(Page Page, List<PdfLine>? Lines, string Text)>();
        var sizes = new Dictionary<double, int>();
        foreach (Page page in document.GetPages())
        {
            List<PdfLine>? lines = ReadPdfLines(page);
            string text = lines == null ? ContentOrderTextExtractor.GetText(page).Trim() : string.Join("\n", lines.Select(l => l.Text));
            foreach (PdfLine line in (lines ?? []).Where(l => l.Size > 0))
            {
                sizes[line.Size] = sizes.GetValueOrDefault(line.Size) + line.Text.Length;
            }

            pages.Add((page, lines, text));
        }

        // The most common font size is the body text; noticeably larger short lines are headings.
        double bodySize = sizes.Count == 0 ? 0 : sizes.MaxBy(pair => pair.Value).Key;
        numbered = pages.Count > 1;
        int scans = 0;
        foreach ((Page page, List<PdfLine>? lines, string text) in pages)
        {
            var builder = new StringBuilder();
            if (text.Trim().Length >= 20)
            {
                if (numbered)
                {
                    builder.Append("_стр. ").Append(page.Number).AppendLine("_").AppendLine();
                }

                if (lines == null)
                {
                    builder.AppendLine(text).AppendLine();
                }
                else
                {
                    AppendPdfLines(builder, lines, bodySize);
                }

                result.Add((page.Number, builder.ToString(), null));
                continue;
            }

            if (scans >= MaxScannedPdfPages)
            {
                result.Add((page.Number, $"_стр. {page.Number}: скан, распознаются первые {MaxScannedPdfPages} страниц_\n\n", null));
                continue;
            }

            IPdfImage? image = page.GetImages().OrderByDescending(i => i.WidthInSamples * i.HeightInSamples).FirstOrDefault();
            if (image != null && image.TryGetPng(out byte[]? png) && png != null)
            {
                scans++;
                result.Add((page.Number, null, png));
            }
            else if (image != null && image.RawBytes.Length > 2 && image.RawBytes[0] == 0xFF && image.RawBytes[1] == 0xD8)
            {
                // JPEG scans are stored as-is (DCTDecode) and can be sent without re-encoding.
                scans++;
                result.Add((page.Number, null, image.RawBytes.ToArray()));
            }
            else
            {
                result.Add((page.Number, $"_стр. {page.Number}: нет текстового слоя_\n\n", null));
            }
        }

        return result;
    }

    /// <summary>One line of a PDF page in reading order: its text, rounded font size and the block it belongs to.</summary>
    private sealed record PdfLine(string Text, double Size, int Block);

    private static readonly Regex PdfMarkerOnly = new(@"^([•●▪■◦○·‣∙\-–—*]|\d{1,2}[.)]?)$", RegexOptions.Compiled);

    /// <summary>
    /// Lines of the page grouped into text blocks (Docstrum) in reading order; null when layout analysis fails.
    /// Bullet and number glyphs that the layout puts into a block of their own are moved back to their lines,
    /// and short cells laid out in rows and columns become a Markdown table.
    /// </summary>
    private static List<PdfLine>? ReadPdfLines(Page page)
    {
        try
        {
            IReadOnlyList<Word> words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            if (words.Count == 0)
            {
                return [];
            }

            List<TextBlock> blocks = DetachIndentedTail(UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words)).ToList());
            var prefixes = new Dictionary<TextLine, string>();
            var skipped = new HashSet<TextBlock>();
            foreach (TextBlock markers in blocks.Where(b => b.TextLines.All(l => PdfMarkerOnly.IsMatch(l.Text.Trim()))))
            {
                foreach (TextLine marker in markers.TextLines)
                {
                    TextLine? target = blocks
                        .Where(b => b != markers && b.BoundingBox.Left > marker.BoundingBox.Right - 1)
                        .SelectMany(b => b.TextLines)
                        .Where(l => Math.Abs(l.BoundingBox.Bottom - marker.BoundingBox.Bottom) < 4 && l.BoundingBox.Left - marker.BoundingBox.Right < 40)
                        .OrderBy(l => l.BoundingBox.Left)
                        .FirstOrDefault();
                    if (target != null)
                    {
                        string glyph = marker.Text.Trim();
                        prefixes[target] = char.IsDigit(glyph[0]) ? glyph.TrimEnd('.', ')') + ". " : "• ";
                    }
                }

                if (markers.TextLines.All(l => prefixes.Keys.Any(t => Math.Abs(t.BoundingBox.Bottom - l.BoundingBox.Bottom) < 4)))
                {
                    skipped.Add(markers);
                }
            }

            Dictionary<TextBlock, List<string[]>> tables = FindPdfTables(blocks.Where(b => !skipped.Contains(b)).ToList(), out HashSet<TextBlock> cells);
            var lines = new List<PdfLine>();
            int blockIndex = 0;
            foreach (TextBlock block in blocks)
            {
                if (skipped.Contains(block) || (cells.Contains(block) && !tables.ContainsKey(block)))
                {
                    continue;
                }

                if (tables.TryGetValue(block, out List<string[]>? table))
                {
                    var markdown = new StringBuilder();
                    AppendMarkdownTable(markdown, table.Select(r => r.ToList()).ToList());
                    lines.Add(new PdfLine(markdown.ToString().TrimEnd(), -1, blockIndex++));
                    continue;
                }

                foreach (TextLine line in block.TextLines)
                {
                    string text = string.Join(" ", line.Words.Select(w => w.Text)).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    List<double> letterSizes = line.Words.SelectMany(w => w.Letters).Select(l => l.PointSize).Where(size => size > 0).ToList();
                    double size = letterSizes.Count == 0 ? 0 : Math.Round(letterSizes.Average() * 2) / 2;
                    lines.Add(new PdfLine(prefixes.GetValueOrDefault(line, string.Empty) + text, size, blockIndex));
                }

                blockIndex++;
            }

            return lines;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The layout sometimes glues the first cell of a table to the paragraph above it: a short last line that starts
    /// far to the right of the paragraph becomes a block of its own.
    /// </summary>
    private static List<TextBlock> DetachIndentedTail(List<TextBlock> blocks)
    {
        var result = new List<TextBlock>(blocks.Count);
        foreach (TextBlock block in blocks)
        {
            IReadOnlyList<TextLine> lines = block.TextLines;
            if (lines.Count >= 2)
            {
                TextLine last = lines[^1];
                double left = lines.Take(lines.Count - 1).Min(l => l.BoundingBox.Left);
                if (last.BoundingBox.Left - left > 30 && last.Text.Trim().Length <= 60)
                {
                    result.Add(new TextBlock(lines.Take(lines.Count - 1).ToList()));
                    result.Add(new TextBlock([last]));
                    continue;
                }
            }

            result.Add(block);
        }

        return result;
    }

    /// <summary>
    /// Single-line short blocks that line up in at least two rows and two columns: a table drawn without a text flow.
    /// The table is keyed by its first cell in reading order; <paramref name="cells"/> holds every block used.
    /// </summary>
    private static Dictionary<TextBlock, List<string[]>> FindPdfTables(List<TextBlock> blocks, out HashSet<TextBlock> cells)
    {
        var tables = new Dictionary<TextBlock, List<string[]>>();
        cells = [];
        List<TextBlock> candidates = blocks.Where(b => b.TextLines.Count == 1 && b.Text.Trim().Length is > 0 and <= 80).ToList();
        var rows = new List<List<TextBlock>>();
        foreach (TextBlock block in candidates.OrderByDescending(b => b.BoundingBox.Bottom))
        {
            List<TextBlock>? row = rows.FirstOrDefault(r => Math.Abs(r[0].BoundingBox.Bottom - block.BoundingBox.Bottom) < 3);
            if (row == null)
            {
                rows.Add([block]);
            }
            else
            {
                row.Add(block);
            }
        }

        rows = rows.Where(r => r.Count >= 2).Select(r => r.OrderBy(b => b.BoundingBox.Left).ToList()).ToList();
        int i = 0;
        while (i < rows.Count)
        {
            // Rows of one table have the same number of cells starting at the same columns.
            int j = i + 1;
            while (j < rows.Count && SameColumns(rows[i], rows[j]))
            {
                j++;
            }

            if (j - i >= 2)
            {
                List<List<TextBlock>> tableRows = rows.GetRange(i, j - i);
                TextBlock first = blocks.First(b => tableRows.Any(r => r.Contains(b)));
                tables[first] = tableRows.Select(r => r.Select(b => b.Text.Trim()).ToArray()).ToList();
                foreach (TextBlock cell in tableRows.SelectMany(r => r))
                {
                    cells.Add(cell);
                }
            }

            i = j;
        }

        return tables;
    }

    private static bool SameColumns(List<TextBlock> a, List<TextBlock> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => Math.Abs(pair.First.BoundingBox.Left - pair.Second.BoundingBox.Left) < 14);

    private static readonly Regex PdfBullet = new(@"^([•●▪■◦○·‣∙\-–—*])\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex PdfNumbered = new(@"^(\d{1,2}[.)])\s+(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Paragraphs (wrapped lines joined, hyphenation removed), headings by font size and bullet or numbered lists,
    /// so the parsed PDF reads like the original document.
    /// </summary>
    private static void AppendPdfLines(StringBuilder builder, List<PdfLine> lines, double bodySize)
    {
        var paragraph = new StringBuilder();
        string? listMarker = null;

        void Flush()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            if (listMarker != null)
            {
                builder.Append(listMarker).Append(' ').AppendLine(paragraph.ToString().Trim());
            }
            else
            {
                if (builder.Length > 0 && builder[^1] == '\n' && (builder.Length < 2 || builder[^2] != '\n') && LastLineIsListItem(builder))
                {
                    builder.AppendLine();
                }

                builder.AppendLine(paragraph.ToString().Trim()).AppendLine();
            }

            paragraph.Clear();
        }

        int index = 0;
        while (index < lines.Count)
        {
            int block = lines[index].Block;
            List<PdfLine> blockLines = lines.Skip(index).TakeWhile(l => l.Block == block).ToList();
            index += blockLines.Count;

            if (blockLines[0].Size < 0)
            {
                Flush();
                listMarker = null;
                if (builder.Length > 0 && !(builder[^1] == '\n' && builder.Length > 1 && builder[^2] == '\n'))
                {
                    builder.AppendLine();
                }

                builder.AppendLine(blockLines[0].Text).AppendLine();
                continue;
            }

            string blockText = string.Join(" ", blockLines.Select(l => l.Text));
            double blockSize = blockLines.Max(l => l.Size);
            bool isHeading = bodySize > 0 && blockSize >= bodySize * 1.18 && blockLines.Count <= 3 && blockText.Length <= 160;
            if (isHeading)
            {
                Flush();
                listMarker = null;
                if (builder.Length > 0 && !(builder[^1] == '\n' && builder.Length > 1 && builder[^2] == '\n'))
                {
                    builder.AppendLine();
                }

                builder.Append(blockSize >= bodySize * 1.5 ? "## " : "### ").AppendLine(blockText.Trim()).AppendLine();
                continue;
            }

            foreach (PdfLine line in blockLines)
            {
                Match bullet = PdfBullet.Match(line.Text);
                Match number = PdfNumbered.Match(line.Text);
                if ((bullet.Success && bullet.Groups[2].Value.Length > 0) || number.Success)
                {
                    Flush();
                    listMarker = number.Success ? number.Groups[1].Value.TrimEnd(')') .TrimEnd('.') + "." : "-";
                    paragraph.Append(number.Success ? number.Groups[2].Value : bullet.Groups[2].Value);
                    continue;
                }

                if (bullet.Success)
                {
                    // A lone bullet glyph: the item text follows on the next line.
                    Flush();
                    listMarker = "-";
                    continue;
                }

                AppendWrapped(paragraph, line.Text);
            }

            // A block ends a paragraph; a list item may continue only inside its block.
            Flush();
            listMarker = null;
        }

        Flush();
        if (builder.Length > 0 && builder[^1] == '\n' && (builder.Length < 2 || builder[^2] != '\n'))
        {
            builder.AppendLine();
        }
    }

    private static bool LastLineIsListItem(StringBuilder builder)
    {
        int end = builder.Length - 1;
        int start = end - 1;
        while (start >= 0 && builder[start] != '\n')
        {
            start--;
        }

        string last = builder.ToString(start + 1, end - start - 1);
        return last.StartsWith("- ", StringComparison.Ordinal) || PdfNumbered.IsMatch(last);
    }

    /// <summary>Joins a wrapped line to the paragraph, removing the hyphen of a word split across lines.</summary>
    private static void AppendWrapped(StringBuilder paragraph, string line)
    {
        if (paragraph.Length == 0)
        {
            paragraph.Append(line);
            return;
        }

        if (paragraph[^1] == '-' && paragraph.Length > 1 && char.IsLetter(paragraph[^2]) && line.Length > 0 && char.IsLower(line[0]))
        {
            paragraph.Length--;
            paragraph.Append(line);
            return;
        }

        paragraph.Append(' ').Append(line);
    }

    private static bool IsPng(byte[] bytes) => bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
