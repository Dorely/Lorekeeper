# Lorekeeper v2 trailer acceptance evidence

This record covers the 90-second v2 feature trailer: a live, end-to-end
agentic book-making run in one synthetic project. Uploading or publishing the
trailer, and any Store or repository change, are separate launch actions.

## Tested source

Tested source commit: 71dfa6394f982fa700db59f3f243d7e64dd9357d

The Debug Blazor Server host was built from the commits below:

- **`cb77979`:** the plan, world, graph, draft and note clips, and the
  off-camera drafting of chapters 3–5.
- **`71dfa63`:** the publish follow-up, results, cover, "and more" and closing
  clips.

The only runtime change between them is the Publish page fix in `71dfa63`
(see "Defects found"). The first publish turn ran on `cb77979`. Its footage
stops before the defect appeared.

## Environment

- **Database:** the owner's development database (port 1455), used with the
  owner's permission.
- **Projects:** only "QA v2 Trailer — The Unquiet Oath" was created or changed.
  It holds only synthetic content.
- **Off limits:** provider and credential settings screens were never opened.
  No credential was read, copied or moved, and existing projects were not
  opened.
- **Model:** every agent turn used the OpenAI account model `gpt-6.1-sol`. The
  responses came from the real service, and none were simulated.
- **Capture:**
  - Headless Chromium recorded the app's page at 1920×1080. No desktop chrome,
    notifications or filesystem paths appear.
  - Long turns were recorded sparsely and sped up in the edit.
  - Every sped-up stretch carries an on-screen label giving the speed.
  - Every cut or follow-up turn carries an on-screen note.

## Scenario ledger

| Scenario | What happened live | Result |
|---|---|---|
| Plan | One Outline Chat prompt with the premise. The agent filled 12 Book Brief fields and created five chapter outlines (about 2 min). | Passed |
| World research | The World assistant ran real web searches (William of Newburgh, the Icelandic sagas, medieval relic beliefs). It wrote the "Risen of Vael" rules, places, factions and characters into the World Brief with a cited source list (about 9 min). | Passed |
| Graph | Story graph with 21 nodes and 52 links built from the brief and outline. | Passed |
| Draft | Editor Chat drafted Chapter 2 in full (2,116 words, about 2.5 min) from the outline, the brief and Chapter 1. Chapters 1 and 3–5 were drafted the same way off camera (1,870, 2,971, 2,780 and 2,872 words). | Passed |
| Note → fix | The user added a review note on a paragraph, with Review Edits on. The agent read the note, revised the paragraph, completed the note, and the change was kept with Keep All. | Passed |
| Publish | One Publish Assistant turn set the author, generated cover art with GPT Image, composed the Core cover, and created a 6×9 paperback, an EPUB ebook and a PDF ebook release. It prepared all three releases (271 s). Both ebooks prepared. | Passed after fix `71dfa63` (paperback: see below) |
| Paperback follow-up | A follow-up turn moved the paperback to Amazon KDP and prepared the interior and full-wrap cover PDFs (128 s). A second follow-up extended the art across the back and spine and prepared again (234 s). Both turns finished with no preparation errors and only artwork-resolution warnings. | Passed |
| Results | The app's own previews showed the cover PDF, a 54-page interior PDF in facing pages, and the EPUB preview. | Passed |

## Defects found

- **`71dfa63` (fixed):** the Publish page's file-preparation poller looked up
  its job with `First()`. Starting a new preparation deletes the target's
  finished jobs, so when the Publish Assistant prepared a release again, the
  page showed "Sequence contains no matching element". The poller now follows
  the target's active job, or stops when there is none. Retested: the agent
  prepared the paperback again in both follow-up turns, replacing its earlier
  jobs while the page watched the Paperback tab, and no error appeared.
- **"Other printer" print releases (not fixed; tracked separately):**
  - The release form offers "Other printer" for paperbacks, but preparation
    always rejects it ("Other-printer releases require a supported print
    artifact profile before preparation.").
  - The first publish turn chose it because the prompt named no printer.
  - The trailer shows the follow-up turn that moved the paperback to Amazon KDP.
- **Chapter 2 draft hang (not reproduced):** one Chapter 2 draft attempt
  stalled for over 17 minutes at `apply_manuscript_operations` and was
  abandoned. The same request then finished in 142 s on a freshly started
  host, and the cause was not diagnosed.
- **Concurrent editor clients (expected):** an earlier Chapter 1 take was
  discarded. A second browser session opened the editor while the first was
  recording, and the authoring fence correctly refused the second writer.

## Trailer cut

The edit is nine clips totalling 90 s:

| Clip | Seconds | Content |
|---|---:|---|
| intro | 4 | The prepared full-wrap cover PDF in the app's preview |
| plan | 13 | Premise → Book Brief and outline |
| world | 10 | Web research → World Brief with sources |
| graph | 4 | Story graph |
| draft | 10 | Chapter 2 drafted in the editor |
| note | 14 | Review note → agent revision → Keep All |
| publish | 24 | The publish turn, the two follow-up turns, and the prepared files |
| more | 6 | Images, History, Import / Export |
| outro | 5 | Interior spread from the prepared paperback |
