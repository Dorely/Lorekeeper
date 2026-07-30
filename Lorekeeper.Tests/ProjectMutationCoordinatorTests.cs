using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;

namespace Lorekeeper.Tests;

public sealed class ProjectMutationCoordinatorTests
{
    [Fact]
    public async Task SeparateCoordinatorsSerializeMutationsForTheSameDatabaseAndProject()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Lorekeeper.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "lorekeeper.db"),
            }.ToString();
            var firstCoordinator = new ProjectMutationCoordinator(connectionString);
            var secondCoordinator = new ProjectMutationCoordinator(connectionString);
            var projectId = Guid.NewGuid();

            await using var firstLease = await firstCoordinator.AcquireAsync(projectId);
            var secondLeaseTask = secondCoordinator.AcquireAsync(projectId).AsTask();
            await Task.Delay(150);
            Assert.False(secondLeaseTask.IsCompleted);

            await firstLease.DisposeAsync();
            await using var secondLease = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
