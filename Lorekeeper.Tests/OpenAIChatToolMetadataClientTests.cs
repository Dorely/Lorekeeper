using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Llm;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Lorekeeper.Tests;

public sealed class OpenAIChatToolMetadataClientTests
{
    [Fact]
    public async Task StreamingToolCall_PreservesExtraContentOnFollowUpRequest()
    {
        var rawUpdate = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(
            BinaryData.FromString(
                """
                {
                  "id": "response-1",
                  "choices": [
                    {
                      "delta": {
                        "role": "assistant",
                        "tool_calls": [
                          {
                            "index": 0,
                            "id": "call-1",
                            "type": "function",
                            "function": {
                              "name": "create_act",
                              "arguments": "{\"title\":\"Act I\"}"
                            },
                            "extra_content": {
                              "google": {
                                "thought_signature": "signed-by-gemini"
                              }
                            }
                          }
                        ]
                      },
                      "finish_reason": null,
                      "index": 0
                    }
                  ],
                  "created": 1785470400,
                  "model": "gemini-3-pro",
                  "object": "chat.completion.chunk"
                }
                """),
            ModelReaderWriterOptions.Json)
            ?? throw new InvalidOperationException("Unable to create the streaming fixture.");
        var functionCall = new FunctionCallContent(
            "call-1",
            "create_act",
            new Dictionary<string, object?> { ["title"] = "Act I" });
        var inner = new RecordingChatClient(
        [
            new ChatResponseUpdate { RawRepresentation = rawUpdate },
            new ChatResponseUpdate { Contents = [functionCall] },
        ]);
        using var client = new OpenAIChatToolMetadataClient(inner);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AIChatMessage(ChatRole.User, "Create an act.")]))
        {
        }

        Assert.NotNull(functionCall.AdditionalProperties);
        Assert.True(functionCall.AdditionalProperties.TryGetValue(
            OpenAIChatToolMetadataClient.ExtraContentPropertyName,
            out var storedExtraContent));
        Assert.Contains("signed-by-gemini", Assert.IsType<string>(storedExtraContent));

        var assistantContents = ChatTurnEngine.BuildAssistantContents(
            string.Empty,
            [new ChatPendingToolCall(functionCall, "call-1", "create_act", """{"title":"Act I"}""", 0)]);
        await client.GetResponseAsync(
        [
            new AIChatMessage(ChatRole.User, "Create an act."),
            new AIChatMessage(ChatRole.Assistant, assistantContents),
            new AIChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent("call-1", """{"success":true}""")]),
        ]);

        var preparedAssistant = Assert.Single(inner.LastMessages!, message => message.Role == ChatRole.Assistant);
        var rawAssistant = Assert.IsAssignableFrom<OpenAI.Chat.ChatMessage>(preparedAssistant.RawRepresentation);
        var assistantJson = ModelReaderWriter.Write(rawAssistant, ModelReaderWriterOptions.Json);
        using var document = JsonDocument.Parse(assistantJson);
        var rawToolCall = document.RootElement.GetProperty("tool_calls")[0];
        Assert.Equal(
            "signed-by-gemini",
            rawToolCall
                .GetProperty("extra_content")
                .GetProperty("google")
                .GetProperty("thought_signature")
                .GetString());
    }

    [Fact]
    public async Task StreamingParallelToolCalls_CorrelatesSparseMetadataAcrossSeparateDeltas()
    {
        var idUpdate = ReadStreamingUpdate(
            """
            {
              "id": "response-1",
              "choices": [
                {
                  "delta": {
                    "role": "assistant",
                    "tool_calls": [
                      {
                        "index": 0,
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "first_tool", "arguments": "{}" }
                      },
                      {
                        "index": 1,
                        "id": "call-2",
                        "type": "function",
                        "function": { "name": "second_tool", "arguments": "{}" }
                      }
                    ]
                  },
                  "finish_reason": null,
                  "index": 0
                }
              ],
              "created": 1785470400,
              "model": "gemini-3-pro",
              "object": "chat.completion.chunk"
            }
            """);
        var metadataUpdate = ReadStreamingUpdate(
            """
            {
              "id": "response-1",
              "choices": [
                {
                  "delta": {
                    "tool_calls": [
                      {
                        "index": 1,
                        "extra_content": {
                          "google": {
                            "thought_signature": "second-call-only"
                          }
                        }
                      }
                    ]
                  },
                  "finish_reason": null,
                  "index": 0
                }
              ],
              "created": 1785470400,
              "model": "gemini-3-pro",
              "object": "chat.completion.chunk"
            }
            """);
        var firstCall = new FunctionCallContent("call-1", "first_tool", null);
        var secondCall = new FunctionCallContent("call-2", "second_tool", null);
        var inner = new RecordingChatClient(
        [
            new ChatResponseUpdate { RawRepresentation = idUpdate },
            new ChatResponseUpdate { RawRepresentation = metadataUpdate },
            new ChatResponseUpdate { Contents = [firstCall, secondCall] },
        ]);
        using var client = new OpenAIChatToolMetadataClient(inner);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AIChatMessage(ChatRole.User, "Use both tools.")]))
        {
        }

        Assert.False(firstCall.AdditionalProperties?.ContainsKey(
            OpenAIChatToolMetadataClient.ExtraContentPropertyName) ?? false);
        Assert.NotNull(secondCall.AdditionalProperties);
        Assert.True(secondCall.AdditionalProperties.TryGetValue(
            OpenAIChatToolMetadataClient.ExtraContentPropertyName,
            out var storedExtraContent));
        Assert.Contains("second-call-only", Assert.IsType<string>(storedExtraContent));
    }

    [Fact]
    public async Task BufferedToolCall_PreservesExtraContent()
    {
        var rawToolCall = ModelReaderWriter.Read<ChatToolCall>(
            BinaryData.FromString(
                """
                {
                  "id": "call-1",
                  "type": "function",
                  "function": {
                    "name": "create_act",
                    "arguments": "{\"title\":\"Act I\"}"
                  },
                  "extra_content": {
                    "google": {
                      "thought_signature": "buffered-signature"
                    }
                  }
                }
                """),
            ModelReaderWriterOptions.Json)
            ?? throw new InvalidOperationException("Unable to create the buffered fixture.");
        var functionCall = new FunctionCallContent(
            "call-1",
            "create_act",
            new Dictionary<string, object?> { ["title"] = "Act I" })
        {
            RawRepresentation = rawToolCall,
        };
        var inner = new RecordingChatClient(
            [],
            new ChatResponse([new AIChatMessage(ChatRole.Assistant, [functionCall])]));
        using var client = new OpenAIChatToolMetadataClient(inner);

        await client.GetResponseAsync([new AIChatMessage(ChatRole.User, "Create an act.")]);

        Assert.NotNull(functionCall.AdditionalProperties);
        Assert.True(functionCall.AdditionalProperties.TryGetValue(
            OpenAIChatToolMetadataClient.ExtraContentPropertyName,
            out var storedExtraContent));
        Assert.Contains("buffered-signature", Assert.IsType<string>(storedExtraContent));
    }

    [Fact]
    public void BuildAssistantContents_RetainsOriginalFunctionCallContent()
    {
        var functionCall = new FunctionCallContent("call-1", "tool", null)
        {
            AdditionalProperties = new()
            {
                [OpenAIChatToolMetadataClient.ExtraContentPropertyName] = """{"provider":"metadata"}""",
            },
        };

        var contents = ChatTurnEngine.BuildAssistantContents(
            "beforeafter",
            [new ChatPendingToolCall(functionCall, "call-1", "tool", "{}", 6)]);

        Assert.Collection(
            contents,
            content => Assert.Equal("before", Assert.IsType<TextContent>(content).Text),
            content => Assert.Same(functionCall, content),
            content => Assert.Equal("after", Assert.IsType<TextContent>(content).Text));
    }

    private static StreamingChatCompletionUpdate ReadStreamingUpdate(string json) =>
        ModelReaderWriter.Read<StreamingChatCompletionUpdate>(
            BinaryData.FromString(json),
            ModelReaderWriterOptions.Json)
        ?? throw new InvalidOperationException("Unable to create the streaming fixture.");

    private sealed class RecordingChatClient(
        IReadOnlyList<ChatResponseUpdate> updates,
        ChatResponse? bufferedResponse = null) : IChatClient
    {
        public IReadOnlyList<AIChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(bufferedResponse ?? new ChatResponse(
                [new AIChatMessage(ChatRole.Assistant, "done")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
