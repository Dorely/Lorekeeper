using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lorekeeper.Llm;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public sealed class CodexImageGenerationService(
    ILlmProviderService providers,
    ICodexAuthService codexAuth,
    IHttpClientFactory httpClientFactory,
    ILogger<CodexImageGenerationService> logger) : ICodexImageGenerationService
{
    private const string DefaultMainlineModel = "gpt-5.5";
    private const string ImageModel = "gpt-image-2";

    public async Task<CodexGeneratedImage> GenerateAsync(
        CodexImageGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveCodexProviderAsync(cancellationToken);
        var token = await codexAuth.GetValidTokenAsync(provider.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Connect OpenAI Codex in Settings before generating publish images.");

        var accountId = CodexProvider.ExtractAccountId(token);
        var preferredModel = DefaultMainlineModel;
        var fallbackModel = string.IsNullOrWhiteSpace(provider.ModelId) ? null : provider.ModelId.Trim();

        try
        {
            return await GenerateWithModelAsync(options, preferredModel, token, accountId, cancellationToken);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(fallbackModel)
            && !string.Equals(preferredModel, fallbackModel, StringComparison.OrdinalIgnoreCase)
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Codex image generation failed with {Model}; retrying with configured Codex model {FallbackModel}.", preferredModel, fallbackModel);
            return await GenerateWithModelAsync(options, fallbackModel, token, accountId, cancellationToken);
        }
    }

    private async Task<LlmProvider> ResolveCodexProviderAsync(CancellationToken cancellationToken)
    {
        var provider = await providers.GetByNameAsync(CodexProvider.Name, cancellationToken);
        if (provider is not null)
            return provider;

        provider = (await providers.GetAllAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.AuthType == AuthType.OAuth);
        return provider
            ?? throw new InvalidOperationException("No OpenAI Codex OAuth provider is configured. Connect OpenAI Codex in Settings first.");
    }

    private async Task<CodexGeneratedImage> GenerateWithModelAsync(
        CodexImageGenerationOptions options,
        string mainlineModel,
        string token,
        string accountId,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(options, mainlineModel);
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, CodexProvider.ResponsesEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("User-Agent", "Lorekeeper");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Codex image generation returned {(int)response.StatusCode}: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;
        string? responseId = null;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            JsonElement evt;
            try
            {
                evt = JsonSerializer.Deserialize<JsonElement>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            var type = evt.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            if (type == "response.created" && evt.TryGetProperty("response", out var createdResponse))
                responseId = ReadString(createdResponse, "id");

            if (type == "response.failed")
                throw new InvalidOperationException(ReadResponseError(evt) ?? "Codex image generation failed.");

            if (type != "response.output_item.done"
                || !evt.TryGetProperty("item", out var item)
                || ReadString(item, "type") != "image_generation_call")
            {
                continue;
            }

            var result = ReadString(item, "result");
            if (string.IsNullOrWhiteSpace(result))
                continue;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(result);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Codex returned an image_generation_call result that was not valid base64.", ex);
            }

            var outputFormat = ReadString(item, "output_format") ?? options.OutputFormat;
            var normalizedFormat = NormalizeOutputFormat(outputFormat);
            return new CodexGeneratedImage(
                bytes,
                $"image/{normalizedFormat}",
                normalizedFormat,
                mainlineModel,
                ImageModel,
                ReadString(item, "revised_prompt"),
                responseId,
                ReadString(item, "id"));
        }

        throw new InvalidOperationException("Codex completed without returning an image_generation_call result.");
    }

    private static Dictionary<string, object?> BuildPayload(CodexImageGenerationOptions options, string mainlineModel)
    {
        var outputFormat = NormalizeOutputFormat(options.OutputFormat);
        var content = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "input_text",
                ["text"] = options.Prompt,
            },
        };
        foreach (var reference in options.ReferenceImages)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{reference.ContentType};base64,{Convert.ToBase64String(reference.Data)}",
            });
        }

        var tool = new Dictionary<string, object?>
        {
            ["type"] = "image_generation",
            ["model"] = ImageModel,
            ["size"] = string.IsNullOrWhiteSpace(options.Size) ? "auto" : options.Size,
            ["quality"] = string.IsNullOrWhiteSpace(options.Quality) ? "auto" : options.Quality,
            ["output_format"] = outputFormat,
            ["background"] = "auto",
        };

        if (outputFormat == "jpeg" && options.OutputCompression is int compression)
            tool["output_compression"] = Math.Clamp(compression, 0, 100);

        return new Dictionary<string, object?>
        {
            ["model"] = mainlineModel,
            ["instructions"] = "Use the image_generation tool to create one publish-ready image from the user's prompt.",
            ["input"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                }
            },
            ["tools"] = new[] { tool },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "image_generation" },
            ["stream"] = true,
            ["store"] = false,
        };
    }

    private static string NormalizeOutputFormat(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "jpeg" => "jpeg",
            _ => "png",
        };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadResponseError(JsonElement evt)
    {
        if (evt.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message))
        {
            return message.GetString();
        }

        return ReadString(evt, "message");
    }

}
