using KoTrans.Core;

internal static class CharacterNameHintTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        CharacterEntry[] names = [new("佐藤美咲", "사토 미사키", "unused"), new("佐藤健太", "사토 켄타", ""),
            new("小鳥遊一樹", "타카나시 카즈키", ""), new("Alice Smith", "앨리스 스미스", "")];
        foreach (var pair in new[] { ("佐藤が来た。", "佐藤", "사토"), ("美咲は待っていた。", "美咲", "미사키"),
            ("小鳥遊さん。", "小鳥遊", "타카나시"), ("一樹君。", "一樹", "카즈키"), ("佐藤先生。", "佐藤", "사토"),
            ("Alice opened it.", "Alice", "앨리스"), ("Mr. Smith arrived.", "Smith", "스미스"),
            ("佐藤美咲が来た。", "佐藤美咲", "사토 미사키") }) {
            var hints = CharacterNameHints.Resolve(pair.Item1, names);
            Check(hints.Count == 1 && hints[0] == new CharacterNameHint(pair.Item2, pair.Item3), "Full/partial name resolution failed: " + pair.Item2);
        }
        var both = CharacterNameHints.Resolve("美咲は佐藤を呼んだ。", names);
        Check(both.Count == 2 && both.Any(h => h.SourceName == "美咲") && both.Any(h => h.SourceName == "佐藤"), "Independent surname/given name hints lost");
        foreach (string source in new[] { "ありがとう。", "Annual report.", "Smithsonian museum.", "佐藤商店へ行く。", "鳥遊を見た。", "美しい。" })
            Check(CharacterNameHints.Resolve(source, names).Count == 0, "Word fragment/unrelated name inferred: " + source);
        Check(CharacterNameHints.Resolve("佐藤。", [..names, new("佐藤", "사또", "")]).Single().KoreanName == "사또", "Explicit spelling must override inferred surname");
        Check(CharacterNameHints.Resolve("佐藤。", [..names, new("佐藤太郎", "사또 타로", "")]).Count == 0, "Conflicting inferred surname must be omitted");
        Check(CharacterNameHints.Resolve("一樹。", [new("小鳥遊一樹", "타카나시카즈키", "")]).Count == 0, "Unseparated Korean name was guessed");
        Check(CharacterNameHints.Resolve("美。", [new("佐藤美", "사토 미", "")]).Count == 0, "Single common character inferred as a name");
        Check(CharacterNameHints.Resolve("美。", [new("美", "미", "")]).Single().KoreanName == "미", "Explicit one-character name should remain supported");
        Check(CharacterNameHints.Resolve("美さん。", [new("佐藤美", "사토 미", "")]).Single().KoreanName == "미", "Single-character name with an honorific was missed");
        Check(CharacterNameHints.Resolve("樹：こんにちは。", [new("樹花子", "이츠키 하나코", "")]).Single().KoreanName == "이츠키", "Single-character surname speaker label was missed");
        Check(CharacterNameHints.Resolve("佐藤さん。", [new("佐藤 美咲", "사토 미사키", "")]).Single().KoreanName == "사토", "Explicitly separated Japanese name lost");
        Check(CharacterNameHints.Resolve("佐藤商店。", [new("佐藤 美咲", "사토 미사키", "")]).Count == 0, "Separated name matched inside a larger Han word");
        Check(CharacterNameHints.Resolve("佐藤。", null).Count == 0, "Disabled dictionary generated hints");
        Console.WriteLine($"PASS: {count} shared full/surname/given-name, ambiguity and word-boundary assertions. Synthetic only.");
    }
}
