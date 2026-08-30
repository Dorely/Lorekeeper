# Image generation prompting for Lorekeeper

Last reviewed: 2026-08-30
Time-sensitive: yes — model aliases, snapshots, dimensions, supported parameters, and Responses API behavior must be rechecked.

## Executive finding

Reliable book illustration prompting is less about ornamental prose than about a stable production brief: identify the asset's use, describe the visible scene and subject, specify the camera/composition and lighting, name constraints, and explicitly divide reference traits into what remains invariant and what must change. The application should compile that brief, label references by actual provider order, derive page geometry, disable rasterized story text by default, and retain the complete audit trail. A mask can help point the model toward a region, but it is not a hard pixel constraint and must never be presented as one.

## Model and API facts

### Evidence

As of the review date, OpenAI describes `gpt-image-2` as its state-of-the-art image generation and editing model, accepting text and image input and producing image output. The model page lists flexible sizes, high-fidelity image inputs, the Image generation and Image edit endpoints, and the dated snapshot `gpt-image-2-2026-04-21` ([OpenAI model page](https://developers.openai.com/api/docs/models/gpt-image-2)).

The Image API supports one-shot generation and editing with direct model selection. The Responses API supports image generation inside multi-step or conversational flows, accepts image inputs in context, and enables iterative editing; a mainline model invokes the image tool and the tool manages its own image-model selection. The Responses image tool can expose a `revised_prompt`, which is important provenance rather than a replacement for Lorekeeper's submitted prompt ([OpenAI image-generation guide](https://developers.openai.com/api/docs/guides/image-generation)).

Current flexible-size constraints are: both edges multiples of 16; maximum edge 3,840 pixels; long-to-short ratio no greater than 3:1; total area from 655,360 through 8,294,400 pixels. The prompting guide cautions that very large outputs above the common 2K range can be more variable ([OpenAI prompting guide](https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide), [OpenAI image-generation guide](https://developers.openai.com/api/docs/guides/image-generation)).

For masked edits, OpenAI says the source and mask must have matching size and format, each be under 50 MB, and the mask must carry an alpha channel. The same guide explicitly says GPT Image masking is entirely prompt-based: the model uses the mask as guidance and may not follow its exact shape precisely. `gpt-image-2` processes inputs at high fidelity by default; the guide says not to send the adjustable `input_fidelity` parameter for this model ([OpenAI image-generation guide](https://developers.openai.com/api/docs/guides/image-generation)).

### Lorekeeper interpretation

Book pages need dimensions derived from the actual publish profile, not a menu of generic square/portrait/landscape presets. References need deterministic ordering because an instruction such as “Image 2 supplies the costume” becomes wrong if application code reorders inputs. The submitted prompt, provider revision, inputs, geometry, and outputs are all distinct records.

### Implementation decisions

- Use `gpt-image-2` as the configured production default and validate every derived or explicit raster against the current constraints.
- Persist the alias/model reported by the provider and the revised prompt; do not pretend a moving alias is a frozen model.
- Treat the Image API and Responses API as different orchestration modes. Lorekeeper's structured brief remains stable even if the transport changes.
- Reject conflicting page target, aspect ratio, and explicit size before sending a request.

### Platform limitations

- Model behavior is probabilistic: a valid prompt cannot guarantee exact anatomy, identity, text, layout, or continuity.
- High input fidelity improves preservation but does not make references immutable.
- A regional guide cannot guarantee that pixels outside its shape remain unchanged or that the boundary will be followed exactly.
- Provider prompt revision can alter emphasis; retaining both prompts is necessary for diagnosis.
- The 2026-07-14 review recorded transparent output as unsupported. The current guide now documents transparent `gpt-image-2` output as a preview feature for PNG and WebP; that preview status is the current limitation, not a general stability guarantee.

## Recommended prompt anatomy

### Evidence

OpenAI's production prompting guide recommends a consistent order — scene/background, subject, key details, then constraints — and recommends stating intended use. It favors short labeled segments for complex work, concrete visual details, explicit viewpoint/framing and lighting, and clearly named placement or negative space when layout matters. For people, pose, gaze, scale, body framing, and object interactions should be explicit ([OpenAI prompting guide](https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide)).

### Lorekeeper interpretation

The story context may explain *why* an illustration matters, but the image model needs a standalone account of *what is visible*. A page brief therefore separates narrative purpose from renderable scene details.

### Implementation decision: stable compilation order

1. Intended use.
2. Scene and background.
3. Subjects.
4. Appearance and continuity anchors.
5. Action.
6. Expression, pose, and gaze.
7. Setting and environment.
8. Style, medium, and palette.
9. Camera and framing.
10. Composition.
11. Lighting and mood.
12. Constraints.
13. Reference manifest in actual provider order.
14. Target geometry, gutter/focal-detail safety, and reserved text regions.
15. Rendered-text policy.

Fields may be blank when irrelevant, but `intendedUse` and `scene` are required. The compiler should not pad weak briefs with generic adjectives such as “beautiful” or “high quality.”

## Reference roles and continuity

### Evidence

The OpenAI guide recommends referring to each input by index and description, explaining how inputs interact, and explicitly separating exclusions/invariants from changes. For surgical edits it recommends “change only X” and repeating what must stay the same. Its children's-book example restates stable character appearance while advancing scene and action ([OpenAI prompting guide](https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide)).

### Lorekeeper interpretation

A character reference contains at least two categories:

- **Identity anchors:** facial structure, proportions, signature markings, age presentation, stable costume design, palette, and purpose-built style traits.
- **Scene-varying traits:** expression, pose, gesture, gaze, action, camera, crop, lighting, setting, weather, and sometimes clothing state.

“Use the same character” is incomplete. Without required changes, a model may copy the reference's pose or composition too literally.

### Implementation decisions

Every `ImageReferenceUse` contains an image ID, role, `traitsToPreserve`, and `traitsThatMustChange`. The compiler resolves the files first, labels them using actual 1-based provider order, and rejects empty IDs, duplicates, missing project images, and references known to be rejected or superseded. For an edit, provider input 1 is always the source canvas; supplemental references begin at input 2.

Prospective character studies stay unattached to canon entities until the user approves them. Rejected designs remain ordinary library assets unless the user asks to delete them, but their entity associations are removed and they cease to be eligible continuity references.

### Platform limitation

An ordinary scene is a weak identity reference because scenery, pose, occlusion, and small subject scale compete with the desired anchor. Lorekeeper should prefer an approved, tightly cropped subject study but cannot infer perfect identity isolation from metadata alone; a vision-capable inspection remains valuable.

## Generation, editing, regional guides, and iteration

### Evidence

OpenAI distinguishes generating a new image from editing an existing one. Its editing guidance repeatedly names the exact change and restates invariant camera, geometry, lighting, layout, and surrounding objects. Multi-turn Responses workflows support incremental refinement. A mask can indicate the intended region, but OpenAI characterizes GPT Image masking as prompt guidance that may not follow the exact shape precisely; it does not establish an enforced editable boundary ([OpenAI image-generation guide](https://developers.openai.com/api/docs/guides/image-generation), [OpenAI prompting guide](https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide)).

### Lorekeeper interpretation

- **Generate** for a genuinely new composition.
- **Edit** when the source canvas should remain recognizably the same image.
- **Regional-guided edit** only when the change is genuinely localized and words cannot reliably identify its location. The guide is a soft pointer, not protection from drift.
- Prefer one controlled correction per iteration over a long list of unrelated changes.
- Inspect the entire result after every edit, including outside a regional guide.

### Implementation decisions

`ImageEditBrief` requires both `change` and `preserve`. The compiler emits a direct “change only” instruction and an invariant list. Regional-guide mode narrows that discipline to the indicated area and asks for surrounding content to be preserved as closely as possible without promising pixel identity.

Lorekeeper uses a stricter application contract than the provider minimum: manual strokes and inline assistant shapes become a binary-alpha PNG with at least one editable pixel, and validation requires the exact source dimensions. Immediately before dispatch, the source is re-encoded to PNG without resizing so the first source and mask have the same format and raster. A guided edit is source-aspect-bound; `auto` derives a provider-valid raster with that aspect, while aspect-changing explicit sizes, layout-bound targets, and reserved regions are rejected. Normalization failure stops the job rather than falling back to an unmasked edit.

Assistant access is deliberately inline-only. `edit_project_image` may carry optional `regionalGuideShapes`, which are validated and persisted atomically with that edit. There is no standalone shape-mask tool and no reusable mask ID or label in assistant schemas. Persisted masks and completed-job links remain part of the audit history. Each job retains the structured brief, compiled prompt, source and reference manifest, mask/source IDs where applicable, geometry, provider revision, output IDs, and per-output provider identifiers.

## Story-page targeting and generated text

### Evidence

The OpenAI guide recommends explicit placement and negative-space instructions where layout matters. It can generate text when exact copy and typography are specified, but it also recommends iteration for imperfect text. W3C fixed-layout guidance warns that text baked into images is harder to adapt and requires textual alternatives; it recommends incorporating textual content into surrounding editable content when possible ([OpenAI prompting guide](https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide), [W3C EPUB Fixed Layout Accessibility](https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/)).

### Lorekeeper interpretation

Story copy belongs in semantic manuscript blocks bound to editable Designed Page
or cover text frames. Art prompts reserve the exact named text regions supplied
by the geometry target and protect focal subjects from trim, bleed, gutter,
spine, and barcode loss. In-image words are appropriate only when intrinsically
pictorial—for example a hand-lettered sign—and explicitly requested.

### Implementation decisions

- `allowRenderedText` defaults to false and the compiler adds “no text, logos, or watermarks.”
- A true value requires explicit desired text; otherwise validation fails.
- Layout-bound Figure, page-frame/surface, and cover-frame/surface targets derive the exact aspect and a deterministic moderate raster near 1.57 MP from the active authoring or release-cover geometry. A provider dimension mismatch is retained as an unattached project image and returned visually with a warning; it is not silently resized, cropped, or rejected.
- Full-page prompts add bleed-aware edges, trim safety, gutter avoidance for spreads, focal-detail safety, and buffered reserved-text rectangles.
- Alt text is stored separately and describes the resulting image's relevant subject, action, setting, and composition. It is never baked into the image.
- The assistant inspects the direct complete canvas in annotated mode during layout work and in clean mode after final scene mutations. These transient previews do not create project-image assets; Press page preview remains a separate pagination/output check.

## Reusable Lorekeeper examples

The examples show structured content, not a second free-form prompt path.

### Standalone illustration

```text
intendedUse: Full-page chapter opener for an upper-middle-grade fantasy novel.
scene: At dawn, a flooded library rises from a misty marsh; a narrow skiff has just reached its broken steps.
subjects: One twelve-year-old navigator and a black marsh heron.
appearance: Copper rain cape, cropped dark curls, round brass compass; no modern objects.
action: The child steadies the skiff and looks up as the heron opens its wings.
cameraFraming: Wide eye-level establishing view; full figure small but legible in the lower right.
composition: Library dominates upper left; navigable negative space in the pale sky at upper right.
lightingMood: Cool fog with a narrow warm sunrise rim; apprehensive but inviting.
constraints: Original illustration; no words, logo, watermark, frame, or photorealism.
```

### Character continuity in a new scene

```text
Reference 1 role: approved character identity study.
Preserve: facial structure, age, cropped curls, copper cape design, brass compass, watercolor line quality.
Must change: running three-quarter pose, alarmed sideward gaze, windblown cape, night lighting, low camera, ruined-market background.
```

### Designed Page art

```text
intendedUse: Background art for a two-page picture-book spread with editable story text overlaid later.
scene: A tiny fox and an enormous sleepy moon share tea on a rooftop above a quiet blue town.
composition: Fox and teapot in the lower-left leaf; moon occupies the upper-right leaf; make the exact regions supplied by the target geometry into a natural twilight band with simple forms, low detail, low contrast variation, and a stable value for transparent editable type—not visible caption panels.
constraints: Keep faces and the teapot away from trim and center gutter; no rendered text.
target: editionId, targetKind, and stable Figure/page/cover target ID; aspect and raster omitted so Lorekeeper derives both.
```

### Regional-guided edit

```text
change: Replace only the red umbrella in the guided region with a closed yellow parasol leaning against the same chair.
preserve: Keep every person, face, pose, chair, camera angle, crop, perspective, lighting direction, shadow, surrounding color relationship, and background detail as close to the source as possible.
constraints: Match contact shadow and watercolor edge texture; add nothing else; no text. Treat the guide as the intended focus, then inspect the complete output because boundary and outside-region changes remain possible.
```

## Contract mapping

| Finding | Runtime home | Lorekeeper contract/behavior |
|---|---|---|
| Stable labeled prompt order | Application automation | `IImagePromptComposer`, `ImageGenerationBrief`, `ImageEditBrief` |
| Reference index and role | Automation + persisted audit | `ImageReferenceUse`, compiled reference manifest |
| Preserve/change separation | Contract + code-owned tool rule | Required edit fields; per-reference preserve/change fields |
| Page geometry and quiet text regions | Automation | `LayoutGenerationTargetDescriptor`, exact variant/cover geometry, named-region appendix |
| No story text by default | Contract validation | `AllowRenderedText = false`; explicit exact text required to enable |
| Optional regional guidance | Application + tool contract | Manual binary-alpha guide editor; inline-only `regionalGuideShapes` on `edit_project_image`; no hard-boundary guarantee |
| Provider revision and output IDs | Persistence/UI | Image-generation job audit fields and job detail display |
| Character approval state | Workflow + entity attachment state | Prospective output unattached; rejected/superseded reference excluded |
| Creative visual direction | User-owned direction | Book Brief visual direction and Project Guidance, not hard-coded style |

## Sources

- OpenAI, “GPT Image Generation Models Prompting Guide,” Mandeep Singh and Emre Okcular, 21 April 2026, https://developers.openai.com/cookbook/examples/multimodal/image-gen-models-prompting-guide (accessed 2026-08-30).
- OpenAI, “Image generation,” update date not displayed, https://developers.openai.com/api/docs/guides/image-generation (accessed 2026-08-30).
- OpenAI, “GPT Image 2 Model,” update date not displayed; reviewed snapshot listing includes 21 April 2026, https://developers.openai.com/api/docs/models/gpt-image-2 (accessed 2026-08-30).
- W3C Publishing Maintenance Working Group, Wendy Reid (editor), “EPUB Fixed Layout Accessibility,” Group Note, 30 May 2024, https://www.w3.org/TR/2024/NOTE-epub-fxl-a11y-20240530/ (accessed 2026-07-14).
