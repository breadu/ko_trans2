using System.Security.Cryptography;
using System.Text.Json;
using KoTrans.Core;
using KoTrans.Ocr;
using KoTrans.Infrastructure;

internal static class PaddlePresetTests
{
    internal static async Task Run()
    {
        int count=0; void Check(bool ok, string text) { if (!ok) throw new Exception(text); count++; }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or OcrInitializationException) { count++; return; } throw new Exception("Invalid model accepted"); }
        string root=Path.Combine(Path.GetTempPath(),"KoTrans2.Presets",Guid.NewGuid().ToString("N"));
        ModelSpec Make(string folder, string kind, string name)
        {
            string dir=Path.Combine(root,folder); Directory.CreateDirectory(dir);
            byte[] bytes=[1,2,3]; File.WriteAllBytes(Path.Combine(dir,"model.onnx"),bytes); File.WriteAllText(Path.Combine(dir,"dictionary.txt"),"a\nあ\n \n");
            var spec=new ModelSpec {Name=name, Kind=kind,Decoder=kind=="detection"?"DB":"CTC",Languages=["en","ja"],
                Sha256=Convert.ToHexString(SHA256.HashData(bytes)), DictionarySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir,"dictionary.txt"))))};
            File.WriteAllText(Path.Combine(dir,"manifest.json"),JsonSerializer.Serialize(spec)); return spec;
        }
        try {
            var det=Make("det","detection","server-det"); Make("en","recognition","english"); Make("ja","recognition","server-rec");
            var fastDet=Make("fast/det","detection","mobile-det"); var fastRec=Make("fast/ja","recognition","mobile-rec");
            var factory=new OcrServiceFactory(OcrEngineCatalog.Default,root); var options=new OcrOptions("ja","ADV",false);
            Check(factory.Presets.Presets.First().Id=="v6-medium-full" && new Profile().OcrOptions().ModelPresetId=="v6-medium-full", "New profile/default order is not v6");
            Check(JsonSerializer.Deserialize<Profile>("{\"OcrModelPresetId\":\"default\"}")!.OcrOptions().ModelPresetId=="default", "Explicit saved legacy preset changed");
            Check(JsonSerializer.Deserialize<Profile>("{\"OcrModelPresetId\":\"v5-det-v6-medium-rec\"}")!.OcrModelPresetId=="v5-det-v6-medium-rec", "Explicit mixed preset changed");
            Check(JsonSerializer.Deserialize<Profile>("{\"OcrModelPresetId\":null}")!.OcrModelPresetId is null,"Explicit corrupt null was silently replaced by v6");
            string before=factory.SelectionKey(options);
            var full=options with {ModelPresetId="v6-medium-full"}; var mixed=options with {ModelPresetId="v5-det-v6-medium-rec"};
            Check(!factory.CheckModels(full).Ready && !factory.CheckModels(mixed).Ready,"Missing v6 silently fell back");
            var v6Det=Make("paddle/v6-medium/det","detection","v6-det"); var v6Rec=Make("paddle/v6-medium/rec","recognition","v6-rec");
            Check(before==factory.SelectionKey(options),"Adding v6 changed default legacy path");
            Check(factory.CheckModels(full).Ready && factory.CheckModels(mixed).Ready,"v6 layout unavailable");
            Check(factory.SelectionKey(mixed).Contains(Path.Combine("fast","det")) && factory.SelectionKey(mixed).Contains(Path.Combine("v6-medium","rec")),"Mixed preset changed preferred detector");
            Check(factory.SelectionKey(options with {Language="en"}).Contains(Path.Combine(root,"en")),"Legacy English fallback changed");
            Check(PaddleModelSelection.Resolve(root,ModelCatalog.PreferredExecution(root),"ja").Recognizer.Root==Path.Combine(root,"fast"),"Default mobile recognizer changed");
            var identity=new OcrRunIdentity(OcrIdentity.From(v6Det),OcrIdentity.From(v6Rec),true,4) {ModelPresetId=full.ModelPresetId,FlushDenormalsToZero=true};
            var replay=OcrServiceFactory.PreparePaddleReplay(root,full,identity);
            Check(replay.RecognizerFolder=="rec" && replay.RecognizerRoot==Path.Combine(root,"paddle","v6-medium"),"Multilingual QA recognizer path lost");
            Reject(()=>OcrServiceFactory.PreparePaddleReplay(root,options,identity));
            Reject(()=>OcrServiceFactory.PreparePaddleReplay(root,full,identity with {Detector=OcrIdentity.From(det)}));
            var legacyIdentity=new OcrRunIdentity(OcrIdentity.From(fastDet),OcrIdentity.From(fastRec),true,4) {FlushDenormalsToZero=true};
            Check(OcrServiceFactory.PreparePaddleReplay(root,options,legacyIdentity).DetectorRoot==Path.Combine(root,"fast"),"Old QA mobile replay changed");
            Check(!JsonSerializer.Serialize(det).Contains("TensorContract"),"Legacy manifest contract hash changed");
            await using(var manager=new OcrServiceManager(factory)) {
                var profile=Guid.NewGuid(); var first=manager.Select(profile,full);
                Check(first.State==OcrState.NotConfigured && ReferenceEquals(first,manager.Select(profile,full)),"Readiness/selection initialized ONNX");
                Check(ReferenceEquals(first,manager.Select(profile,full with {Language="en"})),"Shared multilingual model needlessly recreated on language switch");
                string dict=Path.Combine(root,"paddle/v6-medium/rec/dictionary.txt"); File.AppendAllText(dict,"b\n");
                Check(!ReferenceEquals(first,manager.Select(profile,full)),"Changed dictionary reused native session");
                Reject(()=>ModelCatalog.Load(Path.Combine(root,"paddle/v6-medium"),"rec"));
                Make("paddle/v6-medium/rec","recognition","v6-rec");
            }
            var snapshot=new GameQaSnapshot(new(4,4,16,new byte[64]),new(1,Guid.NewGuid(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,
                "completed",full,GameQaProfile.From(new()),new(4,4,FramePixelFormat.Bgra32,4,4,null),new([],TimeSpan.Zero) {Models=identity},"",new("none"),0,AlgorithmVersion:OcrAlgorithmRevision.Current));
            await using(var service=GameQaReplay.Create(snapshot,factory)) Check(service.State==OcrState.NotConfigured,"QA initialized native model eagerly");
            Reject(()=>factory.CreateReplay(full,identity with {ModelPresetId="default"}));
            File.WriteAllText(Path.Combine(root,"paddle/v6-medium/det/manifest.json"),"{broken");
            Check(!factory.CheckModels(full).Ready,"Corrupt manifest reported ready");
            File.WriteAllBytes(Path.Combine(root,"fast/det/model.onnx"),[3,2,1]);
            Reject(()=>ModelCatalog.Load(Path.Combine(root,"fast"),"det"));
            // A nested legacy paddle layout remains supported independently of the new version folder.
            Make("paddle/det","detection","nested-det"); Make("paddle/ja","recognition","nested-rec");
            Check(factory.SelectionKey(options).Contains(Path.Combine(root,"paddle","ja")),"Nested legacy model layout lost");
        } finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"PASS: {count} Paddle preset/path/integrity/QA assertions (no ONNX).");
    }
}
