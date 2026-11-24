using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReplioDesktop.Views.Controls;

/// <summary>
/// Screenshot thumbnail: the file is decoded at a small width off the UI thread and read fully into memory, so a gallery
/// of full-screen JPEGs opens without a pause and the files stay free to open or delete. Recent thumbnails are cached.
/// </summary>
public sealed class ThumbnailImage : Image
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path),
        typeof(string),
        typeof(ThumbnailImage),
        new PropertyMetadata(null, (d, _) => ((ThumbnailImage)d).Load()));

    private const int DecodeWidth = 360;
    private const int MaxCached = 60;
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();
    private int _version;

    public ThumbnailImage()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
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

        if (Cache.TryGetValue(path, out ImageSource? cached))
        {
            Source = cached;
            return;
        }

        Source = null;
        ImageSource? image = await Task.Run(() => Decode(path));
        if (image == null || version != _version)
        {
            return;
        }

        if (!Cache.ContainsKey(path))
        {
            Cache[path] = image;
            CacheOrder.Enqueue(path);
            while (CacheOrder.Count > MaxCached)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }

        Source = image;
    }

    private static ImageSource? Decode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
