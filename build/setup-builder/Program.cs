using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SetupCuePilot;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using var form = new SetupForm();
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Установка завершилась ошибкой:\n" + ex.Message, Installer.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

/// <summary>Per-user install: files, shortcuts, uninstall entry. No administrator rights required.</summary>
internal static class Installer
{
    public const string AppName = "CuePilot";
    public const string AppExe = "CuePilotDesktop.exe";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CuePilot";
    private const string UninstallScriptName = "uninstall.cmd";
    private static readonly string[] AppProcessNames = { "CuePilotDesktop", "DesktopCapture" };

    // Speech recognition started by DesktopCapture: it can outlive a killed DesktopCapture and keeps ggml-*.dll locked.
    private static readonly string[] SpeechProcessNames = { "whisper-server", "whisper-cli" };

    public static string Version
    {
        get
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string Title => $"Установка {AppName} {Version}";

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppName);

    /// <summary>Folder used by installers before 1.0.0.</summary>
    private static string LegacyInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName,
        "app");

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        AppName + ".lnk");

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        AppName + ".lnk");

    public static Process[] FindRunningApp()
    {
        // Only whisper copies from CuePilot's own folders: a whisper run from elsewhere locks none of our files.
        IEnumerable<Process> speech = SpeechProcessNames.SelectMany(Process.GetProcessesByName).Where(RunsFromInstallFolder);
        return AppProcessNames.SelectMany(Process.GetProcessesByName).Concat(speech).ToArray();
    }

    private static bool RunsFromInstallFolder(Process process)
    {
        string? path = ImagePath(process);
        return path != null && new[] { InstallDirectory, LegacyInstallDirectory }.Any(directory =>
            path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Executable path of a process; unlike Process.MainModule it also works across 32/64-bit.</summary>
    private static string? ImagePath(Process process)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static void StopRunningApp()
    {
        foreach (Process process in FindRunningApp())
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // The process may have exited on its own.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static void Install(bool desktopShortcut, Action<int, string> progress)
    {
        string targetDir = InstallDirectory;
        progress(0, "Подготовка папки установки…");
        PreserveSpeechModels(targetDir);
        CleanDirectory(targetDir);
        Directory.CreateDirectory(targetDir);

        ExtractPayload(targetDir, progress);

        progress(97, "Создание ярлыков…");
        string exePath = Path.Combine(targetDir, AppExe);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(AppExe + " не найден после распаковки.", exePath);
        }

        CreateShortcut(StartMenuShortcutPath, exePath, targetDir);
        if (desktopShortcut)
        {
            CreateShortcut(DesktopShortcutPath, exePath, targetDir);
        }

        progress(99, "Регистрация в «Приложениях»…");
        string uninstallScript = WriteUninstallScript(targetDir);
        RegisterUninstall(targetDir, exePath, uninstallScript);
        RemoveLegacyInstall();
        RefreshShellIcons();
        progress(100, "Готово");
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Explorer caches icons per path: without this, shortcuts keep showing the previous version's icon.</summary>
    private static void RefreshShellIcons()
    {
        const int ShcneAssocChanged = 0x08000000;
        try
        {
            SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Cosmetic only: an old icon until the next sign-in is not worth failing the install.
        }
    }

    public static void Launch()
    {
        string exePath = Path.Combine(InstallDirectory, AppExe);
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = InstallDirectory,
            UseShellExecute = true
        });
    }

    /// <summary>Folder the app downloads speech models into; reinstalls and uninstall leave it alone.</summary>
    private static string SpeechModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName,
        "models");

    /// <summary>
    /// Older installers packed the speech model into the app folder. Move it to the models folder before
    /// the app folder is cleaned, so updating does not make the user download it again.
    /// </summary>
    private static void PreserveSpeechModels(string targetDir)
    {
        string oldModels = Path.Combine(targetDir, "models");
        if (!Directory.Exists(oldModels))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SpeechModelsDirectory);
            foreach (string file in Directory.GetFiles(oldModels, "ggml-*.bin"))
            {
                string target = Path.Combine(SpeechModelsDirectory, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Move(file, target);
                }
            }
        }
        catch (Exception)
        {
            // Not fatal: the model can be downloaded again from Settings.
        }
    }

    /// <summary>Removes files of the previous version; a user's secrets.txt next to the exe is kept.</summary>
    private static void CleanDirectory(string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(targetDir))
        {
            if (!string.Equals(Path.GetFileName(file), "secrets.txt", StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(file);
            }
        }

        foreach (string directory in Directory.GetDirectories(targetDir))
        {
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                DeleteFile(file);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A process that has just been stopped lets go of its files a moment later, so a busy file is retried
    /// for a few seconds; after that the error names the file instead of a bare «ggml-base.dll».
    /// </summary>
    private static void DeleteFile(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(500);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    "Файл занят другой программой:\n" + path +
                    "\n\nЗакройте CuePilot, завершите whisper-server.exe и whisper-cli.exe в Диспетчере задач " +
                    "(или перезагрузите компьютер) и запустите установку снова.",
                    ex);
            }
        }
    }

    private static void ExtractPayload(string targetDir, Action<int, string> progress)
    {
        string rootWithSeparator = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using Stream payloadStream = OpenPayloadStream();
        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
        long totalBytes = Math.Max(1, archive.Entries.Sum(entry => entry.Length));
        long doneBytes = 0;
        int lastPercent = -1;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
            if (!destinationPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Недопустимый путь в архиве: " + entry.FullName);
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);

            doneBytes += entry.Length;
            int percent = (int)(doneBytes * 95 / totalBytes);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress(percent, "Копирование файлов… " + entry.Name);
            }
        }
    }

    private static Stream OpenPayloadStream()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("Установщик повреждён: внутри нет payload.zip.");
    }

    private static void CreateShortcut(string shortcutPath, string exePath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен: ярлык не создан.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = "CuePilot — AI-суфлёр для звонков и встреч";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Uninstall removes the app folder and shortcuts. Sessions and settings in %AppData%\CuePilot are kept.
    /// "(goto) 2>nul" lets the script delete the folder it lives in.
    /// </summary>
    private static string WriteUninstallScript(string targetDir)
    {
        string scriptPath = Path.Combine(targetDir, UninstallScriptName);
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("taskkill /IM CuePilotDesktop.exe /F >nul 2>nul");
        script.AppendLine("taskkill /IM DesktopCapture.exe /F >nul 2>nul");
        string psFolder = targetDir.Replace("'", "''");
        script.AppendLine("powershell -NoProfile -Command \"Get-Process whisper-server,whisper-cli -ErrorAction SilentlyContinue | " +
            $"Where-Object {{ $_.Path -like '{psFolder}\\*' }} | Stop-Process -Force\" >nul 2>nul");
        script.AppendLine($"del /f /q \"{DesktopShortcutPath}\" >nul 2>nul");
        script.AppendLine($"del /f /q \"{StartMenuShortcutPath}\" >nul 2>nul");
        script.AppendLine($"reg delete \"HKCU\\{UninstallKeyPath}\" /f >nul 2>nul");
        script.AppendLine("cd /d \"%TEMP%\"");
        script.AppendLine($"(goto) 2>nul & rd /s /q \"{targetDir}\"");

        // cmd.exe reads batch files in the OEM code page; paths may contain Cyrillic user names.
        Encoding oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        File.WriteAllText(scriptPath, script.ToString(), oem);
        return scriptPath;
    }

    private static void RegisterUninstall(string targetDir, string exePath, string uninstallScript)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath)
            ?? throw new InvalidOperationException("Не удалось записать данные для удаления программы.");
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", exePath + ",0");
        key.SetValue("InstallLocation", targetDir);
        key.SetValue("UninstallString", $"cmd.exe /c \"\"{uninstallScript}\"\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, DirectorySizeKb(targetDir)), RegistryValueKind.DWord);
    }

    private static long DirectorySizeKb(string directory)
    {
        return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) / 1024;
    }

    /// <summary>Earlier installers put only the UI into %LocalAppData%\CuePilot\app; that copy cannot capture audio.</summary>
    private static void RemoveLegacyInstall()
    {
        try
        {
            string legacy = LegacyInstallDirectory;
            if (File.Exists(Path.Combine(legacy, AppExe)))
            {
                Directory.Delete(legacy, recursive: true);
            }
        }
        catch (Exception)
        {
            // A leftover legacy folder is harmless.
        }
    }
}

