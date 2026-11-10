using System.IO;
using System.Windows.Media.Imaging;

namespace SecureChat.Client.Services;

/// <summary>
/// Подготовка аватара: любой поддерживаемый WPF формат (PNG/JPEG/BMP)
/// декодируется и перекодируется в PNG.
/// Сервер принимает только PNG ≤ 2 МБ (проверка магических байт).
/// </summary>
public static class AvatarEncoder
{
    public const int MaxAvatarBytes = 2 * 1024 * 1024;

    /// <exception cref="InvalidOperationException">Файл слишком большой или не является изображением.</exception>
    public static byte[] EncodeToPng(string filePath)
    {
        var source = new FileInfo(filePath);
        if (source.Length > MaxAvatarBytes)
            throw new InvalidOperationException("Файл аватара больше 2 МБ.");

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            using (var stream = source.OpenRead())
            {
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            throw new InvalidOperationException("Не удалось распознать изображение.", ex);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        byte[] png = output.ToArray();

        if (png.Length > MaxAvatarBytes)
            throw new InvalidOperationException("Аватар после перекодирования больше 2 МБ.");
        return png;
    }
}
