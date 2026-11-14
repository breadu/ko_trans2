using System.Text.Json;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class GameMarkerLearningTests
{
    private static int count;
    private static void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
    private static readonly PixelRect Body=new(20,40,240,24),Marker=new(275,50,14,12);
    private static readonly string[] Pages=["今日はとてもよい天気です。","静かな図書館で本を読みます。","遠くの山から不思議な音が聞こえます。","懐かしい友達と駅で待ち合わせます。","新しい世界への扉が開かれました。"];
    private static OcrResult Result(int page,PixelRect? marker=null)=>new([new(Pages[page],.98f,Body),new("VO",.9f,marker??Marker)],TimeSpan.Zero)
        {Candidates=[new(Pages[page],.98f,Body),new("VO",.9f,marker??Marker)],DetectedBoxes=[Body,marker??Marker]};
    private static ImageFrame Frame(int page,bool visible=true,PixelRect? marker=null,bool differentShape=false)
    {
        byte[] pixels=new byte[400*200*4];
        for(int y=Body.Y+3;y<Body.Y+Body.Height-3;y++)for(int x=Body.X+3;x<Body.X+Body.Width-3;x++)
            if((x+y+page)%7<3)Set(x,y,210,210,210);
        var r=marker??Marker;
        if(visible)for(int y=2;y<r.Height-2;y++)for(int x=2;x<r.Width-2;x++)
            if(differentShape ? x<4 || y<4 : x>=r.Width/3 && x<r.Width*2/3 || y>=r.Height/3 && y<r.Height*2/3)Set(r.X+x,r.Y+y,240,130,190);
        void Set(int x,int y,byte red,byte green,byte blue){int i=(y*400+x)*4;pixels[i]=blue;pixels[i+1]=green;pixels[i+2]=red;}
        return new(400,200,1600,pixels);
    }
    private static void Page(GameMarkerLearner learner,int page,long now,bool animate=true)
    {
        learner.ObserveOcr(Frame(page),Result(page),now);
        if(!animate)return;
        learner.ObserveFrame(Frame(page),now+200);
        learner.ObserveFrame(Frame(page,false),now+450);
        learner.ObserveFrame(Frame(page),now+700);
    }
    internal static async Task Run()
    {
        var learner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int i=0;i<8;i++)Page(learner,0,i*1000);
        Check(learner.Status.BestPages==1 && learner.Confirmed.Count==0,"Repeated OCR of one page trained a marker");
        Page(learner,1,9000);Page(learner,2,10000);Page(learner,3,11000);
        Check(learner.Confirmed.Count==0,"Fewer than five pages trained a marker");
        Page(learner,4,12000);
        Check(learner.Confirmed.Count==1 && learner.Status.AnimatedPages==2,"Five independent animated pages did not confirm a marker");
        var patterns=learner.Confirmed;
        Check(GameMarkerGeometry.CanExclude(Frame(0),Marker,Result(0).Lines,[Body,Marker],patterns),"Confirmed independent marker did not match");
        Check(!GameMarkerGeometry.CanExclude(Frame(0,differentShape:true),Marker,Result(0).Lines,[Body,Marker],patterns),"Unknown marker shape was excluded");
        var overlapping=new PixelRect(245,50,14,12);
        Check(!GameMarkerGeometry.CanExclude(Frame(0,marker:overlapping),overlapping,Result(0).Lines,[Body,overlapping],patterns),"Real body overlap was excluded");
        var bodyHeight=new PixelRect(275,40,14,24);
        Check(GameMarkerGeometry.Locate(bodyHeight,Result(0).Lines,[Body,bodyHeight]) is null,"Full-height real glyph qualified");
        var incomplete=Result(0).Lines.Select(l=>l.Bounds==Body ? l with {Text="まだ言葉が続いている"} : l).ToArray();
        Check(!GameMarkerGeometry.CanExclude(Frame(0),Marker,incomplete,[Body,Marker],patterns),"Incomplete body qualified");
        Check(GameMarkerGeometry.Locate(Marker,[new("日本語の長い文章です。",.98f,Body,true)],[Body,Marker]) is null,"Vertical body qualified");
        Check(!GameMarkerGeometry.Supported(new("en","ADV",false)) && !GameMarkerGeometry.Supported(new("ja","ADV",false,EngineId:"future-engine")),"Unsupported engine/language entered learning");
        var staticLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int i=0;i<5;i++)Page(staticLearner,i,i*1000,animate:false);
        Check(staticLearner.Confirmed.Count==0 && staticLearner.Status.BestPages==5,"Static symbols trained without an animation cycle");
        var transition=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int i=0;i<5;i++) {
            transition.ObserveOcr(Frame(i),Result(i),i*1000);
            transition.ObserveFrame(Frame((i+1)%5,false),i*1000+300);
            transition.ObserveFrame(Frame(i),i*1000+600);
        }
        Check(transition.Confirmed.Count==0,"A changed text page was treated as a marker blink");
        var minor=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
        for(int i=0;i<5;i++) {
            var noisy=Result(0);noisy=noisy with {Candidates=[noisy.Lines[0] with {Text=Pages[0].Replace('日',(char)('山'+i))},noisy.Lines[1]]};
            minor.ObserveOcr(Frame(0),noisy,i*1000);minor.ObserveFrame(Frame(0,false),i*1000+300);minor.ObserveFrame(Frame(0),i*1000+600);
        }
        Check(minor.Status.BestPages==1 && minor.Confirmed.Count==0,"Small OCR substitutions counted as independent pages");
        var off=new GameMarkerLearner(MarkerLearningMode.Off,[]);for(int i=0;i<5;i++)Page(off,i,i*1000);
        Check(off.Status.Candidates==0 && off.Confirmed.Count==0,"Off mode learned patterns");
        await StorageAndSession(patterns);
        Console.WriteLine($"PASS: {count} game marker learning assertions; synthetic pixels, temporary metadata and Mock OCR only. No game capture or AI calls.");
    }
    private static async Task StorageAndSession(IReadOnlyList<GameMarkerPattern> patterns)
    {
        string root=Path.Combine(Path.GetTempPath(),"KoTrans.MarkerLearning",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var profile=new Profile{Language="일본어"};var game=new GameWindowIdentity("game","C:\\Synthetic\\game.exe","GameClass","Scene 1");
            string key=MarkerLearningStore.Key(profile,game),path=Path.Combine(root,key+".json");var store=new MarkerLearningStore(root,key);
            Check(key==MarkerLearningStore.Key(profile,game with {Title="Scene 2"}),"A scene title change lost the game key");
            Check(key!=MarkerLearningStore.Key(profile,game with {ExecutablePath="C:\\Other\\game.exe"}),"Different games shared learning");
            Check(key!=MarkerLearningStore.Key(new Profile{Language="일본어"},game),"Different profiles shared learning");
            profile.OcrModelPresetId="default";Check(key!=MarkerLearningStore.Key(profile,game),"Different OCR models shared learning");
            await store.SaveAsync(patterns);var loaded=await store.LoadAsync();
            Check(loaded.Patterns.Count==1 && loaded.Patterns[0].Shapes[0]==patterns[0].Shapes[0],"Confirmed shapes did not reload");
            string json=await File.ReadAllTextAsync(path);
            Check(!json.Contains("Synthetic") && !json.Contains("Scene") && !Pages.Any(json.Contains) && !json.Contains("Pixels"),"Persisted learning contains private game data");
            await using(var stream=new MemoryStream()) {
                await DirectMlProtocol.WriteAsync(stream,new OcrOptions("ja","NVL",false,MarkerPatterns:patterns),DirectMlProtocol.MaxHeader,CancellationToken.None);
                stream.Position=0;var options=await DirectMlProtocol.ReadAsync<OcrOptions>(stream,DirectMlProtocol.MaxHeader,CancellationToken.None);
                Check(options.MarkerPatterns!.Single().Shapes[0]==patterns[0].Shapes[0],"GPU worker lost learned shape options");
            }
            var moved=patterns[0] with {Placements=[new(MarkerAnchor.BlockBottom,.1,.5,.6,.5)]};
            await store.SaveAsync([moved]);
            Check((await store.LoadAsync()).Patterns[0].Placements!.Single().Anchor==MarkerAnchor.BlockBottom,"Following-row placement did not persist");
            await using(var stream=new MemoryStream()) {
                var request=new OcrOptions("ja","NVL",false,MarkerPatterns:[moved],CollectMarkerLearning:true);
                await DirectMlProtocol.WriteAsync(stream,request,DirectMlProtocol.MaxHeader,CancellationToken.None);stream.Position=0;
                var restored=await DirectMlProtocol.ReadAsync<OcrOptions>(stream,DirectMlProtocol.MaxHeader,CancellationToken.None);
                Check(restored.CollectMarkerLearning && restored.MarkerPatterns![0].Placements!.Count==1,"GPU transport lost terminal learning/placement options");
            }
            foreach(var mode in new[]{MarkerLearningMode.Observe,MarkerLearningMode.Apply}) {
                var mock=new MockOcr();var learner=new GameMarkerLearner(mode,loaded.Patterns);int reports=0;
                var service=new MarkerLearningOcrService(mock,mode,learner,store,_=>reports++);
                var frame=Frame(0);service.ObserveFrame(frame);service.ObserveFrame(frame);
                var result=await service.RecognizeAsync(frame,new("ja","ADV",false),CancellationToken.None);
                Check(mock.Calls==1 && mock.Frame==frame && ReferenceEquals(result,mock.Result),"Learning added OCR or changed frame/results");
                Check((mock.Options!.MarkerPatterns?.Count>0)==(mode==MarkerLearningMode.Apply),"Observe/apply modes forwarded the wrong exclusion policy");
                Check(reports<=2,"Identical capture status queued repeated UI reports");
                await service.DisposeAsync();Check(!mock.Disposed,"Learning disposed the shared OCR");
                int stoppedReports=reports;service.ObserveFrame(Frame(1));Check(reports==stoppedReports,"Stopped learner updated a retired UI");
            }
            // Maturing during production wrapper use must persist automatically;
            // a late native result after Stop/reset must never recreate learning.
            await store.ResetAsync();var pendingLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
            for(int i=0;i<4;i++)Page(pendingLearner,i,i*1000);
            var lastPage=new MockOcr{Result=Result(4)};
            var autoSave=new MarkerLearningOcrService(lastPage,MarkerLearningMode.Apply,pendingLearner,store,_=>{});
            await autoSave.RecognizeAsync(Frame(4),new("ja","ADV",false),CancellationToken.None);await autoSave.StopAsync();
            Check((await store.LoadAsync()).Patterns.Count==1,"Newly confirmed pattern was not automatically persisted");
            await store.ResetAsync();var lateLearner=new GameMarkerLearner(MarkerLearningMode.Apply,[]);
            for(int i=0;i<4;i++)Page(lateLearner,i,i*1000);
            var delayed=new DelayedOcr();var late=new MarkerLearningOcrService(delayed,MarkerLearningMode.Apply,lateLearner,store,_=>{});
            var pending=late.RecognizeAsync(Frame(4),new("ja","ADV",false),CancellationToken.None);
            await late.StopAsync();await store.ResetAsync();delayed.Completion.SetResult(Result(4));await pending;
            Check(lateLearner.Confirmed.Count==0 && !File.Exists(path),"Late OCR recreated reset learning");
            await File.WriteAllTextAsync(path,"{broken");
            try {await store.LoadAsync();throw new Exception("Malformed learning loaded");}catch(InvalidDataException){count++;}
            try {await store.SaveAsync(patterns);throw new Exception("Malformed learning overwritten");}catch(InvalidDataException){count++;}
            Check(await File.ReadAllTextAsync(path)=="{broken","Corrupt learning was not preserved");
            await store.ResetAsync();Check(!File.Exists(path) && (await store.LoadAsync()).Patterns.Count==0,"Reset did not clear the selected game");
            var bad=patterns[0] with {X=double.NaN};
            try {await store.SaveAsync([bad]);throw new Exception("NaN geometry saved");}catch(InvalidDataException){count++;}
            try {DirectMlProtocol.ValidateInput(Frame(0),new("ja","ADV",false,MarkerPatterns:[bad]));throw new Exception("Invalid worker pattern accepted");}catch(ArgumentException){count++;}
            try {new MarkerLearningStore(root,"../escape");throw new Exception("Path key escaped");}catch(ArgumentException){count++;}
            var settings=new AppSettings();settings.Validate();
            var settingsDirectory=Path.Combine(root,"Settings");Directory.CreateDirectory(settingsDirectory);
            var settingsStore=new SettingsStore(settingsDirectory);
            foreach(int oldMode in new[]{0,1,2}) {
                var legacySettings=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(settings))!;
                legacySettings["Profiles"]![0]!["MarkerLearning"]=oldMode;
                await File.WriteAllTextAsync(Path.Combine(settingsDirectory,"settings.json"),legacySettings.ToJsonString());
                var restored=await settingsStore.LoadAsync();
                Check(restored.DefaultProfile.Id==settings.DefaultProfile.Id,"Retired learning mode broke profile loading");
                await settingsStore.SaveAsync(restored);
                Check(!File.ReadAllText(Path.Combine(settingsDirectory,"settings.json")).Contains("MarkerLearning"),
                    "Retired learning setting was saved again");
            }
            Check(JsonSerializer.Deserialize<Profile>("{}") is not null,"Profile without retired learning setting failed to load");
        } finally {Directory.Delete(root,true);}
    }
    private sealed class MockOcr:IOcrService
    {
        public int Calls;public bool Disposed;public ImageFrame? Frame;public OcrOptions? Options;
        public OcrResult Result=GameMarkerLearningTests.Result(0);
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t){Calls++;Frame=f;Options=o;return Task.FromResult(Result);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private sealed class DelayedOcr:IOcrService
    {
        public TaskCompletionSource<OcrResult> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OcrState State=>OcrState.Ready;
        public Task InitializeAsync(OcrOptions o,CancellationToken t)=>Task.CompletedTask;
        public Task<OcrResult> RecognizeAsync(ImageFrame f,OcrOptions o,CancellationToken t)=>Completion.Task;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
