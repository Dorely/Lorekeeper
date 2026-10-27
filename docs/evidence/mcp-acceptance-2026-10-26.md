# Local MCP acceptance — 26 October 2026

## Environment and isolation

Windows x64, .NET SDK 10.0.401, official ASP.NET MCP SDK 2.3.0.
HTTP validation used the explicit `http` profile on initially unoccupied port
1455. Electron used the unchanged primary `electron` profile after the HTTP host
was stopped. Both used the same disposable acceptance database and history root;
neither developer nor installed-user creative databases were changed.

Clients: Codex CLI 0.164.0-alpha.11.1 from the installed desktop distribution,
and the official JavaScript MCP SDK 1.32.2 in an interactive Node session.
The existing older PATH CLI was preserved. No MCP test project or general test
harness was created. Raw local logs and disposable outputs were kept under the
ignored `.artifacts/mcp-acceptance/20261026` directory; credentials are excluded
from this record.

## Observed connection and setup results

- Lorekeeper's actual settings flow created one disposable grant, provisioned
  the Codex connection and installed `lorekeeper-external-access` 1.0.0 through
  the supported `~/.agents/skills` directory.
- A fresh compatible Codex session discovered and read the installed skill,
  connected over MCP, opened Outline and Editor workspaces, read context,
  created story/canon records, edited and read back the manuscript, and finished
  with a complete-project checkpoint while Review Edits was off.
- The official SDK discovered tools/resource templates, called the application,
  survived host restarts and recovered unfinished logical work. Final discovery
  advertised 94 tools. Workspaces and role/target changes did not navigate the UI.
- Repeat installation reused the owned configuration entry. An intentionally
  customized skill was preserved with a visible setup message. Manual fallback
  produced read-only private JSON with a loopback HTTP endpoint and bearer header;
  its credential was not displayed in validation output.
- Restricting a live connection to no projects emptied discovery and denied
  exact reads and binary downloads. Removing manuscript/image/voice reads removed
  the manuscript resource template, filtered context, denied exact source reads,
  and returned no restricted search results. Write discovery changed immediately.
- A manuscript batch containing an allowed insertion and a forbidden deletion
  was rejected as a whole; revision and all blocks remained identical.
- Rotation returned HTTP 401 for the old credential. A fresh transport using
  the automatically updated configuration connected and recovered the task.
  Revocation returned HTTP 401 and prevented the existing client from listing
  tools. Missing credentials and hostile Host/Origin requests were rejected
  without HTML error responses. Unknown workspace identities required onboarding.
- MCP calls, workspace opening, saved mutations and output preparation also ran
  against the actual Electron-hosted process. Native Electron screenshot capture
  timed out twice after target/window recovery; native UI interaction is **not**
  counted as verified.

## Live application exercises

| Surface | Observed result |
|---|---|
| Outline | Created a project, act, chapter, Character, Location, chapter-owned Event/beat and fact; created a manual relationship and chapter association. The browser displayed the imported records and accepted an authored character-description edit. |
| Editor | Exact semantic readback retained stable block IDs; inline Strong formatting and a Figure survived. A stale write returned revision conflict and preserved newer prose. The browser showed the external refresh banner, current formatting and artwork. Created and edited independent Designed Page semantic content and scene, inspected real canvas/chapter pixels. Release-only prose changed to “at sunrise” while Core retained “at dawn.” |
| World | Updated World Brief and independent Working Notes; executed configured SerpApi research; read, retained, searched and exactly reread a real public source through guarded application services. One NPS URL read failed and is not evidence of successful research. |
| Images | Uploaded existing artwork through the bundled binary helper, inspected pixels, cropped and attached a derivative to an entity. OpenRouter with `bytedance-seed/seedream-5-0-flash` generated a lantern image and edited its glow from amber to green; actual returned pixels were inspected. A new queued job was cancelled and returned Cancelled with no output. |
| Voice | Created/revised writing samples and a Character voice profile; exact retry reused the original receipt; stale profile content was rejected. A timed-out early sample creation eventually committed; recovery receipts remained readable after task completion. |
| Publish | Updated Core metadata and cover scene, inspected clean/annotated canvas and actual PDF pixels, created an EPUB release, enabled release editing, authored a publication section, inspected readiness, prepared outputs and cancelled a queued Core preparation. Accessibility and unplaced-semantic-content diagnostics blocked an incomplete fixture; after correcting it, EPUB preparation reached Ready without diagnostics. |

