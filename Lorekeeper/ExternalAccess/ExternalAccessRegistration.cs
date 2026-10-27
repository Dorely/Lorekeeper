using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Lorekeeper.ExternalAccess;

public static class ExternalAccessRegistration
{
    public static IServiceCollection AddExternalAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ExternalClientSetupService.EnsureSafeDirectory(ExternalTransferCommand.KeysPath);
        var protection = services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(ExternalTransferCommand.KeysPath))
            .SetApplicationName("Lorekeeper.ExternalAccess");
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        services.AddHttpContextAccessor();
        services.AddOptions<ExternalAccessOptions>().Bind(configuration.GetSection("ExternalAccess"))
            .Validate(value => value.MaximumRequestBytes is >= 16384 and <= 16777216
                && value.MaximumResultBytes is >= 262144 and <= 4194304
                && value.MaximumTransferBytes is >= 1024 and <= 268435456
                && value.MaximumDownloadBytes is >= 1048576 and <= 536870912, "External Access byte limits are outside supported bounds.")
            .ValidateOnStart();
        services.AddScoped<McpClientGrantService>();
        services.AddScoped<ExternalClientSetupService>();
        services.AddScoped<ExternalAccessCommands>();
        services.AddScoped<ExternalAccessDispatcher>();
        services.AddScoped<ExternalTransferStore>();
        services.AddScoped<ExternalResources>();
        services.AddSingleton<ExternalWorkspaceUiEvents>();
        services.AddHostedService<ExternalProjectionRepairWorker>();
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "Lorekeeper", Version = "1" };
            options.ServerInstructions = "Lorekeeper is authoritative creative state. Discover project IDs, then open_workspace for a surface before using its tools. Read the returned context pages. Reconnect or compaction: list_unfinished_work and get_workspace_context. Read exact current revisions before writes, reuse mutationId for uncertain retries, verify manuscript readback and visual results, and finish_work only when the whole task is complete. Transfer acknowledgement advances only a captured handoff baseline.";
        })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler(async (request, ct) => await request.Services!.GetRequiredService<ExternalAccessDispatcher>().ListAsync(ct))
            .WithCallToolHandler(async (request, ct) => await request.Services!.GetRequiredService<ExternalAccessDispatcher>().CallAsync(
                request.Params!.Name, JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>(), ExternalToolCatalog.Json), ct))
            .WithListResourcesHandler(async (request, ct) => await request.Services!.GetRequiredService<ExternalResources>().ListAsync(ct))
            .WithListResourceTemplatesHandler(async (request, ct) => await request.Services!.GetRequiredService<ExternalResources>().TemplatesAsync(ct))
            .WithReadResourceHandler(async (request, ct) => await request.Services!.GetRequiredService<ExternalResources>().ReadAsync(request.Params!.Uri, ct));
        return services;
    }

    public static void MapExternalAccess(this WebApplication app)
    {
        app.MapMcp("/mcp");
        app.MapExternalTransfers();
    }
}
