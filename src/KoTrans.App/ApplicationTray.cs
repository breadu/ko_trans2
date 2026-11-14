using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace KoTrans.App;

internal interface IApplicationTray : IDisposable
{
    bool IsAvailable { get; }
    event Action? OpenRequested;
    event Action? ExitRequested;
    // Optional for non-UI test trays. Native tray implements the live command.
    void SetTranslationCommand(string caption, bool enabled, Action? execute) { }
}

// Created only after the real main window renders. Background tests inject the
// tray contract and never construct NotifyIcon or a native context menu.
internal sealed class ApplicationTray : IApplicationTray
{
    private readonly Forms.NotifyIcon notify;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Drawing.Icon icon;
    private readonly Forms.ToolStripMenuItem translation;
    private Action? translationCommand;
    private bool disposed;
    public bool IsAvailable => !disposed && notify.Visible;
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public ApplicationTray()
    {
        icon = LoadIcon();
        Forms.ContextMenuStrip? createdMenu = null;
        Forms.NotifyIcon? createdNotify = null;
        try {
            menu = createdMenu = new Forms.ContextMenuStrip();
            menu.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke());
            translation = new Forms.ToolStripMenuItem("번역 시작", null, (_, _) => translationCommand?.Invoke());
            menu.Items.Add(translation);
            menu.Items.Add("종료", null, (_, _) => ExitRequested?.Invoke());
            notify = createdNotify = new Forms.NotifyIcon { Icon = icon, Text = "KO Trans 2", ContextMenuStrip = menu };
            notify.DoubleClick += (_, _) => OpenRequested?.Invoke();
            notify.Visible = true;
        }
        catch { createdNotify?.Dispose(); createdMenu?.Dispose(); icon.Dispose(); throw; }
    }
    internal static Drawing.Icon LoadIcon()
    {
        using var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/KoTrans2;component/Assets/kotrans-app-icon.ico"))?.Stream
            ?? throw new InvalidOperationException("알림 아이콘 리소스를 찾을 수 없습니다.");
        using var loaded = new Drawing.Icon(resource);
        return (Drawing.Icon)loaded.Clone();
    }
    public void SetTranslationCommand(string caption, bool enabled, Action? execute)
    {
        if (disposed) return;
        translation.Text = caption; translation.Enabled = enabled; translationCommand = execute;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; notify.Visible = false; notify.Dispose(); menu.Dispose(); icon.Dispose();
        OpenRequested = ExitRequested = null;
        translationCommand = null;
    }
}
