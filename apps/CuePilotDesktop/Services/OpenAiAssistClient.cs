using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CuePilotDesktop.Services;

/// <summary>
/// Client for OpenAI-compatible chat completion APIs (Polza.ai, OpenRouter, OpenAI, local servers).
/// </summary>
public class OpenAiAssistClient
{
    private const int MaxErrorBodyLength = 600;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, bool> _reasoningUnsupported = new(StringComparer.OrdinalIgnoreCase);

    public OpenAiAssistClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<LlmResponse> CompleteAsync(
        LlmEndpoint endpoint,
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        string url = BuildEndpoint(endpoint.BaseUrl, endpoint.EndpointPath);
        object userContent = BuildUserContent(request.UserPrompt, request.Images, out int attachedImages);
        string reasoningKey = url + "|" + request.Model;
        bool sendReasoning = !string.IsNullOrWhiteSpace(request.ReasoningEffort) &&
            endpoint.SupportsReasoningObject &&
            !_reasoningUnsupported.ContainsKey(reasoningKey);

        for (int attempt = 1; ; attempt++)
        {
            string json = BuildPayload(request, userContent, sendReasoning);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            AddApiKeyHeader(httpRequest, endpoint.ApiKey, endpoint.ApiKeyHeaderName, endpoint.ApiKeyPrefix);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }
            catch (HttpRequestException) when (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                continue;
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ParseResponse(body, attachedImages);
                }

                // Some models/providers reject the reasoning block: remember it and retry without it.
                if (sendReasoning && IsClientError(response.StatusCode))
                {
                    _reasoningUnsupported[reasoningKey] = true;
                    sendReasoning = false;
                    continue;
                }

                if (attempt == 1 && IsTransient(response.StatusCode))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                    continue;
                }

                throw new InvalidOperationException(
                    $"LLM request failed ({(int)response.StatusCode}): {Truncate(body, MaxErrorBodyLength)}");
            }
        }
    }

    private static string BuildPayload(LlmRequest request, object userContent, bool sendReasoning)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = userContent }
            }
        };

        if (sendReasoning)
        {
            payload["reasoning"] = new { effort = request.ReasoningEffort, exclude = true };
        }
        else
        {
            // Reasoning models reject custom temperature, so it is only sent without reasoning.
            payload["temperature"] = request.Temperature;
        }

        if (request.MaxTokens is > 0)
        {
            payload["max_tokens"] = request.MaxTokens;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static LlmResponse ParseResponse(string body, int attachedImages)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        return new LlmResponse
        {
            Text = ParseContent(root),
            Usage = ParseUsage(root),
            AttachedImages = attachedImages
        };
    }

    private static string ParseContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        JsonElement first = choices[0];
        if (!first.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        // Some OpenAI-compatible providers can return content blocks.
        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out JsonElement textNode) &&
                    textNode.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textNode.GetString());
                }
            }

            return builder.ToString();
        }

        return string.Empty;
    }

    private static object BuildUserContent(string userPrompt, IReadOnlyList<AssistImageInput>? imageInputs, out int attachedImages)
    {
        attachedImages = 0;
        if (imageInputs == null || imageInputs.Count == 0)
        {
            return userPrompt;
        }

        var content = new List<object>
        {
            new
            {
                type = "text",
                text = userPrompt
            }
        };

        foreach (AssistImageInput image in imageInputs)
        {
            if (string.IsNullOrWhiteSpace(image.FilePath) || !File.Exists(image.FilePath))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(image.FilePath);
            string mime = image.FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            string base64 = Convert.ToBase64String(bytes);
            content.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:{mime};base64,{base64}",
                    detail = string.IsNullOrWhiteSpace(image.Detail) ? "low" : image.Detail
                }
            });
            attachedImages++;
        }

        return content;
    }

    private static string BuildEndpoint(string baseUrl, string endpointPath)
    {
        string normalizedBase = baseUrl.TrimEnd('/');
        string path = string.IsNullOrWhiteSpace(endpointPath) ? "/chat/completions" : endpointPath.Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        return normalizedBase + path;
    }

    internal static void AddApiKeyHeader(
        HttpRequestMessage request,
        string apiKey,
        string apiKeyHeaderName,
        string apiKeyPrefix)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        string headerName = string.IsNullOrWhiteSpace(apiKeyHeaderName)
            ? "Authorization"
            : apiKeyHeaderName.Trim();
        string prefix = string.IsNullOrWhiteSpace(apiKeyPrefix)
            ? "Bearer"
            : apiKeyPrefix.Trim();
        bool noPrefix = string.Equals(prefix, "none", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (noPrefix)
            {
                request.Headers.TryAddWithoutValidation(headerName, apiKey);
                return;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(prefix, apiKey);
            return;
        }

        string value = noPrefix || string.IsNullOrWhiteSpace(prefix) ? apiKey : $"{prefix} {apiKey}";
        request.Headers.TryAddWithoutValidation(headerName, value);
    }

    private static AssistTokenUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int prompt = usage.TryGetProperty("prompt_tokens", out JsonElement promptNode) && promptNode.TryGetInt32(out int p) ? p : 0;
        int completion = usage.TryGetProperty("completion_tokens", out JsonElement completionNode) && completionNode.TryGetInt32(out int c) ? c : 0;
        int total = usage.TryGetProperty("total_tokens", out JsonElement totalNode) && totalNode.TryGetInt32(out int t) ? t : (prompt + completion);
        double? cost = TryGetDouble(usage, "cost_rub");
        return new AssistTokenUsage
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
            CostRub = cost
        };
    }

    private static double? TryGetDouble(JsonElement node, string propertyName)
    {
        return node.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double result)
            ? result
            : null;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
    }

    private static bool IsClientError(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}

public sealed class LlmEndpoint
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string EndpointPath { get; init; } = "/chat/completions";
    public string ApiKeyHeaderName { get; init; } = "Authorization";
    public string ApiKeyPrefix { get; init; } = "Bearer";

    /// <summary>Polza.ai and OpenRouter accept the unified "reasoning": { effort } block.</summary>
    public bool SupportsReasoningObject =>
        BaseUrl.Contains("polza.ai", StringComparison.OrdinalIgnoreCase) ||
        BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
}

public sealed class LlmRequest
{
    public string Model { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPrompt { get; init; } = string.Empty;
    public IReadOnlyList<AssistImageInput>? Images { get; init; }
    public string? ReasoningEffort { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int? MaxTokens { get; init; }
}

public sealed class LlmResponse
{
    public string Text { get; init; } = string.Empty;
    public AssistTokenUsage? Usage { get; init; }
    public int AttachedImages { get; init; }
}

public sealed class AssistImageInput
{
    public string FilePath { get; set; } = string.Empty;
    public string Detail { get; set; } = "low";
}

public sealed class AssistTokenUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public double? CostRub { get; set; }
}
