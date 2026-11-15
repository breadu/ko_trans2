using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using KoTrans.Infrastructure;
namespace KoTrans.App;

internal sealed partial class TranslationWindow : Window
{
    private readonly DictionarySubtitle original;
    private readonly TextBlock translation = new() { Name = "OverlayTranslation", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Name = "OverlayNotice", Visibility = Visibility.Collapsed, FontSize = 12, Foreground = Brushes.LightSkyBlue, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock busy = new() { Name = "OverlayBusy", Text = "⌛", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 17,
        Foreground = new SolidColorBrush(Color.FromRgb(202, 189, 126)), Margin = new Thickness(0, 0, 12, 9),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Profile profile;
    private readonly Dictionary<PipelineStatusArea, string> pipelineErrors = new();
    private string? chromeFailure;
    private string? pipelineWarning;
    private PipelineStatusArea pipelineWarningArea;
    private long pipelineVersion = -1;
    private string reusableTranslation = "";
    private string reusableSource = "";
    internal (string Source, string Translation) SubtitleContent => (original.Text, reusableTranslation);
    internal (string Source, string Translation) RenderedSubtitleContent => (original.Text, translation.Text);
    internal string CurrentExplanationSource => prompt != IdlePrompt.None ? ""
        : profile.ShowOriginal && original.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(original.Text) ? original.Text
        : profile.ShowTranslation && translation.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(translation.Text) ? reusableSource : "";
    private readonly StackPanel textPanel = new() { Margin = new Thickness(20, 42, 20, 16) };
    private double commandWidth = 280, surfaceWidth;
    private bool clickThrough, placementEdited, closed, textExcluded, wideService;
    internal OverlayChromeWindow Chrome { get; }
    internal bool PlacementEdited => placementEdited;
    internal bool ClickThrough => clickThrough;
    internal bool WordDictionaryEnabled => profile.WordDictionaryEnabled;
    internal bool TranslationEnabled => profile.ShowTranslation;
    private readonly CancellationTokenSource dictionaryLifetime = new();
    private CancellationTokenSource? dictionaryLookup;
    private readonly Func<Uri, IntPtr, CancellationToken, Task> openDictionary;
    internal bool ClosedForReplacement { get; private set; }
    internal Task DictionaryCleanup { get; private set; } = Task.CompletedTask;
    internal Task CloseForExitAsync() { Close(); return DictionaryCleanup; }
    internal void CloseForReplacement() { ClosedForReplacement = true; Close(); }
    public bool CaptureExcluded => textExcluded && (!Chrome.IsVisible || Chrome.CaptureExcluded);
    public event Action? PreferencesChanged;
    public event Action<string>? InteractionFailed;
    internal event Action? ToggleTranslationRequested;
    internal event Action? CaptureAreaRequested;
    internal event Action? ExplanationRequested;
    internal event Action? ShowMainRequested;
    internal event Action? DisplayModeChangeRequested;
    internal void RequestDisplayModeChange() => DisplayModeChangeRequested?.Invoke();
    internal void SetDisplayModeChanging(bool changing) => Chrome.SetDisplayMode(profile.ShowOriginal, profile.ShowTranslation, changing);
    internal void RequestTranslationToggle() => ToggleTranslationRequested?.Invoke();
    internal void RequestCaptureArea() => CaptureAreaRequested?.Invoke();
    internal void RequestExplanation() => ExplanationRequested?.Invoke();
    internal void RequestShowMain() => ShowMainRequested?.Invoke();
    internal void SetAutomaticTranslation(bool running, bool starting = false) => Chrome.SetAutomaticTranslation(running, starting);
    internal void SetCaptureAreaAvailable(bool available) => Chrome.SetAreaAvailable(available);
    internal void SetService(TranslationProvider provider)
    {
        wideService = !profile.ShowTranslation || provider == TranslationProvider.HyMtLocal;
        Chrome.SetService(profile.ShowTranslation ? TranslationProviderCatalog.Get(provider).DisplayName : "원문만 표시", wideService);
        Chrome.SetExplanationAvailable(App.MockAi || TranslationProviderCatalog.Get(provider).SupportsStudy);
        UpdateHeaderLayout();
    }
    internal void ReserveCommandSpace(double width) { commandWidth = width; UpdateHeaderLayout(); }
    internal void UpdateHeaderLayout()
    {
        // Narrow subtitles keep all commands and a readable service name without overlap.
        double width = surfaceWidth > 0 ? surfaceWidth : Width;
        Chrome.SetCompactCommands(width < 352);
        bool stacked = width < commandWidth + Chrome.ServiceGroupWidth + (clickThrough ? 44 : 20) + 28;
        Chrome.SetServicePlacement(clickThrough, stacked, commandWidth);
        textPanel.Margin = new Thickness(20, stacked ? 66 : 42, 20, 16);
    }
    internal void SetBusy(bool value)
    {
        if (value) DismissTriggerPrompt();
        busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void SetPipelineResult(PipelineUpdate update)
    {
        if (update.Version < pipelineVersion) return;
        // Version changes for each input inside one pipeline. Only BeginSession/stop resets notices.
        if (update.Version > pipelineVersion) pipelineVersion = update.Version;
        if (update.State is PipelineState.Recognizing or PipelineState.Translating or PipelineState.Error) DismissTriggerPrompt();
        if (update.State == PipelineState.Stopped) ClearPipelineNotices();
        else {
            foreach (var area in pipelineErrors.Keys.ToArray())
                if ((update.RecoveredAreas & area) != 0) pipelineErrors.Remove(area);
            if ((update.RecoveredAreas & pipelineWarningArea) != 0) pipelineWarning = null;
            if (update.State == PipelineState.Error)
                pipelineErrors[update.FailureArea == PipelineStatusArea.None ? PipelineStatusArea.Ocr : update.FailureArea] = update.OverlayMessage ?? update.Message;
            else if (update.Important) {
                pipelineWarning = update.OverlayMessage ?? update.Message;
                pipelineWarningArea = update.FailureArea == PipelineStatusArea.None ? PipelineStatusArea.Input : update.FailureArea;
            }
        }
        var message = ResolveNotice("");
        if (update.Original is not null || update.Translation is not null || status.Text != message)
            SetResult(update.Original, update.Translation, "", update.State == PipelineState.Watching);
        SetBusy(update.IsBusy);
        if (update.State == PipelineState.Stopped) ShowStoppedPrompt();
    }
    internal void ClearPipelineNotices()
    {
        pipelineErrors.Clear(); pipelineWarning = null; pipelineWarningArea = PipelineStatusArea.None;
    }
    private string ResolveNotice(string transient) => chromeFailure
        ?? (pipelineErrors.Count > 0 ? pipelineErrors.Values.Last() : pipelineWarning ?? transient);
    private void ShowNotice(string transient)
    {
        string message = ResolveNotice(transient);
        if (status.Text != message) status.Text = message;
        status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    internal bool MatchesFinalSubtitle(PipelineUpdate update)=>WindowState!=WindowState.Minimized && profile.ShowTranslation && translation.Visibility==Visibility.Visible
        && update.State==PipelineState.Watching && !string.IsNullOrWhiteSpace(update.Original) && !string.IsNullOrWhiteSpace(update.Translation)
        && original.Text==update.Original && reusableTranslation==update.Translation && translation.Text==update.Translation;
    internal void CommitPlacement()
    {
        // Never overwrite a saved monitor with a temporary fallback unless the user edited it.
        if (!placementEdited || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        profile.OverlayWindow = DesktopWindows.RememberOverlay(this); placementEdited = false;
        PreferencesChanged?.Invoke();
    }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => {
        if (!closed && !placementEdited && new WindowInteropHelper(this).Handle != IntPtr.Zero) DesktopWindows.ApplyPlacement(this, profile.OverlayWindow);
    });
    public TranslationWindow(Profile profile,
        Func<string, string, CancellationToken, Task<System.Collections.Generic.IReadOnlyList<DictionaryWord>>>? tokenize = null,
        Func<Uri, IntPtr, CancellationToken, Task>? openDictionary = null,
        Func<string, CancellationToken, Task<JapaneseReading>>? readJapanese = null,
        Func<WindowTarget?>? getGameWindow = null)
    {
        this.profile = profile;
        this.openDictionary = openDictionary ?? ((uri, owner, token) => DictionaryBrowser.OpenAsync(uri, owner, token));
        original = new DictionarySubtitle(tokenize, readJapanese) { Name = "OverlayOriginal", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        original.WordRequested += word => _ = OpenDictionaryAsync(word);
        original.AnalysisFailed += message => {
            if (pipelineErrors.Count == 0 && pipelineWarning is null)
                SetResult(null, null, message);
            InteractionFailed?.Invoke(message);
        };
        Title = "KO Trans 2 · 번역"; MinWidth = 320; MinHeight = 150; Topmost = true; ShowActivated = false; ShowInTaskbar = false;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 34, 46)); Foreground = Brushes.White;
        var panel = textPanel;
        var sourceSurface = new Grid(); sourceSurface.Children.Add(original); sourceSurface.Children.Add(original.CreateRubyLayer());
        panel.Children.Add(status); panel.Children.Add(sourceSurface); panel.Children.Add(translation); panel.Children.Add(instruction);
        var surface = new Grid();
        surface.SizeChanged += (_, e) => { surfaceWidth = e.NewSize.Width; UpdateHeaderLayout(); };
        surface.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        surface.Children.Add(busy); Content = surface;
        Chrome = new OverlayChromeWindow(this);
        PreviewMouseLeftButtonDown += (_, e) => {
            if (!clickThrough && e.ButtonState == MouseButtonState.Pressed && CanDragBody(e.OriginalSource as DependencyObject)) {
                e.Handled = true; DragMove();
            }
        };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(OverlayMessage);
            AttachMagpieOrder(handle, getGameWindow);
            textExcluded = WindowTargets.TryExcludeFromCapture(handle); SetClickThrough(profile.OverlayClickThrough);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        };
        IsVisibleChanged += (_, _) => {
            if (IsVisible && Chrome.InputAvailable) { Chrome.Owner = this; Chrome.SyncBounds(); Chrome.Show(); }
            else { dictionaryLookup?.Cancel(); Chrome.Hide(); }
            if (IsVisible) magpieOrder?.Shown(); else magpieOrder?.Hidden();
        };
        Loaded += (_, _) => magpieOrder?.Shown();
        LocationChanged += (_, _) => Chrome.SyncBounds(); SizeChanged += (_, _) => Chrome.SyncBounds();
        DpiChanged += (_, _) => Chrome.SyncBounds();
        Closed += (_, _) => { closed = true; magpieOrder?.Dispose(); dictionaryLifetime.Cancel(); dictionaryLookup?.Cancel(); dictionaryLookup?.Dispose(); dictionaryLifetime.Dispose(); DictionaryCleanup = original.DisposeAsync(); Chrome.Close(); Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged; };
        Closing += (_, _) => CommitPlacement();
        DesktopWindows.Restore(this, profile.OverlayWindow); Apply(profile); ShowStoppedPrompt();
    }
    internal bool CanDragBody(DependencyObject? node)
    {
        if (clickThrough) return false;
        while (node is not null) {
            if (node == original && original.LookupEnabled) return false;
            if (node is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.Thumb) return false;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : node is System.Windows.Documents.TextElement element ? element.Parent : LogicalTreeHelper.GetParent(node);
        }
        return true;
    }
    internal void ReportInputFailure(int errorCode)
    {
        ChangeClickThrough(false);
        var message = $"자막 조작 영역을 만들지 못했습니다 (Windows 오류 {errorCode}). 클릭 전달을 껐습니다. 설정창에서 번역을 중지하고 자막창을 다시 여세요.";
        chromeFailure = profile.ShowTranslation
            ? "자막 버튼 오류 · 클릭 통과를 껐습니다. 번역을 중지하고 자막창을 다시 여세요."
            : "자막 버튼 오류 · 클릭 통과를 껐습니다. 원문 인식을 중지하고 자막창을 다시 여세요.";
        SetResult(null, null, chromeFailure);
        InteractionFailed?.Invoke(message);
    }
    public void SetResult(string? source, string? translated, string message, bool complete = true)
    {
        if (source is not null && source != original.Text) reusableTranslation = "";
        if (source is not null) original.SetSource(source, profile.Language == "일본어" ? "ja" : "en");
        if (translated is not null) translation.Text = translated;
        if (complete && !string.IsNullOrWhiteSpace(translated)) { reusableTranslation = translated; reusableSource = original.Text; }
        if (!string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(translated)) ClearIdlePrompt();
        ShowNotice(message);
    }
    public void SetClickThrough(bool value)
    {
        clickThrough = value; profile.OverlayClickThrough = value; Chrome.Apply(value);
        RefreshDictionary();
        UpdateHeaderLayout();
        var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        // The text surface really passes input across processes. The separate narrow input frame
        // retains only edges/controls, so WS_EX_TRANSPARENT never swallows its X or move handle.
        SetWindowLongPtr(handle, -20, new IntPtr(value ? style | 0x20 | 0x08000000 : (style & ~0x20) | 0x08000000));
    }
    internal void ChangeClickThrough(bool value)
    {
        if (clickThrough == value) return;
        SetClickThrough(value); PreferencesChanged?.Invoke();
    }
    internal void ChangeWordDictionary(bool value)
    {
        if (clickThrough || profile.WordDictionaryEnabled == value) return;
        profile.WordDictionaryEnabled = value; RefreshDictionary(); PreferencesChanged?.Invoke();
    }
    private void RefreshDictionary()
    {
        bool enabled = profile.WordDictionaryEnabled && !clickThrough && profile.ShowOriginal && prompt == IdlePrompt.None;
        if (!enabled) dictionaryLookup?.Cancel();
        original.SetLookupEnabled(enabled);
        Chrome.ApplyDictionary(profile.WordDictionaryEnabled, !clickThrough);
    }
    private async Task OpenDictionaryAsync(string word)
    {
        if (closed || !original.LookupEnabled) return;
        dictionaryLookup?.Cancel(); dictionaryLookup?.Dispose();
        dictionaryLookup = CancellationTokenSource.CreateLinkedTokenSource(dictionaryLifetime.Token);
        var token = dictionaryLookup.Token;
        try {
            var uri = profile.WordDictionary.SearchUri(word, profile.Language == "일본어" ? "ja" : "en");
            await openDictionary(uri, new WindowInteropHelper(this).Handle, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) {
            if (!closed && !token.IsCancellationRequested) {
                var message = ex switch {
                    DictionaryBrowserException or System.IO.InvalidDataException => ex.Message,
                    UnauthorizedAccessException => "사전 페이지를 열지 못했습니다. 사전 전용 폴더에 접근할 권한을 확인하세요.",
                    _ => "사전 페이지를 열지 못했습니다. 잠시 후 다시 시도해 주세요."
                };
                if (pipelineErrors.Count == 0 && pipelineWarning is null)
                    SetResult(null, null, message);
                InteractionFailed?.Invoke(message);
            }
        }
    }
    internal void MoveOverlay(double dx, double dy)
    {
        if (dx == 0 && dy == 0) return;
        placementEdited = true; Left = (double.IsFinite(Left) ? Left : 0) + dx; Top = (double.IsFinite(Top) ? Top : 0) + dy;
    }
    internal void ResizeOverlay(RegionEdit edges, double dx, double dy)
    {
        double left = double.IsFinite(Left) ? Left : 0, top = double.IsFinite(Top) ? Top : 0;
        double width = Width, height = Height;
        if (edges.HasFlag(RegionEdit.Left)) { double next = Math.Max(MinWidth, width - dx); left += width - next; width = next; }
        if (edges.HasFlag(RegionEdit.Right)) width = Math.Max(MinWidth, width + dx);
        if (edges.HasFlag(RegionEdit.Top)) { double next = Math.Max(MinHeight, height - dy); top += height - next; height = next; }
        if (edges.HasFlag(RegionEdit.Bottom)) height = Math.Max(MinHeight, height + dy);
        if (left == Left && top == Top && width == Width && height == Height) return;
        placementEdited = true; Left = left; Top = top; Width = width; Height = height;
    }
    private IntPtr OverlayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x232) { placementEdited = true; CommitPlacement(); } // WM_EXITSIZEMOVE from body drag.
        if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: keep the game focused.
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    public void Apply(Profile p)
    {
        Opacity = p.OverlayOpacity; translation.FontSize = p.OverlayFontSize;
        translation.Foreground = (Brush)new BrushConverter().ConvertFromString(p.OverlayColor)!;
        original.FontSize = p.OverlayOriginalFontSize;
        original.Foreground = (Brush)new BrushConverter().ConvertFromString(p.OverlayOriginalColor)!;
        ApplyContentVisibility();
        translation.Margin = new Thickness(0, p.ShowOriginal ? 0 : 10, 0, 0);
        original.SetSource(original.Text, p.Language == "일본어" ? "ja" : "en");
        RefreshInstructionStyle();
        SetService(p.Translation.Provider);
        Chrome.SetDisplayMode(p.ShowOriginal, p.ShowTranslation, false);
        SetClickThrough(p.OverlayClickThrough);
    }
}
