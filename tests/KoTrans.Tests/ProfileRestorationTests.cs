using System.Text.Json;
using System.Text.Json.Nodes;
using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class ProfileRestorationTests
{
    internal static async Task Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var dir = Path.Combine(Path.GetTempPath(), "KoTrans2.ProfileTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var settings = new AppSettings(); settings.Validate(); var defaults = settings.DefaultProfile;
            Check(!defaults.CaptureAreaConfigured,"Fresh default unexpectedly has confirmed region");
            Check(defaults.CaptureMode == CaptureMode.Window, "Fresh install must recommend window capture");
            var regionStore=new SettingsStore(dir);
            Check((await regionStore.LoadAsync()).DefaultProfile.Translation.Provider == TranslationProvider.HyMtLocal,
                "A fresh settings store did not default to local AI");
            await regionStore.SaveAsync(settings);
            Check(!(await regionStore.LoadAsync()).DefaultProfile.CaptureAreaConfigured,"Unconfigured region flag lost on restart");
            var legacyArea=JsonSerializer.SerializeToNode(settings)!.AsObject();legacyArea["Profiles"]!.AsArray()[0]!.AsObject().Remove("CaptureAreaConfigured");
            await File.WriteAllTextAsync(Path.Combine(dir,"settings.json"),legacyArea.ToJsonString());
            Check((await regionStore.LoadAsync()).DefaultProfile.CaptureAreaConfigured,"Legacy stored default-size rectangle discarded");
            // An old settings file without CaptureMode still means the historical Region mode.
            var legacyMode=JsonSerializer.SerializeToNode(settings)!.AsObject();
            legacyMode["Profiles"]!.AsArray()[0]!.AsObject().Remove("CaptureMode");
            await File.WriteAllTextAsync(Path.Combine(dir,"settings.json"),legacyMode.ToJsonString());
            Check((await regionStore.LoadAsync()).DefaultProfile.CaptureMode == CaptureMode.Region,
                "Missing legacy CaptureMode must not silently switch to window capture");
            defaults.CaptureMode = CaptureMode.Region;
            await regionStore.SaveAsync(settings);
            Check((await regionStore.LoadAsync()).DefaultProfile.CaptureMode == CaptureMode.Region,
                "Explicit saved screen capture preference changed after the default update");
            defaults.CaptureMode = CaptureMode.Window;
            var legacyStyle=JsonSerializer.SerializeToNode(settings)!.AsObject();
            var legacyProfile=legacyStyle["Profiles"]!.AsArray()[0]!.AsObject(); legacyProfile.Remove("OverlayOriginalFontSize");legacyProfile.Remove("OverlayOriginalColor");
            await File.WriteAllTextAsync(Path.Combine(dir,"settings.json"),legacyStyle.ToJsonString());
            var legacyLoaded=(await regionStore.LoadAsync()).DefaultProfile;
            Check(legacyLoaded.OverlayOriginalFontSize==21 && legacyLoaded.OverlayOriginalColor=="#D3D3D3","Missing original text size did not use the 21 DIP default");
            defaults.OverlayOriginalFontSize=15; await regionStore.SaveAsync(settings);
            Check((await regionStore.LoadAsync()).DefaultProfile.OverlayOriginalFontSize==15,"Explicitly saved original font size was overwritten by the new default");
            defaults.OverlayOriginalFontSize=21;defaults.OverlayOriginalColor="#B347DA";
            defaults.Name = "Renamed default"; defaults.Language = "일본어"; defaults.OverlayFontSize = 25;
            defaults.Translation = new() { Provider = TranslationProvider.Ollama, Model = "model-X" };
            defaults.Translation.OllamaModels = defaults.Translation.OllamaModels.SetItem("model-X",new(OllamaRequestFormat.TranslateGemma,150,12));
            defaults.GameWindow = new("emulator",@"C:\games\emulator.exe","GameClass","Game Original");
            defaults.OverlayWindow.MonitorId = "monitor-serial"; defaults.Triggers.Keys = "Enter";
            foreach (var mode in Enum.GetValues<CaptureMode>()) {
                defaults.CaptureMode = mode;
                defaults.CaptureArea = new(-900, 140, 640, 180);
                defaults.CaptureAreaConfigured = true;
                defaults.WindowCaptureRegion = new(12, 15, 640, 180);
                var fresh = settings.CreateGameProfile("new region test");
                Check(fresh.CaptureMode == CaptureMode.Window && !fresh.CaptureAreaConfigured && fresh.WindowCaptureRegion is null
                    && fresh.CaptureArea == new PixelRect(0, 0, 800, 300) && fresh.GameWindow is null,
                    "New game inherited screen or window coordinates");
                Check(defaults.CaptureAreaConfigured && defaults.CaptureArea.X == -900 && defaults.WindowCaptureRegion.HasValue,
                    "Creating a profile erased an existing crop");
                settings.DeleteProfile(fresh.Id);
            }
            var a = settings.CreateGameProfile("A");
            Check(a.Language == "일본어" && a.Translation.Model == "model-X" && a.OverlayFontSize == 25 && a.Triggers.Keys == "Enter", "New profile ignored Default values");
            Check(a.Id != defaults.Id && a.GameWindow is null, "New profile copied game identity or ID");
            Check(!ReferenceEquals(a.Translation,defaults.Translation) && !ReferenceEquals(a.Triggers,defaults.Triggers) && !ReferenceEquals(a.OverlayWindow,defaults.OverlayWindow),"Nested profile objects were shared");
            a.OverlayOriginalFontSize=33;a.OverlayOriginalColor="#00AABB";
            Check(defaults.OverlayOriginalFontSize==21 && defaults.OverlayOriginalColor=="#B347DA","Original styling mutated template profile");
            a.Language = "영어"; a.Translation.Model = "model-Y"; a.Triggers.Keys = "Space"; a.OverlayWindow.X = -1450;
            a.Translation.OllamaModels = a.Translation.OllamaModels.SetItem("model-X", new());
            Check(defaults.Language == "일본어" && defaults.Translation.Model == "model-X" && defaults.Triggers.Keys == "Enter" && defaults.OverlayWindow.X == 100,"Editing A mutated Default");
            Check(defaults.Translation.OllamaModels["model-X"].Format == OllamaRequestFormat.TranslateGemma,"Model settings mutated Default");
            defaults.OverlayFontSize = 30; var b = settings.CreateGameProfile("B");
            Check(a.OverlayFontSize == 25 && b.OverlayFontSize == 30,"Default changes propagated backward or failed future copies");
            settings.Profiles.Reverse(); settings.Validate();
            Check(settings.DefaultProfileId == defaults.Id && !settings.DeleteProfile(defaults.Id),"Sorting or renaming changed/deleted Default");
            var store = new SettingsStore(dir);
            foreach (var selected in new[] { a, b, a, b }) {
                settings.ActiveProfileId = selected.Id; await store.SaveAsync(settings); var loaded = await store.LoadAsync();
                Check(loaded.ActiveProfileId == selected.Id && loaded.DefaultProfileId == defaults.Id,"Last profile/default failed restart restoration");
                Check(loaded.Profiles.Single(p=>p.Id==a.Id).OverlayWindow.X==-1450,"Profile placement lost during restart");
                Check(loaded.Profiles.Single(p=>p.Id==a.Id).OverlayOriginalFontSize==33 && loaded.Profiles.Single(p=>p.Id==a.Id).OverlayOriginalColor=="#00AABB","Original font/color lost on disk roundtrip");
            }
            settings.DeleteProfile(b.Id); Check(settings.ActiveProfileId == defaults.Id,"Deleting selected profile did not select Default");
            settings.ActiveProfileId = Guid.NewGuid(); settings.Validate(); Check(settings.ActiveProfileId == defaults.Id,"Missing active profile did not fall back to stable Default");
            // Legacy JSON: don't use real settings, preserve every original byte and ID during migration.
            var legacy = new AppSettings { SchemaVersion = 2 }; legacy.Validate();
            legacy.Profiles[0].OverlayColor = "#123ABC"; legacy.Profiles[0].CharacterDictionaryPath = "synthetic-dictionary.json";
            legacy.Profiles.Add(new() { Name="Game", CaptureMode=CaptureMode.Window, WindowCaptureRegion=new(10,20,400,150), CaptureArea=new(-900,300,400,150) });
            legacy.ActiveProfileId=legacy.Profiles[1].Id;
            var node = JsonSerializer.SerializeToNode(legacy)!.AsObject(); node.Remove("DefaultProfileId");
            foreach (var item in node["Profiles"]!.AsArray()) { var obj=item!.AsObject(); foreach(var name in new[]{"CharacterDictionaryEnabled","GameWindow","AutomaticWatch","TriggerDelayMs"}) obj.Remove(name); }
            string original = node.ToJsonString(); string path=Path.Combine(dir,"settings.json"); await File.WriteAllTextAsync(path,original);
            var migrated=await new SettingsStore(dir).LoadAsync();
            Check(migrated.SchemaVersion==3 && migrated.DefaultProfileId==legacy.Profiles[0].Id && migrated.ActiveProfileId==legacy.ActiveProfileId,"Legacy profile IDs/selection changed");
            Check(migrated.Profiles[0].OverlayColor=="#123ABC" && migrated.Profiles[0].CharacterDictionaryEnabled && migrated.Profiles[0].CharacterDictionaryPath=="synthetic-dictionary.json","Legacy color/dictionary behavior changed");
            Check(migrated.Profiles[0].TriggerDelayMs==0 && migrated.Profiles[0].AutomaticWatch,"Missing trigger delay did not use the new zero-ms default");
            Check(migrated.Profiles[1].WindowCaptureRegion==new PixelRect(10,20,400,150),"Legacy relative crop overwritten by absolute coordinates");
            Check(await File.ReadAllTextAsync(path)==original && await File.ReadAllTextAsync(path+".v2.bak")==original,"Migration damaged original/backup");
            var mstore=new SettingsStore(dir); await mstore.LoadAsync(); await mstore.SaveAsync(migrated); var roundtrip=await mstore.LoadAsync();
            Check(roundtrip.DefaultProfileId==migrated.DefaultProfileId && roundtrip.ActiveProfileId==legacy.ActiveProfileId,"Migrated settings failed save/reload");
            var explicitOff = JsonSerializer.SerializeToNode(legacy)!.AsObject();
            explicitOff["Profiles"]!.AsArray()[0]!["TriggerDelayMs"] = 350;
            explicitOff["Profiles"]!.AsArray()[0]!["CharacterDictionaryEnabled"] = false;
            await File.WriteAllTextAsync(path, explicitOff.ToJsonString());
            var explicitSettings = await new SettingsStore(dir).LoadAsync();
            Check(explicitSettings.DefaultProfile.TriggerDelayMs == 350, "Zero-ms default overwrote an explicit saved trigger delay");
            Check(!explicitSettings.DefaultProfile.CharacterDictionaryEnabled,
                "Legacy dictionary migration overwrote an explicit disabled checkbox");
            foreach(var badSize in new[]{double.NaN,double.PositiveInfinity,11d,61d}) {
                var invalid=new AppSettings();invalid.Profiles[0].OverlayOriginalFontSize=badSize;
                try{invalid.Validate();throw new Exception("Invalid original font size accepted");}catch(InvalidDataException){checks++;}
            }
            foreach(var badColor in new[]{"red","#12345G","#12345678",""}) {
                var invalid=new AppSettings();invalid.Profiles[0].OverlayOriginalColor=badColor;
                try{invalid.Validate();throw new Exception("Invalid original color accepted");}catch(InvalidDataException){checks++;}
            }
            var identity=new GameWindowIdentity("emulator",@"C:\games\emulator.exe","GameClass","Game A");
            WindowTarget Target(int id,string title)=>new(new IntPtr(id),id,id,title,"emulator",identity.ExecutablePath,identity.WindowClass);
            var one=Target(2,"Game A"); var other=Target(3,"Game B");
            Check(WindowTargets.Reconnect(identity,[one,other],out bool ambiguous) is null && ambiguous,"Mutable title silently narrowed identical executable/class windows");
            Check(WindowTargets.Reconnect(identity,[],out ambiguous) is null && !ambiguous,"Missing game was invented");
            Check(WindowTargets.Reconnect(identity,[one,Target(4,"Game A")],out ambiguous) is null && ambiguous,"Ambiguous game was arbitrarily chosen");
            Check(WindowTargets.Reconnect(identity,[Target(5,"Game A")],out ambiguous)?.ProcessId==5,"Restarted game still depended on PID/HWND");
            Check(WindowTargets.Reconnect(identity,[Target(6,"Game A - changed scene")],out ambiguous)?.ProcessId==6 && !ambiguous,"Unique executable/class game did not reconnect after title change");
            Check(WindowTargets.Reconnect(identity,[Target(7,"Game A") with {ExecutablePath=@"C:\other\emulator.exe"}],out ambiguous) is null,"Same process name/title captured a different executable");
            Check(WindowTargets.Reconnect(identity,[Target(8,"Game A") with {WindowClass="OtherClass"}],out ambiguous) is null,"Different window class was accepted");
            var legacyIdentity=identity with {ExecutablePath=""};
            Check(WindowTargets.Reconnect(legacyIdentity,[Target(9,"Changed")],out ambiguous) is null,"Legacy metadata guessed a game by process name");
            var moved=WindowCaptureBounds.Resolve(new(-1700,150,1000,700),new(20,30,400,160));
            Check(moved==new PixelRect(-1680,180,400,160),"Relative crop did not follow moved game");
            try { WindowCaptureBounds.Resolve(new(0,0,200,100),new(20,30,400,160)); throw new Exception("Invalid resized crop accepted"); } catch(InvalidDataException) { checks++; }
            var saved=new WindowPlacement { X=-1500,Y=400,Width=480,Height=300,MonitorId="serial-B",MonitorDevice="DISPLAY2",MonitorWorkX=-1600,MonitorWorkY=0 };
            var primary=new DisplayArea("serial-A","DISPLAY1",new(0,0,1920,1080),1,true);
            var portrait=new DisplayArea("serial-B","DISPLAY2",new(-1600,0,1600,2560),2.5);
            string before=JsonSerializer.Serialize(saved);
            var r=OverlayPlacementPolicy.Resolve(saved,[primary,portrait]);
            Check(r==new PlacementResolution(-1500,400,480,300,false),"Negative portrait/250% placement mixed physical pixels and DIPs");
            var fallback=OverlayPlacementPolicy.Resolve(saved,[primary]);
            Check(fallback.Temporary && fallback.X>=0 && JsonSerializer.Serialize(saved)==before,"Disconnected monitor fallback overwrote saved placement");
            Check(OverlayPlacementPolicy.Resolve(saved,[primary,portrait])==r,"Reconnected monitor did not restore original placement");
            var rearranged=portrait with { Device="DISPLAY9",WorkArea=new(1920,-600,1600,2560) };
            var changed=OverlayPlacementPolicy.Resolve(saved,[primary,rearranged]);
            Check(!changed.Temporary && changed.X==2020 && changed.Y==-200,"Monitor ID/origin offset failed rearrangement");
            Check(OverlayPlacementPolicy.Resolve(saved,[primary,portrait with { Id="different-monitor" }]).Temporary,"DeviceName incorrectly treated as permanent identity");
            var narrow=OverlayPlacementPolicy.Resolve(new() { X=-1500,Y=0,Width=900,Height=1400 },[portrait]);
            Check(narrow.Width==640 && narrow.Height==1024,"250% size was not bounded in physical pixels");
            Check(new AppSettings().Profiles.Count==1,"First run lost usable Default");
            var invalidNode=JsonSerializer.SerializeToNode(roundtrip)!.AsObject(); invalidNode["DefaultProfileId"]=Guid.Empty;
            var brokenSource=invalidNode.ToJsonString(); await File.WriteAllTextAsync(path,brokenSource); var brokenStore=new SettingsStore(dir);
            bool rejected=false; try { await brokenStore.LoadAsync(); } catch(InvalidDataException) { rejected=true; }
            Check(rejected && await File.ReadAllTextAsync(path)==brokenSource,"Corrupt v3 default ID was silently reinterpreted");
            try { await brokenStore.SaveAsync(roundtrip); throw new Exception("Corrupt v3 overwritten"); } catch(InvalidOperationException) { checks++; }
            Console.WriteLine($"PASS: {checks} profile restoration assertions (Settings); {clock.Elapsed.TotalMilliseconds:F0}ms. Synthetic only, no desktop/API.");
        } finally { Directory.Delete(dir,true); }
    }
}
