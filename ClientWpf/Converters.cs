// ============================================================
//  Converters.cs — value converters for the WPF UI
// ============================================================
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ChatApp.ClientWpf
{
    public class InitialsConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
        {
            string s = (value as string) ?? "";
            return Initials(s);
        }
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "·";
            name = name.TrimStart('@').Trim();
            var parts = name.Split(new[] { ' ', '\t', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "·";
            if (parts.Length == 1)
                return parts[0].Length == 1
                    ? parts[0].ToUpperInvariant()
                    : parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return ("" + parts[0][0] + parts[1][0]).ToUpperInvariant();
        }
    }

    public class AvatarBrushConverter : IValueConverter
    {
        // pastel palette matching the design
        static readonly Color[] Palette = new[]
        {
            Color.FromRgb(0xB1,0xAB,0x86),  // olive
            Color.FromRgb(0xD9,0xF0,0xBC),  // soft green
            Color.FromRgb(0x9C,0xC9,0x7A),  // bright green
            Color.FromRgb(0xE8,0xDD,0xAC),  // warm beige
            Color.FromRgb(0xBE,0xCE,0xA0),
        };

        public object Convert(object value, Type t, object parameter, CultureInfo c)
        {
            string s = (value as string) ?? "";
            int h = 0;
            foreach (var ch in s) h = h * 31 + ch;
            var color = Palette[Math.Abs(h) % Palette.Length];
            return new SolidColorBrush(color);
        }
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c)
            => (value is Visibility v && v == Visibility.Visible);
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c)
            => Binding.DoNothing;
    }

    public class TimeConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
        {
            if (value is DateTime dt)
                return (dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt).ToString("HH:mm");
            return "";
        }
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
    }

    public class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
            => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
    }

    public class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type t, object parameter, CultureInfo c)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
    }
}
