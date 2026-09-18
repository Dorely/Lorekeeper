using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Citations;

[JsonConverter(typeof(JsonStringEnumConverter<CitationStyle>))]
public enum CitationStyle
{
    Chicago18NotesBibliography,
    APA7,
    MLA9,
}

public sealed record CitationPerson(string Family = "", string Given = "", string Literal = "");

public sealed record CitationDate(int? Year = null, int? Month = null, int? Day = null);

public sealed record CitationRecord(
    Guid Id,
    BibliographicRecordKind Kind,
    string Title,
    string ContainerTitle,
    IReadOnlyList<CitationPerson> Authors,
    IReadOnlyList<CitationPerson> Editors,
    IReadOnlyList<CitationPerson> Translators,
    CitationDate Issued,
    CitationDate Accessed,
    string Edition,
    string Publisher,
    string PublisherPlace,
    string Institution,
    string ThesisType,
    string Volume,
    string Issue,
    string Pages,
    string Doi,
    string Url,
    string Isbn)
{
    public static CitationRecord FromEntity(BibliographicRecord record) => new(
        record.Id,
        record.Kind,
        record.Title,
        record.ContainerTitle,
        ParsePeople(record.AuthorsJson, record.Id, "authors"),
        ParsePeople(record.EditorsJson, record.Id, "editors"),
        ParsePeople(record.TranslatorsJson, record.Id, "translators"),
        new(record.IssuedYear, record.IssuedMonth, record.IssuedDay),
        new(record.AccessedYear, record.AccessedMonth, record.AccessedDay),
        record.Edition,
        record.Publisher,
        record.PublisherPlace,
        record.Institution,
        record.ThesisType,
        record.Volume,
        record.Issue,
        record.Pages,
        record.Doi,
        record.Url,
        record.Isbn);

    private static IReadOnlyList<CitationPerson> ParsePeople(string json, Guid recordId, string field)
    {
        try
        {
            var people = JsonSerializer.Deserialize<List<CitationPerson>>(json, ManuscriptCodec.JsonOptions) ?? [];
            if (people.Any(person => string.IsNullOrWhiteSpace(person.Literal)
                    && string.IsNullOrWhiteSpace(person.Family)))
            {
                throw new InvalidDataException($"Bibliographic record {recordId:D} contains a {field} entry without a family or corporate name.");
            }
            return people;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Bibliographic record {recordId:D} has malformed {field} metadata.", exception);
        }
    }
}

public sealed record CitationOccurrenceIdentity(
    string PublicationTarget,
    string TopLevelContainer,
    IReadOnlyList<string> PlacementPath,
    string CitationAtomId);

public sealed record CitationClusterOccurrence(
    CitationOccurrenceIdentity Identity,
    ManuscriptCitationCluster Cluster);

public sealed record CitationDiagnostic(
    string Code,
    string Message,
    Guid BibliographicRecordId,
    string Field);

