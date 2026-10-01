using Pat.Infrastructure.Ollama;

namespace Pat.Core.Tests;

/// <summary>
/// Интеграционный тест против живой Ollama: если сервер не запущен — тихо проходит
/// (интеграция опциональна), если запущен — проверяет реальный цикл chat/tags.
/// </summary>
public sealed class OllamaIntegrationTests
{
    private readonly OllamaClient _client = new("http://127.0.0.1:11434");

    [Fact]
    public async Task LiveServer_ListsModelsAndAnswers()
    {
        if (!await _client.IsAvailableAsync())
            return; // Ollama не запущена — интеграция опциональна, тест не падает

        var models = await _client.ListModelsAsync();
        Assert.NotEmpty(models);

        var answer = await _client.ChatAsync(
            models[0],
            "Отвечай кратко по-русски.",
            "Ответь ровно одним словом: работает.");

        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotContain("<think>", answer);
    }

    [Fact]
    public void StripThinking_RemovesReasoningBlocks()
    {
        Assert.Equal("Готово.", OllamaClient.StripThinking("<think>размышляю...\nмного</think>Готово."));
        Assert.Equal("Готово.", OllamaClient.StripThinking("<think>x</think>\n\nГотово."));
        Assert.Equal("Просто текст", OllamaClient.StripThinking("Просто текст"));
        Assert.Equal("", OllamaClient.StripThinking("<think>незакрытый"));
    }
}
