namespace KoTrans.App;

public partial class MainWindow
{
    private DeveloperSupportWindow? developerSupport;
    internal Action<DeveloperSupportWindow>? PresentDeveloperSupportOverride { get; set; }

    private void OpenDeveloperSupport(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        if (developerSupport is { } current)
        {
            if (current.IsVisible) current.Activate();
            return;
        }
        if (!IsVisible && PresentDeveloperSupportOverride is null) return;
        var dialog = new DeveloperSupportWindow();
        if (IsVisible) dialog.Owner = this;
        developerSupport = dialog;
        try
        {
            if (PresentDeveloperSupportOverride is { } present) present(dialog);
            else dialog.ShowDialog();
        }
        finally { if (ReferenceEquals(developerSupport, dialog)) developerSupport = null; }
    }
}
