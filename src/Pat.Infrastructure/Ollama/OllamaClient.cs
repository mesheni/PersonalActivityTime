using System.Text.Json;

namespace Pat.Infrastructure.Ollama;

/// <summary>
/// Клиент локальной Ollama (http://localhost:11434 по умолчанию). Данные не покидают
/// машину: /api/chat и /api/tags. Опциональная интеграция — выключена по умолчанию.
/// </summary>
public sealed class OllamaClient
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _host;

    public OllamaClient(string host)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "http://127.0.0.1:11434" : host.TrimEnd('/');
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(180);
        return client;
    }

    /// <summary>Жив ли сервер (быстрый таймаут — для кнопки «Проверить»).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await Http.GetAsync($"{_host}/api/tags", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Список установленных моделей из /api/tags.</summary>
    public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var json = await Http.GetStringAsync($"{_host}/api/tags", timeout.Token).ConfigureAwait(false);
        var tags = JsonSerializer.Deserialize<TagsResponse>(json, Json);
        return tags?.Models.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
               ?? [];
    }

    /// <summary>Запрос к модели: системный промпт + пользовательский текст. Не потоково.</summary>
    public async Task<string> ChatAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ChatRequest(model,
        [
            new ChatMessage("system", systemPrompt),
            new ChatMessage("user", userPrompt),
        ], Stream: false);

        var body = JsonSerializer.Serialize(request, Json);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"{_host}/api/chat", content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var chat = JsonSerializer.Deserialize<ChatResponse>(responseJson, Json);
        return StripThinking(chat?.Message?.Content ?? "");
    }

    /// <summary>Reasoning-модели (qwen3 и т.п.) могут вернуть think-блоки в контенте — убираем.</summary>
    public static string StripThinking(string content)
    {
        if (!content.Contains("<think>", StringComparison.Ordinal))
            return content;

        var start = 0;
        var cleaned = new System.Text.StringBuilder(content.Length);
        while (true)
        {
            var open = content.IndexOf("<think>", start, StringComparison.Ordinal);
            if (open < 0)
            {
                cleaned.Append(content[start..]);
                break;
            }
            cleaned.Append(content[start..open]);
            var close = content.IndexOf("</think>", open, StringComparison.Ordinal);
            if (close < 0)
                break; // незакрытый think — весь хвост отбрасываем
            start = close + "</think>".Length;
        }
        return cleaned.ToString().Trim();
    }

    private sealed record ChatRequest(string Model, ChatMessage[] Messages, bool Stream);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatResponse(ChatMessage? Message);

    private sealed record TagsResponse(TagModel[] Models);

    private sealed record TagModel(string Name);
}
