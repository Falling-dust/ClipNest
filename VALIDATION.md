# ClipNest 1.1 validation

Validated on 2026-11-11 on the supplied Windows machine with .NET Framework 4.8 (release 533325).

| Check | Result |
| --- | --- |
| Release executable build | Passed, no compiler warnings |
| History model | 11 tests passed |
| Window and production startup | 17 tests passed |
| Transparency | Rendered alpha, reset, hide/show retention, and native resize hit testing passed |
| Rendered default window | Inspected; screenshot in `artifacts/ClipNest-preview.png` |
| Native clipboard listener registration | Passed |
| Production tray creation and cleanup | Passed |
| Global shortcut registration | Existing running v1.0 keeps its shortcut; the v1.1 test correctly reported shortcuts unavailable. Ctrl+Alt+H was verified in v1.0 validation. |
| Isolated clipboard integration suite | Compiles; execution blocked by host permission |

The history checks cover strict capacity, oldest unpinned eviction, all-pinned rejection, exact deduplication, immutable payload ownership, Unicode, empty and nonempty editing, stable selection identity, memory bounds, and atomic rejection.

The window checks cover filters, search, text edits, image preview, file paths, pinning, deletion, minimum-size layout, undo isolation, incoming-clip selection/caret preservation, pause/float controls, hide/reopen, cleanup, and production listener/tray startup. The transparency check verifies rendered 50% alpha, native resize hit testing, retention after hide/show, the 70% transparency limit, and Reset returning to fully opaque. Sample-mode copy is verified to leave the Windows clipboard untouched. Production startup is paused immediately before pumping clipboard events and does not import existing content.

During v1.0 validation, Windows refused `CreateWindowStation` with Win32 error 5 (Access denied). The isolated native suite therefore stopped before accessing any clipboard; it did not fall back to the interactive clipboard. Clipboard integration code is unchanged in v1.1, and that suite was not rerun. Actual capture/copy round trips, image/file clipboard interoperability, contention retries, and source-application privacy markers have not been verified end-to-end on this host. The suite is supplied for a Windows environment that permits private window stations.

The executable is unsigned. No code-signing certificate, installer, automatic startup registration, or updater is included.
