# Visual development and concept-art practice

Last reviewed: 2026-08-13
Research access date: 2026-08-13
Scope: art-direction discovery and canonical visual development for Lorekeeper
projects, characters, locations, creatures, props, costumes, and other entities.

This brief is an engineering reference for compact code-owned assistant
instructions. It is not a prompt payload and does not prescribe one house style.
Project-specific choices belong in Book Brief Visual Direction and approved
canonical entity references.

## Professional role

Visual development establishes the look of characters and worlds from story
intent. Disney Animation describes it as exploring and establishing a film's
look from the script and director's vision, using appeal, color, design,
composition, varied techniques, and styles. The durable Lorekeeper equivalent
is a concept artist who reads story canon, helps the author choose a coherent
visual language, and turns approved designs into reusable references.

Concept art is exploratory. Canon is selective. A useful workflow therefore
separates divergent exploration from convergence:

1. establish story purpose, audience, genre, emotional promise, cultural
   context, constraints, and existing visual canon;
2. propose two or three visibly distinct directions;
3. test the highest-impact differences with controlled variants;
4. compare narrative fit, clarity, continuity, and reproducibility;
5. obtain explicit user approval and save a concise Visual Direction;
6. create canonical studies and attach only approved references;
7. label stable variants and detach superseded associations.

Exploratory images stay in the library but do not become canonical merely
because an entity appears in them.

## A practical style vocabulary

Style is multi-dimensional. Medium, movement, genre, and mood are not
interchangeable labels. The assistant should describe observable choices along
these axes:

| Axis | Useful questions and terms |
|---|---|
| Medium/process | Watercolor wash, opaque gouache, ink, colored pencil, pastel, oil/acrylic, collage, cut paper, linocut/woodcut, engraving, flat vector, photographic, dimensional/stop-motion-like, 3D rendered |
| Mark and edge | Loose or controlled marks; dry-brush, granular, clean, broken, hard, soft, lost-and-found, outlined |
| Rendering | Flat, graphic, modeled, painterly, naturalistic, stylized, abstracted; degree of visible detail |
| Shape/proportion | Geometric, organic, angular, rounded, elongated, squat, exaggerated, realistic; repeated motif and silhouette language |
| Palette/value | Restricted or broad palette; warm/cool bias; saturation; dominant, secondary, and accent colors; high-key, low-key, or strong value grouping |
| Light/texture | Diffuse, theatrical, natural, rim-lit, chiaroscuro; smooth, paper grain, impasto, fabric, carved, printed, weathered |
| Composition/camera | Symmetry/asymmetry, density, negative space, focal hierarchy, close/wide view, eye level, high/low angle, lens-like perspective |
| Design lineage | Historical period, regional craft, folk practice, Art Nouveau, Art Deco, impressionistic, expressionistic, mid-century, retro-futurist, comics traditions—translated into visible traits |
| Narrative fit | Intended reader, genre expectations, humor, threat, wonder, intimacy, pace, and the descriptive needs of the manuscript |

The Getty Art & Architecture Thesaurus demonstrates why a controlled,
hierarchical vocabulary is more useful than a flat adjective list: work types,
materials, styles, cultures, processes, and techniques are related but distinct
concepts. Runtime prompts should use plain observable traits, while the research
brief can retain broader art terminology for guidance.

Named references can help users communicate, but the assistant should translate
them into properties the user can confirm and an image model can execute. Terms
such as "whimsical," "cinematic," or "painterly" are incomplete until tied to
shape, mark, value, light, texture, and composition.

## Canonical character and object design

A character sheet should resolve stable identity without freezing performance.
Canonical fields include silhouette, proportions, facial structure, apparent
age, complexion, hair, distinguishing features, costume construction, palette,
materials, and scale. Expression, pose, gesture, gaze, action, framing,
temporary condition, and scene lighting remain variable unless an approved
reference is explicitly labeled as a variant such as "winter costume."

Creatures, vehicles, artifacts, costumes, organizations, and custom entities
use the same distinction. Record construction, materials, motifs, wear, scale,
and functional features as invariants; record state, damage, loadout, activity,
and environment as variants.

Broad narrative scenes are poor identity references when a subject can be
isolated. Use an isolated study or a tight inspected crop for characters,
creatures, and props. Each subject in a group scene needs its own isolated
reference if it is to become canon.

## Canonical environment design

A location is not reducible to a background crop. Its stable identity includes
geography, spatial organization, architecture, materials, scale, landmarks,
circulation, palette anchors, and characteristic light. An intentional broad
environmental study can therefore be the right canonical reference.

Exterior and interior views should be labeled by role. Day/night, season,
weather, historical period, occupancy, celebration, abandonment, damage, and
reconstruction are variants unless the story defines one as the location's
default state. Future scene prompts should preserve the stable place while
explicitly changing these atmospheric variables.

## Prompt and product mapping

The Images assistant should:

- read manuscript, outline, fact, entity, and image data only as grounding;
- create free-standing library images without page or cover geometry;
- write standalone structured prompts using approved visual traits;
- order and label reference images by role and preserved/changed traits;
- inspect outputs before describing them as successful;
- save Visual Direction only after explicit approval and a current-value read;
- attach only deliberate canonical references with descriptive variant labels;
- hand page illustration/composition to Editor and publication/cover work to
  Publish.

The story-building engine can then use Visual Direction and canonical-reference
metadata to write consistent descriptions even when the book is not image-led.

## Sources

- Walt Disney Animation Studios, [Visual Development](https://www.disneyanimation.com/process/visual-development/).
- Walt Disney Animation Studios, [Look Development](https://www.disneyanimation.com/process/look-development/).
- Getty Research Institute, [Art & Architecture Thesaurus](https://www.getty.edu/research/tools/vocabularies/aat/).
- Getty Research Institute, [About the AAT](https://www.getty.edu/research/tools/vocabularies/aat/about.html).
