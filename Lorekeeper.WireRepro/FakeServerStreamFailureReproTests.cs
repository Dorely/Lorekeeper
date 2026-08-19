using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Lorekeeper.WireRepro;

/// <summary>
/// Deterministic reproduction of the two observed failure signatures using a
/// local fake SSE server. These tests prove how the application's client
/// pipeline (OpenAI SDK 2.8.0 + M.E.AI) reacts to gateway misbehavior, without
/// any network access. They are the control group for the real-gateway
/// bisection tests.
/// </summary>
public sealed class FakeServerStreamFailureReproTests
{
    private static async Task RunPipelineAsync(HttpListener listener, Action<string>? log = null)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(GetListenerUrl(listener)),
            NetworkTimeout = TimeSpan.FromMinutes(2),
        };
        var client = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("test-key"),
            options);
        var chat = client.GetChatClient("fake-model").AsIChatClient();

        var messages = new List<AIChatMessage>
        {
            new(ChatRole.System, "You are a test assistant."),
            new(ChatRole.User, "hello"),
        };

        var updates = 0;
        try
        {
            await foreach (var update in chat.GetStreamingResponseAsync(messages, new ChatOptions()))
            {
                updates++;
                log?.Invoke($"update: {string.Join("|", update.Contents.Select(c => c.GetType().Name))}");
            }
            log?.Invoke($"STREAM-COMPLETED updates={updates}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"STREAM-THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static string GetListenerUrl(HttpListener listener) =>
        listener.Prefixes.First();


    [Fact]
    public async Task Unknown_finish_reason_throws_ArgumentOutOfRange_in_sdk_pipeline()
    {
        var listener = StartBehaviorListener("unknown-finish");
        try
        {
            var ex = await Record.ExceptionAsync(() => RunPipelineAsync(listener));
            Assert.NotNull(ex);
            // REPRO PROOF: the OpenAI SDK throws on any finish_reason outside its
            // known set; this is the exact exception observed with Kimi K3.
            Assert.True(
                ex is ArgumentOutOfRangeException
                || ex.Message.Contains("ChatFinishReason", StringComparison.Ordinal),
                $"Expected ArgumentOutOfRangeException mentioning ChatFinishReason, got {ex.GetType().Name}: {ex.Message}");
            TestLog.WriteLine($"[unknown-finish] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Premature_stream_end_throws_HttpIOException_in_sdk_pipeline()
    {
        var listener = StartBehaviorListener("premature-end");
        try
        {
            var ex = await Record.ExceptionAsync(() => RunPipelineAsync(listener));
            Assert.NotNull(ex);
            // REPRO PROOF: a gateway that drops the chunked response mid-stream
            // surfaces as HttpIOException 'The response ended prematurely.'
            var isPremature = ex.Message.Contains("prematurely", StringComparison.Ordinal)
                || ex.Message.Contains("ended", StringComparison.Ordinal);
            Assert.True(isPremature || ex is IOException || ex.InnerException is IOException,
                $"Expected premature-end IOException, got {ex.GetType().Name}: {ex.Message}");
            TestLog.WriteLine($"[premature-end] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            listener.Close();
        }
    }

    private static HttpListener StartBehaviorListener(string behavior)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{18766 + (behavior == "unknown-finish" ? 0 : 1)}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { return; }

                using var reader = new StreamReader(context.Request.InputStream);
                _ = await reader.ReadToEndAsync();

                var response = context.Response;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;

                await using var output = response.OutputStream;
                await using var writer = new StreamWriter(output, new UTF8Encoding(false));

                if (behavior == "unknown-finish")
                {
                    await WriteChunk(writer, "partial text", null);
                    await WriteChunk(writer, null, "unknown_reason_value");
                    await writer.WriteAsync("data: [DONE]\n\n");
                    await writer.FlushAsync();
                }
                else
                {
                    await WriteChunk(writer, "thinking about tools...", null);
                    await writer.FlushAsync();
                    // Abort (not Close) so the connection is reset without the
                    // terminating chunk, matching the gateway drop observed live.
                    response.Abort();
                }
            }
        });
        return listener;
    }

    private static async Task WriteChunk(StreamWriter writer, string? content, string? finishReason)
    {
        var chunk = new JsonObject
        {
            ["id"] = "chatcmpl-1",
            ["object"] = "chat.completion.chunk",
            ["model"] = "fake-model",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = content is null ? new JsonObject() : new JsonObject { ["content"] = content },
                ["finish_reason"] = finishReason,
            }),
        };
        await writer.WriteAsync("data: " + chunk.ToJsonString() + "\n\n");
        await writer.FlushAsync();
    }
}

public static class TestLog
{
    public static void WriteLine(string value) =>
        Console.WriteLine($"[LK-REPRO] {value}");
}
