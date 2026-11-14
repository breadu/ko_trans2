using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;
internal static class MarkerLearningTests
{
    internal static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.MarkerUi",Guid.NewGuid().ToString("N"));
        var settings=new AppSettings();settings.Validate();var game=settings.CreateGameProfile("Synthetic game");var other=settings.CreateGameProfile("Other game");
        game.Language="일본어";game.CaptureMode=KoTrans.Core.CaptureMode.Window;settings.ActiveProfileId=game.Id;
        var target=new WindowTarget(new(60),60,60,"Scene 1","game","C:\\Synthetic\\game.exe","GameClass");
        var store=new SettingsStore(root);var main=Track(new MainWindow(store,new ApiKeyStore(root),()=>[target],_=>new(0,0,800,600)));
        main.ApplyLoadedSettings(settings);
        try {
            main.SelectGameWindow(target);main.ShowProfileSettingsPage(6);Layout(main,1060,900);
            var status=Named<TextBlock>(main,"MarkerLearningStatus");var resetButton=Named<Button>(main,"ResetMarkerLearningButton");
            Check(Named<SmoothScrollViewer>(main,"AdvancedPage").Visibility == Visibility.Visible
                && Named<SmoothScrollViewer>(main,"CapturePage").Visibility == Visibility.Collapsed,
                "Marker-learning recovery must be on the Advanced page, not the normal capture page");
            Check(Equals(resetButton.Content, "문장 끝 표시 학습 초기화"),
                "Recovery command still uses a misleading generic learning reset label");
            var explanation=Named<TextBlock>(main,"MarkerLearningExplanation");
            Check(main.FindName("MarkerLearningChoice") is null && resetButton.IsEnabled
                && explanation.Text.Contains("깜빡이는 표시가 화면 변화로 인식")
                && explanation.Text.Contains("게임별로 문장 끝 표시를 자동 학습")
                && explanation.Text.Contains("\n반복 인식이 계속되거나")
                && explanation.Text.Contains("학습 결과를 초기화하세요")
                && status.Text == "" && status.Visibility == Visibility.Collapsed,
                "Advanced OCR recovery must always explain its purpose and avoid an empty duplicate status");
            Check(Elements<TextBlock>(main).Any(t=>t.Text ==
                "Magpie로 화면을 확대해 사용할 때 적용됩니다. Magpie 사용 중 원본 게임 화면이 제대로 인식되지 않으면 이 옵션을 끄세요."),
                "Magnification guidance must explicitly identify Magpie and the option to disable");
            Check(!Elements<TextBlock>(main).Any(t=>t.Text == "문장 끝의 깜빡이는 표시가 잘못 처리될 때만 사용하세요."),
                "The former warning should be integrated into the explanation instead of a separate line");
            Check(resetButton.ActualWidth>0 && resetButton.ActualHeight>0 && resetButton.ToolTip.ToString()!.Contains("중지"),
                "Recovery button has no layout or restart guidance");
            var visual=new MarkerVisual(0x0018187E7E181800,230,140,190);
            var pattern=new GameMarkerPattern(MarkerAnchor.LineEnd,.6,-.1,.6,.5,[visual]);
            var learningStore=new MarkerLearningStore(main.MarkerLearningDirectory,MarkerLearningStore.Key(game,target.Identity));Complete(learningStore.SaveAsync([pattern]));
            // Previously Off/Observe/Apply profiles and profiles without the
            // retired field all use Apply, without losing their stored patterns.
            foreach(int? legacyMode in new int?[]{null,0,1,2}) {
                string json=JsonSerializer.Serialize(game);
                if(legacyMode is {} value)json=json.Insert(1,$"\"MarkerLearning\":{value},");
                var loaded=JsonSerializer.Deserialize<Profile>(json)!;
                settings.Profiles[settings.Profiles.IndexOf(game)]=loaded;game=loaded;
                main.ApplyLoadedSettings(settings);
                var prepared=CompleteResult(main.PrepareMarkerLearningAsync(game,target.Identity,new NoOcr(),CancellationToken.None));
                Check(prepared is not null && prepared.LearningStatus.Mode==MarkerLearningMode.Apply
                    && prepared.LearningStatus.Confirmed==1 && status.Text.Contains("확정 1개")
                    && status.Visibility == Visibility.Visible,
                    "Automatic learning ignored a legacy profile or lost its visible progress");
                Complete(main.SwitchProfileAsync(other));
                Check(status.Text.Contains("지원") && !status.Text.Contains("확정 1개"),"Another profile inherited learning status");
                Complete(main.SwitchProfileAsync(game));
            }
            int confirmations = 0;
            main.ConfirmMarkerLearningResetOverride = () => { confirmations++; return false; };
            Click(resetButton);
            Check(confirmations == 1 && CompleteResult(learningStore.LoadAsync()).Patterns.Count == 1,
                "Declining marker reset must leave the learned file untouched");
            main.ConfirmMarkerLearningResetOverride = null;
            // Simulate the async reset's pending interval without disk timing or
            // GUI/model startup. Starting here must not load the soon-deleted file.
            var resetting=typeof(MainWindow).GetField("markerLearningResetting",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            resetting.SetValue(main,true);
            try {
                Check(!CompleteResult(main.StartTranslationAsync(true)) && main.FeedbackToast.Detail.Contains("초기화가 끝난"),
                    "Translation startup raced a pending learning reset");
            } finally {resetting.SetValue(main,false);}
            var otherStore=new MarkerLearningStore(main.MarkerLearningDirectory,MarkerLearningStore.Key(other,target.Identity));
            Complete(otherStore.SaveAsync([pattern]));
            var old=main.CreateProfilePipeline(new NoOcr(),new MockTranslationService(),_=>throw new Exception("No capture expected"));
            main.ObserveTranslation(old,game.Id);old.Start(false);
            var reset=main.ResetMarkerLearningAsync();Complete(reset);
            Check(reset.Result && !old.IsRunning && CompleteResult(learningStore.LoadAsync()).Patterns.Count==0
                && status.Text.Contains("초기화"),"Game reset did not stop work or clear its learned metadata");
            Check(CompleteResult(otherStore.LoadAsync()).Patterns.Count==1,"Reset cleared another profile's learning");
            Complete(old.DisposeAsync().AsTask());
            void Unsupported() => Check(CompleteResult(main.PrepareMarkerLearningAsync(game,target.Identity,new NoOcr(),CancellationToken.None)) is null,
                "Automatic learning bypassed its supported-input boundary");
            game.Vertical=true;Unsupported();game.Vertical=false;
            game.Language="영어";Unsupported();game.Language="일본어";
            game.OcrEngineId="future-engine";Unsupported();game.OcrEngineId="paddle";
            game.CaptureMode=KoTrans.Core.CaptureMode.Region;Unsupported();
            game.CaptureMode=KoTrans.Core.CaptureMode.Clipboard;Unsupported();
            game.CaptureMode=KoTrans.Core.CaptureMode.Window;
            Check(CompleteResult(main.PrepareMarkerLearningAsync(game,null,new NoOcr(),CancellationToken.None)) is null,
                "Learning started without a game identity");
            string learningPath=Path.Combine(main.MarkerLearningDirectory,MarkerLearningStore.Key(game,target.Identity)+".json");
            File.WriteAllText(learningPath,"{broken");
            Check(CompleteResult(main.PrepareMarkerLearningAsync(game,target.Identity,new NoOcr(),CancellationToken.None)) is null
                && File.ReadAllText(learningPath)=="{broken" && status.Text.Contains("학습 초기화"),
                "Automatic learning overwrote corrupt metadata or failed to explain recovery");
            Check(CompleteResult(main.ResetMarkerLearningAsync()),"Corrupt learning could not be reset");
            var recovered=CompleteResult(main.PrepareMarkerLearningAsync(game,target.Identity,new NoOcr(),CancellationToken.None));
            Check(recovered is not null && recovered.LearningStatus.Mode==MarkerLearningMode.Apply && recovered.LearningStatus.Confirmed==0,
                "Automatic learning did not recover after reset");
        } finally {Close(main);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static T CompleteResult<T>(Task<T> task){Complete(task);return task.Result;}
    private sealed class NoOcr:IOcrService
    {
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t)=>throw new Exception("No OCR expected");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
