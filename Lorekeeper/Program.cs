using System.Reflection;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Lorekeeper.Authorization;
using Lorekeeper.Printing;
using Lorekeeper.Auth;
using Lorekeeper.Chapters;
using Lorekeeper.ChatTurns;
using Lorekeeper.Components;
using Lorekeeper.Context;
using Lorekeeper.Desktop;
using Lorekeeper.Diagnostics;
using Lorekeeper.EditorChat;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Graph;
using Lorekeeper.Images;
using Lorekeeper.ImagesChat;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Lorekeeper.Publish;
using Lorekeeper.Research;
using Lorekeeper.Search;
using Lorekeeper.Startup;
using Lorekeeper.Tokens;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.GitHub;
using Lorekeeper.VersionHistory.Restore;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Lorekeeper.VersionHistory.Sync;
using Lorekeeper.Writing;

var builder = WebApplication.CreateBuilder(args);
var desktopUpdates = new DesktopUpdateService();
IDesktopReleaseUpdateChecker? desktopReleaseUpdateChecker = null;
var isElectronMode = IsElectronMode(args);
var distributionChannelPolicy = DistributionChannelPolicy.Resolve(
    builder.Environment.IsDevelopment(),
    GetDistributionChannelBuildMetadata());
if (!string.IsNullOrWhiteSpace(distributionChannelPolicy.ValidationError))
    Console.Error.WriteLine($"Desktop updates are disabled: {distributionChannelPolicy.ValidationError}");
var desktopUrl = isElectronMode ? GetDesktopUrl(builder.Configuration) : null;
var enableDesktopDevTools = builder.Environment.IsDevelopment();
var usePerUserDataDirectory = isElectronMode
    && !builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Desktop:UsePerUserDataDirectory", true);
var databaseConnectionString = SqliteConnectionSettings.BuildConnectionString(
    builder.Configuration,
    usePerUserDataDirectory,
    builder.Environment.ContentRootPath);
builder.Configuration["ConnectionStrings:DefaultConnection"] = databaseConnectionString;
var maxInteractiveServerMessageSize = builder.Configuration.GetValue<long?>("Blazor:MaximumReceiveMessageSizeBytes")
    ?? 64L * 1024 * 1024;
var minimumStartupSplashMilliseconds = builder.Configuration.GetValue("Startup:MinimumSplashMilliseconds", 1200);
if (minimumStartupSplashMilliseconds is < 0 or > 10_000)
    throw new InvalidOperationException("Startup:MinimumSplashMilliseconds must be between zero and 10000.");

// Add services to the container.
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddProvider(new DevFileLoggerProvider());
}
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = maxInteractiveServerMessageSize);
builder.Services.AddSingleton<PerformanceTraceWriter>();
builder.Services.AddSingleton<IPerformanceTraceWriter>(services =>
    services.GetRequiredService<PerformanceTraceWriter>());
builder.Services.AddHostedService(services => services.GetRequiredService<PerformanceTraceWriter>());
builder.Services.AddSingleton<ApplicationStartupState>();
builder.Services.AddSingleton<IApplicationStartupState>(services =>
    services.GetRequiredService<ApplicationStartupState>());
builder.Services.AddSingleton(new ApplicationStartupOptions(
    TimeSpan.FromMilliseconds(minimumStartupSplashMilliseconds)));
builder.Services.AddHostedService<ApplicationStartupWorker>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OpenAiAccountOperationCoordinator>();
builder.Services.AddSingleton<OpenAiAuthorizationFlowRegistry>();
builder.Services.AddSingleton<IOpenAiCallbackOriginValidator, OpenAiCallbackOriginValidator>();
if (isElectronMode)
    builder.Services.AddSingleton<IExternalAuthorizationLauncher, ElectronExternalAuthorizationLauncher>();
else
    builder.Services.AddSingleton<IExternalAuthorizationLauncher, BrowserExternalAuthorizationLauncher>();
if (isElectronMode && distributionChannelPolicy.UsesGitHubReleaseChecks)
{
    builder.Services.AddHttpClient<IDesktopReleaseUpdateChecker, GitHubDesktopReleaseUpdateChecker>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Lorekeeper/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
    });
}

