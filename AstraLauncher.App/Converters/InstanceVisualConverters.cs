using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstraLauncher.App.Converters;

/// <summary>Maps a version id to the official Minecraft Wiki block render for its loader.</summary>
public sealed class InstanceIconConverter : IValueConverter
{
    private static readonly ImageSource Vanilla = Load("vanilla.png");
    private static readonly ImageSource Forge = Load("forge.png");
    private static readonly ImageSource NeoForge = Load("neoforge.png");
    private static readonly ImageSource Fabric = Load("fabric.png");
    private static readonly ImageSource Quilt = Load("quilt.png");
    private static readonly ImageSource OptiFine = Load("optifine.png");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string version = value as string ?? string.Empty;
        if (version.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return NeoForge;
        }

        if (version.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return Forge;
        }

        if (version.Contains("quilt", StringComparison.OrdinalIgnoreCase))
        {
            return Quilt;
        }

        if (version.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return Fabric;
        }

        if (version.Contains("optifine", StringComparison.OrdinalIgnoreCase))
        {
            return OptiFine;
        }

        return Vanilla;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static ImageSource Load(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/AstraLauncher.App;component/Assets/Icons/{fileName}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>Maps a version id to a short loader label (Forge / NeoForge / Fabric / Quilt / 原版).</summary>
public sealed class InstanceLoaderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string version = value as string ?? string.Empty;
        if (version.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return "NeoForge";
        }

        if (version.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "Forge";
        }

        if (version.Contains("quilt", StringComparison.OrdinalIgnoreCase))
        {
            return "Quilt";
        }

        if (version.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return "Fabric";
        }

        if (version.Contains("optifine", StringComparison.OrdinalIgnoreCase))
        {
            return "OptiFine";
        }

        return "原版";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
