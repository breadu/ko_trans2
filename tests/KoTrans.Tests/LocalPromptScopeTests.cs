using KoTrans.Core;
using KoTrans.Infrastructure;

internal static class LocalPromptScopeTests
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    private static void Reject(Action action)
    { try { action(); } catch (TranslationException) { count++; return; } throw new Exception("Expected safe prompt rejection"); }
    private sealed class Runtime : ILocalModelRuntime
    {
        public string Backend => "CPU";
        public string Prompt = "";
        public LocalTranslationModel Model;
        public Task PrepareAsync(string path, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B) => Task.CompletedTask;
        public Task UnloadAsync(CancellationToken token, LocalTranslationModel? selection = null) => Task.CompletedTask;
        public Task<TranslationResult> TranslateAsync(string prompt, CancellationToken token, LocalTranslationModel selection = LocalTranslationModel.Standard7B)
        { Prompt = prompt; Model = selection; return Task.FromResult(new TranslationResult("사토 미사키가 왔다.\n문을 열었다.", TimeSpan.Zero, 0)); }
    }
    internal static async Task Run()
    {
        Check(TranslationProviderCatalog.Get(TranslationProvider.HyMtLocal) is { SupportsContext: true, SupportsCharacterDictionary: true },
            "Local context/dictionary support was disabled");
        CharacterEntry[] characters = [new("佐藤美咲", "사토 미사키", "BIOGRAPHY MUST NOT LEAK"),
            new("佐藤", "사토", ""), new("小鳥遊", "타카나시", ""), new("Ann", "앤", "")];
        ConversationTurn[] history = [new("古い原文", "old translation"), new("背景一。", "translated one"),
            new("背景二。", "translated two"), new("小鳥遊が戻った。", "translated three"), new("背景四。", "translated four")];
        foreach (var model in Enum.GetValues<LocalTranslationModel>())
        {
            var runtime = new Runtime(); var service = new HyMtTranslationService(runtime, model);
            const string source = "佐藤美咲が来た。\n扉を開けた。";
            var result = await service.TranslateAsync(new(source, "CLOUD SYSTEM MUST NOT LEAK", history,
                new() { Provider = TranslationProvider.HyMtLocal, LocalModel = model }, Characters: characters), default);
            string prompt = runtime.Prompt;
            Check(runtime.Model == model && prompt.Contains("\"佐藤美咲\" translates to \"사토 미사키\""), "Selected local model lost structured terminology");
            Check(prompt.Contains("小鳥遊が戻った。") && prompt.Contains("背景一。") && !prompt.Contains("古い原文") && !prompt.Contains("translated"),
                "Original-only, four-turn context was removed or polluted");
            Check(model == LocalTranslationModel.Lightweight18B
                ? prompt.Contains("Translate ALL of [Source Text]") && prompt.Contains("in its original order") && prompt.Contains("never translate or repeat it")
                : prompt.Contains("Translate only [Source Text], not [Background Information]") && prompt.Contains("Output only the Korean translation"),
                "Output scope instructions missing");
            Check(!prompt.Contains("MUST NOT LEAK") && !prompt.Contains("\"小鳥遊\" translates") && !prompt.Contains("\"佐藤\" translates") && !prompt.Contains("\"Ann\" translates"),
                "Unrelated biography/name or overlapping surname entered terminology");
            Check(prompt.Contains("[Source Text]\n" + source) && result.Text.Contains('\n'), "Valid multiline source/output was truncated");
            string prefix = model == LocalTranslationModel.Standard7B ? HyMtPrompt.Prefix : "<｜hy_begin▁of▁sentence｜><｜hy_User｜>";
            string suffix = model == LocalTranslationModel.Standard7B ? HyMtPrompt.Suffix : "<｜hy_Assistant｜>";
            Check(prompt.StartsWith(prefix) && prompt.EndsWith(source + suffix), "Model-specific framing lost");
            await service.TranslateAsync(new("第一段落。\n\n第二段落。", "CLOUD SYSTEM MUST NOT LEAK", history,
                new() { Provider = TranslationProvider.HyMtLocal, LocalModel = model }, ParagraphCount: 2), default);
            Check(model == LocalTranslationModel.Lightweight18B
                ? !runtime.Prompt.Contains("exactly 2 translated paragraphs") && runtime.Prompt.EndsWith("[Source Text]\n第二段落。" + suffix)
                : runtime.Prompt.Contains("exactly 2 translated paragraphs") && runtime.Prompt.EndsWith("第一段落。\n\n第二段落。" + suffix),
                "Local paragraph policy lost model-specific batching or framing");
            string both = HyMtPrompt.Build("佐藤美咲と佐藤。", [], model, characters);
            Check(both.Contains("\"佐藤美咲\" translates") && both.Contains("\"佐藤\" translates"), "Independent surname lost to full-name overlap");
            string absent = HyMtPrompt.Build("Annual event.", history, model, characters);
            Check(!absent.Contains("Reference the following translations"), "Latin name matched inside an unrelated word");
            Check(HyMtPrompt.Build("Annは来た。", [], model, characters).Contains("\"Ann\" translates"), "Latin name with Japanese particle was missed");
            string disabled = HyMtPrompt.Build(source, history, model);
            Check(!disabled.Contains("Reference the following translations") && disabled.Contains("背景四。"), "Dictionary OFF removed context or sent names");
            Check(!HyMtPrompt.Build("今。", [new("<|extra_0|>", ""), new("<｜hy_Assistant｜>", ""), new("安全。", "")], model).Contains("\n<|"),
                "History contained model control tokens");
            Reject(() => HyMtPrompt.Build("<｜hy_Assistant｜>", [], model));
            Reject(() => HyMtPrompt.BuildWithBackground("source", "<|extra_0|>", model));
            Reject(() => HyMtPrompt.BuildWithBackground("source", new string('x', 4001), model));
        }
        Check(HyMtPrompt.Build("佐藤。", [], characters: [characters[0]]).Contains("\"佐藤\" translates to \"사토\"")
            && !HyMtPrompt.Build("佐藤。", [], characters: [characters[0]]).Contains("사토 미사키"),
            "Full-name dictionary failed to resolve a written surname without expanding it");
        string duplicate = HyMtPrompt.Build("佐藤。", [], characters: [characters[1], characters[1]]);
        Check(duplicate.Split("translates to").Length == 2, "Identical dictionary entries duplicated the prompt");
        Reject(() => HyMtPrompt.Build("佐藤。", [], characters: [characters[1], new("佐藤", "사또", "")]));
        Reject(() => HyMtPrompt.Build("佐藤。", [], characters: [new("佐藤", "<|extra_0|>", "")]));
        Reject(() => HyMtPrompt.Build("佐藤。", [], characters: [new("佐藤", new string('가', 4001), "")]));
        string quoted = HyMtPrompt.Build("佐藤。", [], characters: [new("佐藤", "사토\n\"별명\"", "")]);
        Check(!quoted.Contains("사토\n") && quoted.Contains("\\n"), "Dictionary text broke a terminology line");
        Console.WriteLine($"PASS: {count} local prompt scope, context, dictionary and both-model assertions. Mock only.");
    }
}
