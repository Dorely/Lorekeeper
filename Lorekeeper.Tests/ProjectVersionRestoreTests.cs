using System.Reflection;
using System.Text;
using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.Graph;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Search;
using Lorekeeper.VersionHistory.Restore;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectVersionRestoreTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void WorldBriefSnapshotsPreserveCurrentTextAndAdaptPredecessors(int schemaVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid());
            payload = payload with { Narrative = payload.Narrative with { WorldBrief = "# World\nEstablished rules." } };
            WriteSnapshotTree(root, payload, schemaVersion: schemaVersion);
            var original = File.ReadAllBytes(Path.Combine(root, "narrative", "narrative.json"));
            var read = new VersionHistorySnapshotReader().Read(root);
            Assert.Equal(schemaVersion < 11 ? "" : payload.Narrative.WorldBrief, read.Payload.Narrative.WorldBrief);
            WriteSnapshotTree(root, payload, schemaVersion: schemaVersion);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "narrative", "narrative.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WorldBriefSnapshotsRejectNullContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteSnapshotTree(root, CreatePayload(Guid.NewGuid(), Guid.NewGuid()), (path, bytes) =>
            {
                if (path != "narrative/narrative.json") return bytes;
                var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
                node["worldBrief"] = null;
                return VersionHistoryCanonicalJson.Serialize(node);
            });
            Assert.Throws<InvalidDataException>(() => new VersionHistorySnapshotReader().Read(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void PreDesignedPageSnapshotsValidateTheirOriginalCompositionShape(int schemaVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: CreateChapter(Guid.NewGuid(), "Chapter"));
            var chapter = Assert.Single(payload.Narrative.Chapters);
            var pageId = Guid.NewGuid();
            var page = new ProjectExportPageComposition(pageId, chapter.Id,
                "Historical page", ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(pageId)), 0, []);
            payload = payload with
            {
                Composition = new VersionHistorySnapshotCompositionArea([]) { LegacyPageCompositions = [page] },
            };
            WriteSnapshotTree(root, payload, schemaVersion: schemaVersion);
            var path = Path.Combine(root, "composition", "composition.json");
            var original = File.ReadAllBytes(path);
            Assert.DoesNotContain("designedPages", Encoding.UTF8.GetString(original), StringComparison.Ordinal);

            foreach (var options in new[] { VersionHistorySnapshotReadOptions.Default,
                new VersionHistorySnapshotReadOptions { IncludeAssetData = false, IncludeSourceDetails = false } })
            {
                var read = new VersionHistorySnapshotReader().Read(root, options: options);
                Assert.Empty(read.Payload.Composition.DesignedPages);
                var restored = Assert.Single(read.Payload.Composition.PageCompositions);
                Assert.Equal(page.Id, restored.Id);
                Assert.Equal(page.ChapterId, restored.ChapterId);
                Assert.Equal(page.Name, restored.Name);
                Assert.Equal(ManuscriptCodec.Serialize(ManuscriptCodec.Deserialize(page.SemanticManuscriptJson, page.Id, page.Revision)),
                    ManuscriptCodec.Serialize(ManuscriptCodec.Deserialize(restored.SemanticManuscriptJson, page.Id, page.Revision)));
                Assert.Equal(original, File.ReadAllBytes(path));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    public void CompositionSchemaAdaptersStillRejectNoncanonicalPayloads(int schemaVersion, bool unknownProperty)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteSnapshotTree(root, CreatePayload(Guid.NewGuid(), Guid.NewGuid()), (path, bytes) =>
            {
                if (path != "composition/composition.json")
                    return bytes;
                if (!unknownProperty)
                    return [.. bytes, (byte)' '];
                var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
                node["unexpected"] = true;
                return VersionHistoryCanonicalJson.Serialize(node);
            }, schemaVersion);
            var exception = Assert.Throws<InvalidDataException>(() => new VersionHistorySnapshotReader().Read(root));
            Assert.Contains("composition/composition.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not in canonical form", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SchemaFiveCoverBindingsAdaptToDescription()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repositoryId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var scene = new CompositionScene
            {
                Objects =
                [
                    new CompositionObject
                    {
                        Id = Guid.NewGuid(),
                        LayerId = Guid.NewGuid(),
                        Kind = CompositionObjectKind.Text,
                        TextBinding = "backCopy",
                    },
                ],
            };
            var cover = new ProjectExportCoverDesign(
                "Book", "", "Author", "", "#ffffff", PublicationBarcodeMode.None,
                50, 50, JsonSerializer.Serialize(scene, ManuscriptCodec.JsonOptions), 1)
            {
                LegacyBackCopy = "Invisible legacy copy",
            };
            var book = new ProjectExportPublicationBook(
                1, "Book", "", "Author", "en", "", "", "Visible description", true, false,
                false, false, true, true, false, false, PublishTitlePageMode.Automatic, [], cover);
            var payload = CreatePayload(repositoryId, projectId) with
            {
                Publication = new VersionHistorySnapshotPublicationArea(book, [], []),
            };
            WriteSnapshotTree(root, payload, schemaVersion: 5);

            var artifact = new VersionHistorySnapshotReader().Read(root, repositoryId, projectId);
            var adaptedCover = artifact.Payload.Publication.PublicationBook?.CoverDesign;

            Assert.NotNull(adaptedCover);
            Assert.Null(adaptedCover.LegacyBackCopy);
            Assert.Contains("description", adaptedCover.CompositionSceneJson, StringComparison.Ordinal);
            Assert.DoesNotContain("backCopy", adaptedCover.CompositionSceneJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderRequiresCanonicalJsonAndExactSchemaFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid());
            var validRoot = Path.Combine(root, "valid");
            WriteSnapshotTree(validRoot, payload);
            var reader = new VersionHistorySnapshotReader();

            var artifact = reader.Read(validRoot, payload.RepositoryId, payload.ProjectId);
            Assert.Equal(payload.ProjectId, artifact.Payload.ProjectId);

            var unexpectedRoot = Path.Combine(root, "unexpected");
            WriteSnapshotTree(unexpectedRoot, payload);
            File.WriteAllText(Path.Combine(unexpectedRoot, "undeclared.txt"), "not part of schema v1");
            var unexpected = Assert.Throws<InvalidDataException>(
                () => reader.Read(unexpectedRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("file set is not exact", unexpected.Message, StringComparison.OrdinalIgnoreCase);

            var noncanonicalRoot = Path.Combine(root, "noncanonical");
            WriteSnapshotTree(
                noncanonicalRoot,
                payload,
                (path, bytes) => path == "project/project.json" ? [.. Encoding.UTF8.GetBytes(" "), .. bytes] : bytes);
            var noncanonical = Assert.Throws<InvalidDataException>(
                () => reader.Read(noncanonicalRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("not in canonical", noncanonical.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderLightweightModeValidatesAssetsWithoutRetainingBinaryData()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageId = Guid.NewGuid();
            var familyId = Guid.NewGuid();
            var faceId = Guid.NewGuid();
            byte[] imageData = [1, 3, 5, 7];
            byte[] fontData = [2, 4, 6, 8, 10];
            var image = new VersionHistoryImageAsset(
                imageId,
                "image.png",
                "image/png",
                $"assets/images/{imageId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex(imageData),
                imageData.LongLength,
                "Image",
                PublishAssetSource.Uploaded,
                string.Empty,
                string.Empty,
                "{}",
                null,
                null,
                null,
                null,
                null);
            var face = new VersionHistoryFontFace(
                faceId,
                "Regular",
                "font.ttf",
                "font/ttf",
                400,
                false,
                $"assets/fonts/{familyId:N}/faces/{faceId:N}/content.ttf",
                VersionHistoryCanonicalJson.Sha256Hex(fontData),
                fontData.LongLength);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid()) with
            {
                Assets = new VersionHistorySnapshotAssetsArea(
                    [image],
                    [],
                    [new VersionHistoryFontFamily(familyId, "Family", true, "Owned", [face])]),
                ImageData = new Dictionary<Guid, byte[]> { [imageId] = imageData },
                FontFaceData = new Dictionary<Guid, byte[]> { [faceId] = fontData },
            };
            WriteSnapshotTree(root, payload);

            var reader = new VersionHistorySnapshotReader();
            var lightweight = reader.Read(
                root,
                payload.RepositoryId,
                payload.ProjectId,
                new VersionHistorySnapshotReadOptions { IncludeAssetData = false });
            var full = reader.Read(root, payload.RepositoryId, payload.ProjectId);

            Assert.Empty(lightweight.Payload.ImageData);
            Assert.Empty(lightweight.Payload.FontFaceData);
            Assert.Equal(imageData, full.Payload.ImageData[imageId]);
            Assert.Equal(fontData, full.Payload.FontFaceData[faceId]);
            Assert.Equal(image.Sha256, Assert.Single(lightweight.Payload.Assets.Images).Sha256);
            Assert.Equal(face.Sha256, Assert.Single(Assert.Single(lightweight.Payload.Assets.FontFamilies).Faces).Sha256);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotReaderLightweightModeRejectsCorruptOrLengthMismatchedAsset(bool changeLength)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageId = Guid.NewGuid();
            byte[] imageData = [1, 2, 3, 4];
            var image = new VersionHistoryImageAsset(
                imageId,
                "image.png",
                "image/png",
                $"assets/images/{imageId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex(imageData),
                imageData.LongLength,
                "Image",
                PublishAssetSource.Uploaded,
                string.Empty,
                string.Empty,
                "{}",
                null,
                null,
                null,
                null,
                null);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid()) with
            {
                Assets = new VersionHistorySnapshotAssetsArea([image], [], []),
                ImageData = new Dictionary<Guid, byte[]> { [imageId] = imageData },
            };
            WriteSnapshotTree(root, payload);
            File.WriteAllBytes(
                Path.Combine(root, image.BlobPath.Replace('/', Path.DirectorySeparatorChar)),
                changeLength ? [9, 9, 9, 9, 9] : [9, 9, 9, 9]);

            var exception = Assert.Throws<InvalidDataException>(() => new VersionHistorySnapshotReader().Read(
                root,
                payload.RepositoryId,
                payload.ProjectId,
                new VersionHistorySnapshotReadOptions { IncludeAssetData = false }));

            Assert.Contains(changeLength ? "length" : "SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderPreservesChapterMetadataAndUsesDirectManuscriptJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chapterId = Guid.NewGuid();
            var chapter = CreateChapter(chapterId, "Complete chapter") with
            {
                ActId = Guid.NewGuid(),
                Body = "Legacy body is retained as chapter metadata.",
                ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId, revision: 17)),
                ManuscriptRevision = 17,
                Order = 4,
                VisualMode = ChapterVisualMode.IllustratedProse,
                PageLayoutKind = ChapterPageLayoutKind.DoubleLandscape,
                PageLayoutJson = "{\"pageWidth\":11}",
                IllustrationLayoutJson = "{\"columns\":2}",
                ExplicitImageContextImageIds = [Guid.NewGuid(), Guid.NewGuid()],
            };
            var payload = CreatePayload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                chapter: chapter,
                acts: [new ProjectExportAct(chapter.ActId!.Value, "Act", "Act synopsis", 0)]);
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(snapshotRoot, payload);

            var manuscriptPath = Path.Combine(
                snapshotRoot,
                "narrative",
                "chapters",
                chapter.Id.ToString("N"),
                "manuscript.json");
            var manuscriptBytes = File.ReadAllBytes(manuscriptPath);
            Assert.StartsWith("{", Encoding.UTF8.GetString(manuscriptBytes));
            Assert.DoesNotContain("\\\"", Encoding.UTF8.GetString(manuscriptBytes));

            var actual = new VersionHistorySnapshotReader()
                .Read(snapshotRoot, payload.RepositoryId, payload.ProjectId)
                .Payload
                .Narrative
                .Chapters
                .Single();
            var expectedManuscript = Encoding.UTF8.GetString(
                VersionHistoryCanonicalJson.SerializeDirectManuscript(chapter.ManuscriptJson));
            var expectedMetadata = VersionHistoryCanonicalJson.Deserialize<VersionHistorySnapshotChapter>(
                VersionHistoryCanonicalJson.Serialize(
                    VersionHistorySnapshotChapter.FromProjectExportChapter(chapter)));

            Assert.Equal(expectedMetadata.Id, actual.Id);
            Assert.Equal(expectedMetadata.ActId, actual.ActId);
            Assert.Equal(expectedMetadata.Title, actual.Title);
            Assert.Equal(expectedManuscript, actual.ManuscriptJson);
            Assert.Equal(expectedMetadata.ManuscriptRevision, actual.ManuscriptRevision);
            Assert.Equal(expectedMetadata.Body, actual.Body);
            Assert.Equal(expectedMetadata.Synopsis, actual.Synopsis);
            Assert.Equal(expectedMetadata.Order, actual.Order);
            Assert.Equal(expectedMetadata.VisualMode, actual.VisualMode);
            Assert.Equal(expectedMetadata.PageLayoutKind, actual.PageLayoutKind);
            Assert.Equal(expectedMetadata.PageLayoutJson, actual.PageLayoutJson);
            Assert.Equal(expectedMetadata.IllustrationLayoutJson, actual.IllustrationLayoutJson);
            Assert.Equal(expectedMetadata.ExplicitImageContextImageIds, actual.ExplicitImageContextImageIds);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderPreservesImageLineageMetadataAndLegacySourceMeaning()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repositoryId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var upscaleId = Guid.NewGuid();
            var sourceMetadata = "{\"transform\":{\"kind\":\"print-upscale\"}}";
            var source = new VersionHistoryImageAsset(
                sourceId,
                "original.png",
                "image/png",
                $"assets/images/{sourceId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex([1, 2, 3]),
                3,
                "Original",
                PublishAssetSource.Uploaded,
                string.Empty,
                string.Empty,
                "{}",
                null,
                null,
                null,
                null,
                null);
            var upscale = new VersionHistoryImageAsset(
                upscaleId,
                "original-upscaled.png",
                "image/png",
                $"assets/images/{upscaleId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex([4, 5, 6]),
                3,
                "Original",
                PublishAssetSource.Upscaled,
                string.Empty,
                string.Empty,
                sourceMetadata,
                sourceId,
                2,
                3,
                90,
                80);
            var payload = CreatePayload(repositoryId, projectId) with
            {
                Assets = new VersionHistorySnapshotAssetsArea([source, upscale], [], []),
                ImageData = new Dictionary<Guid, byte[]>
                {
                    [sourceId] = [1, 2, 3],
                    [upscaleId] = [4, 5, 6],
                },
            };
            var snapshotRoot = Path.Combine(root, "current");
            WriteSnapshotTree(snapshotRoot, payload);

            var artifact = new VersionHistorySnapshotReader().Read(snapshotRoot, repositoryId, projectId);
            var actual = artifact.Payload.Assets.Images.Single(image => image.Id == upscaleId);
            Assert.Equal(PublishAssetSource.Upscaled, actual.Source);
            Assert.Equal(sourceId, actual.DerivedFromImageId);
            using var actualMetadata = JsonDocument.Parse(actual.SourceMetadataJson);
            Assert.Equal(
                "print-upscale",
                actualMetadata.RootElement.GetProperty("transform").GetProperty("kind").GetString());
            Assert.Equal(2, actual.CropXPercent);
            Assert.Equal(3, actual.CropYPercent);
            Assert.Equal(90, actual.CropWidthPercent);
            Assert.Equal(80, actual.CropHeightPercent);
            Assert.Equal([4, 5, 6], artifact.Payload.ImageData[upscaleId]);

            var legacyId = Guid.NewGuid();
            var legacy = new VersionHistoryImageAsset(
                legacyId,
                "legacy-imported.png",
                "image/png",
                $"assets/images/{legacyId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex([7, 8, 9]),
                3,
                "Imported",
                (PublishAssetSource)6,
                string.Empty,
                string.Empty,
                "{}",
                null,
                null,
                null,
                null,
                null);
            var legacyPayload = CreatePayload(repositoryId, projectId) with
            {
                Assets = new VersionHistorySnapshotAssetsArea([legacy], [], []),
                ImageData = new Dictionary<Guid, byte[]> { [legacyId] = [7, 8, 9] },
            };
            var legacyRoot = Path.Combine(root, "legacy");
            WriteSnapshotTree(legacyRoot, legacyPayload, schemaVersion: 4);

            var legacyArtifact = new VersionHistorySnapshotReader().Read(legacyRoot, repositoryId, projectId);
            Assert.Equal(
                PublishAssetSource.Imported,
                Assert.Single(legacyArtifact.Payload.Assets.Images).Source);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderRejectsImageWithMissingLineageParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var imageId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var image = new VersionHistoryImageAsset(
                imageId,
                "upscaled.png",
                "image/png",
                $"assets/images/{imageId:N}/content.png",
                VersionHistoryCanonicalJson.Sha256Hex([1]),
                1,
                "Upscaled",
                PublishAssetSource.Upscaled,
                string.Empty,
                string.Empty,
                "{}",
                sourceId,
                null,
                null,
                null,
                null);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid()) with
            {
                Assets = new VersionHistorySnapshotAssetsArea([image], [], []),
                ImageData = new Dictionary<Guid, byte[]> { [imageId] = [1] },
            };
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(snapshotRoot, payload);

            var exception = Assert.Throws<InvalidDataException>(
                () => new VersionHistorySnapshotReader().Read(snapshotRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains(sourceId.ToString("N"), exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"accessToken\":\"secret\"}")]
    public void SnapshotReaderRejectsMalformedOrSensitiveDirectManuscriptJson(string manuscriptJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: CreateChapter(Guid.NewGuid(), "Chapter"));
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(
                snapshotRoot,
                payload,
                (path, bytes) => path.EndsWith("/manuscript.json", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes(manuscriptJson)
                    : bytes);

            var exception = Assert.Throws<InvalidDataException>(
                () => new VersionHistorySnapshotReader().Read(snapshotRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderRejectsManuscriptOwnedByAnotherChapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chapter = CreateChapter(Guid.NewGuid(), "Chapter");
            var otherChapter = CreateChapter(Guid.NewGuid(), "Other chapter");
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: chapter);
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(
                snapshotRoot,
                payload,
                (path, bytes) => path.EndsWith("/manuscript.json", StringComparison.Ordinal)
                    ? VersionHistoryCanonicalJson.SerializeDirectManuscript(otherChapter.ManuscriptJson)
                    : bytes);

            var exception = Assert.Throws<InvalidDataException>(
                () => new VersionHistorySnapshotReader().Read(snapshotRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MajorAreaMergeChangesOnlyTheSelectedArea()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var currentChapterId = Guid.NewGuid();
        var targetChapterId = Guid.NewGuid();
        var current = CreatePayload(
            repositoryId,
            projectId,
            projectName: "Current project",
            chapter: CreateChapter(currentChapterId, "Current chapter"));
        var target = CreatePayload(
            repositoryId,
            projectId,
            projectName: "Historical project",
            chapter: CreateChapter(targetChapterId, "Historical chapter"),
            graph: new VersionHistorySnapshotGraphArea(
                [new ProjectExportNode("Character", "historical", "Historical", [], DateTime.UnixEpoch, DateTime.UnixEpoch)],
                []));

        var merged = Merge(
            current,
            target,
            VersionHistoryRestoreSelection.ForMajorAreas(["project"]));

        Assert.Equal("Historical project", merged.Project.Project.Name);
        Assert.Equal(current.Narrative.Chapters.Select(item => item.Id), merged.Narrative.Chapters.Select(item => item.Id));
        Assert.Equal(current.Graph.Nodes.Select(item => item.Key), merged.Graph.Nodes.Select(item => item.Key));
        Assert.Equal(current.RepositoryId, merged.RepositoryId);
        Assert.Equal(current.ProjectId, merged.ProjectId);
    }

    [Fact]
    public void Schema8RetainedSourceUsesPerSourceManifestAndOpensOriginalOnlyWhenRequested()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repositoryId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var extractionId = Guid.NewGuid();
            var bytes = Encoding.UTF8.GetBytes("retained source bytes");
            var hash = VersionHistoryCanonicalJson.Sha256Hex(bytes);
            var normalized = new string('x', 9000);
            var blockId = Guid.NewGuid();
            var retained = new VersionHistoryRetainedSource(
                sourceId, "Source", "artifact", string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, "text/plain", "{}",
                extractionId,
                new VersionHistorySourceOriginal(SourceOriginalState.Available, "source.txt", "text/plain",
                    bytes.Length, hash, [new VersionHistorySourceOriginalChunk(Guid.NewGuid(), 0, hash, bytes.Length)]),
                [new VersionHistorySourceExtraction(extractionId, 0, "test", "1", "{}", SourceRetentionValidator.Sha256(normalized),
                    SourceExtractionStatus.Ready, string.Empty, normalized, [], [],
                    [new VersionHistorySourceBlock(blockId, null, 0, "paragraph", "", "", null, 0, normalized.Length,
                        normalized, SourceRetentionValidator.Sha256(normalized), "{}")])],
                [], []);
            var payload = CreatePayload(repositoryId, projectId) with
            {
                Sources = new VersionHistorySnapshotSourcesArea([]) { RetainedSources = [retained] },
                Graph = new VersionHistorySnapshotGraphArea([], [new ProjectExportEdge(
                    new ProjectExportNodeRef(EntityTypeService.SourceBlockNodeType, blockId.ToString("N")),
                    new ProjectExportNodeRef(EntityTypeService.ProjectNodeType, projectId.ToString("N")),
                    "Supports", new Dictionary<string, object?>(), 0, DateTime.UnixEpoch, DateTime.UnixEpoch)]),
            };
            WriteSnapshotTree(
                root,
                payload,
                schemaVersion: 8,
                sourceOriginalData: new Dictionary<string, byte[]>(StringComparer.Ordinal) { [hash] = bytes });

            var lightweight = new VersionHistorySnapshotReader().Read(root, repositoryId, projectId);
            Assert.False(File.Exists(Path.Combine(root, "sources", "sources.json")));
            Assert.Single(lightweight.Payload.Sources.RetainedSources);
            Assert.Empty(lightweight.Payload.SourceOriginalBlobs);

            var reader = new VersionHistorySnapshotReader();
            var review = reader.Read(root, repositoryId, projectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceDetails = false, IncludeAssetData = false });
            Assert.Empty(review.Payload.Sources.RetainedSources);
            var summary = Assert.Single(review.Payload.Sources.ReviewSummaries!);
            Assert.Equal(4097, summary.ReadableText.Length);
            Assert.False(summary.ReadableTextComplete);
            var expanded = reader.Read(root, repositoryId, projectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceDetails = false, UnboundedSourceReviewId = sourceId });
            Assert.Equal(normalized, Assert.Single(expanded.Payload.Sources.ReviewSummaries!).ReadableText);
            Assert.True(Assert.Single(expanded.Payload.Sources.ReviewSummaries!).ReadableTextComplete);
            var comparer = new VersionHistorySnapshotComparer();
            Assert.Empty(comparer.Compare(lightweight.Payload, review.Payload).GetArea("sources").Entries);
            var exact = new VersionHistoryCompareOptions
            {
                UnboundedReadableTextEntry = new("sources", "sources", sourceId.ToString("N")),
            };
            Assert.Throws<InvalidOperationException>(() => comparer.Compare(review.Payload, expanded.Payload, exact));
            Assert.Empty(comparer.Compare(lightweight.Payload, expanded.Payload, exact).GetArea("sources").Entries);
            var changedInactive = lightweight.Payload with
            {
                Sources = new VersionHistorySnapshotSourcesArea([retained with
                {
                    Extractions = [retained.Extractions[0], retained.Extractions[0] with { Id = Guid.NewGuid(), Ordinal = 1 }],
                }]),
            };
            Assert.Single(comparer.Compare(review.Payload, changedInactive).GetArea("sources").Entries);

            var restore = new VersionHistorySnapshotReader().Read(root, repositoryId, projectId,
                new VersionHistorySnapshotReadOptions { IncludeSourceOriginalBlobs = true });
            using var source = restore.Payload.SourceOriginalBlobs[hash].OpenRead();
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
            Assert.Equal(normalized, Assert.Single(restore.Payload.Sources.RetainedSources).Extractions.Single().NormalizedText);
            File.AppendAllText(Path.Combine(root, "sources", sourceId.ToString("N"), "source.json"), " ");
            Assert.Throws<InvalidDataException>(() => restore.Payload.Sources.RetainedSources[0]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(SourceExtractionStatus.Extracting)]
    [InlineData(SourceExtractionStatus.Failed)]
    public void RetainedSourceWithoutActiveExtractionPreservesOriginalAndRejectsInvalidClaims(SourceExtractionStatus status)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper", Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Encoding.UTF8.GetBytes("original retained before preprocessing");
            var hash = VersionHistoryCanonicalJson.Sha256Hex(bytes);
            var extraction = new VersionHistorySourceExtraction(Guid.NewGuid(), 0,
                "book-artifact-preprocessor", "m4.1", "{}", SourceRetentionValidator.Sha256(string.Empty),
                status, string.Empty, string.Empty, [], [], []);
            var retained = new VersionHistoryRetainedSource(Guid.NewGuid(), "Source", "artifact", "", "", "",
                "", "", "", "application/pdf", "{}", null,
                new VersionHistorySourceOriginal(SourceOriginalState.Available, "source.pdf", "application/pdf",
                    bytes.Length, hash, [new VersionHistorySourceOriginalChunk(Guid.NewGuid(), 0, hash, bytes.Length)]),
                [extraction], [], []);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid()) with
            {
                Sources = new VersionHistorySnapshotSourcesArea([retained]),
            };
            var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [hash] = bytes };
            WriteSnapshotTree(root, payload, sourceOriginalData: originals);
            var reader = new VersionHistorySnapshotReader();
            var restored = reader.Read(root, options: new() { IncludeSourceOriginalBlobs = true }).Payload;
            var source = Assert.Single(restored.Sources.RetainedSources);
            Assert.Null(source.ActiveExtractionVersionId);
            Assert.Equal(status, Assert.Single(source.Extractions).Status);
            using (var original = restored.SourceOriginalBlobs[hash].OpenRead())
            using (var copy = new MemoryStream())
            {
                original.CopyTo(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
            var review = reader.Read(root, options: new() { IncludeSourceDetails = false }).Payload;
            Assert.Equal(string.Empty, Assert.Single(review.Sources.ReviewSummaries!).ReadableText);
            Assert.Empty(new VersionHistorySnapshotComparer().Compare(restored, review).GetArea("sources").Entries);

            foreach (var invalid in new[]
            {
                retained with { ActiveExtractionVersionId = extraction.Id },
                retained with { ActiveExtractionVersionId = Guid.NewGuid() },
                retained with { Extractions = [] },
                retained with { Original = retained.Original with { Length = bytes.Length + 1 } },
            })
            {
                WriteSnapshotTree(root, payload with { Sources = new VersionHistorySnapshotSourcesArea([invalid]) },
                    sourceOriginalData: originals);
                Assert.Throws<InvalidDataException>(() => reader.Read(root));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void PredecessorSourceAdaptsAtTheReaderBoundary(int schemaVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper", Guid.NewGuid().ToString("N"));
        try
        {
            var sourceId = Guid.NewGuid();
            var legacy = new ProjectExportIngestSource(sourceId, "Legacy", "text", "", "Synopsis", "",
                "Preserved text", SourceRetentionValidator.Sha256("Preserved text"), "", "", "", null,
                "text/plain", "{}", DateTime.UnixEpoch, DateTime.UnixEpoch, [], [], []);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid());
            WriteSnapshotTree(root, payload, (path, bytes) => path == "sources/sources.json"
                ? VersionHistoryCanonicalJson.Serialize(new { Sources = new[] { legacy } }) : bytes, schemaVersion);
            var restored = new VersionHistorySnapshotReader().Read(root).Payload;
            var source = Assert.Single(restored.Sources.RetainedSources);
            Assert.Equal(sourceId, source.Id);
            Assert.Equal(SourceOriginalState.OriginalUnavailable, source.Original.State);
            Assert.Equal("Preserved text", Assert.Single(source.Extractions).NormalizedText);
            Assert.Equal(SourceExtractionStatus.LegacyImmutable, source.Extractions[0].Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreAssetReadsRevalidateFilesAndHonorCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper", Guid.NewGuid().ToString("N"));
        try
        {
            var imageId = Guid.NewGuid();
            byte[] bytes = [1, 2, 3];
            var image = new VersionHistoryImageAsset(imageId, "image.png", "image/png",
                $"assets/images/{imageId:N}/content.png", VersionHistoryCanonicalJson.Sha256Hex(bytes), bytes.Length,
                "Description", PublishAssetSource.Uploaded, "", "", "{}", null, null, null, null, null);
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid()) with
            {
                Assets = new VersionHistorySnapshotAssetsArea([image], [], []),
                ImageData = new Dictionary<Guid, byte[]> { [imageId] = bytes },
            };
            WriteSnapshotTree(root, payload);
            using var cancellation = new CancellationTokenSource();
            var reader = new VersionHistorySnapshotReader();
            var restored = reader.Read(root, options: new()
            {
                IncludeSourceOriginalBlobs = true, CancellationToken = cancellation.Token,
            }).Payload;
            Assert.Equal(bytes, restored.ImageData[imageId]);
            File.WriteAllBytes(Path.Combine(root, image.BlobPath), [3, 2, 1]);
            Assert.Throws<InvalidDataException>(() => restored.ImageData[imageId]);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => restored.ImageData[imageId]);
            Assert.Throws<OperationCanceledException>(() => reader.Read(root, options: new() { CancellationToken = cancellation.Token }));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Schema8ReaderUpgradesItsDirectV5ManuscriptAfterValidatingThePredecessor()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chapter = CreateChapter(Guid.NewGuid(), "Rich predecessor");
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: chapter);
            WriteSnapshotTree(
                root,
                payload,
                (path, bytes) =>
                {
                    if (!path.EndsWith("/manuscript.json", StringComparison.Ordinal))
                        return bytes;
                    var manuscript = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
                    manuscript["schemaVersion"] = 5;
                    manuscript.Remove("notes");
                    return VersionHistoryCanonicalJson.SerializeDirectManuscript(manuscript.ToJsonString());
                },
                schemaVersion: 8);

            var restored = new VersionHistorySnapshotReader()
                .Read(root, payload.RepositoryId, payload.ProjectId)
                .Payload.Narrative.Chapters.Single();
            var manuscript = ManuscriptCodec.Deserialize(
                restored.ManuscriptJson,
                chapter.Id,
                chapter.ManuscriptRevision);

            Assert.Equal(ManuscriptDocument.CurrentSchemaVersion, manuscript.SchemaVersion);
            Assert.Empty(manuscript.Notes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Schema7DesignedPageRestoreSelectsCurrentDataWithoutLegacyCompositionData()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var chapter = CreateChapter(chapterId, "Chapter") with
        {
            ManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 1,
                Content = [new ManuscriptBlock
                {
                    Id = "page-placement",
                    Type = ManuscriptBlockType.DesignedPage,
                    StyleRole = ManuscriptStyleRoles.DesignedPage,
                    DesignedPageId = pageId,
                }],
            }),
            ManuscriptRevision = 1,
        };
        var current = CreatePayload(repositoryId, projectId, chapter: chapter);
        var historical = current with
        {
            Composition = new VersionHistorySnapshotCompositionArea(
            [
                new ProjectExportDesignedPage(pageId, "Restored page", null,
                [
                    new ProjectExportDesignedPageContent(contentId, pageId, null, "{}", "", 2, [], null),
                ]),
            ]),
        };

        var merged = Merge(current, historical, VersionHistoryRestoreSelection.ForMajorAreas(["composition"]));

        Assert.Empty(merged.Composition.PageCompositions);
        var page = Assert.Single(merged.Composition.DesignedPages);
        Assert.Equal(pageId, page.Id);
        Assert.Equal(contentId, Assert.Single(page.Contents).Id);
    }

    [Fact]
    public void HistoryRestoreIndexesCoreAndReleasePlacementsWithTheSameBlockId()
    {
        var projectId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        const string blockId = "reused-placement-block";
        var document = new ManuscriptDocument
        {
            ManuscriptId = chapterId,
            Revision = 2,
            Content = [new ManuscriptBlock
            {
                Id = blockId,
                Type = ManuscriptBlockType.DesignedPage,
                StyleRole = ManuscriptStyleRoles.DesignedPage,
                DesignedPageId = pageId,
            }],
        };
        var chapter = CreateChapter(chapterId, "Chapter") with
        {
            ManuscriptJson = ManuscriptCodec.Serialize(document),
            ManuscriptRevision = document.Revision,
        };
        var edition = new ProjectExportPublicationEdition(
            editionId, "Release", PublicationEditionFormat.DigitalPdf, PublicationVendor.Generic,
            string.Empty, PublicationEditionStatus.Draft, false, 1,
            string.Empty, string.Empty, string.Empty, "en", string.Empty, string.Empty, string.Empty, string.Empty,
            true, false, false, false, false, true, false, false,
            PublishTitlePageMode.Automatic, 6, 9, 0.75, null,
            default, default, default, false, false, [], null)
        {
            ChapterOverrides = [new ProjectExportEditionChapterOverride(
                Guid.NewGuid(), chapterId, ManuscriptCodec.Serialize(document), document.Revision, 1, "base",
                DateTime.UnixEpoch, DateTime.UnixEpoch)],
        };
        var payload = CreatePayload(repositoryId, projectId, chapter: chapter) with
        {
            Composition = new VersionHistorySnapshotCompositionArea([DesignedPage(pageId, "Shared", 1)]),
            Publication = new VersionHistorySnapshotPublicationArea(null, [edition], []),
        };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);

        ProjectVersionRestoreService.AddDesignedPagePlacements(db, projectId, payload);

        var references = db.ChangeTracker.Entries<DesignedPagePlacementReference>()
            .Select(entry => entry.Entity).OrderBy(reference => reference.EditionId).ToList();
        Assert.Equal(2, references.Count);
        Assert.All(references, reference => Assert.Equal(blockId, reference.Id));
        Assert.Contains(references, reference => reference.EditionId is null && reference.ContainerId == chapterId);
        Assert.Contains(references, reference => reference.EditionId == editionId && reference.ContainerId == chapterId);
    }

    [Fact]
    public void ReleaseClonePageContentAndPlacementRemapPreserveIsolation()
    {
        var sourcePageId = Guid.NewGuid();
        var clonePageId = Guid.NewGuid();
        var sourceEditionId = Guid.NewGuid();
        var cloneEditionId = Guid.NewGuid();
        var sourceVariantId = Guid.NewGuid();
        var source = new DesignedPageContent
        {
            ProjectId = Guid.NewGuid(),
            DesignedPageId = sourcePageId,
            EditionId = sourceEditionId,
            SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(Guid.NewGuid())),
            AccessibilityDescription = "A release-only page.",
            Revision = 4,
            ActiveVariantId = sourceVariantId,
            Variants =
            [
                new DesignedPageVariant
                {
                    Id = sourceVariantId,
                    GeometryKey = "6x9",
                    SceneJson = "{}",
                    Revision = 3,
                },
            ],
        };

        var cloned = PublicationEditionService.CloneDesignedPageContent(source, clonePageId, cloneEditionId);
        Assert.Equal(clonePageId, cloned.DesignedPageId);
        Assert.Equal(cloneEditionId, cloned.EditionId);
        Assert.NotEqual(source.Id, cloned.Id);
        Assert.Equal(source.AccessibilityDescription, cloned.AccessibilityDescription);
        var clonedVariant = Assert.Single(cloned.Variants);
        Assert.NotEqual(sourceVariantId, clonedVariant.Id);
        Assert.Equal(clonedVariant.Id, cloned.ActiveVariantId);

        var manuscriptId = Guid.NewGuid();
        var remappedJson = PublicationEditionService.RemapDesignedPagePlacements(
            ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = manuscriptId,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "release-page-placement",
                        Type = ManuscriptBlockType.DesignedPage,
                        StyleRole = ManuscriptStyleRoles.DesignedPage,
                        DesignedPageId = sourcePageId,
                    },
                ],
            }),
            manuscriptId,
            0,
            new Dictionary<Guid, Guid> { [sourcePageId] = clonePageId });
        var remapped = ManuscriptCodec.Deserialize(remappedJson, manuscriptId, 0);
        Assert.Equal(clonePageId, Assert.Single(remapped.Content).DesignedPageId);

        var importedContentId = Guid.NewGuid();
        var rehomed = ProjectImportJobProcessor.RehomeImportedEditionContent(
            ManuscriptCodec.CreateEmpty(Guid.NewGuid()),
            importedContentId,
            new Dictionary<string, string>());
        Assert.Equal(importedContentId, rehomed.ManuscriptId);
    }

    [Fact]
    public void Schema7SelectiveDesignedPageRestoreReplacesOnlyTheIndependentPage()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var unrelatedPageId = Guid.NewGuid();
        var chapter = CreateChapter(chapterId, "Chapter") with
        {
            ManuscriptJson = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Content = [new ManuscriptBlock
                {
                    Id = "shared-placement",
                    Type = ManuscriptBlockType.DesignedPage,
                    StyleRole = ManuscriptStyleRoles.DesignedPage,
                    DesignedPageId = pageId,
                }],
            }),
        };
        var current = CreatePayload(repositoryId, projectId, chapter: chapter) with
        {
            Composition = new VersionHistorySnapshotCompositionArea(
            [
                DesignedPage(pageId, "Current", 3),
                DesignedPage(unrelatedPageId, "Unrelated", 8),
            ]),
        };
        var source = current with
        {
            Composition = new VersionHistorySnapshotCompositionArea(
            [
                DesignedPage(pageId, "Historical", 1),
                DesignedPage(unrelatedPageId, "Unrelated", 8),
            ]),
        };
        var method = typeof(ProjectVersionRestoreService).GetMethod(
            "SynthesizeDesignedPageRestore", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ProjectVersionRestoreService).FullName, "SynthesizeDesignedPageRestore");
        var restored = (VersionHistorySnapshotPayload)(method.Invoke(null,
            [source, current, pageId])
            ?? throw new InvalidOperationException("The designed page restore returned no payload."));

        Assert.Empty(restored.Composition.PageCompositions);
        Assert.Equal("Historical", restored.Composition.DesignedPages.Single(page => page.Id == pageId).Name);
        Assert.Equal(8, Assert.Single(restored.Composition.DesignedPages.Single(page => page.Id == unrelatedPageId).Contents).Revision);
    }

    [Fact]
    public void SelectiveRestoreChoosesCurrentOrTargetAssetBytesAndHashes()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        byte[] currentData = [1, 2, 3];
        byte[] targetData = [7, 8, 9, 10];
        var currentImage = new VersionHistoryImageAsset(
            imageId,
            "current.png",
            "image/png",
            $"assets/images/{imageId:N}/content.png",
            VersionHistoryCanonicalJson.Sha256Hex(currentData),
            currentData.LongLength,
            "Current",
            PublishAssetSource.Uploaded,
            string.Empty,
            string.Empty,
            "{}",
            null,
            null,
            null,
            null,
            null);
        var targetImage = currentImage with
        {
            FileName = "target.png",
            Sha256 = VersionHistoryCanonicalJson.Sha256Hex(targetData),
            ByteLength = targetData.LongLength,
        };
        var current = CreatePayload(repositoryId, projectId) with
        {
            Assets = new VersionHistorySnapshotAssetsArea([currentImage], [], []),
            ImageData = new Dictionary<Guid, byte[]> { [imageId] = currentData },
        };
        var target = CreatePayload(repositoryId, projectId) with
        {
            Assets = new VersionHistorySnapshotAssetsArea([targetImage], [], []),
            ImageData = new Dictionary<Guid, byte[]> { [imageId] = targetData },
        };

        var assetsRestored = Merge(current, target, VersionHistoryRestoreSelection.ForMajorAreas(["assets"]));
        var assetsUnchanged = Merge(current, target, VersionHistoryRestoreSelection.ForMajorAreas(["narrative"]));

        Assert.Equal(targetData, assetsRestored.ImageData[imageId]);
        Assert.Equal(targetImage.Sha256, Assert.Single(assetsRestored.Assets.Images).Sha256);
        Assert.Equal(currentData, assetsUnchanged.ImageData[imageId]);
        Assert.Equal(currentImage.Sha256, Assert.Single(assetsUnchanged.Assets.Images).Sha256);
    }

    [Fact]
    public void SelectedChapterMergeReplacesAddsRemovesAndRestoresOnlySelectedAnnotations()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var currentChapterId = Guid.NewGuid();
        var removedChapterId = Guid.NewGuid();
        var unrelatedChapterId = Guid.NewGuid();
        var addedChapterId = Guid.NewGuid();
        var current = CreatePayload(
            repositoryId,
            projectId,
            chapters:
            [
                CreateChapter(currentChapterId, "Current title"),
                CreateChapter(removedChapterId, "Removed title"),
                CreateChapter(unrelatedChapterId, "Unrelated title"),
            ],
            annotations:
            [
                CreateAnnotation(Guid.NewGuid(), currentChapterId, "Current annotation"),
                CreateAnnotation(Guid.NewGuid(), removedChapterId, "Removed annotation"),
                CreateAnnotation(Guid.NewGuid(), unrelatedChapterId, "Unrelated annotation"),
            ]);
        var target = CreatePayload(
            repositoryId,
            projectId,
            chapters:
            [
                CreateChapter(currentChapterId, "Historical replacement"),
                CreateChapter(addedChapterId, "Added title"),
            ],
            annotations:
            [
                CreateAnnotation(Guid.NewGuid(), currentChapterId, "Historical annotation"),
                CreateAnnotation(Guid.NewGuid(), addedChapterId, "Added annotation"),
            ]);

        var merged = Merge(
            current,
            target,
            VersionHistoryRestoreSelection.ForSelectedChapters(
                [currentChapterId, removedChapterId, addedChapterId],
                VersionHistoryAnnotationRestoreMode.SelectedChapterAnnotations));

        Assert.Equal(
            new[] { currentChapterId, unrelatedChapterId, addedChapterId }.OrderBy(id => id),
            merged.Narrative.Chapters.Select(item => item.Id));
        Assert.Equal("Historical replacement", merged.Narrative.Chapters.Single(item => item.Id == currentChapterId).Title);
        Assert.Equal("Unrelated title", merged.Narrative.Chapters.Single(item => item.Id == unrelatedChapterId).Title);
        Assert.Equal(
            new[] { currentChapterId, unrelatedChapterId, addedChapterId }.OrderBy(id => id),
            merged.Narrative.Annotations.Select(item => item.ChapterId).OrderBy(id => id));
        Assert.Equal("Historical annotation", merged.Narrative.Annotations.Single(item => item.ChapterId == currentChapterId).NoteText);
        Assert.Equal("Unrelated annotation", merged.Narrative.Annotations.Single(item => item.ChapterId == unrelatedChapterId).NoteText);
    }

    [Fact]
    public async Task UnsafeCrossAreaDependencyFailsClosedBeforeMutatingLiveProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "restore-invalid.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                setup.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Live project",
                    Slug = "live-project",
                });
                await setup.SaveChangesAsync();
            }

            var invalidPayload = CreatePayload(
                repositoryId,
                projectId,
                graph: new VersionHistorySnapshotGraphArea(
                    [],
                    [new ProjectExportEdge(
                        new ProjectExportNodeRef("Character", "missing-from-snapshot"),
                        new ProjectExportNodeRef("Character", "also-missing"),
                        "related",
                        [],
                        null,
                        DateTime.UnixEpoch,
                        DateTime.UnixEpoch)]));
            var manifest = new VersionHistorySnapshotManifest(
                VersionHistorySnapshotContract.FormatId,
                VersionHistorySnapshotContract.SchemaVersion,
                repositoryId,
                projectId,
                VersionHistorySnapshotContract.IncludedAreas,
                new string('c', 64),
                new string('d', 64),
                []);
            var loaded = new ProjectVersionLoadedCheckpoint(
                new GitCommitMetadata(
                    new string('a', 40),
                    new string('b', 40),
                    "invalid dependency",
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    []),
                manifest,
                invalidPayload,
                null);
            var service = new ProjectVersionRestoreService(
                new StubHistoryService(loaded),
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                authoringFence: new PassthroughAuthoringMutationFence());

            var exception = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.RestoreAsync(
                    projectId,
                    new string('a', 40),
                    VersionHistoryRestoreSelection.ForWholeProject()));

            Assert.Equal("MissingGraphDependency", exception.Code);
            await using var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            var project = await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
            Assert.Equal("Live project", project.Name);
            Assert.Equal("live-project", project.Slug);
            Assert.Equal(0, await verify.ProjectVersionCheckpoints.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRefusesQueuedWorkAndForceDiscardsBlockedOperationalRows(bool withCitations)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "restore-queued-work.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var headCommit = new string('a', 40);
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                var project = new Project
                {
                    Id = projectId,
                    Name = "Live project",
                    Slug = "live-project",
                };
                setup.Projects.Add(project);
                setup.ProjectVersionRepositories.Add(new ProjectVersionRepository
                {
                    Id = repositoryId,
                    ProjectId = projectId,
                    Project = project,
                    CreativeRevision = 1,
                    HeadCommitSha = headCommit,
                });
                var source = new IngestSource
                {
                    ProjectId = projectId,
                    Title = "Blocked source",
                    UserInstructions = "Ingest instructions",
                };
                var extraction = new SourceExtractionVersion
                {
                    Id = source.Id,
                    SourceId = source.Id,
                    Source = source,
                    Ordinal = 0,
                    Extractor = "test",
                    ExtractorVersion = "1",
                    ContentHash = SourceRetentionValidator.Sha256("Source text"),
                    Status = SourceExtractionStatus.LegacyImmutable,
                    NormalizedText = "Source text",
                };
                source.ActiveExtractionVersionId = extraction.Id;
                source.ExtractionVersions.Add(extraction);
                source.Original = new SourceOriginal
                {
                    SourceId = source.Id,
                    Source = source,
                    State = SourceOriginalState.OriginalUnavailable,
                    FileName = "source.txt",
                    MediaType = "text/plain",
                };
                setup.IngestSources.Add(source);
                setup.IngestJobs.Add(new IngestJob
                {
                    ProjectId = projectId,
                    SourceId = source.Id,
                    Source = source,
                    Instructions = "Ingest",
                    Status = IngestJobStatus.StopRequested,
                });
                setup.ContestBatches.Add(new ContestBatch
                {
                    ProjectId = projectId,
                    ChapterId = Guid.NewGuid(),
                    OriginalManuscriptRevision = 1,
                    OriginalManuscriptHash = "hash",
                    Status = ContestBatchStatus.Completed,
                });
                setup.ProjectVersionOperations.Add(new ProjectVersionOperation
                {
                    ProjectVersionRepositoryId = repositoryId,
                    Kind = ProjectVersionOperationKind.Checkpoint,
                    Status = ProjectVersionOperationStatus.Running,
                });
                await setup.SaveChangesAsync();
            }

            var recordId = Guid.NewGuid();
            var manuscript = CitationPreservationTests.Document(recordId, null);
            var payload = CreatePayload(repositoryId, projectId);
            payload = payload with { Narrative = payload.Narrative with { WorldBrief = "Saved world principles." } };
            if (withCitations)
            {
                payload = payload with
                {
                    Narrative = payload.Narrative with
                    {
                        Chapters = [CreateChapter(manuscript.ManuscriptId, "Cited chapter") with
                        {
                            ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                        }],
                    },
                    Sources = payload.Sources with
                    {
                        RetainedSources = Enumerable.Range(0, 2).Select(index =>
                        {
                            var sourceId = Guid.NewGuid();
                            var extractionId = Guid.NewGuid();
                            return new VersionHistoryRetainedSource(sourceId, $"Retained {index}", "text", "", "", "",
                                "", "", "", "text/plain", "{}", extractionId,
                                new VersionHistorySourceOriginal(SourceOriginalState.OriginalUnavailable, "source.txt", "text/plain", 0, null, []),
                                [new VersionHistorySourceExtraction(extractionId, 0, "test", "1", "{}", SourceRetentionValidator.Sha256("Retained text"),
                                    SourceExtractionStatus.Ready, "", "Retained text", [], [], [])], [], []);
                        }).ToList(),
                        UnlinkedBibliographicRecords = [new VersionHistoryBibliographicRecord(
                            recordId, null, BibliographicRecordKind.Book, "Preserved source", "",
                            "[]", "[]", 2024, "Publisher", "", "", "", "", "", "", null,
                            "", "Preserved notes", "[]", 2, 29, "Second", "", "", 2024, 3, 1)],
                    },
                    Publication = payload.Publication with
                    {
                        PublicationBook = new ProjectExportPublicationBook(
                            1, "Book", "", "Author", "en", "", "", "", true, false,
                            false, false, true, true, false, false, PublishTitlePageMode.Automatic, [], null)
                        { CitationStyle = Lorekeeper.Citations.CitationStyle.APA7 },
                    },
                };
                var snapshotRoot = Path.Combine(root, "citation-snapshot");
                Assert.Contains(new VersionHistorySnapshotComparer().Compare(CreatePayload(repositoryId, projectId), payload)
                    .GetArea("sources").Entries, entry => entry.Category == "bibliography");
                WriteSnapshotTree(snapshotRoot, payload);
                payload = new VersionHistorySnapshotReader().Read(snapshotRoot, repositoryId, projectId,
                    new() { IncludeSourceOriginalBlobs = true }).Payload;
            }
            var loaded = new ProjectVersionLoadedCheckpoint(
                new GitCommitMetadata(
                    headCommit,
                    new string('b', 40),
                    "target checkpoint",
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    []),
                new VersionHistorySnapshotManifest(
                    VersionHistorySnapshotContract.FormatId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    repositoryId,
                    projectId,
                    VersionHistorySnapshotContract.IncludedAreas,
                    new string('c', 64),
                    new string('d', 64),
                    []),
                payload,
                null);
            var history = new StubHistoryService(loaded)
            {
                Status = new ProjectVersionStatusView(
                    new ProjectVersionRepositoryView(
                        projectId,
                        repositoryId,
                        1,
                        null,
                        null,
                        headCommit,
                        null,
                        null,
                        ProjectVersionRepositoryHealth.Healthy,
                        false,
                        null),
                    null),
                CheckpointFactory = () => new ProjectVersionCheckpointView(
                    Guid.NewGuid(),
                    repositoryId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    2,
                    new string('e', 64),
                    new string('f', 64),
                    headCommit,
                    null,
                    ProjectVersionCheckpointKind.Manual,
                    ProjectVersionCheckpointSource.Local,
                    "test",
                    DateTime.UtcNow),
            };
            var mutation = new ProjectMutationCoordinator($"Data Source={databasePath}");
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                mutation);
            var service = new ProjectVersionRestoreService(
                history,
                database,
                mutation,
                new NoopOutlineGraphSync(),
                new NoopContextIndexingService(),
                new NoopIngestGraphSync(),
                new NoopGraphStore(),
                new NoopGraphAutoLinkService(),
                new NoopProjectSearchIndex(),
                authoringFence: new PassthroughAuthoringMutationFence());

            var exception = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.RestoreAsync(
                    projectId,
                    headCommit,
                    VersionHistoryRestoreSelection.ForWholeProject()));

            Assert.Equal("WorkInProgress", exception.Code);
            Assert.Equal("Restore is refused while queued or running project work exists.", exception.Message);
            Assert.NotNull(exception.Blockers);
            Assert.Equal(3, exception.Blockers!.Count);
            await using (var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal(1, await verify.IngestJobs.CountAsync());
                Assert.Equal(1, await verify.ContestBatches.CountAsync());
                Assert.Equal(1, await verify.ProjectVersionOperations.CountAsync());
                Assert.Equal("Live project", (await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId)).Name);
            }

            var result = await service.RestoreAsync(
                projectId,
                headCommit,
                VersionHistoryRestoreSelection.ForWholeProject(),
                discardQueuedWork: true);

            Assert.Equal(projectId, result.ProjectId);
            await using (var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal(0, await verify.IngestJobs.CountAsync());
                Assert.Equal(0, await verify.ContestBatches.CountAsync());
                Assert.Equal(0, await verify.ProjectVersionOperations.CountAsync());
                Assert.Equal("Project", (await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId)).Name);
                Assert.Equal("Saved world principles.", (await verify.WorldBriefs.SingleAsync(item => item.ProjectId == projectId)).Content);
                if (withCitations)
                {
                    Assert.Equal(2, await verify.IngestSources.CountAsync(item => item.ProjectId == projectId));
                    Assert.Equal(2, await verify.SourceExtractionVersions.CountAsync(item => item.NormalizedText == "Retained text"));
                    var restored = await verify.Chapters.SingleAsync(item => item.ProjectId == projectId);
                    var citations = ManuscriptTraversal.EnumerateCitations(ManuscriptCodec.Deserialize(restored.ManuscriptJson))
                        .SelectMany(item => item.Cluster.Items).ToList();
                    Assert.Equal(3, citations.Count);
                    Assert.All(citations, item => Assert.Equal(recordId, item.BibliographicRecordId));
                    var record = await verify.BibliographicRecords.SingleAsync(item => item.ProjectId == projectId);
                    Assert.Equal("Preserved notes", record.Notes);
                    Assert.Equal(29, record.IssuedDay);
                    Assert.Equal(2024, record.AccessedYear);
                    Assert.Equal(Lorekeeper.Citations.CitationStyle.APA7,
                        (await verify.PublicationBooks.SingleAsync(item => item.ProjectId == projectId)).CitationStyle);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckoutRequiresExactHeadAndRecordsOnlyTheImportedCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "checkout.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var oldCommit = new string('a', 40);
            var targetCommit = new string('b', 40);
            var targetTree = new string('c', 40);
            var payload = CreatePayload(repositoryId, projectId, projectName: "Remote project");
            var artifact = new VersionHistorySnapshotArtifact(
                root,
                new VersionHistorySnapshotManifest(
                    VersionHistorySnapshotContract.FormatId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    repositoryId,
                    projectId,
                    VersionHistorySnapshotContract.IncludedAreas,
                    new string('d', 64),
                    new string('e', 64),
                    []),
                payload);
            var loaded = new ProjectVersionLoadedCheckpoint(
                new GitCommitMetadata(
                    targetCommit,
                    targetTree,
                    "remote",
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    []),
                artifact.Manifest,
                payload,
                null);
            var history = new StubHistoryService(loaded)
            {
                Status = new ProjectVersionStatusView(
                    new ProjectVersionRepositoryView(
                        projectId,
                        repositoryId,
                        1,
                        null,
                        null,
                        oldCommit,
                        null,
                        null,
                        ProjectVersionRepositoryHealth.Healthy,
                        false,
                        null),
                    null),
            };

            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                var project = new Project
                {
                    Id = projectId,
                    Name = "Live project",
                    Slug = "live-project",
                };
                setup.Projects.Add(project);
                setup.ProjectVersionRepositories.Add(new ProjectVersionRepository
                {
                    Id = repositoryId,
                    ProjectId = projectId,
                    Project = project,
                    CreativeRevision = 1,
                    HeadCommitSha = oldCommit,
                });
                await setup.SaveChangesAsync();
            }

            var mutation = new ProjectMutationCoordinator($"Data Source={databasePath}");
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                mutation);
            var service = new ProjectVersionRestoreService(
                history,
                database,
                mutation,
                new NoopOutlineGraphSync(),
                new NoopContextIndexingService(),
                new NoopIngestGraphSync(),
                new NoopGraphStore(),
                new NoopGraphAutoLinkService(),
                new NoopProjectSearchIndex(),
                authoringFence: new PassthroughAuthoringMutationFence());
            var checkout = new VersionHistoryValidatedProjectCheckout(
                projectId,
                artifact,
                targetCommit,
                targetTree);

            var mismatch = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.CheckoutValidatedSnapshotAsync(checkout));
            Assert.Equal("CheckoutHeadMismatch", mismatch.Code);
            Assert.Equal(0, history.CreateCheckpointCalls);

            history.Status = new ProjectVersionStatusView(
                new ProjectVersionRepositoryView(
                    projectId,
                    repositoryId,
                    1,
                    null,
                    null,
                    targetCommit,
                    artifact.Manifest.ContentHash,
                    null,
                    ProjectVersionRepositoryHealth.Healthy,
                    false,
                    null),
                null);
            await using (var advance = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await advance.ProjectVersionRepositories
                    .Where(item => item.Id == repositoryId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.HeadCommitSha, targetCommit)
                        .SetProperty(item => item.HeadContentHash, artifact.Manifest.ContentHash));
            }
            await using (var advanced = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var repository = await advanced.ProjectVersionRepositories.AsNoTracking()
                    .SingleAsync(item => item.Id == repositoryId);
                Assert.Equal(targetCommit, repository.HeadCommitSha);
                Assert.Equal(artifact.Manifest.ContentHash, repository.HeadContentHash);
            }
            var result = await service.CheckoutValidatedSnapshotAsync(checkout);

            Assert.True(result.CheckpointCreated);
            Assert.Equal(targetCommit, result.HeadCommitSha);
            Assert.Equal(0, history.CreateCheckpointCalls);
            await using var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            Assert.Equal("Remote project", (await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId)).Name);
            Assert.Equal(1, await verify.ProjectVersionCheckpoints.CountAsync(item => item.ProjectVersionRepositoryId == repositoryId));
            Assert.Equal(ProjectVersionCheckpointSource.Remote, await verify.ProjectVersionCheckpoints.Select(item => item.Source).SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPreservesStableCreativeIdsAndRefusesIdentityCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "restore.db")}")
                .Options;
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await setup.Database.MigrateAsync();

            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var actId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var chapterManuscript = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId));
            var artifact = new VersionHistorySnapshotArtifact(
                root,
                new VersionHistorySnapshotManifest(
                    VersionHistorySnapshotContract.FormatId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    repositoryId,
                    projectId,
                    VersionHistorySnapshotContract.IncludedAreas,
                    new string('c', 64),
                    new string('d', 64),
                    []),
                new VersionHistorySnapshotPayload(
                    repositoryId,
                    projectId,
                    new VersionHistorySnapshotProjectArea(
                        new ProjectExportProject(projectId, "Imported project", "imported-project", "guidance", true, false)
                        {
                            // Schema-v1 snapshots may contain the former
                            // workflow field, but import must not restore it.
                            LegacyAiChangeApprovalEnabled = true,
                        },
                        null,
                        false,
                        []),
                    new VersionHistorySnapshotNarrativeArea(
                        null,
                        [],
                        [],
                        [new ProjectExportAct(actId, "Act one", "Synopsis", 0)],
                        [new ProjectExportChapter
                        {
                            Id = chapterId,
                            ActId = actId,
                            Title = "Chapter one",
                            ManuscriptJson = chapterManuscript,
                            ManuscriptRevision = 0,
                            Synopsis = "Chapter synopsis",
                            Order = 0,
                        }],
                        [],
                        [],
                        []),
                    new VersionHistorySnapshotGraphArea([], []),
                    new VersionHistorySnapshotSourcesArea([]),
                    new VersionHistorySnapshotAssetsArea([], [], []),
                    new VersionHistorySnapshotManuscriptArea([]),
                    new VersionHistorySnapshotCompositionArea([]),
                    new VersionHistorySnapshotPublicationArea(null, [], [])));

            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator($"Data Source={Path.Combine(root, "restore.db")}"));
            var service = new ProjectVersionRestoreService(
                null!,
                database,
                new ProjectMutationCoordinator($"Data Source={Path.Combine(root, "restore.db")}"),
                new NoopOutlineGraphSync(),
                new NoopContextIndexingService(),
                new NoopIngestGraphSync(),
                new NoopGraphStore(),
                new NoopGraphAutoLinkService(),
                new NoopProjectSearchIndex(),
                authoringFence: new PassthroughAuthoringMutationFence());
            var import = new VersionHistoryValidatedSnapshotImport(
                artifact,
                new string('a', 40),
                new string('b', 40));

            var result = await service.ImportValidatedSnapshotAsync(import);

            Assert.Equal(projectId, result.ProjectId);
            Assert.Equal(repositoryId, result.RepositoryId);
            Assert.Equal(ProjectVersionCheckpointKind.Imported, result.ImportedCheckpoint.Kind);
            await using (var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var project = await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
                Assert.Equal("Imported project", project.Name);
                Assert.Equal("imported-project", project.Slug);
                Assert.False(project.ReviewEditsEnabled);
                Assert.Equal(actId, await verify.Acts.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(chapterId, await verify.Chapters.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(repositoryId, await verify.ProjectVersionRepositories.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(new string('a', 40), await verify.ProjectVersionCheckpoints.Where(item => item.ProjectVersionRepositoryId == repositoryId).Select(item => item.CommitSha).SingleAsync());
            }

            var collision = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.ImportValidatedSnapshotAsync(import));
            Assert.Equal("ProjectIdentityCollision", collision.Code);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static VersionHistorySnapshotPayload Merge(
        VersionHistorySnapshotPayload current,
        VersionHistorySnapshotPayload target,
        VersionHistoryRestoreSelection selection)
    {
        var method = typeof(ProjectVersionRestoreService).GetMethod(
            "MergePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ProjectVersionRestoreService).FullName, "MergePayload");
        return (VersionHistorySnapshotPayload)(method.Invoke(null, [current, target, selection])
            ?? throw new InvalidOperationException("The restore merge returned no payload."));
    }

    private static void WriteSnapshotTree(
        string root,
        VersionHistorySnapshotPayload payload,
        Func<string, byte[], byte[]>? transform = null,
        int? schemaVersion = null,
        IReadOnlyDictionary<string, byte[]>? sourceOriginalData = null)
    {
        var effectiveSchemaVersion = schemaVersion ?? VersionHistorySnapshotContract.SchemaVersion;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["project/project.json"] = VersionHistoryCanonicalJson.Serialize(payload.Project),
            ["narrative/narrative.json"] = effectiveSchemaVersion < 11
                ? VersionHistoryCanonicalJson.Serialize(new VersionHistorySnapshotNarrativeFileV10(
                    payload.Narrative.BookBrief, payload.Narrative.BookBriefCanonSourceIds, payload.Narrative.EntityTypes,
                    payload.Narrative.Acts, payload.Narrative.WritingSamples, payload.Narrative.ContextPreferences, payload.Narrative.Annotations))
                : VersionHistoryCanonicalJson.Serialize(VersionHistorySnapshotNarrativeFile.FromArea(payload.Narrative)),
            ["graph/graph.json"] = VersionHistoryCanonicalJson.Serialize(payload.Graph),
            ["assets/assets.json"] = VersionHistoryCanonicalJson.Serialize(payload.Assets),
            ["manuscript/styles.json"] = VersionHistoryCanonicalJson.Serialize(payload.Manuscript),
            ["composition/composition.json"] = effectiveSchemaVersion < VersionHistorySnapshotContract.DesignedPagesSchemaVersion
                ? VersionHistoryCanonicalJson.Serialize(new { PageCompositions = payload.Composition.PageCompositions })
                : VersionHistoryCanonicalJson.Serialize(payload.Composition),
            ["publication/publication.json"] = VersionHistoryCanonicalJson.Serialize(payload.Publication),
        };
        if (effectiveSchemaVersion >= 8)
        {
            files["sources/index.json"] = VersionHistoryCanonicalJson.Serialize(new VersionHistorySourceIndex(
                payload.Sources.RetainedSources.Select(source => source.Id).OrderBy(id => id).ToList(),
                payload.Sources.UnlinkedBibliographicRecords));
            foreach (var source in payload.Sources.RetainedSources)
            {
                files[$"sources/{source.Id:N}/source.json"] = VersionHistoryCanonicalJson.Serialize(source);
                foreach (var reference in source.Original.Chunks)
                    files[$"sources/blobs/{reference.BlobSha256}.bin"] = sourceOriginalData?[reference.BlobSha256]
                        ?? throw new InvalidOperationException("Schema-8 retained-source fixtures require explicit source bytes.");
            }
        }
        else
        {
            Assert.Empty(payload.Sources.RetainedSources);
            files["sources/sources.json"] = VersionHistoryCanonicalJson.Serialize(new { Sources = Array.Empty<ProjectExportIngestSource>() });
        }
        foreach (var chapter in payload.Narrative.Chapters)
        {
            var chapterDirectory = $"narrative/chapters/{chapter.Id:N}";
            files[$"{chapterDirectory}/chapter.json"] = VersionHistoryCanonicalJson.Serialize(
                VersionHistorySnapshotChapter.FromProjectExportChapter(chapter));
            files[$"{chapterDirectory}/manuscript.json"] = VersionHistoryCanonicalJson.SerializeDirectManuscript(
                chapter.ManuscriptJson);
        }
        foreach (var image in payload.Assets.Images)
            files[image.BlobPath] = payload.ImageData.GetValueOrDefault(image.Id) ?? [];
        foreach (var family in payload.Assets.FontFamilies)
        foreach (var face in family.Faces)
            files[face.BlobPath] = payload.FontFaceData.GetValueOrDefault(face.Id) ?? [];
        if (transform is not null)
        {
            foreach (var path in files.Keys.ToList())
                files[path] = transform(path, files[path]);
        }

        var contentHash = VersionHistoryCanonicalJson.Sha256Hex(files.Select(item => (item.Key, item.Value)));
        var manifest = new VersionHistorySnapshotManifest(
            VersionHistorySnapshotContract.FormatId,
            effectiveSchemaVersion,
            payload.RepositoryId,
            payload.ProjectId,
            VersionHistorySnapshotContract.IncludedAreas,
            contentHash,
            string.Empty,
            files.Select(item => new VersionHistorySnapshotFile(
                item.Key,
                item.Value.LongLength,
                VersionHistoryCanonicalJson.Sha256Hex(item.Value))).ToList());
        manifest = manifest with
        {
            ManifestHash = VersionHistoryCanonicalJson.Sha256Hex(VersionHistoryCanonicalJson.Serialize(manifest)),
        };

        files[VersionHistorySnapshotContract.ManifestFileName] = VersionHistoryCanonicalJson.Serialize(manifest);
        foreach (var (path, bytes) in files)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }
    }

    private static VersionHistorySnapshotPayload CreatePayload(
        Guid repositoryId,
        Guid projectId,
        string projectName = "Project",
        ProjectExportChapter? chapter = null,
        IReadOnlyList<ProjectExportChapter>? chapters = null,
        IReadOnlyList<ProjectExportManuscriptAnnotation>? annotations = null,
        IReadOnlyList<ProjectExportAct>? acts = null,
        VersionHistorySnapshotGraphArea? graph = null)
    {
        var selectedChapters = chapters ?? (chapter is null ? [] : [chapter]);
        return new VersionHistorySnapshotPayload(
            repositoryId,
            projectId,
            new VersionHistorySnapshotProjectArea(
                new ProjectExportProject(projectId, projectName, projectName.ToLowerInvariant().Replace(' ', '-'), "guidance", true, false),
                null,
                false,
                []),
            new VersionHistorySnapshotNarrativeArea(
                null,
                [],
                [],
                acts ?? [],
                selectedChapters,
                [],
                [],
                annotations ?? []),
            graph ?? new VersionHistorySnapshotGraphArea([], []),
            new VersionHistorySnapshotSourcesArea([]),
            new VersionHistorySnapshotAssetsArea([], [], []),
            new VersionHistorySnapshotManuscriptArea([]),
            new VersionHistorySnapshotCompositionArea([]),
            new VersionHistorySnapshotPublicationArea(null, [], []));
    }

    private static ProjectExportChapter CreateChapter(Guid id, string title) => new()
    {
        Id = id,
        Title = title,
        ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(id)),
        ManuscriptRevision = 0,
        Synopsis = title + " synopsis",
        Order = 0,
    };

    private static ProjectExportDesignedPage DesignedPage(Guid pageId, string name, long revision)
    {
        var contentId = Guid.NewGuid();
        return new ProjectExportDesignedPage(pageId, name, null,
        [
            new ProjectExportDesignedPageContent(contentId, pageId, null, "{}", string.Empty, revision, [], null),
        ]);
    }

    private static ProjectExportManuscriptAnnotation CreateAnnotation(Guid id, Guid chapterId, string note) =>
        new(
            id,
            chapterId,
            "Chapter",
            null,
            null,
            ManuscriptAnnotationKind.Note,
            note,
            0,
            0,
            ManuscriptAnnotationAnchorState.Current,
            "block",
            0,
            "block",
            1,
            "quote",
            string.Empty,
            string.Empty,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch);

    private sealed class StubHistoryService(ProjectVersionLoadedCheckpoint checkpoint) : IProjectVersionHistoryService
    {
        public ProjectVersionStatusView? Status { get; set; }

        public Func<ProjectVersionCheckpointView>? CheckpointFactory { get; set; }

        public int CreateCheckpointCalls { get; private set; }

        public Task<ProjectVersionRepositoryView?> GetRepositoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectVersionRepositoryView?>(null);

        public Task<ProjectVersionRepositoryView> EnsureRepositoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionRepositoryView>();

        public Task<ProjectVersionCheckpointView> CreateCheckpointAsync(Guid projectId, ProjectVersionCheckpointKind kind, string semanticMessage, string? requestKey = null, DateTimeOffset? authoredAt = null, CancellationToken cancellationToken = default)
        {
            CreateCheckpointCalls++;
            return CheckpointFactory is { } factory
                ? Task.FromResult(factory())
                : Unsupported<ProjectVersionCheckpointView>();
        }

        public Task<ProjectVersionTimelineView?> GetTimelineAsync(Guid projectId, int maxCheckpoints = 100, int maxOperations = 100, CancellationToken cancellationToken = default) => Task.FromResult<ProjectVersionTimelineView?>(null);

        public Task<int> ClearFailedOperationNoticesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<ProjectVersionStatusView?> GetStatusAsync(Guid projectId, bool includeCurrentSnapshotHash = true, CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task SetReviewEditsEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());

        public Task<ProjectVersionReviewView?> GetReviewAsync(Guid projectId, IReadOnlyCollection<ProjectVersionReviewTarget>? targets = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewView?>();

        public Task<ProjectVersionReviewChapter?> GetReviewChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewChapter?>();

        public Task<ProjectVersionHistoricalChapterReview?> GetLatestAffectingChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, int maxCommits = 100, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionHistoricalChapterReview?>();

        public Task<ProjectVersionHistoricalRestoreResult> RestoreHistoricalChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, string historicalCommitSha, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Undo manuscript to historical checkpoint", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionHistoricalRestoreResult>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalCheckpointAsync(Guid projectId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved Review Edits", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> ApproveAllAndDisableReviewEditsAsync(Guid projectId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved Review Edits before disabling Review Edits", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalForChapterAsync(Guid projectId, ProjectVersionReviewTarget target, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved chapter review changes", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalForOtherAsync(Guid projectId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved other project changes", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalForBlocksAsync(Guid projectId, ProjectVersionReviewTarget target, IReadOnlyCollection<string> blockIds, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved selected manuscript changes", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> ApproveReviewDesignedPageAsync(Guid projectId, Guid designedPageId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved Designed Page change", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionReviewBlockMutationResult> RestoreReviewBlocksAsync(Guid projectId, ProjectVersionReviewTarget target, IReadOnlyCollection<string> blockIds, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Undid selected manuscript changes", CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewBlockMutationResult>();

        public Task<ProjectVersionReviewBlockMutationResult> EditReviewBlockAsync(Guid projectId, ProjectVersionReviewTarget target, string blockId, string text, ProjectVersionReviewConcurrencyToken expectedToken, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewBlockMutationResult>();

        public Task<ProjectVersionLoadedCheckpoint> LoadCheckpointAsync(Guid projectId, string commitSha, CancellationToken cancellationToken = default) => Task.FromResult(checkpoint);

        public Task<ProjectVersionLoadedCheckpointLease> LoadCheckpointForRestoreAsync(Guid projectId, string commitSha, CancellationToken cancellationToken = default) => Task.FromResult(new ProjectVersionLoadedCheckpointLease(checkpoint));

        public Task<ProjectVersionLoadedCheckpoint> LoadCheckpointForComparisonAsync(Guid projectId, string commitSha, CancellationToken cancellationToken = default, Guid? unboundedSourceId = null) => Task.FromResult(checkpoint);

        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PassthroughAuthoringMutationFence : IAuthoringMutationFence
    {
        public Guid ProcessIncarnationId { get; } = Guid.NewGuid();

        public ValueTask<IAsyncDisposable> RegisterWriterAsync(
            AuthoringWriterRegistration registration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void UpdateWriterState(AuthoringWriterState state) =>
            throw new NotSupportedException();

        public Task<T> ExecuteAsync<T>(
            AuthoringFenceRequest request,
            Func<AuthoringFenceContext, CancellationToken, Task<T>> consume,
            CancellationToken cancellationToken = default) =>
            consume(new AuthoringFenceContext(ProcessIncarnationId, []), cancellationToken);
    }

    private sealed class NoopOutlineGraphSync : IOutlineGraphSync
    {
        public Task EnsureProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureActAsync(Act act, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureChapterAsync(Chapter chapter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RepairProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopContextIndexingService : IContextIndexingService
    {
        public Task ReindexEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexChapterAsync(Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexActAsync(Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceChunkAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexWritingSampleAsync(Guid sampleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteWritingSampleAsync(Guid projectId, Guid sampleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopGraphAutoLinkService : IGraphAutoLinkService
    {
        public Task<IReadOnlyList<GraphAutoMentionLink>> RefreshEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
        public Task<IReadOnlyList<GraphAutoMentionLink>> RefreshSourceAsync(Guid projectId, string sourceType, Guid sourceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
        public Task<IReadOnlyList<GraphAutoMentionLink>> ListEntityAutoMentionLinksAsync(Guid projectId, Guid entityId, int maxResults = 12, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
    }

    private sealed class NoopProjectSearchIndex : IProjectSearchIndex
    {
        public Task StoreAsync(ProjectSearchIndexChunk chunk, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreManyAsync(IEnumerable<ProjectSearchIndexChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectLexicalSearchResult>> SearchAsync(ProjectLexicalSearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectLexicalSearchResult>>([]);
        public Task DeleteBySourceAsync(string sourceType, string sourceId, string scopeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopIngestGraphSync : IIngestGraphSync
    {
        public Task EnsureSourceAsync(IngestSource source, IReadOnlyList<IngestSourceChunk> sourceChunks, IReadOnlyList<IngestSourceBlock>? sourceBlocks = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopGraphStore : IGraphStore
    {
        public Task<GraphNode> UpsertNodeAsync(Guid projectId, string nodeType, string key, string? label = null, IDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode>(null!);
        public Task<GraphEdge> UpsertEdgeAsync(long fromNodeId, long toNodeId, string edgeType, IDictionary<string, object?>? properties = null, int? sortOrder = null, CancellationToken cancellationToken = default) => Task.FromResult<GraphEdge>(null!);
        public Task RemoveNodeAsync(long nodeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveEdgeAsync(long edgeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GraphNode?> GetNodeAsync(long nodeId, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode?>(null);
        public Task<GraphNode?> FindNodeAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode?>(null);
        public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(long nodeId, GraphTraversalOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphNode>>([]);
        public Task<IReadOnlyList<GraphPath>> FindPathsAsync(long fromNodeId, long toNodeId, GraphTraversalOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphPath>>([]);
    }
}
