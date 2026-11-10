using System.Windows;
using System.Windows.Controls;
using SecureChat.Client.Models;

namespace SecureChat.Client;

/// <summary>
/// Единый список чатов: шаблон выбирается по типу элемента (контакт или канал).
/// </summary>
public class ChatItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ContactTemplate { get; set; }
    public DataTemplate? ChannelTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ChannelItem ? ChannelTemplate : ContactTemplate;
}
