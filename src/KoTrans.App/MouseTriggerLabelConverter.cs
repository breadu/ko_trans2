using System.Globalization;
using System.Windows.Data;

namespace KoTrans.App;

// Display only: the existing settings, input parser and runtime still use English mouse tokens.
internal sealed class MouseTriggerLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            "None" => "없음",
            "Left" => "왼쪽 클릭",
            "Right" => "오른쪽 클릭",
            "Middle" => "가운데 클릭 (휠 클릭)",
            "X1" => "측면 버튼 1",
            "X2" => "측면 버튼 2",
            _ => value?.ToString() ?? ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
