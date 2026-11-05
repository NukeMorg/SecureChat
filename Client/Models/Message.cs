using System;
using System.Windows;

namespace SecureChat.Client.Models;

// Сообщение в диалоге: текст, файл, статус, отправитель.
public class DialogMessage
{
    public string Id { get; set; }
    public int SenderId { get; set; }
    public int ReceiverId { get; set; }
    public string Text { get; set; }
    public string FilePath { get; set; }
    public string ContentType { get; set; } // "Text" или "File"
    public string Status { get; set; }
    public bool IsOutgoing { get; set; }
    public DateTime Timestamp { get; set; }

    // Каналы: имя отправителя над текстом (у Direct-сообщений пустое)
    public string SenderName { get; set; } = "";
    public bool HasSenderName => !string.IsNullOrEmpty(SenderName);

    // Reply и маркер редактирования (Фаза D)
    public string? ReplyToMessageId { get; set; }
    public string ReplyPreview { get; set; } = "";
    public bool HasReply => !string.IsNullOrEmpty(ReplyPreview);
    public bool IsEdited { get; set; }
    public string EditedMark => IsEdited ? " · изм." : "";

    // Размер файла, отформатированный с локализованными единицами (Лаба 8)
    public string FileSizeText { get; set; } = "";
    public bool HasFileSize => !string.IsNullOrEmpty(FileSizeText);

    // Видимость блоков в зависимости от типа содержимого
    public Visibility TextVisibility => ContentType == "Text" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisibility => ContentType == "File" ? Visibility.Visible : Visibility.Collapsed;
}