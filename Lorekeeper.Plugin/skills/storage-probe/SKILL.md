---
name: storage-probe
description: Open the Lorekeeper synthetic storage probe to inspect host file capabilities and manually check save and reopen behavior.
---

Call `open_storage_probe` to show the storage probe. It uses synthetic content
only and does not open a Lorekeeper database or call a model provider.

If the host cannot render this MCP App, report that limitation. Do not substitute
an ordinary browser page, mock `window.openai`, or simulated storage bridge.

An upload receipt does not prove persistence. Verify a saved file after closing
and reopening it. Distinguish file-library new-version uploads from conditional
updates to a file opened through the host file extension entrypoint.

Use the stale-save action only for the synthetic fixture and preserve the newer
revision. Do not use existing user projects for the probe.
