using System.Globalization;
using System.Windows.Data;
namespace KoTrans.App;
internal sealed class ProviderNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TranslationProvider provider ? TranslationProviderCatalog.Get(provider).DisplayName : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
