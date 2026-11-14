using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// The selected request protocol is independent of model IDs and aliases. Add adapters here;
// never infer a specialized model's training contract from an arbitrary model name.
internal interface IOllamaPromptFormat
{
    bool SupportsStudy { get; }
    object[] Messages(TranslationRequest request);
}
internal static class OllamaPromptFormats
{
    private static readonly IReadOnlyDictionary<OllamaRequestFormat, IOllamaPromptFormat> formats = new Dictionary<OllamaRequestFormat, IOllamaPromptFormat> {
        [OllamaRequestFormat.Chat] = new ChatFormat(),
        [OllamaRequestFormat.TranslateGemma] = new TranslateGemmaFormat(),
        [OllamaRequestFormat.HyMt] = new HyMtFormat()
    };
    public static IOllamaPromptFormat Get(OllamaRequestFormat format) => formats.TryGetValue(format, out var adapter) ? adapter : throw new TranslationException("지원하지 않는 Ollama 요청 형식입니다. 모델별 요청 방식을 확인하세요.");
    private sealed class HyMtFormat : IOllamaPromptFormat
    {
        public bool SupportsStudy => false;
        public object[] Messages(TranslationRequest request) => [new {
            role = "user",
            content = HyMtPrompt.BuildOllamaUserMessage(request.Text, request.History,
                characters: request.Characters, paragraphCount: request.ParagraphCount)
        }];
    }
    private sealed class ChatFormat : IOllamaPromptFormat
    {
        public bool SupportsStudy => true;
        public object[] Messages(TranslationRequest request)
        {
            if (request.OutputSchema is not null) return HttpTranslationService.Messages(request, true);
            // Restate the task AFTER the reference dictionary, close to the actual input.
            // Small chat models must not treat the last biography or prior reply as output.
            var scoped = request with { SystemPrompt = request.SystemPrompt +
                "\nTranslate only current_source into Korean. previous_source_context and character reference data are context only, not text to output. Return only the translation; do not add names absent from current_source." };
            return HttpTranslationService.Messages(scoped, true);
        }
    }
    private sealed class TranslateGemmaFormat : IOllamaPromptFormat
    {
        public bool SupportsStudy => false;
        public object[] Messages(TranslationRequest request)
        {
            var source = request.SourceLanguage switch { "en" => "English", "ja" => "Japanese", _ => throw new TranslationException("TranslateGemma 번역에는 원문 언어가 필요합니다. 프로필에서 영어 또는 일본어를 선택하세요.") };
            // Ollama's official TranslateGemma guide requires one user turn and two blank lines.
            var format = request.ParagraphCount > 1 ? TranslationParagraphs.Instruction(request.ParagraphCount) + "\n" : "";
            var prompt = $"You are a professional {source} ({request.SourceLanguage}) to Korean (ko) translator. Your goal is to accurately convey the meaning and nuances of the original {source} text while adhering to Korean grammar, vocabulary, and cultural sensitivities.\nProduce only the Korean translation, without any additional explanations or commentary. {format}Please translate the following {source} text into Korean:\n\n\n{request.Text}";
            return [new { role = "user", content = prompt }];
        }
    }
}
