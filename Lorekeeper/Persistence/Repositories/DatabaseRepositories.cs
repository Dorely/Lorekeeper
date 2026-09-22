namespace Lorekeeper.Persistence.Repositories;

public sealed class DatabaseRepositories(AppDatabaseReadOperation operation)
{
    public IActRepository Acts { get; } = new ActRepository(operation);
    public IChapterRepository Chapters { get; } = new ChapterRepository(operation);
    public IContestRepository Contests { get; } = new ContestRepository(operation);
    public IEditorContextPreferenceRepository EditorContextPreferences { get; } = new EditorContextPreferenceRepository(operation);
    public IEditorConversationRepository EditorConversations { get; } = new EditorConversationRepository(operation);
    public IEditorRevisionRepository EditorRevisions { get; } = new EditorRevisionRepository(operation);
    public IEmbeddingConfigurationRepository EmbeddingConfigurations { get; } = new EmbeddingConfigurationRepository(operation);
    public IGraphEdgeRepository GraphEdges { get; } = new GraphEdgeRepository(operation);
    public IGraphEntityTypeRepository GraphEntityTypes { get; } = new GraphEntityTypeRepository(operation);
    public IGraphNodeRepository GraphNodes { get; } = new GraphNodeRepository(operation);
    public IIngestRepository Ingest { get; } = new IngestRepository(operation);
    public ILlmProviderRepository LlmProviders { get; } = new LlmProviderRepository(operation);
    public IOAuthTokenRepository OAuthTokens { get; } = new OAuthTokenRepository(operation);
    public IOpenAiAccountRepository OpenAiAccounts { get; } = new OpenAiAccountRepository(operation);
    public IOutlineConversationRepository OutlineConversations { get; } = new OutlineConversationRepository(operation);
    public IProjectImageConversationRepository ProjectImageConversations { get; } = new ProjectImageConversationRepository(operation);
    public IProjectImportRepository ProjectImports { get; } = new ProjectImportRepository(operation);
    public IProjectRepository Projects { get; } = new ProjectRepository(operation);
    public IProjectReferenceRepository ProjectReferences { get; } = new ProjectReferenceRepository(operation);
    public IPublishConversationRepository PublishConversations { get; } = new PublishConversationRepository(operation);
    public IResearchConversationRepository ResearchConversations { get; } = new ResearchConversationRepository(operation);
    public ISearchProviderRepository SearchProviders { get; } = new SearchProviderRepository(operation);
    public IWebIngestCandidateRepository WebIngestCandidates { get; } = new WebIngestCandidateRepository(operation);
    public IVoiceConversationRepository VoiceConversations { get; } = new VoiceConversationRepository(operation);
    public IWritingSampleRepository WritingSamples { get; } = new WritingSampleRepository(operation);
}
