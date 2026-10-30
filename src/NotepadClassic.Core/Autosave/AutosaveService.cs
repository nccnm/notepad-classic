using NotepadClassic.Core.Documents;
using NotepadClassic.Core.Editing;
using NotepadClassic.Core.Localization;
using NotepadClassic.Core.Services;

namespace NotepadClassic.Core.Autosave;

/// <summary>What <see cref="AutosaveService.Restore"/> did.</summary>
public enum AutosaveRestoreOutcome
{
    /// <summary>The id is not a session id; nothing was read.</summary>
    InvalidId,

    /// <summary>No manifest for this id (a restart after a crash, or already restored).</summary>
    NotFound,

    /// <summary>The manifest cannot be read or is not one this version knows; the data is kept.</summary>
    Unreadable,

    /// <summary>A clean untitled document: nothing to open. The data is deleted.</summary>
    Nothing,

    /// <summary>The document was clean: its file was opened again by path. The data is deleted.</summary>
    Reopened,

    /// <summary>The dirty text is back, marked dirty. The data is deleted.</summary>
    Restored,

    /// <summary>The dirty text could not be loaded; the user was told and the data is kept.</summary>
    Kept,
}

/// <summary>
/// Autosave and Restart Manager (<c>WM_QUERYENDSESSION</c>, <c>WM_ENDSESSION</c>, <c>WM_CLOSE</c>). Only a Restart Manager close
/// (<c>ENDSESSION_CLOSEAPP</c>) saves the document without asking; a normal logoff or shutdown
/// still asks the save-changes question.
/// </summary>
public sealed class AutosaveService
{
    /// <summary>An entry with a manifest that no run has restored within this time is deleted at
    /// startup. Restart Manager restarts a closed program within minutes, or at the next logon
    /// after a reboot; a month leaves room for a machine that stays off for a while.</summary>
    public static readonly TimeSpan CompleteEntryLifetime = TimeSpan.FromDays(30);

    /// <summary>Files without a manifest can never be restored (a save cut short); they are
    /// deleted after this time, which leaves a save still in progress in another window alone.</summary>
    public static readonly TimeSpan IncompleteEntryLifetime = TimeSpan.FromDays(1);

    private readonly DocumentService _documents;
    private readonly IEditorSurface _editor;
    private readonly IAutosaveStore _store;
    private readonly IProcessDirectory _directory;
    private readonly IDialogService _dialogs;
    private readonly ILocalizedStrings _strings;

    public AutosaveService(
        DocumentService documents, IEditorSurface editor, IAutosaveStore store, IProcessDirectory directory, IDialogService dialogs,
        ILocalizedStrings strings, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(sessionId);
        _documents = documents;
        _editor = editor;
        _store = store;
        _directory = directory;
        _dialogs = dialogs;
        _strings = strings;
        SessionId = sessionId;
    }

    /// <summary>This run's id (<c>CreateSessionGuidString</c>).</summary>
    public string SessionId { get; }

    /// <summary>The session is ending for Restart Manager (<c>+0x18</c> of the session object):
    /// <c>WM_CLOSE</c> then saves instead of asking.</summary>
    public bool Armed { get; private set; }

    /// <summary><c>RegisterApplicationRestart("RestartByRestartManager:&lt;id&gt;", 0)</c> at startup.</summary>
    public bool Register(IRestartRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration.Register(AutosaveSession.RestartCommandLine(SessionId));
    }

    /// <summary>
    /// <c>WM_QUERYENDSESSION</c>: true lets the session end. A save dialog of
    /// <see cref="DocumentService.CheckSave"/> being open blocks it with two beeps and string 24; a
    /// clean document agrees; a Restart Manager close agrees without asking when the autosave
    /// folder can take the text; otherwise the save-changes question decides.
    /// </summary>
    /// <param name="closeApp"><c>lParam &amp; ENDSESSION_CLOSEAPP</c>.</param>
    public bool QueryEndSession(bool closeApp)
    {
        if (_documents.State.IsSaveDialogOpen)
        {
            _dialogs.ShowMessage(_strings.GetString(StringKeys.CannotShutDown), MessageKind.ShutdownBlocked);
            return false;
        }

        if (!_documents.IsDirty)
        {
            return true;
        }

        if (closeApp && CanSave())
        {
            return true;
        }

        return _documents.CheckSave();
    }

