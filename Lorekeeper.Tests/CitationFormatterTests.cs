using Lorekeeper.Citations;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Tests;

public sealed class CitationFormatterTests
{
    private static readonly Guid BookId = Guid.Parse("80000000-0000-0000-0000-000000000001");
    private static readonly Guid ArticleId = Guid.Parse("80000000-0000-0000-0000-000000000002");

    [Fact]
    public void Golden_styles_match_independently_authored_outputs()
    {
        var formatter = new CitationFormatter();
        var records = Records();
        var occurrences = Occurrences();

        var chicago = formatter.Format(CitationStyle.Chicago18NotesBibliography, records, occurrences);
        Assert.Equal(ICitationFormatter.FormatterIdentity, chicago.FormatterId);
        Assert.Collection(
            chicago.Occurrences,
            item => Assert.Equal("Linh Nguyen, Maps of Quiet Water, 2nd ed. (North Window Press, 2024), 42.", item.NoteText),
            item => Assert.Equal("Nguyen, Maps of Quiet Water, 47.", item.NoteText),
            item => Assert.Equal("Nguyen, Maps of Quiet Water, 5.", item.NoteText),
            item => Assert.Equal("Linh Nguyen, \"Tidal memory in coastal archives,\" Journal of Small Geographies 12, no. 3 (June 2024): 110, https://doi.org/10.5555/tidal-memory.", item.NoteText));
        Assert.Equal([1, 2, 1, 3], chicago.Occurrences.Select(item => item.NoteNumber));
        Assert.Collection(
            chicago.Bibliography,
            item => Assert.Equal("Nguyen, Linh. Maps of Quiet Water. 2nd ed. North Window Press, 2024.", item),
            item => Assert.Equal("Nguyen, Linh. \"Tidal memory in coastal archives.\" Journal of Small Geographies 12, no. 3 (June 2024): 101-119. https://doi.org/10.5555/tidal-memory.", item));
        Assert.DoesNotContain(chicago.Occurrences, item => item.NoteText!.Contains("Ibid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(chicago.Occurrences[0].NoteRuns!, run => run.Italic && run.Text == "Maps of Quiet Water");
        Assert.Contains(chicago.BibliographyEntries[0].Runs, run => run.Italic && run.Text == "Maps of Quiet Water");

        var apa = formatter.Format(CitationStyle.APA7, records, occurrences);
        Assert.Equal(
            ["(Nguyen, 2024a, p. 42)", "(Nguyen, 2024a, p. 47)", "(Nguyen, 2024a, p. 5)", "(Nguyen, 2024b, p. 110)"],
            apa.Occurrences.Select(item => item.InlineText));
        Assert.Collection(
            apa.Bibliography,
            item => Assert.Equal("Nguyen, L. (2024a). Maps of Quiet Water (2nd ed.). North Window Press.", item),
            item => Assert.Equal("Nguyen, L. (2024b). Tidal memory in coastal archives. Journal of Small Geographies, 12(3), 101-119. https://doi.org/10.5555/tidal-memory", item));
        Assert.Contains(apa.BibliographyEntries[1].Runs, run => run.Italic && run.Text.Contains("Journal of Small Geographies, 12", StringComparison.Ordinal));

        var mla = formatter.Format(CitationStyle.MLA9, records, occurrences);
        Assert.Equal(["(Nguyen, Maps of Quiet Water 42)", "(Nguyen, Maps of Quiet Water 47)", "(Nguyen, Maps of Quiet Water 5)", "(Nguyen, \"Tidal memory in coastal archives\" 110)"], mla.Occurrences.Select(item => item.InlineText));
        Assert.Equal(
            [
                "Nguyen, Linh. Maps of Quiet Water. 2nd ed., North Window Press, 2024.",
                "Nguyen, Linh. \"Tidal memory in coastal archives.\" Journal of Small Geographies, vol. 12, no. 3, 2024, pp. 101-119. doi:10.5555/tidal-memory.",
            ],
            mla.Bibliography);
    }

    [Theory]
    [InlineData(CitationStyle.Chicago18NotesBibliography)]
    [InlineData(CitationStyle.APA7)]
    [InlineData(CitationStyle.MLA9)]
    public void Missing_metadata_is_retained_with_actionable_diagnostics(CitationStyle style)
    {
        var record = new CitationRecord(
            Guid.Parse("80000000-0000-0000-0000-000000000004"),
            BibliographicRecordKind.Report,
            "Annual waterline survey",
            string.Empty,
            [], [], [], new(), new(), string.Empty, string.Empty, string.Empty,
            "Harbor Research Office", string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, "https://example.invalid/waterline-survey", string.Empty);
        var occurrence = Occurrence("missing", "chapter:missing", record.Id, "", "");

        var result = new CitationFormatter().Format(style, [record], [occurrence]);

        Assert.Contains(result.Diagnostics, item => item.Code == "CITATION_MISSING_AUTHOR" && item.Field == "authors");
        Assert.Contains(result.Diagnostics, item => item.Code == "CITATION_MISSING_ISSUED_YEAR" && item.Field == "issued.year");
        Assert.Single(result.Bibliography);
    }

    [Fact]
    public void Edited_and_translated_books_keep_contributors_and_semantic_titles()
    {
        var book = Records()[0] with
        {
            Authors = [], Editors = [new("River", "Ada"), new("Field", "Bo")],
            Translators = [new("Stone", "Cal")], Edition = "", PublisherPlace = "Discarded place",
        };
        var occurrence = Occurrence("edited", "chapter:one", book.Id, "page", "9");
        var chicago = new CitationFormatter().Format(CitationStyle.Chicago18NotesBibliography, [book], [occurrence]);
        Assert.Equal("Ada River and Bo Field, eds., Maps of Quiet Water, trans. Cal Stone (North Window Press, 2024), 9.", chicago.Occurrences[0].NoteText);
        Assert.Contains("Translated by Cal Stone", chicago.Bibliography[0]);
        Assert.DoesNotContain(chicago.Diagnostics, diagnostic => diagnostic.Code == "CITATION_MISSING_AUTHOR");
        var apa = new CitationFormatter().Format(CitationStyle.APA7, [book], [occurrence]);
        Assert.Equal("(River & Field, 2024, p. 9)", apa.Occurrences[0].InlineText);
        Assert.Contains("River, A., & Field, B. (Eds.). (2024).", apa.Bibliography[0]);
        Assert.Contains("C. Stone, Trans.", apa.Bibliography[0]);
    }

    [Fact]
    public void Apa_disambiguation_expands_different_author_groups_without_false_year_suffixes()
    {
        var first = Records()[0] with { Authors = [new("River", "Ada"), new("Field", "Bo"), new("Stone", "Cal")] };
        var second = first with { Id = ArticleId, Title = "Other work", Authors = [new("River", "Ada"), new("Hill", "Dee"), new("Stone", "Cal")] };
        var result = new CitationFormatter().Format(CitationStyle.APA7, [first, second],
            [Occurrence("one", "chapter:one", first.Id, "", ""), Occurrence("two", "chapter:one", second.Id, "", "")]);
        Assert.Equal(["(River, Field, & Stone, 2024)", "(River, Hill, & Stone, 2024)"], result.Occurrences.Select(item => item.InlineText));
        Assert.DoesNotContain(result.Bibliography, text => text.Contains("2024a", StringComparison.Ordinal));
    }

    [Fact]
    public void Apa_missing_author_uses_title_once_and_suffixes_extend_past_twenty_six()
    {
        var anonymous = Records()[0] with { Authors = [], Issued = new() };
        var formatter = new CitationFormatter();
        var result = formatter.Format(CitationStyle.APA7, [anonymous], [Occurrence("one", "chapter:one", anonymous.Id, "", "")]);
        Assert.Equal("(Maps of Quiet Water, n.d.)", result.Occurrences[0].InlineText);
        Assert.Equal(1, result.Bibliography[0].Split("Maps of Quiet Water", StringSplitOptions.None).Length - 1);
        Assert.Contains(result.Occurrences[0].InlineRuns, run => run.Italic);
        var books = Enumerable.Range(0, 28).Select(index => Records()[0] with { Id = Guid.NewGuid(), Title = $"Book {index:D2}" }).ToList();
        var many = formatter.Format(CitationStyle.APA7, books, books.Select((book, index) => Occurrence($"atom{index}", "chapter:one", book.Id, "", "")).ToList());
        Assert.Equal("(Nguyen, 2024aa)", many.Occurrences[26].InlineText);
        Assert.Equal("(Nguyen, 2024ab)", many.Occurrences[27].InlineText);
    }

    [Fact]
    public void Apa_same_surname_authors_and_cluster_order_preserve_item_ordinals()
    {
        var first = Records()[0] with { Authors = [new("River", "Ada")] };
        var second = first with { Id = ArticleId, Title = "The Other Work", Authors = [new("River", "Bo")] };
        var occurrence = Occurrence("cluster", "chapter:one", second.Id, "pages", "3-4");
        occurrence = occurrence with { Cluster = occurrence.Cluster with { Items =
            [.. occurrence.Cluster.Items, new() { BibliographicRecordId = first.Id, LocatorLabel = "paragraph", LocatorValue = "2" }] } };
        var result = new CitationFormatter().Format(CitationStyle.APA7, [second, first], [occurrence]);
        Assert.Equal("(A. River, 2024, para. 2; B. River, 2024, pp. 3-4)", result.Occurrences[0].InlineText);
        Assert.Equal([1, 0], result.Occurrences[0].Items.Select(item => item.Ordinal));
        Assert.Equal([first.Id, second.Id], result.BibliographyEntries.Select(item => item.BibliographicRecordId));
    }

    [Fact]
    public void Mla_edited_book_and_chapter_keep_editor_translator_and_page_metadata()
    {
        var book = Records()[0] with { Authors = [], Editors = [new("River", "Ada")], Translators = [new("Stone", "Cal")] };
        var chapter = book with { Id = ArticleId, Kind = BibliographicRecordKind.BookChapter, Title = "Tides", ContainerTitle = "Collected Waters", Authors = [new("Field", "Bo")], Pages = "8-19" };
        var result = new CitationFormatter().Format(CitationStyle.MLA9, [book, chapter],
            [Occurrence("book", "chapter:one", book.Id, "", ""), Occurrence("chapter", "chapter:one", chapter.Id, "page", "9")]);
        Assert.Contains("River, Ada, editor. Maps of Quiet Water. Translated by Cal Stone, 2nd ed., North Window Press, 2024.", result.Bibliography);
        Assert.Contains("Field, Bo. \"Tides.\" Collected Waters, translated by Cal Stone, edited by Ada River, 2nd ed., North Window Press, 2024, pp. 8-19.", result.Bibliography);
    }

    [Theory]
    [InlineData(BibliographicRecordKind.Book, "publisher")]
    [InlineData(BibliographicRecordKind.BookChapter, "containerTitle")]
    [InlineData(BibliographicRecordKind.JournalArticle, "containerTitle")]
    [InlineData(BibliographicRecordKind.MagazineArticle, "containerTitle")]
    [InlineData(BibliographicRecordKind.NewspaperArticle, "containerTitle")]
    [InlineData(BibliographicRecordKind.WebPage, "url")]
    [InlineData(BibliographicRecordKind.Thesis, "institution")]
    [InlineData(BibliographicRecordKind.Report, "institution")]
    public void Missing_field_matrix_retains_work_and_identifies_the_field(BibliographicRecordKind kind, string field)
    {
        var record = Records()[0] with { Kind = kind, Publisher = "", ContainerTitle = "", Institution = "", Url = "", Doi = "" };
        foreach (var style in Enum.GetValues<CitationStyle>())
        {
            var result = new CitationFormatter().Format(style, [record], [Occurrence("missing", "chapter:one", record.Id, "", "")]);
            Assert.Single(result.Bibliography);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Field == field && diagnostic.BibliographicRecordId == record.Id);
            Assert.Contains(record.Title, result.Bibliography[0]);
        }
    }

    [Fact]
    public void Clusters_keep_item_identities_and_apply_style_punctuation()
    {
        var records = new[] { Records()[0] with { Issued = new(2022) }, Records()[1] };
        var occurrence = Occurrence("cluster", "chapter:one", ArticleId, "", "");
        occurrence = occurrence with { Cluster = occurrence.Cluster with { Items =
            [.. occurrence.Cluster.Items, new() { BibliographicRecordId = BookId }] } };
        var apa = new CitationFormatter().Format(CitationStyle.APA7, records, [occurrence]);
        Assert.Equal("(Nguyen, 2022, 2024)", apa.Occurrences[0].InlineText);
        Assert.Equal([1, 0], apa.Occurrences[0].Items.Select(item => item.Ordinal));
        Assert.Equal(2, apa.Bibliography.Count);
        var chicago = new CitationFormatter().Format(CitationStyle.Chicago18NotesBibliography, records, [occurrence]);
        Assert.DoesNotContain(".;", chicago.Occurrences[0].NoteText!);
        Assert.Contains("; Linh Nguyen", chicago.Occurrences[0].NoteText!);
    }

    [Fact]
    public void Anonymous_web_titles_and_corporate_publishers_keep_semantics()
    {
        var record = Records()[0] with { Kind = BibliographicRecordKind.WebPage, Title = "Where is the water?", Authors = [], ContainerTitle = "Harbor Office", Url = "https://example.invalid/water", Edition = "" };
        var occurrence = Occurrence("web", "chapter:one", record.Id, "", "");
        var formatter = new CitationFormatter();
        var apa = formatter.Format(CitationStyle.APA7, [record], [occurrence]);
        Assert.Equal("(Where is the water?, 2024)", apa.Occurrences[0].InlineText);
        Assert.Contains(apa.Occurrences[0].InlineRuns, run => run.Italic && run.Text == record.Title);
        Assert.Equal("Where is the water? (2024). Harbor Office. https://example.invalid/water", apa.Bibliography[0]);
        var corporate = record with { Authors = [new(Literal: "Harbor Office")] };
        var corporateResult = formatter.Format(CitationStyle.APA7, [corporate], [occurrence]);
        Assert.Equal("Harbor Office. (2024). Where is the water? https://example.invalid/water", corporateResult.Bibliography[0]);
        foreach (var style in new[] { CitationStyle.Chicago18NotesBibliography, CitationStyle.MLA9 })
        {
            var result = formatter.Format(style, [record], [occurrence]);
            Assert.Contains("\"Where is the water?\"", result.Bibliography[0]);
            Assert.DoesNotContain("?.", result.Bibliography[0]);
        }
    }

    [Theory]
    [InlineData(BibliographicRecordKind.MagazineArticle,
        "River, Ada. \"Water study.\" Harbor Review, June 7, 2024.",
        "River, A. (2024, June 7). Water study. Harbor Review, 10-20.",
        "River, Ada. \"Water study.\" Harbor Review, 7 June 2024, pp. 10-20.")]
    [InlineData(BibliographicRecordKind.NewspaperArticle,
        "River, Ada. \"Water study.\" Harbor Review, June 7, 2024.",
        "River, A. (2024, June 7). Water study. Harbor Review, 10-20.",
        "River, Ada. \"Water study.\" Harbor Review, 7 June 2024, pp. 10-20.")]
    [InlineData(BibliographicRecordKind.WebPage,
        "River, Ada. \"Water study.\" Harbor Review, June 7, 2024.",
        "River, A. (2024, June 7). Water study. Harbor Review.",
        "River, Ada. \"Water study.\" Harbor Review, 7 June 2024.")]
    [InlineData(BibliographicRecordKind.Report,
        "River, Ada. Water study. Harbor University, North Press, 2024.",
        "River, A. (2024). Water study. Harbor University. North Press.",
        "River, Ada. Water study. Harbor University, North Press, 2024.")]
    [InlineData(BibliographicRecordKind.Thesis,
        "River, Ada. \"Water study.\" PhD diss., Harbor University, North Press, 2024.",
        "River, A. (2024). Water study [PhD diss., Harbor University].",
        "River, Ada. Water study. 2024, Harbor University, PhD diss.")]
    public void Supported_record_kinds_have_independently_authored_bibliography_goldens(
        BibliographicRecordKind kind, string chicago, string apa, string mla)
    {
        var record = Records()[0] with { Kind = kind, Title = "Water study", Authors = [new("River", "Ada")],
            ContainerTitle = "Harbor Review", Issued = new(2024, 6, 7), Edition = "", Publisher = "North Press",
            Institution = "Harbor University", ThesisType = "PhD diss.", Pages = "10-20" };
        var occurrence = Occurrence("kind", "chapter:one", record.Id, "page", "12");
        var formatter = new CitationFormatter();
        Assert.Equal(chicago, formatter.Format(CitationStyle.Chicago18NotesBibliography, [record], [occurrence]).Bibliography[0]);
        Assert.Equal(apa, formatter.Format(CitationStyle.APA7, [record], [occurrence]).Bibliography[0]);
        Assert.Equal(mla, formatter.Format(CitationStyle.MLA9, [record], [occurrence]).Bibliography[0]);
    }

    private static IReadOnlyList<CitationRecord> Records() =>
    [
        new(
            BookId, BibliographicRecordKind.Book, "Maps of Quiet Water", string.Empty,
            [new CitationPerson("Nguyen", "Linh")], [], [], new(2024), new(), "2",
            "North Window Press", "Portland", string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
        new(
            ArticleId, BibliographicRecordKind.JournalArticle, "Tidal memory in coastal archives",
            "Journal of Small Geographies", [new CitationPerson("Nguyen", "Linh")], [], [],
            new(2024, 6), new(), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            "12", "3", "101-119", "10.5555/tidal-memory", string.Empty, string.Empty),
    ];

    private static IReadOnlyList<CitationClusterOccurrence> Occurrences() =>
    [
        Occurrence("81000000-0000-0000-0000-000000000001", "chapter:40000000-0000-0000-0000-000000000001", BookId, "page", "42"),
        Occurrence("81000000-0000-0000-0000-000000000002", "chapter:40000000-0000-0000-0000-000000000001", BookId, "page", "47"),
        Occurrence("81000000-0000-0000-0000-000000000003", "chapter:40000000-0000-0000-0000-000000000002", BookId, "page", "5", ["placement:23000000-0000-0000-0000-000000000101"]),
        Occurrence("81000000-0000-0000-0000-000000000004", "chapter:40000000-0000-0000-0000-000000000001", ArticleId, "page", "110"),
    ];

    private static CitationClusterOccurrence Occurrence(
        string atomId,
        string container,
        Guid recordId,
        string locatorLabel,
        string locatorValue,
        IReadOnlyList<string>? placementPath = null) =>
        new(
            new("core", container, placementPath ?? [], atomId),
            new ManuscriptCitationCluster
            {
                Items =
                [
                    new ManuscriptCitationItem
                    {
                        BibliographicRecordId = recordId,
                        LocatorLabel = locatorLabel,
                        LocatorValue = locatorValue,
                    },
                ],
            });
}
