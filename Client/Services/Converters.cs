using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace SecureChat.Client.Services
{
    // Конвертер bool → HorizontalAlignment (Right для исходящих, Left для входящих).
    public class BoolToHorizontalAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isOutgoing)
                return isOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Конвертер bool → Visibility (Visible если true, Collapsed если false).
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Конвертер, извлекающий имя файла из полного пути.
    public class FileNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path)
                return Path.GetFileName(path);
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}