    /// <summary><c>CanAutosave</c>: room for the text as UTF-16 with its NUL, <c>(length + 1) * 2</c> bytes.</summary>
    private bool CanSave()
    {
        try
        {
            return _store.CanSave((_editor.TextLength + 1L) * 2);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary><c>WM_ENDSESSION</c>: when the session really ends for Restart Manager, arms the
    /// session and saves.</summary>
    /// <param name="ending"><c>wParam</c>.</param>
    /// <param name="closeApp"><c>lParam &amp; ENDSESSION_CLOSEAPP</c>.</param>
    public void EndSession(bool ending, bool closeApp)
    {
        if (!ending || !closeApp)
        {
            return;
        }

        Armed = true;
        Save();
    }

    /// <summary>
    /// <c>WM_CLOSE</c> after the settings are saved: true lets the window close. A dirty document
    /// is saved without asking when the session is armed and the save works; otherwise the
    /// save-changes question decides.
    /// </summary>
    public bool ConfirmClose()
    {
        if (!_documents.IsDirty)
        {
            return true;
        }

        return (Armed && Save()) || _documents.CheckSave();
    }

    /// <summary>
    /// <c>SaveAutosaveState</c>: the text first when dirty, then the manifest, so a manifest never
    /// names text that is not there. Any failure deletes what was written, as the original deletes
    /// its registry key.
    /// </summary>
    public bool Save()
    {
        var state = _documents.State;
        var dirty = _documents.IsDirty;
        var manifest = new AutosaveManifest(
            AutosaveManifest.CurrentVersion, state.CurrentEncoding, state.IsUntitled ? null : state.Path, dirty, _directory.Current);
        try
        {
            if (dirty)
            {
                using var lease = _editor.AcquireText();
                _store.WriteText(SessionId, lease.Text);
            }

            _store.WriteManifest(SessionId, manifest.ToJson());
            return true;
        }
        catch (IOException)
        {
            TryDelete(SessionId);
            return false;
        }
    }

    /// <summary>
    /// <c>RestoreAutosaveState</c> for <c>RestartByRestartManager:&lt;id&gt;</c>. The current
    /// directory comes back first. Dirty text is loaded with the document's path, title and
    /// encoding and marked dirty; a clean named document is opened again by path; a clean untitled
    /// one opens nothing. The data is deleted afterwards, except when the dirty text failed to
    /// load or the manifest cannot be read (deliberate difference, parity.md).
    /// </summary>
    public AutosaveRestoreOutcome Restore(string id)
    {
        if (!AutosaveSession.IsValidId(id))
        {
            return AutosaveRestoreOutcome.InvalidId;
        }

        AutosaveManifest? manifest;
        try
        {
            var json = _store.ReadManifest(id);
            if (json is null)
            {
                return AutosaveRestoreOutcome.NotFound;
            }

            manifest = AutosaveManifest.Parse(json);
        }
        catch (IOException)
        {
            return AutosaveRestoreOutcome.Unreadable;
        }

        if (manifest is null)
        {
            return AutosaveRestoreOutcome.Unreadable;
        }

        if (!string.IsNullOrEmpty(manifest.CurrentDirectory))
        {
            _ = _directory.TrySetCurrent(manifest.CurrentDirectory);
        }

        AutosaveRestoreOutcome outcome;
        if (manifest.HasText)
        {
            if (!_documents.RestoreAutosavedText(_store.TextPath(id), manifest.DocumentPath, manifest.Encoding))
            {
                return AutosaveRestoreOutcome.Kept;
            }

            outcome = AutosaveRestoreOutcome.Restored;
        }
        else if (manifest.DocumentPath is { } path)
        {
            if (_documents.Load(path, manifest.Encoding) == LoadOutcome.OpenFailed)
            {
                _documents.ShowFileError(path, _documents.LastOpenError);
            }

            outcome = AutosaveRestoreOutcome.Reopened;
        }
        else
        {
            outcome = AutosaveRestoreOutcome.Nothing;
        }

        TryDelete(id);
        return outcome;
    }

    /// <summary>
    /// Deliberate difference (parity.md): the original never deletes autosave data that no run
    /// restores. At startup the port deletes entries older than <see cref="CompleteEntryLifetime"/>,
    /// or <see cref="IncompleteEntryLifetime"/> when they have no manifest.
    /// </summary>
    /// <returns>How many entries were deleted.</returns>
    public int CleanOrphans()
    {
        IReadOnlyList<AutosaveEntry> entries;
        try
        {
            entries = _store.List();
        }
        catch (IOException)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var entry in entries)
        {
            var lifetime = entry.HasManifest ? CompleteEntryLifetime : IncompleteEntryLifetime;
            if (entry.Id != SessionId && entry.Age >= lifetime && TryDelete(entry.Id))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private bool TryDelete(string id)
    {
        try
        {
            _store.Delete(id);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
