# ClipNest

A portable Windows clipboard shelf: keep your last 20 clips in a floating window, edit text, and copy an earlier item whenever you need it.

## Run

1. Download `ClipNest.exe` from the [latest GitHub release](https://github.com/Falling-dust/ClipNest/releases/latest), or use `dist\ClipNest.exe` after building locally. Open it on Windows 10 or Windows 11.
2. Copy text, an image, or files in another application. New clips appear automatically, newest first.
3. Select a clip to inspect it. Text edits save automatically to the in-memory history.
4. Click **Copy** to put the selected clip on the Windows clipboard, then paste it into the destination application.

The application uses .NET Framework 4.8, which is included in current Windows 10/11 installations. No installer, account, or separate Python runtime is needed. You can move the executable to another folder and launch it there.

To switch from an earlier running version, choose **Exit and forget history** from its tray menu, then open the new executable. Exiting ends that clipboard-history session; the current Windows clipboard remains available.

## Everyday controls

- **Search** filters the history without removing items.
- **Pin** protects a clip from automatic eviction. Pins count toward the same 20-item limit and last only for this app session. If all 20 clips are pinned, new clips are skipped until you unpin or remove one.
- **Pause** temporarily stops capturing. Resume starts with the next clipboard change; it does not import the clipboard's current contents.
- **Float** switches the window's always-on-top behavior. It is on by default; the window can be dragged and resized.
- **Transparency** adjusts the entire window immediately from 0% (fully opaque) to 70% transparent. Drag the slider below the editor, or focus it and use the arrow keys. **Reset** restores a fully opaque window. The tray menu also offers **Reset transparency**. This preference lasts for the current session and resets on the next launch.
- **Delete** removes the selected clip. **Clear history** removes every clip, including pinned ones, after confirmation.
- **Clear Windows clipboard** is a separate menu action. Clearing the history alone leaves the Windows clipboard unchanged.
- Closing the window with **X**, or pressing **Esc**, hides it in the system tray. Capture continues while it is hidden.
- Use the tray icon to show/hide the window, pause capture, clear history, or **Exit**. Exit releases the in-memory history; the current Windows clipboard is left unchanged.

Editing a saved text clip does not change the system clipboard until you copy the clip again. Text is handled as plain Unicode text; rich formatting is not preserved. Images can be previewed and copied; file clips contain file/folder paths, not backups of the files themselves. Copying file clips copies the references and does not move, delete, or modify the source files.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+V` | Show or hide ClipNest from another application |
| `Esc` | Hide the window |
| `Ctrl+F` | Focus search |
| `Ctrl+Enter` | Copy the selected clip |
| `Ctrl+Shift+P` | Pin or unpin the selected clip |
| `Delete` while the history list has focus | Remove the selected clip |

If another application already owns `Ctrl+Alt+V`, ClipNest tries `Ctrl+Alt+H` instead. The active shortcut is shown in the footer, tray tooltip, and Help. If both are unavailable, the tray icon remains usable.

## Storage and limits

ClipNest does not save clipboard content to disk, send it over the network, or add itself to Windows startup. History starts empty on each launch. It also does not import the pre-existing clipboard when it starts.

The app honors supported clipboard exclusion markers supplied by source applications. This is not universal password detection: ordinary copied text may still include secrets. Pause capture before copying material you do not want in the history, or clear the history afterward. Windows and other applications may maintain their own clipboard histories independently of ClipNest.

| Limit | Value |
| --- | --- |
| History | 20 clips, including pinned clips |
| Text | 1,000,000 UTF-16 characters per clip |
| Image | 16 MiB encoded PNG and 16 megapixels |
| Files/folders | 200 paths per clip |
| Total retained payload | 64 MiB |

When capacity or the payload budget is reached, the oldest unpinned clips are removed first. A clip that cannot fit without removing pinned items is skipped. A rejected edit leaves its previous saved contents intact. Actual process memory also includes the interface, decoded image previews, and temporary clipboard buffers, so the payload budget is not a process-memory ceiling.

Duplicate payloads reuse the existing history entry and move it to the top. Text comparison is exact, including case and whitespace. Pinning does not make an item permanent, and clearing or exiting also removes pinned clips.

## Build and test

From the project folder in PowerShell:

```powershell
.\build.ps1
.\build.ps1 -Test
```

The build script locates the installed .NET Framework C# compiler and produces `dist\ClipNest.exe`. It uses Windows framework assemblies and does not download packages. `-Test` also runs the history-model tests.

If an earlier build is running, keep that session open and build to a separate folder:

```powershell
.\build.ps1 -Test -OutputDirectory .\dist\v1.1
.\tests\run-ui-tests.ps1 -ApplicationPath .\dist\v1.1\ClipNest.exe
```

For the window tests and optional isolated native clipboard tests:

```powershell
.\tests\run-ui-tests.ps1
.\tests\run-clipboard-tests.ps1
```

The window suite uses sample clips and checks production listener/tray startup with capture paused before processing events. It does not read or write clipboard content. The native suite requires permission to create a private Windows window station; it stops before clipboard access if isolation is unavailable. See `VALIDATION.md` for the checks completed on this machine.

For a sample-data preview without reading or changing the real clipboard:

```powershell
.\dist\ClipNest.exe --demo
```

Source is in `src`; model tests are in `tests`. The build is unsigned. For wider distribution, sign the executable with your own code-signing certificate; Windows may show its usual warnings for an unsigned download.
