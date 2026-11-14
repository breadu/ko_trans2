using System.Globalization;
using System.Windows.Data;

namespace KoTrans.App;

public sealed class DefaultProfileConverter : DependencyObject, IMultiValueConverter
{
    public static readonly DependencyProperty DefaultProfileIdProperty = DependencyProperty.Register(nameof(DefaultProfileId), typeof(Guid), typeof(DefaultProfileConverter));
    public Guid DefaultProfileId { get => (Guid)GetValue(DefaultProfileIdProperty); set => SetValue(DefaultProfileIdProperty, value); }
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is Guid profile && values[1] is Guid selectedDefault && profile == selectedDefault;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
