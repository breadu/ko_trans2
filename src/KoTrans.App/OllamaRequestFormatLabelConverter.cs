using System.Globalization;
using System.Windows.Data;
using KoTrans.Core;

namespace KoTrans.App;

// Display-only descriptions. ComboBox items and persisted values remain OllamaRequestFormat enums.
internal sealed class OllamaRequestFormatLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            OllamaRequestFormat.Chat => "Chat (일반 대화형 모델)",
            OllamaRequestFormat.TranslateGemma => "TranslateGemma (번역 전용 모델)",
            OllamaRequestFormat.HyMt => "HY-MT (번역 전용 모델)",
            _ => value?.ToString() ?? ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
