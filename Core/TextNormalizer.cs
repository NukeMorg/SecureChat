using System.Text;

namespace SecureChat.Core;

/// <summary>
/// NFC-нормализация (NormalizationForm.FormC) выполняется
/// на клиенте перед шифрованием, чтобы одинаковый видимый текст всегда
/// давал одинаковый шифртекст и корректный клиентский поиск.
/// </summary>
public static class TextNormalizer
{
    public static string NormalizeToNfc(string text) =>
        text.Normalize(NormalizationForm.FormC);
}