if (isElectronMode)
{
    builder.Services.AddSingleton<IDesktopUpdateService>(desktopUpdates);
    builder.Services.AddElectron();
    builder.UseElectron(args, () => ElectronAppReady(
        desktopUrl!,
        enableDesktopDevTools,
        distributionChannelPolicy,
        desktopUpdates,
        () => desktopReleaseUpdateChecker));
    builder.WebHost.UseUrls(desktopUrl!);
}
else
{
    builder.Services.AddSingleton<IDesktopUpdateService>(desktopUpdates);
}

// Persistence
builder.Services.AddLorekeeperPersistence(builder.Configuration);
builder.Services.AddSingleton<IProjectMutationCoordinator>(
    _ => new ProjectMutationCoordinator(databaseConnectionString));

// Version history
builder.Services.AddSingleton(new GitRepositoryStoreOptions
{
    HistoryRoot = builder.Configuration["VersionHistory:HistoryRoot"],
    IsPackaged = usePerUserDataDirectory,
    DevelopmentDataRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..")),
});
builder.Services.AddSingleton<IGitRepositoryStore, GitRepositoryStore>();
builder.Services.AddSingleton<IVersionHistorySnapshotReader, VersionHistorySnapshotReader>();
builder.Services.AddSingleton<IVersionHistorySnapshotComparer, VersionHistorySnapshotComparer>();
builder.Services.AddSingleton<ProjectVersionHistoryCache>();
builder.Services.AddScoped<IVersionHistorySnapshotWriter, VersionHistorySnapshotWriter>();
builder.Services.AddScoped<ProjectVersionHistoryService>();
builder.Services.AddScoped<IProjectVersionHistoryService>(services =>
    services.GetRequiredService<ProjectVersionHistoryService>());
builder.Services.AddSingleton<ProjectVersionHistoryUiEvents>();
builder.Services.AddSingleton<IProjectVersionAutoPushQueue, ProjectVersionAutoPushQueue>();
builder.Services.AddScoped<ProjectVersionAutoPushService>();
builder.Services.AddHostedService<ProjectVersionAutoPushWorker>();
builder.Services.AddScoped<IProjectVersionHistoryReconciliationService, ProjectVersionHistoryReconciliationService>();
builder.Services.AddScoped<IAssistantVersionCheckpointService, AssistantVersionCheckpointService>();
builder.Services.AddScoped<ProjectVersionRestoreService>();
builder.Services.AddScoped<IProjectVersionRestoreService>(services =>
    services.GetRequiredService<ProjectVersionRestoreService>());
builder.Services.Configure<GitHubConnectionOptions>(
    builder.Configuration.GetSection(GitHubConnectionOptions.SectionName));
