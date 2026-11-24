using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Replio.Avalonia.Views.Controls;

/// <summary>
/// Screenshot thumbnail: the file is decoded at a small width off the UI thread, so a gallery of full-screen pictures
/// opens without a pause and the files stay free to open or delete. Recent thumbnails are cached.
/// </summary>
public sealed class ThumbnailImage : Image
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ThumbnailImage, string?>(nameof(Path));

    private const int DecodeWidth = 360;
    private const int MaxCached = 60;
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();
    private int _version;

    static ThumbnailImage()
    {
        PathProperty.Changed.AddClassHandler<ThumbnailImage>((image, _) => image.Load());
    }

    public ThumbnailImage()
    {
        Stretch = Stretch.Uniform;
    }

    protected override Type StyleKeyOverride => typeof(Image);

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    private async void Load()
    {
        int version = ++_version;
        string? path = Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            Source = null;
            return;
        }

        if (Cache.TryGetValue(path, out Bitmap? cached))
        {
            Source = cached;
            return;
        }

        Source = null;
        Bitmap? bitmap = await Task.Run(() => Decode(path));
        if (bitmap == null || version != _version)
        {
            return;
        }

        if (!Cache.ContainsKey(path))
        {
            Cache[path] = bitmap;
            CacheOrder.Enqueue(path);
            while (CacheOrder.Count > MaxCached)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }

        Source = bitmap;
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
