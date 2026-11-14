using System.Collections.Concurrent;
using System.IO;
using System.Windows.Controls;
using KoTrans.App;
using KoTrans.Core;
using KoTrans.Infrastructure;
using static KoTrans.WpfTests.Program;

namespace KoTrans.WpfTests;

internal static class NvlPresentationTests
{
    internal static void Rows()
    {
        string dir=Path.Combine(Path.GetTempPath(),"KoTrans2.NvlRows",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var settings=new AppSettings();settings.Validate();settings.DefaultProfile.Language="일본어";settings.DefaultProfile.ReadingMode="NVL";settings.DefaultProfile.ShowOriginal=true;
        var main=Track(new MainWindow(new SettingsStore(dir),new ApiKeyStore(dir)));main.ApplyLoadedSettings(settings);
        var overlay=Track(main.CreateOverlayWindow());Track(overlay.Chrome);
        var ocr=new RowsOcr();var ai=new Ai();var frame=ocr.Frame("一。");
        var pipe=new TranslationPipeline(ocr,ai,_=>Task.FromResult(Volatile.Read(ref frame)),new("ja","NVL",false),new(),new(),"synthetic",settle:TimeSpan.FromMilliseconds(30));
        int completed=0;pipe.Updated+=u=>{if(u.State==PipelineState.Watching && u.Translation is not null)Interlocked.Increment(ref completed);};
        try {
            main.ObserveTranslation(pipe,settings.ActiveProfileId);pipe.Start(false);
            var original=(TextBox)main.FindName("OcrText");var translated=(TextBox)main.FindName("TranslatedText");
            void Send(string expectedOriginal,string expectedTranslation,params string[] rows) {
                int before=Volatile.Read(ref completed);
                Volatile.Write(ref frame,ocr.Frame(rows));Complete(pipe.RequestTriggeredAsync());
                Until(()=>Volatile.Read(ref completed)>before && translated.Text.Replace("\r\n","\n")==expectedTranslation,"Incremental translation UI missing");
                Check(original.Text.Replace("\r\n","\n")==expectedOriginal,"Main original rows flattened or followed increments");
                Layout(overlay,720,400);
                Check(Elements<TextBlock>(overlay).Any(t=>t.Text==expectedOriginal),"Overlay source row text differs from native Y layout");
                Check(Elements<TextBlock>(overlay).Any(t=>t.Text==expectedTranslation),"Overlay translation rows do not follow blocks");
            }
            Send("一。","T:一。","一。");
            Send("一。二。","T:一。\nT:二。","一。二。");
            Send("一。二。\n三の途中","T:一。\nT:二。\nT:三の途中","一。二。","三の途中");
            Send("一。二。\n三の途中から完成。","T:一。\nT:二。\nT:三の途中から完成。","一。二。","三の途中から完成。");
            Check(ai.Calls==4,"Display formatting introduced duplicate AI calls");
            void Clear() {
                Volatile.Write(ref frame,ocr.Frame());Complete(pipe.RequestTriggeredAsync());
                Until(()=>((MainViewModel)main.DataContext).Status.Contains("문자를 찾지 못했습니다"),"Temporary clear not observed by the UI");
                Check(translated.Text.Replace("\r\n","\n")=="T:一。\nT:二。\nT:三の途中から完成。","Clear erased the formatted translation");
            }
            Clear();
            Send("一。二。\n三の途中から完成。","T:一。\nT:二。\nT:三の途中から完成。","一。二。","三の途中から完成。");
            Check(ai.Calls==4,"Identical body restoration resent paid translation");
            Clear();
            Send("一。二。\n三の途中から完成。\n四。","T:一。\nT:二。\nT:三の途中から完成。\nT:四。","一。二。","三の途中から完成。","四。");
            Check(ai.Calls==5,"Returned page with an addition translated the retained body again");
            Send("「行こう」", "T:「行こう」", "「行こう」");
            Send("「行こう」\n[待って]", "T:「行こう」\nT:[待って]", "「行こう」", "[待って]");
            Send("「行こう」\n[待って]\n\"いいよ\"", "T:「行こう」\nT:[待って]\nT:\"いいよ\"", "「行こう」", "[待って]", "\"いいよ\"");
            Send("「行こう」\n[待って]\n\"いいよ\"どうして？", "T:「行こう」\nT:[待って]\nT:\"いいよ\"\nT:どうして？", "「行こう」", "[待って]", "\"いいよ\"どうして？");
            Check(ai.Calls==9, "Dialogue delimiters retransmitted prior translation or changed UI request counts");
            int beforeFull = Volatile.Read(ref completed);
            Complete(pipe.RequestManualAsync(true));
            const string fullTranslation = "T:「行こう」\nT:[待って]\nT:\"いいよ\"\nT:どうして？";
            Until(() => Volatile.Read(ref completed) > beforeFull, "Full-page retranslation did not complete");
            Layout(overlay,720,400);
            Check(ai.Calls == 10 && translated.Text.Replace("\r\n","\n") == fullTranslation
                && Elements<TextBlock>(overlay).Any(t => t.Text == fullTranslation),
                "Full-page retranslation flattened logical paragraphs in the main view or overlay");
        }finally{Complete(pipe.DisposeAsync().AsTask());Close(overlay);Close(overlay.Chrome);Close(main);Directory.Delete(dir,true);}
    }
    private sealed class RowsOcr : IOcrService
    {
        private readonly ConcurrentDictionary<ImageFrame,OcrResult> results=new();
        public OcrState State=>OcrState.Ready;
        public ImageFrame Frame(params string[] rows) {
            var lines=rows.Select((s,i)=>new OcrLine(s,1,new(40,40+i*36,s.Length*20,20))).ToArray();var bytes=new byte[1600*240];
            bytes[3]=(byte)(results.Count+1);
            foreach(var l in lines)for(int y=l.Bounds.Y+3;y<l.Bounds.Y+17;y++)for(int x=l.Bounds.X+3;x<l.Bounds.X+l.Bounds.Width-3;x++)bytes[y*1600+x*4]=220;
            var frame=new ImageFrame(400,240,1600,bytes);results[frame]=new(lines,TimeSpan.Zero);return frame;
        }
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame frame,OcrOptions o,CancellationToken t) {
            var result=results[frame];
            if(o.SearchArea is { } r)result=result with {Lines=result.Lines.Where(l=>l.Bounds.Y>=r.Y && l.Bounds.Y+l.Bounds.Height<=r.Y+r.Height).ToArray()};
            return Task.FromResult(result);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Ai : ITranslationService
    {
        public int Calls;
        public Task<TranslationResult> TranslateAsync(TranslationRequest r,CancellationToken t){Interlocked.Increment(ref Calls);return Task.FromResult(new TranslationResult(string.Join("\n\n", r.Text.Split("\n\n").Select(p => "T:" + p)),TimeSpan.Zero,0));}
    }
}
