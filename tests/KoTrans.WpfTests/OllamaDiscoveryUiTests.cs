using System.IO;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class OllamaDiscoveryUiTests
{
    internal static void PendingConnectionLifetime()
    {
        foreach (bool dispose in new[] { false, true })
        foreach (bool fail in new[] { false, true })
        {
            string dir = FeedbackTests.DirectoryPath();
            var profile = new Profile { Translation = new() { Provider = TranslationProvider.Ollama, Model = "saved:1" } };
            bool canceled = false, disposed = false;
            var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var editor = new TranslationSettingsEditor(profile, new(), new ApiKeyStore(dir), ollamaLookup: (_, token) => {
                token.Register(() => canceled = true);
                return response.Task; // Deliberately ignore cancellation to simulate a late server response.
            });
            var host = Track(new Window { Content = editor });
            try
            {
                Layout(host, 900, 700);
                var status = Named<TextBlock>(host, "OllamaConnectionStatus");
                var pending = editor.CheckOllamaConnectionAsync();
                Check(!Named<Button>(host, "OllamaConnectionCheck").IsEnabled, "Pending connection permits duplicate button requests");
                if (dispose) { editor.Dispose(); disposed = true; }
                else Named<ComboBox>(host, "Provider").SelectedItem = TranslationProvider.OpenAI;
                string statusAfterChange = status.Text;
                if (fail) response.SetException(new TranslationException("synthetic late failure"));
                else response.SetResult([new("late-model")]);
                Complete(pending); Pump();
                Check(canceled && status.Text == statusAfterChange && profile.Translation.Model == "saved:1"
                    && profile.Translation.Provider == TranslationProvider.Ollama,
                    "Late Ollama connection result changed a closed/switched editor or persisted a draft");
                if (!dispose)
                    Check(status.Visibility == Visibility.Collapsed && !Named<Button>(host, "OllamaConnectionCheck").IsEnabled,
                        "Late Ollama completion re-enabled controls for another provider");
            }
            finally { if (!disposed) editor.Dispose(); Close(host); Directory.Delete(dir, true); }
        }

        string retryDir = FeedbackTests.DirectoryPath();
        var replies = new List<TaskCompletionSource<IReadOnlyList<ProviderModel>>>();
        var retryEditor = new TranslationSettingsEditor(new Profile { Translation = new() { Provider = TranslationProvider.Ollama } },
            new(), new ApiKeyStore(retryDir), ollamaLookup: (_, _) => {
                var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
                replies.Add(response); return response.Task;
            });
        var retryHost = Track(new Window { Content = retryEditor });
        try
        {
            var first = retryEditor.CheckOllamaConnectionAsync();
            var second = retryEditor.CheckOllamaConnectionAsync();
            replies[1].SetResult([new("current")]); Complete(second);
            string current = Named<TextBlock>(retryHost, "OllamaConnectionStatus").Text;
            replies[0].SetException(new TranslationException("synthetic superseded failure")); Complete(first);
            Check(current.Contains("설치된 모델 1개") && Named<TextBlock>(retryHost, "OllamaConnectionStatus").Text == current
                && Named<Button>(retryHost, "OllamaConnectionCheck").IsEnabled,
                "An older connection failure replaced a newer successful result at the same address");
        }
        finally { retryEditor.Dispose(); Close(retryHost); Directory.Delete(retryDir, true); }
    }

    // Verify real WPF layout gaps, not only the Margin values, for both picker variants.
    private static void CheckModelPickerSpacing(ModelListWindow dialog, bool hasSearch)
    {
        Layout(dialog, 540, 500);
        var surface = (UIElement)dialog.Content;
        var list = Named<ListBox>(dialog, "AvailableModels");
        var selected = Named<TextBox>(dialog, "SelectedModelId");
        var status = Named<TextBlock>(dialog, "ModelListStatus");
        double listBottom = list.TranslatePoint(new Point(0, list.ActualHeight), surface).Y;
        double selectedTop = selected.TranslatePoint(new Point(), surface).Y;
        double selectedBottom = selected.TranslatePoint(new Point(0, selected.ActualHeight), surface).Y;
        double statusTop = status.TranslatePoint(new Point(), surface).Y;
        Check(list.ActualHeight >= 100 && selectedTop - listBottom is >= 7 and <= 17
            && statusTop - selectedBottom is >= 16 and <= 30,
            "Model picker has oversized list/selection gap or a cramped selected-ID/status gap");
        var search = Named<TextBox>(dialog, "ModelSearch");
        if (hasSearch)
        {
            double searchBottom = search.TranslatePoint(new Point(0, search.ActualHeight), surface).Y;
            double listTop = list.TranslatePoint(new Point(), surface).Y;
            Check(search.Visibility == Visibility.Visible && listTop - searchBottom is >= 6 and <= 16,
                "Model picker search/list gap should be compact and balanced");
        }
        else
            Check(((DockPanel)search.Parent).Visibility == Visibility.Collapsed && list.Margin.Top >= 4,
                "Ollama should hide search without leaving a large unused gap above the list");
    }

    internal static void ConnectionAndDraft()
    {
        var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.Wpf.Ollama", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var profile = new Profile { Translation = new() {
            Provider = TranslationProvider.Ollama, Model = "first:1", OllamaAddress = "http://localhost:11434"
        } };
        profile.Translation.OllamaModels = profile.Translation.OllamaModels.SetItem("second:1", new(OllamaRequestFormat.TranslateGemma, 150, 12));
        var hotkeys = new HotkeyOptions();
        var calls = new List<string>();
        bool fail = false, hold = false, canceled = false;
        var gate = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<ProviderModel>> Lookup(string address, CancellationToken token)
        {
            calls.Add(address);
            if (hold) { token.Register(() => canceled = true); return gate.Task; }
            if (fail) throw new TranslationException("서버에 연결할 수 없습니다.");
            return Task.FromResult<IReadOnlyList<ProviderModel>>([new("second:1")]);
        }
        var editor = new TranslationSettingsEditor(profile, hotkeys, new ApiKeyStore(dir), ollamaLookup: Lookup);
        var host = Track(new Window { Content = editor });
        try
        {
            Layout(host, 900, 700);
            var endpoint = Named<TextBox>(host, "Endpoint");
            var model = Named<TextBox>(host, "Model");
            var status = Named<TextBlock>(host, "OllamaConnectionStatus");
            var check = Named<Button>(host, "OllamaConnectionCheck");
            var fields = (StackPanel)((DockPanel)endpoint.Parent).Parent;
            Check(fields.Children.IndexOf((DockPanel)endpoint.Parent) < fields.Children.IndexOf((DockPanel)model.Parent)
                && endpoint.Text == "http://localhost:11434" && check.IsEnabled,
                "Ollama address should be editable and above the model ID");
            var modelButton = Named<Button>(host, "ModelListButton");
            void CheckOllamaRowAlignment()
            {
                var surface = (UIElement)host.Content;
                double connectionLeft = check.TranslatePoint(new Point(), surface).X;
                double modelButtonLeft = modelButton.TranslatePoint(new Point(), surface).X;
                double addressRight = endpoint.TranslatePoint(new Point(endpoint.ActualWidth, 0), surface).X;
                double modelRight = model.TranslatePoint(new Point(model.ActualWidth, 0), surface).X;
                Check(check.ActualWidth == 104 && modelButton.ActualWidth == 104
                    && Math.Abs(connectionLeft - modelButtonLeft) < 1
                    && Math.Abs(addressRight - modelRight) < 1
                    && addressRight + 7 <= connectionLeft,
                    "Ollama server check and model list buttons must share an edge and align both input right edges");
            }
            CheckOllamaRowAlignment();
            Layout(host, 740, 740);
            CheckOllamaRowAlignment();
            Layout(host, 900, 700);
            endpoint.Text = "http://localhost:11435";
            Complete(editor.CheckOllamaConnectionAsync());
            Check(calls.SequenceEqual(["http://localhost:11435"]) && status.Text == "Ollama 연결됨 · 설치된 모델 1개"
                && profile.Translation.OllamaAddress == "http://localhost:11434",
                "Connection check must use the unsaved address without persisting it");

            var dialog = Track(editor.CreateModelListWindow());
            try
            {
                Complete(dialog.ReloadAsync()); Layout(dialog, 540, 500);
                Check(calls.Last() == "http://localhost:11435" && dialog.Title == "Ollama 모델 목록",
                    "Model picker did not use the current Ollama address");
                CheckModelPickerSpacing(dialog, hasSearch: false);
                Named<ListBox>(dialog, "AvailableModels").SelectedIndex = 0;
                Click(Named<Button>(dialog, "ApplyModel"));
                Check(model.Text == "second:1" && Named<ComboBox>(host, "OllamaFormat").SelectedItem is OllamaRequestFormat.TranslateGemma
                    && Named<TextBox>(host, "Timeout").Text == "150" && Named<TextBox>(host, "OllamaKeepAlive").Text == "12"
                    && profile.Translation.Model == "first:1",
                    "Ollama selection must restore model-specific draft settings without saving");
            }
            finally { Close(dialog); }

            var cloudList = Track(new ModelListWindow(TranslationProvider.OpenAI,
                _ => Task.FromResult<IReadOnlyList<ProviderModel>>([new("gpt-example"), new("gpt-example-mini")])));
            try
            {
                Complete(cloudList.ReloadAsync());
                CheckModelPickerSpacing(cloudList, hasSearch: true);
            }
            finally { Close(cloudList); }

            fail = true;
            Complete(editor.CheckOllamaConnectionAsync());
            Check(status.Text.Contains("연결할 수 없습니다") && model.Text == "second:1",
                "Connection failure should not erase the selected model ID");
            fail = false; hold = true;
            var pending = editor.CheckOllamaConnectionAsync();
            endpoint.Text = "http://localhost:11436";
            gate.SetResult([new("old-server-model")]); Complete(pending);
            Check(canceled && status.Text.Contains("주소가 변경됐습니다") && model.Text == "second:1",
                "Late response from an older server address should be discarded");
            var unsaved = editor.ReadTranslationOptions();
            Check(unsaved.OllamaAddress == "http://localhost:11436" && unsaved.Model == "second:1"
                && profile.Translation.OllamaAddress == "http://localhost:11434", "Unsaved Ollama draft changed persisted settings");
            Check(editor.ApplyChanges(profile, hotkeys) && profile.Translation.OllamaAddress == "http://localhost:11436"
                && profile.Translation.Model == "second:1"
                && profile.Translation.SettingsForOllama() == new OllamaModelSettings(OllamaRequestFormat.TranslateGemma, 150, 12),
                "Saving Ollama settings lost the address or model-specific options");
            var reopened = new TranslationSettingsEditor(profile, hotkeys, new ApiKeyStore(dir), ollamaLookup: Lookup);
            var reopenedHost = Track(new Window { Content = reopened });
            try {
                Layout(reopenedHost, 900, 700);
                Check(Named<TextBox>(reopenedHost, "Endpoint").Text == "http://localhost:11436"
                    && Named<TextBox>(reopenedHost, "Model").Text == "second:1"
                    && Named<ComboBox>(reopenedHost, "OllamaFormat").SelectedItem is OllamaRequestFormat.TranslateGemma,
                    "Saved Ollama address and model settings did not reopen");
            }
            finally { reopened.Dispose(); Close(reopenedHost); }
        }
        finally { editor.Dispose(); Close(host); Directory.Delete(dir, true); }
    }
}
