using Xunit;
using Xunit.Abstractions;

namespace Lorekeeper.WireRepro;

/// <summary>
/// Opt-in bisection of the real Cline gateway using the exact provider rows
/// from the development database. Answers: which component of the large initial
/// request (system-prompt size, tool count, reasoning effort, output budget)
/// triggers the observed failures?
///
/// Gated: skips silently unless LK_TEST_CLINE_KEY is set (the key from the dev
/// DB parent provider row). Never part of the normal test workflow.
/// </summary>
public sealed class ClineGatewayBisectionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan GatewayTimeout = TimeSpan.FromMinutes(11);

    private static string? ClineKey =>
        Environment.GetEnvironmentVariable("LK_TEST_CLINE_KEY");

    private static ProviderRow RequireQwenRow() =>
        DevDb.LoadProvider(p => p.ModelId.Contains("qwen", StringComparison.OrdinalIgnoreCase));

    private static ProviderRow RequireKimiRow() =>
        DevDb.LoadProvider(p => p.ModelId.Contains("kimi", StringComparison.OrdinalIgnoreCase));

    private static string RealUserPrompt =>
        "lets go ahead and write the first draft for chapter 2 now. The time skip should establish briefly the events between the chapters.";

    private async Task<SseResult> RunScenarioAsync(
        ProviderRow provider,
        string fullSystemPrompt,
        double systemFraction,
        int toolCount,
        string? effort,
        int? maxTokens,
        string scenarioName,
        bool realisticToolSchemas = false,
        string maxTokensField = "max_completion_tokens")
    {
        var systemPrompt = systemFraction >= 1.0
            ? fullSystemPrompt
            : systemFraction <= 0
                ? null
                : "You are Lorekeeper's Editor. Draft the requested chapter continuation in the established voice.\n\n" + fullSystemPrompt[(int)(fullSystemPrompt.Length * (1 - systemFraction))..];

        var request = new SseRequest(
            provider.EndpointUrl,
            provider.ApiKey!,
            provider.ModelId,
            systemPrompt,
            RealUserPrompt,
            toolCount,
            effort,
            maxTokens,
            MaxTokensField: maxTokensField,
            RealisticToolSchemas: realisticToolSchemas)
        {
            Scenario = scenarioName,
        };
        var result = await RawSse.SendAsync(request);
        output.WriteLine($"SCENARIO {result.Scenario}: status={result.StatusCode} chunks={result.Chunks} textChars={result.TextChars} reasoningChars={result.ReasoningChars} toolDeltas={result.ToolCallDeltas} finish={result.FinishReason ?? "(none)"} firstByte={result.FirstByteMs}ms lastByte={result.LastByteMs}ms total={result.TotalMs}ms transportError={result.TransportError ?? "(none)"}");
        if (result.TextSample is { Length: > 0 })
            output.WriteLine($"  text: {result.TextSample.Replace("\n", " ")[..Math.Min(200, result.TextSample.Length)]}");
        foreach (var evt in result.Events.Take(5))
            output.WriteLine($"  event: {evt}");
        return result;
    }

    [Fact]
    public async Task B00_baseline_tiny_request_no_tools_small_system()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 0.05, 0, null, null, "B00-tiny");
        Assert.False(result.HasTransportError, $"transport error: {result.TransportError}");
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task B01_full_system_40_tools_no_budget()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, null, "B01-full");
        output.WriteLine($"B01 outcome: finish={result.FinishReason} transportError={result.TransportError}");
    }

    [Fact]
    public async Task B02_full_system_no_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 0, null, null, "B02-fullsys-notools");
    }

    [Fact]
    public async Task B03_small_system_40_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 0.05, 40, null, null, "B03-smallsys-tools");
    }

    [Fact]
    public async Task B04_half_system_40_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 0.5, 40, null, null, "B04-halfsys-tools");
    }

    [Fact]
    public async Task B05_full_system_40_tools_with_budget_16384()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, 16384, "B05-full-budget");
    }

    [Fact]
    public async Task B06_full_system_40_tools_effort_high()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, "high", null, "B06-full-effort");
    }

    [Fact]
    public async Task B07_full_system_1_tool()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 1, null, null, "B07-full-1tool");
    }

    [Fact]
    public async Task B08_full_system_10_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 10, null, null, "B08-full-10tools");
    }

    [Fact]
    public async Task B09_full_system_20_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 20, null, null, "B09-full-20tools");
    }

    [Fact]
    public async Task B10_full_system_40_realistic_schema_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, null, "B10-full-40realtools", realisticToolSchemas: true);
    }

    [Fact]
    public async Task B11_small_system_40_realistic_schema_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 0.05, 40, null, null, "B11-small-40realtools", realisticToolSchemas: true);
    }

    [Fact]
    public async Task B12_full_system_40_tools_budget_4096()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, 4096, "B12-full-budget4096");
    }

    [Fact]
    public async Task B13_full_system_no_tools_budget_4096()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 0, null, 4096, "B13-fullsys-notools-budget4096");
        output.WriteLine($"B13 outcome: finish={result.FinishReason} transportError={result.TransportError} totalMs={result.TotalMs}");
    }

    [Fact]
    public async Task B14_half_system_no_tools()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 0.5, 0, null, null, "B14-halfsys-notools");
        output.WriteLine($"B14 outcome: finish={result.FinishReason} transportError={result.TransportError} totalMs={result.TotalMs}");
    }

    [Fact]
    public async Task K01_kimi_full_system_40_tools_no_budget()
    {
        if (ClineKey is null) return;
        var provider = RequireKimiRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, null, "K01-kimi-full");
        output.WriteLine($"K01 outcome: finish={result.FinishReason} transportError={result.TransportError} totalMs={result.TotalMs}");
    }

    [Fact]
    public async Task K02_kimi_full_system_40_tools_budget_4096()
    {
        if (ClineKey is null) return;
        var provider = RequireKimiRow();
        var result = await RunScenarioAsync(provider, DevDb.LoadLatestEditorSystemPrompt(), 1.0, 40, null, 4096, "K02-kimi-full-budget4096");
        output.WriteLine($"K02 outcome: finish={result.FinishReason} transportError={result.TransportError} totalMs={result.TotalMs}");
    }

    [Fact]
    public async Task B15_full_system_40_tools_budget_4096_legacy_field()
    {
        if (ClineKey is null) return;
        var provider = RequireQwenRow();
        var result = await RunScenarioAsync(
            provider,
            DevDb.LoadLatestEditorSystemPrompt(),
            1.0,
            40,
            null,
            4096,
            "B15-full-budget4096-legacyfield",
            maxTokensField: "max_tokens");
        output.WriteLine($"B15 outcome: finish={result.FinishReason} transportError={result.TransportError} totalMs={result.TotalMs} reasoningChars={result.ReasoningChars}");
    }
}