/// <summary>«Эхо» palette and font shared by the setup window controls.</summary>
internal static class EchoTheme
{
    public static readonly Color Bg = Color.FromArgb(0x0E, 0x11, 0x16);
    public static readonly Color Surface = Color.FromArgb(0x17, 0x1B, 0x22);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x1E, 0x24, 0x2D);
    public static readonly Color Border = Color.FromArgb(0x2A, 0x31, 0x3C);
    public static readonly Color HoverBorder = Color.FromArgb(0x3A, 0x45, 0x53);
    public static readonly Color Text = Color.FromArgb(0xE9, 0xEE, 0xF5);
    public static readonly Color Muted = Color.FromArgb(0x9A, 0xA5, 0xB5);
    public static readonly Color Accent = Color.FromArgb(0x5E, 0xF0, 0xC1);
    public static readonly Color AccentInk = Color.FromArgb(0x0E, 0x11, 0x16);

    private static readonly System.Drawing.Text.PrivateFontCollection Fonts = LoadFonts();

    /// <summary>Manrope embedded in the setup; Segoe UI when it cannot be loaded.</summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular)
    {
        FontFamily? family = Fonts.Families.FirstOrDefault();
        if (family != null && family.IsStyleAvailable(style))
        {
            return new Font(family, size, style, GraphicsUnit.Pixel);
        }

        return new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
    }

    private static System.Drawing.Text.PrivateFontCollection LoadFonts()
    {
        var collection = new System.Drawing.Text.PrivateFontCollection();
        foreach (string name in new[] { "Manrope-Medium.ttf", "Manrope-Bold.ttf" })
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (stream == null)
                {
                    continue;
                }

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);
                // GDI+ keeps reading the font from this memory: it is never freed for the life of the setup.
                IntPtr memory = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, memory, data.Length);
                collection.AddMemoryFont(memory, data.Length);
            }
            catch (Exception)
            {
                // Fall back to Segoe UI.
            }
        }

        return collection;
    }

    /// <summary>GDI+ text: GDI text rendering cannot draw fonts from a PrivateFontCollection.</summary>
    public static void DrawText(Graphics g, string text, Font font, RectangleF rect, Color color,
        StringAlignment horizontal = StringAlignment.Near, StringAlignment vertical = StringAlignment.Center,
        StringTrimming trimming = StringTrimming.EllipsisCharacter)
    {
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = horizontal,
            LineAlignment = vertical,
            Trimming = trimming
        };
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format);
    }

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Max(0, Math.Min(1, amount));
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }
}

