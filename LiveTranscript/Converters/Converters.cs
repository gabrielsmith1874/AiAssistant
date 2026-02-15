using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LiveTranscript.Converters
{
    /// <summary>
    /// Converts a speaker label string to a distinct color brush.
    /// </summary>
    public class SpeakerToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush[] SpeakerColors = new[]
        {
            new SolidColorBrush(Color.FromRgb(0, 210, 211)),   // Cyan
            new SolidColorBrush(Color.FromRgb(255, 107, 157)),  // Rose
            new SolidColorBrush(Color.FromRgb(255, 195, 0)),    // Amber
            new SolidColorBrush(Color.FromRgb(130, 255, 130)),  // Lime
            new SolidColorBrush(Color.FromRgb(180, 130, 255)),  // Lavender
            new SolidColorBrush(Color.FromRgb(255, 160, 90)),   // Tangerine
            new SolidColorBrush(Color.FromRgb(100, 200, 255)),  // Sky
            new SolidColorBrush(Color.FromRgb(255, 130, 220)),  // Pink
        };

        static SpeakerToColorConverter()
        {
            foreach (var brush in SpeakerColors)
                brush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string speaker || string.IsNullOrEmpty(speaker))
                return SpeakerColors[0];

            // Stable hash to speaker index
            int hash = Math.Abs(speaker.GetHashCode());
            return SpeakerColors[hash % SpeakerColors.Length];
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Returns reduced opacity for partial (non-final) transcript entries.
    /// </summary>
    public class FinalToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isFinal)
                return isFinal ? 1.0 : 0.6;
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts bool to Visibility (true=Visible, false=Collapsed).
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