Actual binary helper downloads matched advertised lengths and SHA-256:

| Output | Bytes | SHA-256 |
|---|---:|---|
| Core reading PDF, 4 pages | 2645950 | `78b5cd8dde2d13ba7d7ea114d1dc34638761ca932740e4c26c5a46c6cf5f57ed` |
| Prepared EPUB | 744164 | `c8331664d6e26185b0f931a656d5091206a22441f018e902b73d00861e9dcdb0` |

The EPUB ZIP contained its correct mimetype, OPF and 17 entries, including the
release-specific story. These results establish local artifact preparation and
retrieval, not external publication or vendor acceptance.

## Disposable game round trip and recovery

The fixture imported story structure, semantic prose, canon, relationships,
artwork and a canonical entity-image association through ordinary services.
Stable external keys mapped its chapter, character and artwork to exact IDs.
An initial immutable saved-state capture was acknowledged as the import baseline.

In the browser, the story changed from dusk to dawn and then from “Warm light”
to “Emerald light”; the second change occurred after capture. Exact before/after
reads proved the earlier capture still held “Warm light” and a later comparison
held “Emerald light.” Acknowledgement replay returned the same outcome; an older
capture could not replace the newer acknowledged baseline. A separate authored
character edit appeared in the canonical graph comparison. All five changed
structured files were read completely before/after and copied into a disposable
external game fixture before acknowledgement. Later saved edits remained outside
that captured baseline. No real game repository was modified.

Review Edits on finished work without checkpointing and left mutations pending.
Review Edits off created the complete-project checkpoint in the fresh Codex
workflow. Host restarts preserved logical work, and stale context continuations
restarted at page one. Final fixture inspection found four completed tasks,
52 durable mutation receipts, zero pending projection repairs, four immutable
captures and one revoked grant.

## Limits and unperformed checks

- Contest Mode's live edit restriction was not exercised before fixture cleanup:
  the isolated fixture had no configured chat provider and the UI disabled Contest Mode. Existing
  owning Contest guards remain in the shared mutation path; compilation is not
  counted as a live Contest test.
- No embedding provider, attached Git remote, OAuth flow, external publication,
  physical print vendor or non-Windows platform was exercised.
- Claude Code's guided installation and a fresh Claude skill session were not
  exercised. Codex installation/discovery and the official SDK connection were.
- Native Electron window capture/interaction remains unverified because of the
  capture timeouts. Electron-hosted MCP operation and browser UI work were
  exercised separately.
- Delayed projection repair was not deliberately fault-injected. Atomic
  content/receipt commit, rollback and migration preservation have automated
  data-safety coverage; the live fixture ended with no outstanding repairs.

## Cleanup and final verification

Every validation HTTP/Electron process and the interactive SDK client was stopped;
port 1455 was released. The disposable grant was revoked. Its private connection
file, owned test skill and configuration entry were removed. Parsed comparison
confirmed all unrelated Codex configuration and its four existing MCP servers
were preserved. The secret-bearing disposable database and its retained transfer
storage were removed; sanitized creative outputs remain in the ignored evidence
directory. Original provider credentials and creative content were preserved.

Final verification passed: `dotnet build Lorekeeper.sln` with zero warnings and
errors; `dotnet test Lorekeeper.Tests\Lorekeeper.Tests.csproj` with 203 passed,
zero failed and zero skipped; notice checking for 75 exact NuGet packages; and
bundled skill validation. `git diff --check` passed. Press sources were not
changed; live PDF preparation used native Press 2.1.17. No further screen
interaction was performed after the user resumed using the PC.