/// <summary>Base for the painted controls: double-buffered, smooth hover fade like the app buttons.</summary>
internal abstract class EchoControl : Control
{
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 15 };
    private float _hover;
    private bool _hovering;

    protected EchoControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = EchoTheme.Bg;
        Cursor = Cursors.Hand;
        _fadeTimer.Tick += (_, _) =>
        {
            float target = _hovering && Enabled ? 1f : 0f;
            _hover += Math.Sign(target - _hover) * 0.12f;
            if (Math.Abs(target - _hover) < 0.12f)
            {
                _hover = target;
                _fadeTimer.Stop();
            }

            Invalidate();
        };
    }

    /// <summary>0 when idle, 1 when the cursor is over the control.</summary>
    protected float Hover => _hover;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovering = true;
        _fadeTimer.Start();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovering = false;
        _fadeTimer.Start();
        base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected static Graphics Prepare(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        return e.Graphics;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class EchoButton : EchoControl, IButtonControl
{
    public bool Accent { get; set; }

    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value)
    {
    }

    public void PerformClick()
    {
        if (Enabled)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = Prepare(e);
        g.Clear(BackColor);
        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        float radius = Height * 0.3f;
        Color fill = Accent
            ? EchoTheme.Mix(EchoTheme.Accent, Color.White, Hover * 0.18f)
            : EchoTheme.Mix(EchoTheme.SurfaceAlt, Color.White, Hover * 0.06f);
        if (!Enabled)
        {
            fill = EchoTheme.Mix(fill, BackColor, 0.5f);
        }

        using (GraphicsPath path = EchoTheme.Rounded(rect, radius))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (!Accent)
            {
                using var pen = new Pen(EchoTheme.Mix(EchoTheme.Border, EchoTheme.HoverBorder, Hover), 1);
                g.DrawPath(pen, path);
            }
        }

        Color textColor = Accent ? EchoTheme.AccentInk : EchoTheme.Text;
        using var font = EchoTheme.Font(Height * 0.36f, FontStyle.Bold);
        EchoTheme.DrawText(g, Text, font, rect, textColor, StringAlignment.Center);
    }
}

