# 0014: Allow all repository file types without application file caps

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

A live automatic Taskboard run completed planning and approval but failed patch
validation because its dependency-free Node smoke test used the standard `.mjs`
JavaScript module extension. The user explicitly requested removing file-type
and file-name restrictions, file-count caps, and file-size caps rather than
expanding the extension allowlist one type at a time.

## Decision

Collect any Git-changed regular repository file, regardless of name or extension.
Remove application-imposed file-count, per-file size, total-content, and
path-length caps. Keep repository confinement, Git metadata protection,
operation consistency, duplicate-path checks, and regular-file checks.

Store file content as bytes, serialized as base64 in the private persisted patch.
Pass bytes directly to Git when creating blobs so binary data, encodings, and
line endings survive publication unchanged. This is the current patch contract;
no text-patch compatibility decoder is provided.

Keep read-only AI source-inspection filters separate from change collection.
Git's ignore rules still control unstaged new files; explicitly staged files
are collected. Real credentials and secrets must not be committed, even though
configuration file names are not blocked.

## Consequences

Implementers can change configuration, dotfiles, lock files, extensionless files,
and binary assets. Patches may consume more memory, storage, and publication time;
platform and execution-time constraints still apply.

Deploy the API and worker together and finish active generation before changing
the patch contract. Existing stored patches using another contract cannot be
republished by the new code. This does not automatically execute tests or
establish a separate QA approval gate.