builder.Services.AddHttpClient<IGitHubConnectionService, GitHubConnectionService>(client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<ProjectVersionSyncService>();
builder.Services.AddScoped<IProjectVersionSyncService>(services =>
    services.GetRequiredService<ProjectVersionSyncService>());
builder.Services.AddScoped<IProjectVersionCloneImportService, ProjectVersionCloneImportService>();
builder.Services.AddScoped<IProjectVersionRemoteUpdateService, ProjectVersionRemoteUpdateService>();

// Knowledge
builder.Services.AddScoped<SqliteVecVectorStore>();
builder.Services.AddScoped<IVectorStore>(sp => sp.GetRequiredService<SqliteVecVectorStore>());
builder.Services.AddScoped<IVectorStoreMaintenance>(sp => sp.GetRequiredService<SqliteVecVectorStore>());
builder.Services.AddScoped<IGraphStore, RelationalGraphStore>();
builder.Services.AddScoped<IProjectGraphService, ProjectGraphService>();
builder.Services.AddSingleton<ITextChunker, OverlappingTextChunker>();

// LLM
builder.Services.Configure<EmbeddingRebuildOptions>(builder.Configuration.GetSection(EmbeddingRebuildOptions.SectionName));
builder.Services.AddSingleton<IEmbeddingRebuildQueue, EmbeddingRebuildQueue>();
builder.Services.AddScoped<IEmbeddingClient, EmbeddingClient>();
builder.Services.AddScoped<IEmbeddingConfigurationService, EmbeddingConfigurationService>();
builder.Services.AddScoped<IEmbeddingService, ProviderEmbeddingService>();
builder.Services.AddScoped<EmbeddingRebuildService>();
builder.Services.AddScoped<ILlmProviderService, LlmProviderService>();
builder.Services.AddScoped<IOpenAiAccountService, OpenAiAccountService>();
builder.Services.AddScoped<ISystemPromptComposer, SystemPromptComposer>();
builder.Services.AddScoped<OpenAiAccountTokenService>();
builder.Services.AddScoped<IOpenAiAccountTokenService>(services =>
    services.GetRequiredService<OpenAiAccountTokenService>());
builder.Services.AddScoped<IOpenAiAccountAuthorizationService, OpenAiAccountAuthorizationService>();
builder.Services.AddHostedService<EmbeddingRebuildWorker>();

// Search providers
builder.Services.AddScoped<IWebSearchClient, SerpApiWebSearchClient>();
builder.Services.AddScoped<IWebSearchClient, BraveWebSearchClient>();
builder.Services.AddScoped<IWebSearchProviderFactory, WebSearchProviderFactory>();
builder.Services.AddScoped<ISearchProviderService, SearchProviderService>();
builder.Services.AddScoped<IProjectSearchIndex, SqliteFtsProjectSearchIndex>();
builder.Services.AddScoped<IProjectSearchService, ProjectSearchService>();
builder.Services.AddScoped<IGraphAutoLinkService, GraphAutoLinkService>();

// Token counting + advisory chat limits
builder.Services.Configure<TokenCountingOptions>(builder.Configuration.GetSection(TokenCountingOptions.SectionName));
builder.Services.AddOptions<ChatTokenLimitOptions>()
    .Bind(builder.Configuration.GetSection(ChatTokenLimitOptions.SectionName))
    .Validate(options => options.DefaultMaxInputTokens > 0, "ChatTokens:DefaultMaxInputTokens must be greater than zero.")
    .Validate(
        options => options.ModelMaxInputTokens.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0),
        "ChatTokens:ModelMaxInputTokens keys must be non-empty and values must be greater than zero.")
    .Validate(
        options => options.ModelMaxInputTokens.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == options.ModelMaxInputTokens.Count,
        "ChatTokens:ModelMaxInputTokens cannot contain model IDs that differ only by case.")
    .ValidateOnStart();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<EditorChatOptions>(builder.Configuration.GetSection(EditorChatOptions.SectionName));
builder.Services.AddSingleton<TiktokenTokenCounter>();
builder.Services.AddSingleton<CharEstimateTokenCounter>();
builder.Services.AddSingleton<ITokenCounter, CompositeTokenCounter>();
builder.Services.AddSingleton<ChatTokenLimitResolver>();
builder.Services.AddSingleton<IChatContextCompactionService>(services =>
    new ChatContextCompactionService(
        services.GetRequiredService<ITokenCounter>(),
        services.GetRequiredService<ChatTokenLimitResolver>()));
builder.Services.AddSingleton<ChatTurnEngine>();
builder.Services.AddSingleton<ChatTurnRuntime>();
builder.Services.AddScoped<IChatImageAttachmentService, ChatImageAttachmentService>();

// Projects
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IProjectReferenceService, ProjectReferenceService>();
builder.Services.AddScoped<IBookBriefService, BookBriefService>();
builder.Services.AddScoped<IChatClientFactory, ChatClientFactory>();
builder.Services.AddScoped<IVisionModelClientFactory, VisionModelClientFactory>();
builder.Services.AddScoped<IModelCatalogService, ModelCatalogService>();
builder.Services.AddScoped<IOpenAiAccountModelCatalogService, OpenAiAccountModelCatalogService>();