internal sealed class EchoCheckBox : EchoControl
{
    private bool _checked = true;

    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = Prepare(e);
        g.Clear(BackColor);
        float box = Height * 0.62f;
        var boxRect = new RectangleF(1, (Height - box) / 2, box, box);
        using (GraphicsPath path = EchoTheme.Rounded(boxRect, box * 0.3f))
        {
            Color fill = Checked ? EchoTheme.Accent : EchoTheme.Bg;
            Color border = Checked ? EchoTheme.Accent : EchoTheme.Mix(Color.FromArgb(0x5C, 0x68, 0x78), EchoTheme.Muted, Hover);
            using var brush = new SolidBrush(Enabled ? fill : EchoTheme.Mix(fill, BackColor, 0.5f));
            using var pen = new Pen(border, 1);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (Checked)
        {
            using var pen = new Pen(EchoTheme.AccentInk, Math.Max(2f, box * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(boxRect.X + box * 0.24f, boxRect.Y + box * 0.52f),
                new PointF(boxRect.X + box * 0.43f, boxRect.Y + box * 0.70f),
                new PointF(boxRect.X + box * 0.76f, boxRect.Y + box * 0.30f)
            });
        }

        using var font = EchoTheme.Font(Height * 0.5f);
        var textRect = new RectangleF(box + Height * 0.45f, 0, Width - box - Height * 0.45f, Height);
        EchoTheme.DrawText(g, Text, font, textRect, Enabled ? EchoTheme.Text : EchoTheme.Muted);
    }
}

internal sealed class EchoProgress : Control
{
    private int _value;

    public EchoProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = EchoTheme.Bg;
    }

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Max(0, Math.Min(100, value));
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath path = EchoTheme.Rounded(track, track.Height / 2))
        using (var brush = new SolidBrush(EchoTheme.SurfaceAlt))
        {
            e.Graphics.FillPath(brush, path);
        }

        if (_value > 0)
        {
            var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * _value / 100f), track.Height);
            using GraphicsPath path = EchoTheme.Rounded(fill, fill.Height / 2);
            using var brush = new SolidBrush(EchoTheme.Accent);
            e.Graphics.FillPath(brush, path);
        }
    }
}

internal sealed class SetupForm : Form
{
    private readonly float _scale;
    private readonly EchoCheckBox _desktopShortcutCheck;
    private readonly EchoCheckBox _launchCheck;
    private readonly EchoProgress _progressBar;
    private readonly EchoButton _installButton;
    private readonly EchoButton _cancelButton;
    private readonly EchoButton _closeButton;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _smallFont;
    private string _status = "Без прав администратора. Ваши настройки и ключ API сохранятся.";
    private bool _installed;
    private Point _dragStart;

    public SetupForm()
    {
        _scale = DeviceDpi / 96f;
        Text = Installer.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = EchoTheme.Bg;
        ClientSize = new Size(S(560), S(330));
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;
        ShowInTaskbar = true;

        _titleFont = EchoTheme.Font(S(26), FontStyle.Bold);
        _bodyFont = EchoTheme.Font(S(14));
        _smallFont = EchoTheme.Font(S(13));

        _closeButton = new EchoButton { Text = "✕", Bounds = new Rectangle(S(512), S(14), S(32), S(30)) };
        _desktopShortcutCheck = new EchoCheckBox { Text = "Создать ярлык на рабочем столе", Bounds = new Rectangle(S(28), S(148), S(500), S(28)) };
        _launchCheck = new EchoCheckBox { Text = "Запустить CuePilot после установки", Bounds = new Rectangle(S(28), S(180), S(500), S(28)) };
        _progressBar = new EchoProgress { Bounds = new Rectangle(S(28), S(226), S(504), S(8)) };
        _installButton = new EchoButton { Text = "Установить", Accent = true, Bounds = new Rectangle(S(272), S(274), S(148), S(40)) };
        _cancelButton = new EchoButton { Text = "Отмена", Bounds = new Rectangle(S(428), S(274), S(104), S(40)), DialogResult = DialogResult.Cancel };

        _installButton.Click += InstallButton_OnClick;
        _cancelButton.Click += (_, _) => Close();
        _closeButton.Click += (_, _) => Close();
        AcceptButton = _installButton;
        CancelButton = _cancelButton;
        Controls.AddRange(new Control[]
        {
            _closeButton, _desktopShortcutCheck, _launchCheck, _progressBar, _installButton, _cancelButton
        });
    }

    public bool Succeeded => _installed;

    private int S(float value) => (int)Math.Round(value * _scale);