public sealed record FormattedCitationItem(
    int Ordinal,
    Guid BibliographicRecordId,
    IReadOnlyList<CitationRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

public sealed record FormattedCitationCluster(
    CitationOccurrenceIdentity Identity,
    IReadOnlyList<CitationRun> InlineRuns,
    int? NoteNumber,
    IReadOnlyList<CitationRun>? NoteRuns,
    IReadOnlyList<FormattedCitationItem> Items)
{
    public string InlineText => string.Concat(InlineRuns.Select(run => run.Text));
    public string? NoteText => NoteRuns is null ? null : string.Concat(NoteRuns.Select(run => run.Text));
}

public sealed record FormattedBibliographyEntry(Guid BibliographicRecordId, IReadOnlyList<CitationRun> Runs)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

public sealed record CitationFormattingResult(
    string FormatterId,
    CitationStyle Style,
    IReadOnlyList<FormattedCitationCluster> Occurrences,
    string BibliographyTitle,
    IReadOnlyList<FormattedBibliographyEntry> BibliographyEntries,
    IReadOnlyList<CitationDiagnostic> Diagnostics)
{
    public IReadOnlyList<string> Bibliography => BibliographyEntries.Select(entry => entry.Text).ToList();
}

public interface ICitationFormatter
{
    const string FormatterIdentity = "lorekeeper-citations-v1";

    CitationFormattingResult Format(
        CitationStyle style,
        IReadOnlyList<CitationRecord> records,
        IReadOnlyList<CitationClusterOccurrence> occurrences);
}

public sealed class CitationFormatter : ICitationFormatter
{
    public CitationFormattingResult Format(
        CitationStyle style,
        IReadOnlyList<CitationRecord> records,
        IReadOnlyList<CitationClusterOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(occurrences);
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style));

        var byId = records.ToDictionary(record => record.Id);
        var citedIds = occurrences.SelectMany(occurrence => occurrence.Cluster.Items)
            .Select(item => item.BibliographicRecordId).Distinct().ToList();
        foreach (var citedId in citedIds)
        {
            if (!byId.ContainsKey(citedId))
                throw new InvalidDataException($"Citation references missing bibliographic record {citedId:D}.");
        }

        var cited = citedIds.Select(id => byId[id]).ToList();
        var mlaAmbiguousAuthors = cited.GroupBy(ShortAuthor, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(record => record.Id)).ToHashSet();
        var diagnostics = Diagnostics(style, cited);
        IReadOnlyDictionary<Guid, string> apaSuffixes = style == CitationStyle.APA7
            ? ApaYearSuffixes(cited)
            : new Dictionary<Guid, string>();
        var apaNames = style == CitationStyle.APA7 ? ApaNames(cited) : new Dictionary<Guid, CitationText>();
        var formatted = new List<FormattedCitationCluster>(occurrences.Count);
        var chicagoSeen = new HashSet<Guid>();
        var noteNumbers = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var occurrence in occurrences)
        {
            var items = new List<FormattedCitationItem>(occurrence.Cluster.Items.Count);
            var clusterItems = occurrence.Cluster.Items.Select((item, ordinal) => (item, ordinal));
            if (style == CitationStyle.APA7)
                clusterItems = clusterItems.OrderBy(entry => SortKey(byId[entry.item.BibliographicRecordId]), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => byId[entry.item.BibliographicRecordId].Issued.Year ?? int.MinValue)
                    .ThenBy(entry => byId[entry.item.BibliographicRecordId].Issued.Month ?? 0)
                    .ThenBy(entry => byId[entry.item.BibliographicRecordId].Issued.Day ?? 0)
                    .ThenBy(entry => SortTitle(byId[entry.item.BibliographicRecordId].Title), StringComparer.OrdinalIgnoreCase);
            foreach (var (item, ordinal) in clusterItems)
            {
                var record = byId[item.BibliographicRecordId];
                CitationText text = style switch
                {
                    CitationStyle.Chicago18NotesBibliography => chicagoSeen.Add(record.Id)
                        ? ChicagoFullNote(record, item)
                        : ChicagoShortNote(record, item),
                    CitationStyle.APA7 => ApaInText(record, item, apaNames[record.Id], apaSuffixes.GetValueOrDefault(record.Id, string.Empty)),
                    CitationStyle.MLA9 => MlaInText(record, item, mlaAmbiguousAuthors.Contains(record.Id)),
                    _ => throw new ArgumentOutOfRangeException(nameof(style)),
                };
                items.Add(new(ordinal, record.Id, text.Runs));
            }

            if (style == CitationStyle.Chicago18NotesBibliography)
            {
                noteNumbers.TryGetValue(occurrence.Identity.TopLevelContainer, out var current);
                var number = current + 1;
                noteNumbers[occurrence.Identity.TopLevelContainer] = number;
                formatted.Add(new(
                    occurrence.Identity,
                    [new(number.ToString(CultureInfo.InvariantCulture))],
                    number,
                    CitationText.Join("; ", items.Select((item, index) => index + 1 == items.Count
                        ? CitationText.FromRuns(item.Runs) : CitationText.FromRuns(item.Runs).WithoutTerminalPeriod()).ToArray()).Runs,
                    items));
            }
            else
            {
                formatted.Add(new(
                    occurrence.Identity,
                    [new("("), .. (style == CitationStyle.APA7 ? ApaClusterRuns(items, occurrence.Cluster, byId, apaSuffixes) : JoinRuns(items)), new(")")],
                    null,
                    null,
                    items));
            }
        }

        var bibliography = cited
            .OrderBy(SortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => style == CitationStyle.APA7 ? record.Issued.Year ?? int.MinValue : 0)
            .ThenBy(record => style == CitationStyle.APA7 ? record.Issued.Month ?? 0 : 0)
            .ThenBy(record => style == CitationStyle.APA7 ? record.Issued.Day ?? 0 : 0)
            .ThenBy(record => SortTitle(record.Title), StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Id)
            .Select(record => new FormattedBibliographyEntry(record.Id, (style switch
            {
                CitationStyle.Chicago18NotesBibliography => ChicagoBibliography(record),
                CitationStyle.APA7 => ApaBibliography(record, apaSuffixes.GetValueOrDefault(record.Id, string.Empty)),
                CitationStyle.MLA9 => MlaBibliography(record),
                _ => throw new ArgumentOutOfRangeException(nameof(style)),
            }).Runs))
            .ToList();
        return new(
            ICitationFormatter.FormatterIdentity,
            style,
            formatted,
            style switch
            {
                CitationStyle.APA7 => "References",
                CitationStyle.MLA9 => "Works Cited",
                _ => "Bibliography",
            },
            bibliography,
            diagnostics);
    }

    private static IReadOnlyList<CitationRun> JoinRuns(IReadOnlyList<FormattedCitationItem> items) =>
        items.SelectMany((item, index) => index == 0 ? item.Runs : new[] { new CitationRun("; ") }.Concat(item.Runs)).ToList();

    private static IReadOnlyList<CitationRun> ApaClusterRuns(
        IReadOnlyList<FormattedCitationItem> items,
        ManuscriptCitationCluster cluster,
        IReadOnlyDictionary<Guid, CitationRecord> records,
        IReadOnlyDictionary<Guid, string> suffixes)
    {
        var runs = new List<CitationRun>();
        string? previousContributors = null;
        foreach (var item in items)
        {
            var source = cluster.Items[item.Ordinal];
            var record = records[item.BibliographicRecordId];
            var plain = string.IsNullOrWhiteSpace(source.Prefix) && string.IsNullOrWhiteSpace(source.Suffix)
                && string.IsNullOrWhiteSpace(source.LocatorValue) && PrimaryContributors(record).Count > 0;
            var contributors = plain ? ContributorKey(record) : null;
            var sameAuthors = contributors is not null && contributors == previousContributors;
            if (runs.Count > 0) runs.Add(new(sameAuthors ? ", " : "; "));
            if (sameAuthors) runs.Add(new(ApaYear(record, suffixes.GetValueOrDefault(record.Id, string.Empty))));
            else runs.AddRange(item.Runs);
            previousContributors = contributors;
        }
        return runs;
    }

    private static IReadOnlyList<CitationDiagnostic> Diagnostics(
        CitationStyle style,
        IEnumerable<CitationRecord> records)
    {
        var result = new List<CitationDiagnostic>();
        foreach (var record in records)
        {
            Missing(record.Title, "TITLE", "title", "Enter the work's title.");
            if (IsContainedWork(record.Kind))
                Missing(record.ContainerTitle, "CONTAINER", "containerTitle", "Enter the journal, periodical, book, or site containing this work.");
            if (record.Kind is BibliographicRecordKind.Book or BibliographicRecordKind.BookChapter)
                Missing(record.Publisher, "PUBLISHER", "publisher", "Enter the publisher when known; the missing publisher is omitted from output.");
            if (record.Kind == BibliographicRecordKind.Thesis)
            {
                Missing(record.Institution, "INSTITUTION", "institution", "Enter the degree-granting institution.");
                Missing(record.ThesisType, "THESIS_TYPE", "thesisType", "Enter the thesis or dissertation type.");
            }
            if (record.Kind == BibliographicRecordKind.Report && string.IsNullOrWhiteSpace(record.Publisher))
                Missing(record.Institution, "INSTITUTION", "institution", "Enter the issuing institution or publisher for the report.");
            if (record.Kind == BibliographicRecordKind.WebPage && string.IsNullOrWhiteSpace(record.Doi))
                Missing(record.Url, "URL", "url", "Enter the page URL so readers can locate this work.");
            if (PrimaryContributors(record).Count == 0)
            {
                result.Add(new(
                    "CITATION_MISSING_AUTHOR",
                    $"'{record.Title}' has no personal or corporate author; {style} will use its title as the author fallback.",
                    record.Id,
                    "authors"));
            }
            if (record.Issued.Year is null)
            {
                result.Add(new(
                    "CITATION_MISSING_ISSUED_YEAR",
                    $"'{record.Title}' has no issued year; {style} will retain the record and omit or label the missing date.",
                    record.Id,
                    "issued.year"));
            }
            void Missing(string value, string code, string field, string action)
            {
                if (string.IsNullOrWhiteSpace(value))
                    result.Add(new("CITATION_MISSING_" + code, $"'{record.Title}': {action}", record.Id, field));
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<Guid, string> ApaYearSuffixes(IEnumerable<CitationRecord> records)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var group in records.GroupBy(record => $"{ContributorKey(record)}|{record.Issued.Year?.ToString(CultureInfo.InvariantCulture) ?? "n.d."}", StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(record => record.Issued.Month ?? 0).ThenBy(record => record.Issued.Day ?? 0)
                .ThenBy(record => SortTitle(record.Title), StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Id).ToList();
            if (ordered.Count == 1)
            {
                result[ordered[0].Id] = string.Empty;
                continue;
            }
            for (var index = 0; index < ordered.Count; index++)
                result[ordered[index].Id] = AlphabeticSuffix(index);
        }
        return result;
    }

    private static string AlphabeticSuffix(int index)
    {
        var suffix = string.Empty;
        do
        {
            suffix = (char)('a' + index % 26) + suffix;
            index = index / 26 - 1;
        } while (index >= 0);
        return suffix;
    }

    private static string ContributorKey(CitationRecord record) => PrimaryContributors(record).Count == 0
        ? record.Title.Trim()
        : JsonSerializer.Serialize(PrimaryContributors(record).Select(person => new
        {
            Family = person.Family.Trim(), Given = person.Given.Trim(), Literal = person.Literal.Trim(),
        }));

    private static Dictionary<Guid, CitationText> ApaNames(IReadOnlyList<CitationRecord> records)
    {
        var result = new Dictionary<Guid, CitationText>();
        var ambiguousSurnames = records.Select(PrimaryContributors).Where(people => people.Count > 0)
            .Select(people => people[0]).Where(person => string.IsNullOrWhiteSpace(person.Literal))
            .GroupBy(person => person.Family.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(person => Initials(person.Given)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var people = PrimaryContributors(record);
            if (people.Count == 0)
            {
                result[record.Id] = IsContainedWork(record.Kind) && record.Kind != BibliographicRecordKind.WebPage
                    ? $"\"{record.Title.Trim()}\"" : CitationText.Italic(record.Title.Trim());
                continue;
            }
            var count = people.Count > 2 ? 1 : people.Count;
            var similar = records.Where(other => other.Id != record.Id && other.Issued.Year == record.Issued.Year
                && ContributorKey(other) != ContributorKey(record)).Select(PrimaryContributors).ToList();
            while (count < people.Count && similar.Any(other => other.Count >= count
                && people.Take(count).Select(ShortPerson).SequenceEqual(other.Take(count).Select(ShortPerson), StringComparer.OrdinalIgnoreCase)))
                count++;
            // "et al." must abbreviate at least two contributors.
            if (people.Count - count == 1) count = people.Count;
            var names = people.Take(count).Select(ShortPerson).ToList();
            if (string.IsNullOrWhiteSpace(people[0].Literal) && ambiguousSurnames.Contains(people[0].Family.Trim()))
                names[0] = JoinNonEmpty(" ", Initials(people[0].Given), names[0]);
            result[record.Id] = count < people.Count ? string.Join(", ", names) + " et al."
                : names.Count == 1 ? names[0]
                : string.Join(", ", names.Take(names.Count - 1)) + (names.Count > 2 ? ", & " : " & ") + names[^1];
        }
        return result;
    }

    private static CitationText ChicagoFullNote(CitationRecord record, ManuscriptCitationItem item)
    {
        var authors = ChicagoAuthors(record, bibliography: false);
        CitationText details = record.Kind switch
        {
            BibliographicRecordKind.Book => ChicagoBookPublication(record),
            BibliographicRecordKind.BookChapter => ChicagoChapterPublication(record),
            BibliographicRecordKind.JournalArticle => ChicagoJournalPublication(record),
            BibliographicRecordKind.MagazineArticle or BibliographicRecordKind.NewspaperArticle => ChicagoPeriodicalPublication(record),
            BibliographicRecordKind.WebPage => ChicagoWebPublication(record),
            BibliographicRecordKind.Report or BibliographicRecordKind.Thesis => ChicagoReportPublication(record),
            _ => string.Empty,
        };
        var contained = IsContainedWork(record.Kind);
        CitationText title = contained ? $"\"{record.Title.Trim()},\""
            : record.Kind == BibliographicRecordKind.Thesis ? $"\"{record.Title.Trim()}\"" : CitationText.Italic(record.Title.Trim());
        var lead = CitationText.Join(", ", authors, title);
        var titleAndDetails = contained
            ? CitationText.Join(" ", lead, details.TrimStart(',', ' '))
            : lead + details;
        return Compose(
            item.Prefix,
            titleAndDetails,
            Locator(item, ChicagoLocatorLabel),
            item.Suffix,
            TerminalUrl(record),
            record.Kind == BibliographicRecordKind.JournalArticle ? ": " : ", ");
    }

    private static CitationText ChicagoShortNote(CitationRecord record, ManuscriptCitationItem item)
    {
        var author = ShortAuthor(record);
        CitationText title = IsContainedWork(record.Kind) || record.Kind == BibliographicRecordKind.Thesis
            ? $"\"{ShortTitle(record.Title)}\"" : CitationText.Italic(ShortTitle(record.Title));
        var lead = CitationText.Join(", ", author, title);
        return Compose(item.Prefix, lead, Locator(item, ChicagoLocatorLabel), item.Suffix, string.Empty);
    }

    private static CitationText ChicagoBibliography(CitationRecord record)
    {
        var authors = ChicagoAuthors(record, bibliography: true);
        CitationText details = record.Kind switch
        {
            BibliographicRecordKind.Book => ChicagoBookPublication(record, bibliography: true),
            BibliographicRecordKind.BookChapter => ChicagoChapterPublication(record, bibliography: true),
            BibliographicRecordKind.JournalArticle => ChicagoJournalPublication(record, bibliography: true),
            BibliographicRecordKind.MagazineArticle or BibliographicRecordKind.NewspaperArticle => ChicagoPeriodicalPublication(record, bibliography: true),
            BibliographicRecordKind.WebPage => ChicagoWebPublication(record, bibliography: true),
            BibliographicRecordKind.Report or BibliographicRecordKind.Thesis => ChicagoReportPublication(record, bibliography: true),
            _ => string.Empty,
        };
        var contained = IsContainedWork(record.Kind) || record.Kind == BibliographicRecordKind.Thesis;
        CitationText title = contained ? QuotedTitle(record.Title) : CitationText.Italic(record.Title.Trim());
        var lead = CitationText.Join(". ", authors, title);
        var titleAndDetails = contained
            ? CitationText.Join(" ", lead, details.TrimStart('.', ',', ' '))
            : lead + details;
        var terminal = TerminalUrl(record, leadingSpace: true);
        return string.IsNullOrEmpty(terminal)
            ? titleAndDetails.Period()
            : (titleAndDetails.Period() + terminal).Period();
    }

    private static CitationText ChicagoBookPublication(CitationRecord record, bool bibliography = false)
    {
        var edition = Edition(record.Edition, chicago: true);
        var date = record.Issued.Year?.ToString(CultureInfo.InvariantCulture) ?? "n.d.";
        var publisher = record.Publisher;
        var contributors = JoinNonEmpty(bibliography ? ". " : ", ",
            record.Authors.Count > 0 && record.Editors.Count > 0
                ? (bibliography ? "Edited by " : "ed. ") + People(record.Editors, false) : string.Empty,
            record.Translators.Count > 0
                ? (bibliography ? "Translated by " : "trans. ") + People(record.Translators, false) : string.Empty);
        if (bibliography)
            return $"{PrefixPeriod(contributors)}{PrefixPeriod(edition.TrimEnd('.'))}{PrefixPeriod(JoinNonEmpty(", ", publisher, date))}";
        var publication = JoinNonEmpty(", ", publisher, date);
        return $"{PrefixComma(contributors)}{PrefixComma(edition)}{(string.IsNullOrEmpty(publication) ? string.Empty : $" ({publication})")}";
    }

    private static CitationText ChicagoChapterPublication(CitationRecord record, bool bibliography = false)
    {
        CitationText container = string.IsNullOrWhiteSpace(record.ContainerTitle) ? string.Empty : (CitationText)(bibliography ? " In " : " in ") + CitationText.Italic(record.ContainerTitle);
        if (record.Editors.Count > 0)
            container += (bibliography ? ", edited by " : ", ed. ") + People(record.Editors, false);
        return container + ChicagoBookPublication(record with { Editors = [] }, bibliography);
    }

    private static CitationText ChicagoJournalPublication(CitationRecord record, bool bibliography = false)
    {
        CitationText container = string.IsNullOrWhiteSpace(record.ContainerTitle) ? string.Empty : (CitationText)", " + CitationText.Italic(record.ContainerTitle);
        if (!string.IsNullOrWhiteSpace(record.Volume)) container += $" {record.Volume}";
        if (!string.IsNullOrWhiteSpace(record.Issue)) container += $", no. {record.Issue}";
        var date = ChicagoDate(record.Issued);
        if (!string.IsNullOrEmpty(date)) container += $" ({date})";
        if (bibliography && !string.IsNullOrWhiteSpace(record.Pages)) container += $": {record.Pages}";
        return container;
    }

    private static CitationText ChicagoPeriodicalPublication(CitationRecord record, bool bibliography = false)
    {
        CitationText container = string.IsNullOrWhiteSpace(record.ContainerTitle) ? string.Empty : (CitationText)", " + CitationText.Italic(record.ContainerTitle);
        var date = ChicagoDate(record.Issued);
        if (!string.IsNullOrEmpty(date)) container += $", {date}";
        return container;
    }

    private static string ChicagoWebPublication(CitationRecord record, bool bibliography = false)
    {
        var container = string.IsNullOrWhiteSpace(record.ContainerTitle) ? string.Empty : $", {record.ContainerTitle}";
        var date = ChicagoDate(record.Issued);
        if (!string.IsNullOrEmpty(date)) container += $", {date}";
        if (record.Issued.Year is null && record.Accessed.Year is not null)
            container += $", accessed {ChicagoDate(record.Accessed)}";
        return container;
    }

    private static string ChicagoReportPublication(CitationRecord record, bool bibliography = false)
    {
        var institution = string.Join(", ", new[] { record.Kind == BibliographicRecordKind.Thesis ? record.ThesisType : string.Empty,
            record.Institution, record.Publisher }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        var date = record.Issued.Year?.ToString(CultureInfo.InvariantCulture) ?? "n.d.";
        return bibliography
            ? PrefixPeriod(JoinNonEmpty(", ", institution, date))
            : $" ({JoinNonEmpty(", ", institution, date)})";
    }

    private static CitationText ApaInText(CitationRecord record, ManuscriptCitationItem item, CitationText author, string yearSuffix)
    {
        var year = ApaYear(record, yearSuffix);
        return CitationText.Join(", ", CitationText.Join(" ", item.Prefix, author), year, Locator(item, ApaLocatorLabel), item.Suffix);
    }

    private static string ApaYear(CitationRecord record, string suffix) =>
        (record.Issued.Year?.ToString(CultureInfo.InvariantCulture) ?? "n.d.")
        + (record.Issued.Year is null && suffix.Length > 0 ? "-" : string.Empty) + suffix;

    private static CitationText ApaBibliography(CitationRecord record, string yearSuffix)
    {
        var people = PrimaryContributors(record);
        var author = ApaPeople(people);
        if (record.Authors.Count == 0 && people.Count > 0)
            author += people.Count == 1 ? " (Ed.)." : " (Eds.).";
        var date = ApaYear(record, yearSuffix);
        if (record.Kind is BibliographicRecordKind.MagazineArticle or BibliographicRecordKind.NewspaperArticle or BibliographicRecordKind.WebPage
            && record.Issued.Month is >= 1 and <= 12)
        {
            date += ", " + CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(record.Issued.Month.Value);
            if (record.Issued.Day is >= 1 and <= 31) date += " " + record.Issued.Day.Value;
        }
        CitationText title = IsContainedWork(record.Kind) && record.Kind != BibliographicRecordKind.WebPage
            ? record.Title : CitationText.Italic(record.Title);
        var contributors = JoinNonEmpty("; ",
            record.Translators.Count > 0 ? ApaPeople(record.Translators, false) + ", Trans." : string.Empty,
            record.Authors.Count > 0 && record.Editors.Count > 0 && record.Kind == BibliographicRecordKind.Book
                ? ApaPeople(record.Editors, false) + (record.Editors.Count == 1 ? ", Ed." : ", Eds.") : string.Empty,
            Edition(record.Edition, chicago: false));
        if (record.Kind == BibliographicRecordKind.Book)
            title += PrefixSpaceParenthetical(contributors);
        if (record.Kind == BibliographicRecordKind.Thesis)
            title += PrefixSpaceBracket(JoinNonEmpty(", ", record.ThesisType, record.Institution));
        CitationText lead = people.Count == 0
            ? title.Period() + $" ({date})."
            : ((CitationText)author).Period() + $" ({date}). " + title.Period();
        CitationText details = record.Kind switch
        {
            BibliographicRecordKind.Book => ApaPublisher(record, record.Publisher),
            BibliographicRecordKind.JournalArticle => CitationText.Italic(record.ContainerTitle + PrefixComma(record.Volume)) + $"{Parenthesize(record.Issue)}{PrefixComma(record.Pages)}",
            BibliographicRecordKind.MagazineArticle or BibliographicRecordKind.NewspaperArticle => CitationText.Italic(record.ContainerTitle) + PrefixComma(record.Pages),
            BibliographicRecordKind.BookChapter => (CitationText)"In "
                + (record.Editors.Count > 0 ? ApaPeople(record.Editors, false) + (record.Editors.Count == 1 ? " (Ed.), " : " (Eds.), ") : string.Empty)
                + CitationText.Italic(record.ContainerTitle)
                + PrefixSpaceParenthetical(JoinNonEmpty(", ", Edition(record.Edition, false),
                    string.IsNullOrWhiteSpace(record.Pages) ? string.Empty : "pp. " + record.Pages)) + "." + PrefixSpace(record.Publisher),
            BibliographicRecordKind.Thesis => string.Empty,
            BibliographicRecordKind.Report => string.Join(". ", new[] { ApaPublisher(record, record.Institution), ApaPublisher(record, record.Publisher) }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)),
            _ => ApaPublisher(record, record.ContainerTitle),
        };
        var entry = CitationText.Join(" ", lead, details);
        var terminal = TerminalUrl(record, leadingSpace: true);
        return string.IsNullOrEmpty(terminal)
            ? entry.Period()
            : entry.Period() + terminal;
    }

    private static CitationText MlaInText(CitationRecord record, ManuscriptCitationItem item, bool ambiguousAuthor)
    {
        var author = ShortAuthor(record);
        CitationText lead = author;
        if (string.IsNullOrWhiteSpace(author) || ambiguousAuthor)
        {
            CitationText title = IsContainedWork(record.Kind)
                ? $"\"{ShortTitle(record.Title)}\"" : CitationText.Italic(ShortTitle(record.Title));
            lead = CitationText.Join(", ", lead, title);
        }
        return CitationText.Join(" ", item.Prefix, lead, item.LocatorValue, item.Suffix);
    }

    private static string ApaPublisher(CitationRecord record, string publisher) => record.Authors.Count == 1
        && string.Equals(record.Authors[0].Literal.Trim(), publisher.Trim(), StringComparison.OrdinalIgnoreCase)
        ? string.Empty : publisher.Trim();

    private static CitationText MlaBibliography(CitationRecord record)
    {
        var people = PrimaryContributors(record);
        var author = people.Count > 2
            ? Person(people[0], true) + ", et al."
            : People(people, invertFirst: true);
        if (record.Authors.Count == 0 && people.Count > 0)
            author += people.Count == 1 ? ", editor" : ", editors";
        CitationText title = IsContainedWork(record.Kind) ? MlaTitle(record) : CitationText.Italic(record.Title.Trim());
        var lead = CitationText.Join(". ", author, title);
        var contributors = JoinNonEmpty(", ",
            record.Translators.Count > 0 ? "translated by " + People(record.Translators, false) : string.Empty,
            record.Editors.Count > 0 && (record.Authors.Count > 0 || record.Kind != BibliographicRecordKind.Book)
                ? "edited by " + People(record.Editors, false) : string.Empty);
        if (record.Kind == BibliographicRecordKind.Book && contributors.Length > 0)
            contributors = char.ToUpperInvariant(contributors[0]) + contributors[1..];
        CitationText details = record.Kind switch
        {
            BibliographicRecordKind.Book => JoinNonEmpty(", ", contributors, Edition(record.Edition, chicago: true), record.Publisher, Year(record)),
            BibliographicRecordKind.JournalArticle => CitationText.Join(", ", CitationText.Italic(record.ContainerTitle), string.IsNullOrWhiteSpace(record.Volume) ? string.Empty : $"vol. {record.Volume}", string.IsNullOrWhiteSpace(record.Issue) ? string.Empty : $"no. {record.Issue}", Year(record), string.IsNullOrWhiteSpace(record.Pages) ? string.Empty : $"pp. {record.Pages}"),
            BibliographicRecordKind.BookChapter => CitationText.Join(", ", CitationText.Italic(record.ContainerTitle), contributors, Edition(record.Edition, true), record.Publisher, Year(record), string.IsNullOrWhiteSpace(record.Pages) ? string.Empty : "pp. " + record.Pages),
            BibliographicRecordKind.Thesis => JoinNonEmpty(", ", Year(record), record.Institution, record.ThesisType),
            BibliographicRecordKind.Report => JoinNonEmpty(", ", string.Join(", ", new[] { record.Institution, record.Publisher }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)), Year(record)),
            BibliographicRecordKind.MagazineArticle or BibliographicRecordKind.NewspaperArticle => CitationText.Join(", ",
                CitationText.Italic(record.ContainerTitle), MlaDate(record.Issued), string.IsNullOrWhiteSpace(record.Pages) ? string.Empty : "pp. " + record.Pages),
            _ => CitationText.Join(", ", CitationText.Italic(record.ContainerTitle), MlaDate(record.Issued)),
        };
        var identifier = !string.IsNullOrWhiteSpace(record.Doi)
            ? $"doi:{NormalizeDoi(record.Doi)}"
            : record.Url.Trim();
        var entry = CitationText.Join(" ", IsContainedWork(record.Kind) ? lead : lead.Period(), details);
        if (!string.IsNullOrWhiteSpace(identifier))
            entry = entry.Period() + " " + identifier;
        if (record.Kind == BibliographicRecordKind.WebPage && record.Accessed.Year is not null)
            entry = entry.Period() + " Accessed " + MlaDate(record.Accessed);
        return entry.Period();
    }

    private static string People(IReadOnlyList<CitationPerson> people, bool invertFirst)
    {
        var formatted = people.Select((person, index) => Person(person, invertFirst && index == 0)).ToList();
        return formatted.Count switch
        {
            0 => string.Empty,
            1 => formatted[0],
            2 => $"{formatted[0]}{(invertFirst ? "," : string.Empty)} and {formatted[1]}",
            _ => $"{string.Join(", ", formatted.Take(formatted.Count - 1))}, and {formatted[^1]}",
        };
    }

    private static string ApaPeople(IReadOnlyList<CitationPerson> people, bool invert = true)
    {
        var formatted = people.Select(person =>
        {
            if (!string.IsNullOrWhiteSpace(person.Literal)) return person.Literal.Trim();
            var initials = Initials(person.Given);
            return invert ? JoinNonEmpty(", ", person.Family.Trim(), initials) : JoinNonEmpty(" ", initials, person.Family.Trim());
        }).ToList();
        if (formatted.Count > 20)
            return string.Join(", ", formatted.Take(19)) + ", … " + formatted[^1];
        return formatted.Count switch
        {
            0 => string.Empty,
            1 => formatted[0],
            2 => $"{formatted[0]}, & {formatted[1]}",
            _ => $"{string.Join(", ", formatted.Take(formatted.Count - 1))}, & {formatted[^1]}",
        };
    }

    private static string Person(CitationPerson person, bool invert)
    {
        if (!string.IsNullOrWhiteSpace(person.Literal)) return person.Literal.Trim();
        return invert
            ? JoinNonEmpty(", ", person.Family.Trim(), person.Given.Trim())
            : JoinNonEmpty(" ", person.Given.Trim(), person.Family.Trim());
    }

    private static IReadOnlyList<CitationPerson> PrimaryContributors(CitationRecord record) =>
        record.Authors.Count == 0 && record.Kind == BibliographicRecordKind.Book ? record.Editors : record.Authors;

    private static string ChicagoAuthors(CitationRecord record, bool bibliography)
    {
        var people = PrimaryContributors(record);
        var names = !bibliography && people.Count > 2
            ? Person(people[0], false) + " et al."
            : bibliography && people.Count > 6
                ? string.Join(", ", people.Take(3).Select((person, index) => Person(person, index == 0))) + ", et al."
                : People(people, bibliography);
        if (record.Authors.Count == 0 && people.Count > 0)
            names += people.Count == 1 ? ", ed." : ", eds.";
        return names;
    }

    private static string ShortAuthor(CitationRecord record)
    {
        var people = PrimaryContributors(record);
        return people.Count switch
        {
            0 => string.Empty,
            1 => ShortPerson(people[0]),
            2 => ShortPerson(people[0]) + " and " + ShortPerson(people[1]),
            _ => ShortPerson(people[0]) + " et al.",
        };
    }

    private static string ShortPerson(CitationPerson person) =>
        string.IsNullOrWhiteSpace(person.Literal) ? person.Family.Trim() : person.Literal.Trim();

    private static string SortKey(CitationRecord record) => PrimaryContributors(record).Count == 0
        ? SortTitle(record.Title) : string.Join("\u001f", PrimaryContributors(record).Select(person => Person(person, true)));

    private static string SortTitle(string title)
    {
        var value = title.Trim();
        foreach (var article in new[] { "A ", "An ", "The " })
            if (value.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return value[article.Length..];
        return value;
    }

    private static string Initials(string given) => string.Join(' ', given.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => string.Join('-', part.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + "."))));

    private static string MlaTitle(CitationRecord record) => record.Kind switch
    {
        BibliographicRecordKind.BookChapter or
        BibliographicRecordKind.JournalArticle or
        BibliographicRecordKind.MagazineArticle or
        BibliographicRecordKind.NewspaperArticle or
        BibliographicRecordKind.WebPage => QuotedTitle(record.Title),
        _ => record.Title.Trim(),
    };

    private static string QuotedTitle(string title) => "\"" + ((CitationText)title.Trim()).Period().Text + "\"";

    private static bool IsContainedWork(BibliographicRecordKind kind) => kind is
        BibliographicRecordKind.BookChapter or
        BibliographicRecordKind.JournalArticle or
        BibliographicRecordKind.MagazineArticle or
        BibliographicRecordKind.NewspaperArticle or
        BibliographicRecordKind.WebPage;

    private static CitationText Compose(
        string prefix,
        CitationText lead,
        string locator,
        string suffix,
        string terminal,
        string locatorSeparator = ", ")
    {
        var body = CitationText.Join(" ", prefix, lead);
        if (!string.IsNullOrWhiteSpace(locator)) body += locatorSeparator + locator;
        if (!string.IsNullOrWhiteSpace(suffix)) body += $", {suffix.Trim()}";
        return (body + terminal).Period();
    }

    private static string Locator(ManuscriptCitationItem item, Func<string, string> label) =>
        string.IsNullOrWhiteSpace(item.LocatorValue)
            ? string.Empty
            : JoinNonEmpty(" ", label(item.LocatorLabel), item.LocatorValue.Trim());

    private static string ChicagoLocatorLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "page" or "p" => string.Empty,
        "chapter" => "chap.",
        "section" => "sec.",
        "paragraph" => "para.",
        "volume" => "vol.",
        var value => value,
    };

    private static string ApaLocatorLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "page" or "p" => "p.",
        "pages" or "pp" => "pp.",
        "chapter" => "Chapter",
        "section" => "Section",
        "paragraph" => "para.",
        var value => value,
    };

    private static string TerminalUrl(CitationRecord record, bool leadingSpace = false)
    {
        var value = !string.IsNullOrWhiteSpace(record.Doi)
            ? $"https://doi.org/{NormalizeDoi(record.Doi)}"
            : record.Url.Trim();
        return string.IsNullOrEmpty(value) ? string.Empty : (leadingSpace ? " " : ", ") + value;
    }

    private static string NormalizeDoi(string value) => value.Trim()
        .Replace("https://doi.org/", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("doi:", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string ChicagoDate(CitationDate date)
    {
        if (date.Year is null) return string.Empty;
        if (date.Month is not (>= 1 and <= 12)) return date.Year.Value.ToString(CultureInfo.InvariantCulture);
        var month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month.Value);
        return date.Day is (>= 1 and <= 31)
            ? $"{month} {date.Day.Value}, {date.Year.Value}"
            : $"{month} {date.Year.Value}";
    }

    private static string MlaDate(CitationDate date)
    {
        if (date.Year is null) return string.Empty;
        if (date.Month is not (>= 1 and <= 12)) return date.Year.Value.ToString(CultureInfo.InvariantCulture);
        string[] months = ["Jan.", "Feb.", "Mar.", "Apr.", "May", "June", "July", "Aug.", "Sept.", "Oct.", "Nov.", "Dec."];
        return JoinNonEmpty(" ", date.Day?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, months[date.Month.Value - 1], date.Year.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string Edition(string value, bool chicago)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var edition))
            return trimmed;
        var suffix = edition % 100 is 11 or 12 or 13 ? "th" : (edition % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return chicago ? $"{edition}{suffix} ed." : $"{edition}{suffix} ed.";
    }

    private static string ShortTitle(string value) => value.Trim();

    private static string Year(CitationRecord record) =>
        record.Issued.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string JoinNonEmpty(string separator, params string[] values) => string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
    private static string PrefixComma(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $", {value.Trim()}";
    private static string PrefixPeriod(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $". {value.Trim()}";
    private static string PrefixSpace(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $" {value.Trim()}";
    private static string PrefixSpaceParenthetical(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $" ({value.Trim()})";
    private static string PrefixSpaceBracket(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $" [{value.Trim()}]";
    private static string Parenthesize(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"({value.Trim()})";
}