// Chapters
builder.Services.AddScoped<ChapterService>();
builder.Services.AddScoped<IChapterSemanticProjectionService, ChapterSemanticProjectionService>();
builder.Services.AddScoped<IChapterService>(services => services.GetRequiredService<ChapterService>());
builder.Services.AddScoped<IManuscriptService>(services => services.GetRequiredService<ChapterService>());
builder.Services.AddScoped<IManuscriptAnnotationService, ManuscriptAnnotationService>();
builder.Services.AddSingleton<Lorekeeper.Authoring.IAuthoringHistoryRuntime, Lorekeeper.Authoring.AuthoringHistoryRuntime>();
builder.Services.AddScoped<Lorekeeper.Authoring.IAuthoringCompoundManuscriptRestoreService, Lorekeeper.Authoring.AuthoringCompoundManuscriptRestoreService>();
builder.Services.AddScoped<Lorekeeper.Authoring.IAuthoringMutationContextAccessor, Lorekeeper.Authoring.AuthoringMutationContextAccessor>();
builder.Services.AddSingleton<IDatabaseMigrationRecoveryService, DatabaseMigrationRecoveryService>();
builder.Services.AddSingleton<IManuscriptMigrationService, ManuscriptMigrationService>();
builder.Services.AddScoped<IDatabaseStartupMigrationService, DatabaseStartupMigrationService>();
builder.Services.AddScoped<IVisualCompositionMigrationService, VisualCompositionMigrationService>();
builder.Services.AddScoped<IAuthoringPageMigrationService, AuthoringPageMigrationService>();
builder.Services.AddScoped<IPublicationCoreMigrationService, PublicationCoreMigrationService>();
builder.Services.AddScoped<IEditionContentMigrationService, EditionContentMigrationService>();
builder.Services.AddScoped<IPublicationSectionMigrationService, PublicationSectionMigrationService>();
builder.Services.AddScoped<IDesignedPageMigrationService, DesignedPageMigrationService>();
builder.Services.AddScoped<IProjectImageService, ProjectImageService>();
builder.Services.AddScoped<IProjectFontService, ProjectFontService>();
builder.Services.AddSingleton<ITypographyDefaultsService, TypographyDefaultsService>();
builder.Services.AddScoped<IManuscriptStyleService, ManuscriptStyleService>();
builder.Services.AddScoped<Lorekeeper.Composition.IDesignedPageService, Lorekeeper.Composition.DesignedPageService>();
builder.Services.AddScoped<Lorekeeper.Composition.IProjectPageSetupService, Lorekeeper.Composition.ProjectPageSetupService>();
builder.Services.AddScoped<Lorekeeper.Composition.IChapterPreviewService, Lorekeeper.Composition.ChapterPreviewService>();
builder.Services.AddScoped<Lorekeeper.Composition.ICompositionCanvasPreviewService, Lorekeeper.Composition.CompositionCanvasPreviewService>();
builder.Services.Configure<EntityVisualContextOptions>(builder.Configuration.GetSection(EntityVisualContextOptions.SectionName));
builder.Services.AddScoped<IEntityVisualExampleService, EntityVisualExampleService>();
builder.Services.AddScoped<IEntityVisualContextService, EntityVisualContextService>();
builder.Services.AddScoped<IReferenceVisualService, ReferenceVisualService>();
builder.Services.AddOptions<ProjectImageGenerationOptions>()
    .Bind(builder.Configuration.GetSection(ProjectImageGenerationOptions.SectionName))
    .Validate(options => options.MaxProviderOutputBytes > 0, "Images:MaxProviderOutputBytes must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddScoped<IProjectImageProvider, CodexProjectImageProvider>();
builder.Services.AddScoped<IProjectImageDefaultRasterResolver, ProjectImageDefaultRasterResolver>();
builder.Services.AddScoped<IProjectImageJobService, ProjectImageJobService>();
builder.Services.AddScoped<IImagePromptComposer, ImagePromptComposer>();
builder.Services.AddScoped<IAgentProjectImageWorkflow, AgentProjectImageWorkflow>();
builder.Services.AddSingleton<IProjectImageGenerationRuntime, ProjectImageGenerationRuntime>();
builder.Services.AddHostedService<ProjectImageGenerationStartupWorker>();

// Outline
builder.Services.AddScoped<IActService, ActService>();
builder.Services.AddScoped<IEntityService, EntityService>();
builder.Services.AddScoped<IEntityTypeService, EntityTypeService>();
builder.Services.AddScoped<IProjectFactService, ProjectFactService>();
builder.Services.AddScoped<IOutlineGraphSync, OutlineGraphSync>();
builder.Services.AddScoped<IOutlineWorkingContextBuilder, OutlineWorkingContextBuilder>();
builder.Services.AddScoped<OutlineCollaborationTools>();
builder.Services.AddScoped<IOutlineCollaborationService, OutlineCollaborationService>();
builder.Services.AddSingleton<IBookFormatGuidanceService, BookFormatGuidanceService>();
builder.Services.AddSingleton<IOutlineChatTurnRunner, OutlineChatTurnRunner>();

// Writing samples
builder.Services.AddScoped<IWritingSampleService, WritingSampleService>();
builder.Services.AddScoped<WritingCoachTools>();
builder.Services.AddScoped<IWritingCoachService, WritingCoachService>();
builder.Services.AddSingleton<IWritingCoachTurnRunner, WritingCoachTurnRunner>();

// Ingest
builder.Services.Configure<IngestSourceStructureOptions>(builder.Configuration.GetSection(IngestSourceStructureOptions.SectionName));
builder.Services.Configure<BookArtifactIngestOptions>(builder.Configuration.GetSection(BookArtifactIngestOptions.SectionName));
builder.Services.AddSingleton<IIngestJobQueue, IngestJobQueue>();
builder.Services.AddSingleton<IIngestJobNotifier, IngestJobNotifier>();
builder.Services.AddScoped<IIngestSourceStructureBuilder, IngestSourceStructureBuilder>();
builder.Services.AddScoped<IBookArtifactPreprocessor, BookArtifactPreprocessor>();
builder.Services.AddScoped<IIngestGraphSync, IngestGraphSync>();
builder.Services.AddScoped<IIngestGraphCleanup, IngestGraphCleanup>();
builder.Services.AddScoped<IIngestVectorIndexingService, IngestVectorIndexingService>();
builder.Services.AddScoped<IngestAgentTools>();
builder.Services.AddScoped<IngestJobProcessor>();
builder.Services.AddScoped<IIngestService, IngestService>();
builder.Services.AddHostedService<IngestJobWorker>();

// Research
builder.Services.Configure<WebResearchOptions>(builder.Configuration.GetSection(WebResearchOptions.SectionName));
builder.Services.AddSingleton<IWebFetchCoordinator, WebFetchCoordinator>();
builder.Services.AddSingleton<IWebHttpFetchClient, WebHttpFetchClient>();
builder.Services.AddSingleton<IWebLinkPolicy, WebLinkPolicy>();
builder.Services.AddSingleton<IWebRobotsPolicy, WebRobotsPolicy>();
builder.Services.AddScoped<IWebPageSourceReader, MediaWikiWebPageSourceReader>();
builder.Services.AddScoped<IWebPageReader, HttpWebPageReader>();
builder.Services.AddScoped<IWebIngestCandidateService, WebIngestCandidateService>();
builder.Services.AddScoped<ResearchTools>();
builder.Services.AddScoped<IResearchService, ResearchService>();
builder.Services.AddSingleton<IResearchChatTurnRunner, ResearchChatTurnRunner>();

// Import / export
builder.Services.AddSingleton<IProjectImportJobQueue, ProjectImportJobQueue>();
builder.Services.AddSingleton<IProjectImportJobNotifier, ProjectImportJobNotifier>();
builder.Services.AddScoped<IProjectImportExportService, ProjectImportExportService>();
builder.Services.AddScoped<ProjectImportJobProcessor>();
builder.Services.AddHostedService<ProjectImportJobWorker>();

// Publish
builder.Services.AddScoped<IPublishExportFormatter, PlainTextPublishFormatter>();
builder.Services.AddScoped<IPublishExportFormatter, MarkdownPublishFormatter>();
builder.Services.AddScoped<IPublishExportFormatter, EpubPublishFormatter>();
builder.Services.AddScoped<IPublishService, PublishService>();
builder.Services.AddScoped<IPublicationBookService, PublicationBookService>();
builder.Services.AddScoped<IPublicationSectionService, PublicationSectionService>();
builder.Services.AddScoped<IPublicationReleasePresetService, PublicationReleasePresetService>();
builder.Services.AddSingleton<IPrintArtifactProfileRegistry, PrintArtifactProfileRegistry>();
builder.Services.AddScoped<IPrintGeometryService, PrintGeometryService>();
builder.Services.AddScoped<IPrintArtifactProfileMigrationService, PrintArtifactProfileMigrationService>();
builder.Services.AddScoped<IPublicationEffectiveConfigurationResolver, PublicationEffectiveConfigurationResolver>();
builder.Services.AddScoped<IPublicationEditionService, PublicationEditionService>();
builder.Services.AddScoped<IEditionContentService, EditionContentService>();
builder.Services.AddScoped<IPublicationActorContext, PublicationActorContext>();
builder.Services.Configure<PublicationPressOptions>(
    builder.Configuration.GetSection(PublicationPressOptions.SectionName));
builder.Services.AddSingleton<IPublicationPressRuntime, PublicationPressRuntime>();
builder.Services.AddSingleton<IPublicationPressInstallationRoot, PublicationPressInstallationRoot>();
builder.Services.AddSingleton<IPublicationRenderQueue, PublicationRenderQueue>();
builder.Services.AddScoped<IPublicationRenderService, PublicationRenderService>();
builder.Services.AddSingleton<PublicationArtifactPreviewCache>();
builder.Services.AddScoped<IPublicationArtifactPreviewService, PublicationArtifactPreviewService>();
builder.Services.AddOptions<PrintingOptions>().BindConfiguration(PrintingOptions.SectionName)
    .Validate(value => value.MaxSourceBytes is > 0 and <= 268435456
        && value.MaxPagesPerJob is > 0 and <= 200
        && value.MaxSessions is > 0 and <= 32
        && value.SessionMinutes is >= 1 and <= 120
        && value.MaxSessionBytes is > 0 and <= 536870912
        && value.MaxRasterPixels is >= 20000000 and <= 120000000, "Printing limits are outside the supported bounds.")
    .ValidateOnStart();
builder.Services.AddSingleton<PrintSessionStore>();
builder.Services.AddScoped<IPrintPreparationService, PrintPreparationService>();
if (isElectronMode)
    builder.Services.AddScoped<IPrintHost, Lorekeeper.Desktop.ElectronPrintHost>();
else
    builder.Services.AddScoped<IPrintHost, BrowserPrintHost>();
builder.Services.AddSingleton<PublicationEpubPreviewCache>();
builder.Services.AddScoped<IPublicationEpubPreviewService, PublicationEpubPreviewService>();
builder.Services.AddScoped<IPublicationCoverService, PublicationCoverService>();
builder.Services.AddScoped<IPublicationPackageService, PublicationPackageService>();
builder.Services.AddSingleton<IPublicationPreparationQueue, PublicationPreparationQueue>();
builder.Services.AddSingleton<PublicationPreparationCancellationRegistry>();
builder.Services.AddScoped<IPublicationPreparationService, PublicationPreparationService>();
builder.Services.AddScoped<IPublicationImagePreparationService, PublicationImagePreparationService>();
builder.Services.AddScoped<IPublicationDiagnosticPresentationService, PublicationDiagnosticPresentationService>();
builder.Services.AddHostedService<PublicationPreparationWorker>();
builder.Services.AddScoped<PublicationRenderProcessor>();
builder.Services.AddScoped<IPublicationPaginationService>(services => services.GetRequiredService<PublicationRenderProcessor>());
builder.Services.AddHostedService<PublicationRenderWorker>();
builder.Services.AddScoped<IPublishAssistantTools, PublishAssistantTools>();
builder.Services.AddScoped<IPublishChatService, PublishChatService>();
builder.Services.AddSingleton<IPublishChatTurnRunner, PublishChatTurnRunner>();
builder.Services.AddSingleton<IPublicationEditionMigrationService, PublicationEditionMigrationService>();
builder.Services.AddSingleton<IPublicationPressMigrationService, PublicationPressMigrationService>();

// Context + editor chat
builder.Services.AddScoped<ContextBuilder>();
builder.Services.AddScoped<IContextBuilder>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IEditorContextService>(sp => sp.GetRequiredService<ContextBuilder>());
builder.Services.AddScoped<IEditorPendingReviewInspector, EditorPendingReviewInspector>();
builder.Services.AddScoped<IVectorIndexWorkCoordinator, VectorIndexWorkCoordinator>();
builder.Services.AddScoped<IContextIndexingService, ContextIndexingService>();
builder.Services.AddScoped<IContextRecommendationService, ContextRecommendationService>();
builder.Services.AddScoped<IEntityRelationContextService, EntityRelationContextService>();
builder.Services.AddScoped<EditorChatTools>();
builder.Services.AddScoped<EditorManuscriptApplyService>();
builder.Services.AddScoped<IEditorContestService, EditorContestService>();
builder.Services.AddSingleton<IEditorContestRunRegistry, EditorContestRunRegistry>();
builder.Services.AddScoped<IEditorContestMutationContext, EditorContestMutationContext>();
builder.Services.AddScoped<IEditorContestMutationGuard, EditorContestMutationGuard>();
builder.Services.AddSingleton<IEditorRevisionJobNotifier, EditorRevisionJobNotifier>();
builder.Services.AddScoped<EditorRevisionAgentProcessor>();
builder.Services.AddScoped<IEditorRevisionAgentService, EditorRevisionAgentService>();
builder.Services.AddScoped<IEditorChatService, EditorChatService>();
builder.Services.AddSingleton<IEditorChatTurnRunner, EditorChatTurnRunner>();
builder.Services.AddScoped<ImagesChatTools>();
builder.Services.AddScoped<IImagesChatService, ImagesChatService>();
builder.Services.AddSingleton<IImagesChatTurnRunner, ImagesChatTurnRunner>();

var app = builder.Build();
if (isElectronMode && distributionChannelPolicy.UsesGitHubReleaseChecks)
    desktopReleaseUpdateChecker = app.Services.GetRequiredService<IDesktopReleaseUpdateChecker>();
desktopUpdates.SetInstalledVersion(GetInstalledAppVersion());

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (!isElectronMode)
    {
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!isElectronMode)
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<Lorekeeper.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapCodexOAuth();
app.MapProjectImages();
app.MapProjectFonts();
app.MapPublishEndpoints();
app.MapPrintingEndpoints();

app.Run();

static async Task ElectronAppReady(
    string desktopUrl,
    bool enableDevTools,
    DistributionChannelPolicy distributionChannelPolicy,
    DesktopUpdateService desktopUpdates,
    Func<IDesktopReleaseUpdateChecker?> getReleaseUpdateChecker)
{
    var options = new BrowserWindowOptions
    {
        Title = "Lorekeeper",
        Show = false,
        Width = 1440,
        Height = 960,
        MinWidth = 1024,
        MinHeight = 700,
        Center = true,
        IsRunningBlazor = true,
        WebPreferences = new WebPreferences
        {
            DevTools = enableDevTools,
            NodeIntegration = false,
            NodeIntegrationInWorker = false,
            NodeIntegrationInSubFrames = false,
            ContextIsolation = true,
            Sandbox = true,
            WebSecurity = true,
            AllowRunningInsecureContent = false,
            Plugins = false,
            ExperimentalFeatures = false,
            WebviewTag = false,
            EnableRemoteModule = false
        }
    };

    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        options.AutoHideMenuBar = true;

    if (OperatingSystem.IsMacOS())
        Electron.App.WindowAllClosed += Electron.App.Quit;

    var browserWindow = await Electron.WindowManager.CreateWindowAsync(options, desktopUrl);
    browserWindow.OnReadyToShow += () => browserWindow.Show();

    var currentVersion = await Electron.App.GetVersionAsync();
    desktopUpdates.SetInstalledVersion(currentVersion);
    if (distributionChannelPolicy.UpdatePolicy == DesktopUpdatePolicy.StoreManaged)
    {
        desktopUpdates.MarkStoreManaged(currentVersion);
    }
    else if (distributionChannelPolicy.UpdatePolicy == DesktopUpdatePolicy.ManualGitHubRelease)
    {
        var releaseUpdateChecker = getReleaseUpdateChecker();
        if (releaseUpdateChecker is null)
            throw new InvalidOperationException("The Free distribution channel requires its release update checker.");

        desktopUpdates.EnableManualDownloads(OpenReleaseInDefaultBrowserAsync);
        desktopUpdates.EnableManualCheck(
            cancellation => CheckForManualUpdateAsync(currentVersion, releaseUpdateChecker, desktopUpdates, cancellation));
    }
}

static async Task OpenReleaseInDefaultBrowserAsync(Uri releaseUri, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var error = await Electron.Shell.OpenExternalAsync(releaseUri.AbsoluteUri);
    if (!string.IsNullOrWhiteSpace(error))
        throw new InvalidOperationException($"The default browser could not open the release page: {error}");
}

static async Task CheckForManualUpdateAsync(
    string currentVersion,
    IDesktopReleaseUpdateChecker releaseUpdateChecker,
    DesktopUpdateService desktopUpdates,
    CancellationToken cancellationToken)
{
    try
    {
        var priorStatus = desktopUpdates.Snapshot.Status;
        if (priorStatus is DesktopUpdateStatus.Idle or DesktopUpdateStatus.Unsupported)
        {
            desktopUpdates.MarkChecking();
        }

        var update = await releaseUpdateChecker.CheckAsync(currentVersion, cancellationToken);
        if (update is null)
            desktopUpdates.MarkIdle(currentVersion);
        else
            desktopUpdates.MarkManualUpdateAvailable(update.Version, update.ReleaseUri);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        // Keep any previously discovered release visible through transient failures.
        if (desktopUpdates.Snapshot.Status == DesktopUpdateStatus.Checking)
            desktopUpdates.MarkIdle(currentVersion);
        Console.Error.WriteLine($"Manual desktop update check failed: {exception.Message}");
    }
}

static bool IsElectronMode(string[] args) =>
    args.Any(IsElectronArgument);

static bool IsElectronArgument(string arg)
{
    var normalized = arg.TrimStart('-', '/');
    return normalized.Equals("electron", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronPort=", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronPID=", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("electronAuthToken=", StringComparison.OrdinalIgnoreCase);
}

static string? GetDistributionChannelBuildMetadata()
{
    var values = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(attribute => attribute.Key.Equals(DistributionChannelPolicy.BuildMetadataKey, StringComparison.Ordinal))
        .Select(attribute => attribute.Value)
        .ToArray();

    return values is { Length: 1 } ? values[0] : null;
}

static string? GetInstalledAppVersion()
{
    var informationalVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informationalVersion))
    {
        var plusSeparator = informationalVersion.IndexOf('+');
        return (plusSeparator >= 0 ? informationalVersion[..plusSeparator] : informationalVersion).Trim();
    }

    return Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
}

static string GetDesktopUrl(IConfiguration configuration)
{
    var bindHost = configuration["Desktop:BindHost"];
    if (string.IsNullOrWhiteSpace(bindHost))
        throw new InvalidOperationException("Desktop:BindHost must be configured to run the desktop shell.");

    if (bindHost.Contains("://", StringComparison.Ordinal))
        throw new InvalidOperationException("Desktop:BindHost must be a host name only, without a URL scheme.");

    var httpPort = configuration.GetValue<int?>("Desktop:HttpPort")
        ?? throw new InvalidOperationException("Desktop:HttpPort must be configured to run the desktop shell.");
    if (httpPort is <= 0 or > 65535)
        throw new InvalidOperationException("Desktop:HttpPort must be between 1 and 65535.");

    return $"http://{bindHost.Trim()}:{httpPort}";
}
