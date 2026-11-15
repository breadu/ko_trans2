using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using KoTrans.Infrastructure;

namespace KoTrans.App;

// Read-only explanation viewer: acquisition, AI analysis, cancellation and
// caching remain in MainWindow.Study / Infrastructure. No manual input UI.
internal sealed class StudyWindow : Window
{
    private static readonly Brush Canvas = Paint(0x10, 0x1B, 0x2A);
    private static readonly Brush Card = Paint(0x1C, 0x2B, 0x3D);
    private static readonly Brush TranslationCard = Paint(0x1E, 0x34, 0x4A);
    private static readonly Brush Ink = Paint(0xEC, 0xF4, 0xFC);
    private static readonly Brush Muted = Paint(0xA5, 0xBC, 0xD1);
    private static readonly Brush Accent = Paint(0x79, 0xBD, 0xFA);
    private static readonly Brush Divider = Paint(0x34, 0x4E, 0x65);
    private static readonly Brush ErrorInk = Paint(0xFF, 0xBF, 0x9D);

    private readonly AppSettings settings;
    private Profile placementOwner;
    private readonly Func<WindowPlacement?> capturePlacement;
    private readonly Func<WindowPlacement, PlacementResolution> restorePlacement;
    private WindowPlacement? lastNormalPlacement, placementBeforeEdit;
    private bool temporaryPlacement, placementEdited, restoringPlacement, placementRestored;
    internal WindowPlacement SavedPlacement => placementOwner.StudyWindow ?? settings.StudyWindow;
    private readonly IPronunciationPlayer audio;
    private readonly Func<string?> resolveVoiceDirectory;
    private readonly TextBlock audioFeedback = new() { Name = "StudyAudioFeedback", Foreground = Muted,
        FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private CancellationTokenSource? voiceCancellation;
    private Task voiceTask = Task.CompletedTask;
    private long voiceVersion;
    private string sourceLanguage = "en";
    private readonly List<(string Word, Button Button)> pronunciationButtons = [];
    private readonly StackPanel results = new() { Name = "StudySections" };
    private readonly Border sourcePanel;
    private readonly TextBlock original = new()
    {
        Name = "StudyOriginalText",
        TextWrapping = TextWrapping.Wrap, Foreground = Ink,
        FontSize = 21, LineHeight = 38, LineStackingStrategy = LineStackingStrategy.MaxHeight,
        Margin = new Thickness(0, 9, 0, 0)
    };
    private readonly JapaneseRubyLinePanel rubySentence = new()
    {
        Name = "StudyRubySentence", Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 9, 0, 0)
    };
    private readonly TextBlock reading = new()
    {
        Name = "StudyReadingLine",
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = 112, LineHeight = 27, Foreground = Muted,
        FontSize = 15, Margin = new Thickness(0, 12, 0, 0)
    };
    private readonly TextBlock readingNotice = new()
    {
        Visibility = Visibility.Collapsed, Foreground = Muted,
        FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
        Margin = new Thickness(0, 7, 0, 0)
    };
    private readonly TextBlock feedback = new()
    {
        Name = "StudyFeedback", Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap, Foreground = Ink,
        FontSize = 22, FontWeight = FontWeights.SemiBold,
        LineHeight = 34, Margin = new Thickness(13, 7, 0, 0)
    };
    private readonly RotateTransform hourglassRotation = new();
    private readonly System.Windows.Shapes.Path loadingHourglass = new()
    {
        Name = "StudyLoadingHourglass",
        // Compact Lucide-style hourglass outline, not the old filled 44px bow-tie.
        Data = Geometry.Parse(
            "M 5,2 H 19 M 5,22 H 19 " +
            "M 17,22 V 17.828 C 17,17.298 16.789,16.789 16.414,16.414 " +
            "L 12,12 16.414,7.586 C 16.789,7.211 17,6.702 17,6.172 V 2 " +
            "M 7,2 V 6.172 C 7,6.702 7.211,7.211 7.586,7.586 " +
            "L 12,12 7.586,16.414 C 7.211,16.789 7,17.298 7,17.828 V 22"),
        Stroke = Accent, StrokeThickness = 1.8,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = Brushes.Transparent,
        Stretch = Stretch.Uniform,
        Width = 25, Height = 25, RenderTransformOrigin = new Point(0.5, 0.5),
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 9, 0)
    };
    private readonly DockPanel progressRow = new()
    {
        Name = "StudyProgressRow", Visibility = Visibility.Collapsed,
        LastChildFill = true, Margin = new Thickness(3, 20, 3, 7)
    };
    private readonly SmoothScrollViewer scroll;
    private bool exiting, showingMessage, animationRunning;
    private string input = "", lastStatus = "";

    public bool CaptureExcluded { get; private set; }
    public string Input => input;
    internal string LastStatus => lastStatus;
    internal bool IsLoadingAnimationRunning => animationRunning;
    internal Task VoicePreparation => voiceTask;
    public event Action? Hidden, PreferencesChanged;

    public StudyWindow(AppSettings settings, IPronunciationPlayer? audio = null, Func<string?>? resolveVoiceDirectory = null,
        Profile? profile = null, Func<WindowPlacement?>? capturePlacement = null,
        Func<WindowPlacement, PlacementResolution>? restorePlacement = null)
    {
        this.settings = settings;
        placementOwner = profile ?? settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId) ?? settings.Profiles[0];
        this.capturePlacement = capturePlacement ?? (() => WindowState == WindowState.Normal
            && new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero
                ? DictionaryPopupPlacement.Remember(handle)?.Placement : null);
        this.restorePlacement = restorePlacement ?? (saved => DesktopWindows.ApplyPlacement(this, saved));
        this.audio = audio ?? new PronunciationPlayer();
        this.resolveVoiceDirectory = resolveVoiceDirectory ?? (() => VoiceDataPaths.FindDirectory(AppContext.BaseDirectory, settings.VoiceDirectory));
        // Normal playback notifications must not insert a row into the reader.
        this.audio.Error += AudioStatus;
        Title = "KO Trans 2 · 문장 해설" + (App.MockAi ? " · MOCK AI" : "");
        MinWidth = 470; MinHeight = 390;
        ShowActivated = false; Topmost = settings.StudyTopmost;
        Background = Canvas; Foreground = Ink;
        FontFamily = new FontFamily("맑은 고딕"); FontSize = 13;
        SetStartupSize();

        var document = new StackPanel { Margin = new Thickness(22, 21, 22, 29) };
        var sourceContent = new StackPanel();
        sourceContent.Children.Add(Label("원문"));
        sourceContent.Children.Add(original);
        sourceContent.Children.Add(rubySentence);
        sourceContent.Children.Add(reading);
        sourceContent.Children.Add(readingNotice);
        sourcePanel = new Border
        {
            Name = "StudySourcePanel", Background = Card, Visibility = Visibility.Collapsed,
            BorderBrush = Divider, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 15, 18, 17),
            Child = sourceContent
        };
        document.Children.Add(sourcePanel);
        loadingHourglass.RenderTransform = hourglassRotation;
        DockPanel.SetDock(loadingHourglass, Dock.Left);
        progressRow.Children.Add(loadingHourglass);
        progressRow.Children.Add(feedback);
        document.Children.Add(progressRow);
        document.Children.Add(audioFeedback);
        document.Children.Add(results);
        scroll = new SmoothScrollViewer
        {
            Name = "StudyReaderScroll", Content = document, Background = Canvas,
            BorderThickness = new Thickness(0), Focusable = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Content = scroll;

        SourceInitialized += (_, _) => {
            RestoreSavedPlacement();
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(PlacementMessage);
            CaptureExcluded = CaptureExclusionPolicy.TryExclude(handle);
        };
        LocationChanged += (_, _) => TrackNormalPlacement();
        SizeChanged += (_, _) => TrackNormalPlacement();
        Closing += OnClosing;
    }

    // Topmost is still loaded from existing global settings, but no settings
    // controls live inside the reader itself.
    internal void RefreshGlobalAccess() => Topmost = settings.StudyTopmost;

    private void SetStartupSize()
    {
        Width = Math.Max(MinWidth, SavedPlacement.Width);
        Height = Math.Max(MinHeight, SavedPlacement.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }
    internal void BindProfile(Profile profile)
    {
        if (placementOwner == profile) return;
        CommitPlacement(); // Commit to the opening profile, not the newly selected one.
        placementOwner = profile;
        lastNormalPlacement = null; temporaryPlacement = placementEdited = placementRestored = false;
        if (IsVisible && WindowState == WindowState.Normal && new WindowInteropHelper(this).Handle != IntPtr.Zero) RestoreSavedPlacement();
        else SetStartupSize();
    }
    internal PlacementResolution RestoreSavedPlacement()
    {
        restoringPlacement = true;
        try {
            var resolved = restorePlacement(SavedPlacement);
            Width = Math.Max(MinWidth, resolved.Width); Height = Math.Max(MinHeight, resolved.Height);
            temporaryPlacement = resolved.Temporary; placementEdited = false;
            placementRestored = true;
            lastNormalPlacement = capturePlacement();
            return resolved;
        } finally { restoringPlacement = false; }
    }
    internal void ShowReader()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !placementRestored) {
            WindowState = WindowState.Normal;
            if (new WindowInteropHelper(this).Handle != IntPtr.Zero) RestoreSavedPlacement();
        }
        Show();
    }
    internal void TrackNormalPlacement()
    {
        if (placementRestored && !restoringPlacement && WindowState == WindowState.Normal)
            lastNormalPlacement = capturePlacement() ?? lastNormalPlacement;
    }
    internal void UserPlacementChanged()
    {
        placementEdited = true;
        TrackNormalPlacement();
    }
    internal void BeginPlacementEdit() => placementBeforeEdit = capturePlacement() ?? lastNormalPlacement;
    internal void EndPlacementEdit()
    {
        if (placementBeforeEdit is null || !SamePlacement(placementBeforeEdit, capturePlacement() ?? lastNormalPlacement))
            UserPlacementChanged();
        placementBeforeEdit = null;
    }
    private IntPtr PlacementMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeResizeBorder.HitTestMessage)
            return NativeResizeBorder.Handle(this, handle, wParam, lParam, ref handled);
        if (message == 0x231) BeginPlacementEdit(); // WM_ENTERSIZEMOVE
        if (message == 0x232) EndPlacementEdit(); // WM_EXITSIZEMOVE; Esc may have canceled it.
        return IntPtr.Zero;
    }
    private static bool SamePlacement(WindowPlacement? a, WindowPlacement? b) => a is not null && b is not null
        && a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height
        && a.MonitorId == b.MonitorId && a.MonitorDevice == b.MonitorDevice
        && a.MonitorWorkX == b.MonitorWorkX && a.MonitorWorkY == b.MonitorWorkY;
    internal void CommitPlacement(bool notifyIfUnchanged = false)
    {
        if (!placementRestored) return;
        var placement = capturePlacement() ?? lastNormalPlacement;
        if (placement is null || temporaryPlacement && !placementEdited) return;
        var saved = placementOwner.StudyWindow;
        if (SamePlacement(saved, placement)) {
            // A prior disk save may have failed. A fresh close must retry even
            // if the in-memory rectangle has not changed.
            if (notifyIfUnchanged) PreferencesChanged?.Invoke();
            return;
        }
        placementOwner.StudyWindow = placement;
        temporaryPlacement = placementEdited = false;
        PreferencesChanged?.Invoke();
    }

    public void SetSource(string text, string? language = null)
    {
        var nextLanguage = language ?? ExplanationPrompt.DetectLanguage(text, "en");
        if (input != text || sourceLanguage != nextLanguage) Clear();
        sourceLanguage = nextLanguage;
        input = text;
        original.Text = text;
        sourcePanel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        scroll.ResetToTop();
    }

    // Diagnostic request counts remain accessible to the owner if needed;
    // they are never rendered to the user.
    public void SetStatus(string text) => lastStatus = text;

    public void SetBusy(bool busy, string message)
    {
        SetStatus(message);
        if (busy)
        {
            showingMessage = false;
            feedback.Foreground = Ink;
            feedback.FontSize = 22;
            feedback.FontWeight = FontWeights.SemiBold;
            feedback.Text = "문장 해설을 가져오는 중…";
            feedback.Visibility = Visibility.Visible;
            loadingHourglass.Visibility = Visibility.Visible;
            progressRow.Visibility = Visibility.Visible;
            StartLoadingAnimation();
        }
        else
        {
            StopLoadingAnimation();
            loadingHourglass.Visibility = Visibility.Collapsed;
            if (!showingMessage)
            {
                feedback.Visibility = Visibility.Collapsed;
                progressRow.Visibility = Visibility.Collapsed;
            }
        }
    }

    public void FinishBusy() => SetBusy(false, lastStatus);

    public void ShowError(string message) => ShowMessage(message, ErrorInk);
    internal void ShowNotice(string message) => ShowMessage(message, Muted);
    private void ShowMessage(string message, Brush color)
    {
        SetStatus(message);
        StopLoadingAnimation();
        showingMessage = true;
        loadingHourglass.Visibility = Visibility.Collapsed;
        feedback.Foreground = color;
        feedback.FontSize = 17;
        feedback.FontWeight = FontWeights.Normal;
        feedback.Text = message;
        feedback.Visibility = Visibility.Visible;
        progressRow.Visibility = Visibility.Visible;
    }

    private void StartLoadingAnimation()
    {
        if (animationRunning) return;
        // Slow, understated motion instead of a large, fast spinning symbol.
        var rotation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(4.2)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        hourglassRotation.BeginAnimation(RotateTransform.AngleProperty, rotation);
        animationRunning = true;
    }

    private void StopLoadingAnimation()
    {
        if (!animationRunning) return;
        hourglassRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        hourglassRotation.Angle = 0;
        animationRunning = false;
    }

    public void Clear()
    {
        scroll.StopAnimation();
        voiceVersion++; voiceCancellation?.Cancel(); pronunciationButtons.Clear();
        audioFeedback.Visibility = Visibility.Collapsed;
        StopLoadingAnimation();
        results.Children.Clear();
        rubySentence.Children.Clear();
        rubySentence.Visibility = Visibility.Collapsed;
        original.Visibility = Visibility.Visible;
        original.Text = input;
        original.LineHeight = 38;
        reading.Text = "";
        reading.Visibility = Visibility.Collapsed;
        readingNotice.Visibility = Visibility.Collapsed;
        loadingHourglass.Visibility = Visibility.Collapsed;
        feedback.Visibility = Visibility.Collapsed;
        progressRow.Visibility = Visibility.Collapsed;
        showingMessage = false;
        audio.Stop();
    }

    public void Render(StudyResult result, JapaneseReading? localReading = null)
    {
        Clear();
        RenderReading(localReading);
        if (!string.IsNullOrWhiteSpace(result.FullTranslation))
            AddTranslation(result.FullTranslation);
        if (!string.IsNullOrWhiteSpace(result.SentenceExplanation))
            AddExplanation(result.SentenceExplanation);
        if (result.Vocabulary.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 21, 0, 0) };
            list.Children.Add(SectionHeader("핵심 단어 · 표현"));
            foreach (var word in result.Vocabulary) AddVocabulary(list, word);
            results.Children.Add(list);
        }
        if (pronunciationButtons.Count > 0) {
            voiceCancellation = new CancellationTokenSource();
            voiceTask = PreparePronunciationsAsync(pronunciationButtons.ToArray(), voiceVersion, voiceCancellation, voiceTask);
        }
        scroll.ResetToTop();
    }

    private async Task PreparePronunciationsAsync((string Word, Button Button)[] buttons, long serial, CancellationTokenSource cancellation, Task previous)
    {
        try {
            await previous;
            var directory = await Task.Run(resolveVoiceDirectory, cancellation.Token);
            if (directory is null) return;
            var available = await audio.FindWordsAsync(directory, buttons.Select(b => b.Word).ToArray(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (exiting || serial != voiceVersion) return;
            foreach (var (word, button) in buttons) {
                if (!available.Contains(word)) continue;
                button.Visibility = Visibility.Visible;
                button.Click += async (_, _) => {
                    if (!exiting && serial == voiceVersion && sourceLanguage == "en") {
                        audioFeedback.Visibility = Visibility.Collapsed;
                        await audio.PlayAsync(directory, word);
                    }
                };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TranslationException or ArgumentException) {
            if (!exiting && serial == voiceVersion) AudioStatus("로컬 발음 자료를 읽을 수 없습니다. voice/word.idx와 파일 상태를 확인하세요.");
        }
        finally { if (voiceCancellation == cancellation) voiceCancellation = null; cancellation.Dispose(); }
    }
    private void AudioStatus(string message)
    {
        if (exiting) return;
        audioFeedback.Text = message; audioFeedback.Visibility = Visibility.Visible;
    }
    private static bool IsEnglishWord(string word) => word.Length <= 200
        && System.Text.RegularExpressions.Regex.IsMatch(word, @"^\p{L}+(?:['’\-]\p{L}+)*$")
        && word.All(c => !char.IsLetter(c) || c <= '\u024F');

    // Local Japanese reading is text only, shown under the original rather
    // than in a separate results section or a pronunciation player.
    public void RenderReading(JapaneseReading? candidate)
    {
        // Reset earlier ruby when a later reading result is replaced.
        rubySentence.Children.Clear();
        rubySentence.Visibility = Visibility.Collapsed;
        original.Visibility = Visibility.Visible;
        original.Text = input;
        original.LineHeight = 38;
        // Ruby belongs to the full Japanese sentence. Do not enlarge kanji
        // alone: kana and punctuation remain at the same 21pt base size.
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Text))
        {
            reading.Text = "";
            reading.Visibility = Visibility.Collapsed;
            readingNotice.Visibility = Visibility.Collapsed;
            return;
        }

        if (JapaneseRubyLayout.TryRender(rubySentence, input, candidate.Tokens, 21, 14, Ink, Accent))
        {
            // Every 21pt kanji/kana glyph is bottom-aligned within the same
            // line by JapaneseRubyLinePanel. Only the small ruby sits above it.
            original.Visibility = Visibility.Collapsed;
            rubySentence.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(rubySentence, input);
            reading.Text = "";
            reading.Visibility = Visibility.Collapsed;
            readingNotice.Text = "한자 위의 작은 글씨는 로컬 사전의 읽기 후보예요. 고유명사·동형어는 다를 수 있습니다.";
            readingNotice.Visibility = Visibility.Visible;
            return;
        }

        // Synthetic/legacy readings without aligned tokens retain a small
        // textual fallback instead of guessing kanji-to-kana alignments.
        if (!string.Equals(candidate.Text, input, StringComparison.Ordinal))
        {
            reading.Text = "한자 읽기 후보 · " + candidate.Text;
            reading.Visibility = Visibility.Visible;
            readingNotice.Text = "로컬 사전의 읽기 후보예요. 고유명사·동형어는 정확하지 않을 수 있습니다.";
            readingNotice.Visibility = Visibility.Visible;
        }
    }

    private void AddTranslation(string text)
    {
        var body = new StackPanel();
        body.Children.Add(Label("전체 해석"));
        var translation = Prose(text, 23, Ink, 36);
        translation.Name = "StudyTranslationText";
        translation.FontWeight = FontWeights.Medium;
        translation.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(translation);
        results.Children.Add(new Border
        {
            Name = "StudyTranslationPanel",
            Background = TranslationCard, BorderBrush = Paint(0x3C, 0x68, 0x8C),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 16, 18, 19),
            Margin = new Thickness(0, 17, 0, 0), Child = body
        });
    }

    private void AddExplanation(string text)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 23, 0, 0) };
        panel.Children.Add(SectionHeader("문장 구조 · 뉘앙스"));
        var explanation = Prose(text, 17, Ink, 29);
        explanation.Name = "StudyExplanationText";
        explanation.Margin = new Thickness(0, 11, 0, 0);
        panel.Children.Add(explanation);
        results.Children.Add(panel);
    }

    private void AddVocabulary(StackPanel parent, VocabularyItem word)
    {
        var item = new StackPanel();
        var heading = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (JapaneseRubyLayout.CanShowRuby(word.Word, word.Pronunciation))
            heading.Children.Add(JapaneseRubyLayout.CreateRubyWord(word.Word, word.Pronunciation, 21, 14, Accent, Accent));
        else
        {
            heading.Children.Add(new TextBlock
            {
                Text = word.Word, Foreground = Accent,
                FontSize = 21, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            });
            if (sourceLanguage == "en" && !string.IsNullOrWhiteSpace(word.Pronunciation))
            {
                heading.Children.Add(new TextBlock
                {
                    Text = word.Pronunciation, Foreground = Muted, FontSize = 15,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 2, 0, 0)
                });
            }
        }
        if (sourceLanguage == "en" && IsEnglishWord(word.Word.Trim())) {
            var button = new Button { Content = "🔊", Tag = word.Word.Trim(),
                Visibility = Visibility.Collapsed, Width = 32, Height = 30, Padding = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0), Foreground = Accent, Background = Brushes.Transparent,
                BorderBrush = Divider, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button, word.Word.Trim() + " 단어 발음 재생");
            pronunciationButtons.Add((word.Word.Trim(), button)); heading.Children.Add(button);
        }
        item.Children.Add(heading);

        if (!string.IsNullOrWhiteSpace(word.MeaningKorean))
        {
            var korean = Prose(word.MeaningKorean, 18, Ink, 29);
            korean.Margin = new Thickness(0, 7, 0, 0);
            item.Children.Add(korean);
        }
        if (!string.IsNullOrWhiteSpace(word.MeaningSource))
        {
            var definition = Prose(word.MeaningSource, 15, Muted, 25);
            definition.Margin = new Thickness(0, 5, 0, 0);
            item.Children.Add(definition);
        }
        if (word.Examples.Count > 0)
        {
            var examples = new StackPanel();
            foreach (var example in word.Examples)
            {
                var group = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
                group.Children.Add(Prose(example.Source, 16, Ink, 27));
                var translatedExample = Prose(example.Korean, 15, Muted, 25);
                translatedExample.Margin = new Thickness(0, 4, 0, 0);
                group.Children.Add(translatedExample);
                examples.Children.Add(group);
            }
            item.Children.Add(new Border
            {
                BorderBrush = Divider, BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(12, 0, 0, 0),
                Margin = new Thickness(0, 12, 0, 2), Child = examples
            });
        }
        if (!string.IsNullOrWhiteSpace(word.Tip))
        {
            var tip = Prose("사용 팁 · " + word.Tip, 15, Paint(0xAD, 0xD6, 0xE7), 25);
            tip.Margin = new Thickness(0, 12, 0, 0);
            item.Children.Add(tip);
        }
        parent.Children.Add(new Border
        {
            BorderBrush = Divider, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 15, 0, 16),
            Margin = new Thickness(0, 4, 0, 0), Child = item
        });
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Accent, FontSize = 14,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text, FontSize = 19, FontWeight = FontWeights.SemiBold,
        Foreground = Ink
    };

    private static TextBlock Prose(string text, double size, Brush color, double lineHeight) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        FontSize = size, Foreground = color, LineHeight = lineHeight
    };

    private static Brush Paint(byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromRgb(r, g, b));

    public void ScrollRemote(bool down)
    {
        if (!IsVisible) return;
        scroll.ScrollPage(down);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        HideResident();
    }

    private void HideResident()
    {
        scroll.StopAnimation();
        StopLoadingAnimation();
        CommitPlacement(notifyIfUnchanged: true);
        audio.Stop();
        audioFeedback.Visibility = Visibility.Collapsed;
        Hidden?.Invoke();
        Hide();
    }

    public async Task CloseForExitAsync()
    {
        scroll.Dispose();
        StopLoadingAnimation();
        exiting = true; voiceVersion++; voiceCancellation?.Cancel();
        await voiceTask;
        await audio.DisposeAsync();
        audio.Error -= AudioStatus;
        Close();
    }
}