    protected override CreateParams CreateParams
    {
        get
        {
            // Drop shadow around the borderless window.
            const int CsDropShadow = 0x00020000;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var border = new Pen(EchoTheme.Border, 1))
        {
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        DrawLogo(g, new RectangleF(S(28), S(26), S(52), S(52)));

        float x = S(92);
        using (var typographic = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            float cueWidth = g.MeasureString("Cue", _titleFont, PointF.Empty, typographic).Width;
            EchoTheme.DrawText(g, "Cue", _titleFont, new RectangleF(x, S(22), S(200), S(40)), EchoTheme.Text);
            EchoTheme.DrawText(g, "Pilot", _titleFont, new RectangleF(x + cueWidth + S(2), S(22), S(200), S(40)), EchoTheme.Accent);
        }

        EchoTheme.DrawText(g, "Установка версии " + Installer.Version, _smallFont, new RectangleF(x + S(2), S(60), S(300), S(20)), EchoTheme.Muted);

        var pathRect = new Rectangle(S(28), S(98), S(504), S(36));
        using (GraphicsPath path = EchoTheme.Rounded(pathRect, S(10)))
        using (var fill = new SolidBrush(EchoTheme.Surface))
        using (var pen = new Pen(EchoTheme.Border, 1))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        EchoTheme.DrawText(g, "Папка:  " + Installer.InstallDirectory, _smallFont,
            Rectangle.Inflate(pathRect, -S(12), 0), EchoTheme.Muted, trimming: StringTrimming.EllipsisPath);

        EchoTheme.DrawText(g, _status, _smallFont, new RectangleF(S(28), S(240), S(504), S(24)), EchoTheme.Muted);
    }

    /// <summary>The app logo: a «C» with a mint dot and one echo ring.</summary>
    private static void DrawLogo(Graphics g, RectangleF rect)
    {
        float unit = rect.Width / 120f;
        using (var ring = new Pen(Color.FromArgb(90, EchoTheme.Accent), 2 * unit))
        {
            g.DrawEllipse(ring, rect.X + 14 * unit, rect.Y + 14 * unit, 92 * unit, 92 * unit);
        }

        using (var stroke = new Pen(EchoTheme.Text, 10 * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            // Arc from the upper right through the left side to the lower right, like the app's EchoLogo.
            g.DrawArc(stroke, rect.X + 30 * unit, rect.Y + 30 * unit, 60 * unit, 60 * unit, 45, 270);
        }

        using var dot = new SolidBrush(EchoTheme.Accent);
        g.FillEllipse(dot, rect.X + 78 * unit, rect.Y + 54 * unit, 13 * unit, 13 * unit);
    }

    private void SetStatus(string status)
    {
        _status = status;
        Invalidate(new Rectangle(S(28), S(240), S(504), S(24)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragStart = e.Location;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // The window has no system title bar: drag it by its background.
        if (e.Button == MouseButtons.Left)
        {
            Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }

        base.OnMouseMove(e);
    }

    private void InstallButton_OnClick(object? sender, EventArgs e)
    {
        if (_installed)
        {
            Close();
            return;
        }

        if (Installer.FindRunningApp().Length > 0)
        {
            DialogResult answer = MessageBox.Show(
                "CuePilot сейчас запущен. Закрыть его и продолжить установку?",
                Installer.Title,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                return;
            }

            Installer.StopRunningApp();
        }

        bool desktopShortcut = _desktopShortcutCheck.Checked;
        bool launch = _launchCheck.Checked;
        SetBusy(true);

        var worker = new Thread(() =>
        {
            try
            {
                Installer.Install(desktopShortcut, ReportProgress);
                BeginInvoke(new Action(() => OnInstalled(launch)));
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() => OnFailed(ex)));
            }
        })
        {
            IsBackground = true,
            Name = "CuePilot setup"
        };
        worker.Start();
    }

    private void ReportProgress(int percent, string status)
    {
        BeginInvoke(new Action(() =>
        {
            _progressBar.Value = percent;
            SetStatus(status);
        }));
    }

    private void OnInstalled(bool launch)
    {
        _installed = true;
        SetStatus("CuePilot установлен. Ярлык: рабочий стол и меню «Пуск».");
        _installButton.Text = "Готово";
        _installButton.Enabled = true;
        _cancelButton.Visible = false;
        _closeButton.Enabled = true;
        if (launch)
        {
            try
            {
                Installer.Launch();
            }
            catch (Exception ex)
            {
                MessageBox.Show("CuePilot установлен, но не запустился:\n" + ex.Message, Installer.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OnFailed(Exception ex)
    {
        SetBusy(false);
        _progressBar.Value = 0;
        SetStatus("Ошибка установки.");
        MessageBox.Show("Установка завершилась ошибкой:\n" + ex.Message, Installer.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _closeButton.Enabled = !busy;
        _desktopShortcutCheck.Enabled = !busy;
        _launchCheck.Enabled = !busy;
        UseWaitCursor = busy;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Do not leave a half-copied install behind.
        if (!_installButton.Enabled && !_installed)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
