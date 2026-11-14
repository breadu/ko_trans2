using System.Collections.Generic;
using System.Diagnostics;
using KoTrans.Infrastructure;

namespace KoTrans.App;

internal sealed class ModelListWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProviderModel>>> load;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TranslationProvider provider;
    private readonly ListBox list = new() { Name = "AvailableModels", DisplayMemberPath = "Id", MinHeight = 100 };
    private readonly TextBox selectedId = new() { Name = "SelectedModelId", IsReadOnly = true, Margin = new Thickness(0, 5, 0, 18) };
    private readonly TextBox search = new() { Name = "ModelSearch", Margin = new Thickness(0, 6, 0, 3) };
    private readonly TextBlock status = new() { Name = "ModelListStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button refresh = new() { Content = "목록 새로 고침" }, apply = new() { Name = "ApplyModel", Content = "모델 ID 적용", IsEnabled = false };
    private readonly Button prices = new() { Name = "OfficialPrices", Content = "공식 요금표", ToolTip = "목록 API에는 가격 정보가 포함되지 않습니다." };
    private readonly DockPanel searchRow;
    private IReadOnlyList<ProviderModel> models = [];
    private bool loading, closed;
    internal event Action<string>? ModelSelected;
    internal Task? LoadingTask { get; private set; }
    internal ModelListWindow(TranslationProvider provider, Func<CancellationToken, Task<IReadOnlyList<ProviderModel>>> load)
    {
        this.provider = provider; this.load = load;
        Title = TranslationProviderCatalog.Get(provider).DisplayName + " 모델 목록";
        Width = 560; Height = 540; MinWidth = 390; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 27, 42)); Foreground = new SolidColorBrush(Color.FromRgb(231, 241, 251));
        FontFamily = new FontFamily("맑은 고딕"); FontSize = 14;
        ApplyDarkStyles();
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "모델을 선택해 적용하거나 아래 ID를 선택해 복사하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        static DockPanel Labelled(string text, TextBox input) {
            var row = new DockPanel { LastChildFill = true };
            var label = new TextBlock { Text = text, Width = 105, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left); row.Children.Add(label); row.Children.Add(input); return row;
        }
        search.ToolTip = "모델 ID 검색"; searchRow = Labelled("검색어 입력:", search);
        searchRow.Visibility = provider == TranslationProvider.Ollama ? Visibility.Collapsed : Visibility.Visible;
        top.Children.Add(searchRow); root.Children.Add(top);
        var bottom = new StackPanel { Margin = new Thickness(0, 5, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Labelled("선택된 모델:", selectedId)); bottom.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemWidth = 134, ItemHeight = 44 };
        prices.Visibility = provider == TranslationProvider.Ollama ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { apply, refresh, prices }) { button.Margin = new Thickness(0, 0, 8, 6); button.Padding = new Thickness(12, 8, 12, 8); buttons.Children.Add(button); }
        apply.Background = new SolidColorBrush(Color.FromRgb(48, 137, 202));
        bottom.Children.Add(buttons); root.Children.Add(bottom);
        list.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(list); Content = root;
        list.SelectionChanged += (_, _) => { selectedId.Text = (list.SelectedItem as ProviderModel)?.Id ?? ""; apply.IsEnabled = selectedId.Text.Length > 0; };
        search.TextChanged += (_, _) => Filter();
        apply.Click += (_, _) => ApplySelection(); list.MouseDoubleClick += (_, _) => ApplySelection();
        refresh.Click += async (_, _) => await ReloadAsync();
        prices.Click += (_, _) => {
            try { Process.Start(new ProcessStartInfo(provider == TranslationProvider.Gemini ? "https://ai.google.dev/gemini-api/docs/pricing" : "https://openai.com/api/pricing/") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { status.Text = "브라우저를 열지 못했습니다. 공식 API 요금표를 확인하세요."; }
        };
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); if (LoadingTask is null || LoadingTask.IsCompleted) lifetime.Dispose(); };
    }
    private void ApplyDarkStyles()
    {
        var ink = new SolidColorBrush(Color.FromRgb(231, 241, 251));
        var outline = new SolidColorBrush(Color.FromRgb(69, 96, 122));
        var field = new SolidColorBrush(Color.FromRgb(20, 35, 51));
        var textStyle = new Style(typeof(TextBox));
        textStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, field));
        textStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, ink));
        textStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, outline));
        textStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(11, 8, 11, 8)));
        Resources[typeof(TextBox)] = textStyle;

        var buttonStyle = new Style(typeof(Button));
        if (TryFindResource("DarkDisabledButton") is Style shared) buttonStyle.BasedOn = shared;
        buttonStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 69, 94))));
        buttonStyle.Setters.Add(new Setter(Button.ForegroundProperty, ink));
        buttonStyle.Setters.Add(new Setter(Button.BorderBrushProperty, outline));
        Resources[typeof(Button)] = buttonStyle;

        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(28, 43, 61))));
        itemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, ink));
        itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 8, 10, 8)));
        itemStyle.Setters.Add(new Setter(ListBoxItem.FocusVisualStyleProperty, null));
        var itemTemplate = new ControlTemplate(typeof(ListBoxItem));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ListBoxItem.BackgroundProperty));
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ListBoxItem.PaddingProperty));
        surface.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        itemTemplate.VisualTree = surface;
        itemStyle.Setters.Add(new Setter(ListBoxItem.TemplateProperty, itemTemplate));
        itemStyle.Triggers.Add(new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true,
            Setters = { new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(49, 85, 113))) } });
        itemStyle.Triggers.Add(new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true,
            Setters = { new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 89, 129))) } });
        itemStyle.Triggers.Add(new Trigger { Property = ListBoxItem.IsKeyboardFocusWithinProperty, Value = true,
            Setters = { new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(48, 137, 202))) } });
        list.ItemContainerStyle = itemStyle;
        list.Background = new SolidColorBrush(Color.FromRgb(28, 43, 61));
        list.Foreground = ink; list.BorderBrush = outline; list.BorderThickness = new Thickness(1);
    }
    private void ApplySelection()
    {
        if (list.SelectedItem is not ProviderModel model || closed) return;
        ModelSelected?.Invoke(model.Id);
        if (IsVisible) Close();
    }
    private void Filter()
    {
        list.ItemsSource = models.Where(m => m.Id.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        selectedId.Clear(); apply.IsEnabled = false;
    }
    internal Task ReloadAsync()
    {
        if (loading || closed) return LoadingTask ?? Task.CompletedTask;
        return LoadingTask = LoadCoreAsync();
    }
    private async Task LoadCoreAsync()
    {
        loading = true; refresh.IsEnabled = false; status.Text = provider == TranslationProvider.Ollama ? "설치된 모델 조회 중…" : "공식 모델 목록 조회 중…";
        try {
            var result = await load(lifetime.Token);
            if (closed) return;
            models = result; Filter();
            status.Text = provider == TranslationProvider.Ollama
                ? models.Count == 0 ? "Ollama 연결됨 · 설치된 모델 없음" : $"Ollama 연결됨 · 설치된 모델 {models.Count}개"
                : models.Count == 0 ? "조회 가능한 모델이 없습니다." : $"{models.Count}개 · 비용은 공식 요금표에서 확인하세요. 번역 호환성은 모델별로 다릅니다.";
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "모델 목록 조회를 취소했습니다."; }
        catch (Exception ex) when (ex is TranslationException or IOException or UnauthorizedAccessException) { if (!closed) status.Text = ex is TranslationException ? ex.Message : "인증 정보를 읽을 수 없습니다. 기본 프로필에서 키를 확인하세요."; }
        finally { loading = false; if (closed) lifetime.Dispose(); else refresh.IsEnabled = true; }
    }
}
