using System.Text;
using NotepadClassic.Core.Autosave;
using NotepadClassic.Core.Localization;
using NotepadClassic.Core.Services;
using NotepadClassic.Core.Tests.Fakes;
using NotepadClassic.Core.Text;

namespace NotepadClassic.Core.Tests.Autosave;

/// <summary>Autosave and Restart Manager (when it saves, the save and
/// restore flows); <c>WM_QUERYENDSESSION</c>, <c>WM_ENDSESSION</c>, <c>WM_CLOSE</c>.</summary>
public sealed class AutosaveServiceTests
{
    private const string FilePath = @"C:\docs\Note.txt";
    private const string Id = Harness.SessionId;
    private const string OtherId = "00000000-1111-2222-3333-444444444444";

    private static Harness DirtyNamed(string text = "changed", TextEncodingKind encoding = TextEncodingKind.Utf8)
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "saved", encoding);
        h.Editor.Type(text);
        return h;
    }

    private static void Seed(Harness h, string id, AutosaveManifest manifest, string? text = null)
    {
        h.AutosaveStore.Manifests[id] = manifest.ToJson();
        if (text is not null)
        {
            h.Files.Files[h.AutosaveStore.TextPath(id)] = Encoding.Unicode.GetBytes(text);
        }
    }

    private static AutosaveManifest Manifest(string? path, bool hasText, TextEncodingKind encoding = TextEncodingKind.Utf8Bom, string? directory = @"C:\Work") =>
        new(AutosaveManifest.CurrentVersion, encoding, path, hasText, directory);

    // ---- Session id ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Session_id_is_upper_case_except_the_third_group()
    {
        var id = AutosaveSession.FormatId(new Guid("1a2b3c4d-5e6f-7a8b-9c0d-ef1234567890"));

        id.Should().Be("1A2B3C4D-5E6F-7a8b-9C0D-EF1234567890");
        AutosaveSession.IsValidId(id).Should().BeTrue();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Restart_command_line_is_the_prefix_and_the_id()
    {
        var registration = new FakeRestartRegistration();

        new Harness().Autosave.Register(registration).Should().BeTrue();

        registration.CommandLines.Should().Equal("RestartByRestartManager:" + Id);
        AutosaveSession.RestartCommandLine(Id).Should().HaveLength(60);
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData("1A2B3C4D-5E6F-7a8b-9C0D-EF1234567890", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", true)]
    [InlineData(null, false)]
    [InlineData("1A2B3C4D-5E6F-7a8b-9C0D-EF123456789", false)]
    [InlineData("1A2B3C4D-5E6F-7a8b-9C0D-EF12345678901", false)]
    [InlineData("1A2B3C4D_5E6F-7a8b-9C0D-EF1234567890", false)]
    [InlineData("1A2B3C4D-5E6F-7a8b-9C0D-EF123456789G", false)]
    [InlineData(@"..\..\..\..\Windows\System32\a.txt.xx", false)]
    public void Only_hex_ids_in_the_guid_layout_name_autosave_files(string? id, bool valid) =>
        AutosaveSession.IsValidId(id).Should().Be(valid);

    // ---- Manifest ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Manifest_round_trips_paths_with_commas()
    {
        var manifest = Manifest(@"C:\a,b\c, d.txt", hasText: true, TextEncodingKind.Gb18030, @"C:\x,y");

        AutosaveManifest.Parse(manifest.ToJson()).Should().Be(manifest);
        manifest.ToJson().Should().Contain("\"encoding\": \"Gb18030\"");
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"version":2,"encoding":"Utf8","documentPath":null,"hasText":false}""")]
    [InlineData("""{"version":1,"encoding":"Utf32","documentPath":null,"hasText":false}""")]
    [InlineData("""{"version":1,"encoding":99,"documentPath":null,"hasText":false}""")]
    [InlineData("""{"version":1,"encoding":"Utf8","documentPath":"","hasText":false}""")]
    public void Manifests_this_version_cannot_restore_are_refused(string json) =>
        AutosaveManifest.Parse(json).Should().BeNull();

    // ---- WM_QUERYENDSESSION ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void An_open_save_dialog_blocks_the_session_end_with_string_24()
    {
        var h = DirtyNamed();
        bool? answer = null;
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);
        h.Dialogs.DuringSaveChanges = () => answer = h.Autosave.QueryEndSession(closeApp: true);

        h.Documents.CheckSave();

        answer.Should().BeFalse();
        h.Dialogs.Messages.Should().Equal((ResxStrings.Instance.GetString(StringKeys.CannotShutDown), MessageKind.ShutdownBlocked));
        h.AutosaveStore.CanSaveRequests.Should().BeEmpty();
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_clean_document_lets_the_session_end(bool closeApp)
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "saved");

        h.Autosave.QueryEndSession(closeApp).Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_restart_manager_close_agrees_without_asking_when_the_text_fits()
    {
        var h = DirtyNamed("12345");

        h.Autosave.QueryEndSession(closeApp: true).Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().BeEmpty();
        h.AutosaveStore.CanSaveRequests.Should().Equal(12L);
        h.AutosaveStore.Manifests.Should().BeEmpty("the save happens at WM_ENDSESSION");
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData(SaveChangesChoice.Cancel, false)]
    [InlineData(SaveChangesChoice.DontSave, true)]
    public void A_normal_logoff_asks_the_save_changes_question(SaveChangesChoice choice, bool ends)
    {
        var h = DirtyNamed();
        h.Dialogs.SaveChangesAnswers.Enqueue(choice);

        h.Autosave.QueryEndSession(closeApp: false).Should().Be(ends);

        h.Dialogs.SaveChangesQuestions.Should().ContainSingle();
        h.AutosaveStore.CanSaveRequests.Should().BeEmpty();
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData(false)]
    [InlineData(true)]
    public void A_restart_manager_close_asks_when_the_autosave_folder_cannot_take_the_text(bool throws)
    {
        var h = DirtyNamed();
        h.AutosaveStore.CanSaveResult = false;
        h.AutosaveStore.CanSaveThrows = throws;
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);

        h.Autosave.QueryEndSession(closeApp: true).Should().BeFalse();

        h.Dialogs.SaveChangesQuestions.Should().ContainSingle();
    }

    // ---- WM_ENDSESSION and WM_CLOSE ----

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Only_an_ending_restart_manager_session_saves(bool ending, bool closeApp)
    {
        var h = DirtyNamed();

        h.Autosave.EndSession(ending, closeApp);

        h.Autosave.Armed.Should().BeFalse();
        h.AutosaveStore.Manifests.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void An_ending_restart_manager_session_arms_and_saves()
    {
        var h = DirtyNamed("unsaved text", TextEncodingKind.Utf16BE);

        h.Autosave.EndSession(ending: true, closeApp: true);

        h.Autosave.Armed.Should().BeTrue();
        h.AutosaveStore.TextOf(Id).Should().Be("unsaved text");
        h.AutosaveStore.ManifestOf(Id).Should().Be(Manifest(FilePath, hasText: true, TextEncodingKind.Utf16BE));
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Close_of_a_clean_document_never_asks()
    {
        var h = new Harness();

        h.Autosave.ConfirmClose().Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().BeEmpty();
    }

    [Theory]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    [InlineData(SaveChangesChoice.Cancel, false)]
    [InlineData(SaveChangesChoice.DontSave, true)]
    public void Close_asks_while_the_session_is_not_armed(SaveChangesChoice choice, bool closes)
    {
        var h = DirtyNamed();
        h.Dialogs.SaveChangesAnswers.Enqueue(choice);

        h.Autosave.ConfirmClose().Should().Be(closes);

        h.AutosaveStore.Manifests.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Close_of_an_armed_session_saves_again_without_asking()
    {
        var h = DirtyNamed("first");
        h.Autosave.EndSession(ending: true, closeApp: true);
        h.Editor.Type("second");

        h.Autosave.ConfirmClose().Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().BeEmpty();
        h.AutosaveStore.TextOf(Id).Should().Be("second");
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Close_of_an_armed_session_asks_when_the_save_fails()
    {
        var h = DirtyNamed();
        h.Autosave.EndSession(ending: true, closeApp: true);
        h.AutosaveStore.FailWriteManifest = true;
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);

        h.Autosave.ConfirmClose().Should().BeFalse();

        h.Dialogs.SaveChangesQuestions.Should().ContainSingle();
        h.AutosaveStore.Deleted.Should().Equal(Id);
        h.AutosaveStore.TextOf(Id).Should().BeNull("a failed save deletes what it wrote");
    }

    // ---- SaveAutosaveState ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_clean_document_saves_only_its_path_encoding_and_directory()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "saved", TextEncodingKind.Ansi);
        h.ProcessDirectory.Current = @"D:\Projects";

        h.Autosave.Save().Should().BeTrue();

        h.AutosaveStore.ManifestOf(Id).Should().Be(Manifest(FilePath, hasText: false, TextEncodingKind.Ansi, @"D:\Projects"));
        h.AutosaveStore.TextOf(Id).Should().BeNull();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void An_untitled_document_saves_no_path()
    {
        var h = new Harness();
        h.Editor.Type("draft");

        h.Autosave.Save().Should().BeTrue();

        var manifest = h.AutosaveStore.ManifestOf(Id)!;
        manifest.DocumentPath.Should().BeNull();
        manifest.HasText.Should().BeTrue();
        manifest.Encoding.Should().Be(h.State.DefaultEncoding);
        h.AutosaveStore.TextOf(Id).Should().Be("draft");
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_failed_text_write_leaves_no_manifest()
    {
        var h = DirtyNamed();
        h.AutosaveStore.FailWriteText = true;

        h.Autosave.Save().Should().BeFalse();

        h.AutosaveStore.Manifests.Should().BeEmpty();
        h.AutosaveStore.Deleted.Should().Equal(Id);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_failed_save_survives_a_failed_delete()
    {
        var h = DirtyNamed();
        h.AutosaveStore.FailWriteManifest = true;
        h.AutosaveStore.FailDelete = true;

        h.Autosave.Save().Should().BeFalse();
    }

    // ---- RestoreAutosaveState ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Dirty_text_comes_back_with_the_documents_path_title_and_encoding()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = Encoding.UTF8.GetBytes("on disk");
        Seed(h, OtherId, Manifest(FilePath, hasText: true, TextEncodingKind.Utf8Bom, @"D:\Somewhere"), "unsaved\r\ntext");

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Restored);

        h.Editor.Text.Should().Be("unsaved\r\ntext");
        h.Editor.Modified.Should().BeTrue();
        h.Documents.IsDirty.Should().BeTrue();
        h.State.Path.Should().Be(FilePath);
        h.State.IsUntitled.Should().BeFalse();
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf8Bom);
        h.Window.Title.Should().StartWith("*Note.txt");
        h.StatusView.Updates.Should().BePositive();
        h.ProcessDirectory.Changes.Should().Equal(@"D:\Somewhere");
        h.AutosaveStore.Deleted.Should().Equal(OtherId);
        h.Shell.RecentDocuments.Should().BeEmpty("the autosave file is not a document the user opened");
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Dirty_untitled_text_comes_back_untitled_and_dirty()
    {
        var h = new Harness();
        Seed(h, OtherId, Manifest(null, hasText: true, TextEncodingKind.Utf16LE), "draft");

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Restored);

        h.State.IsUntitled.Should().BeTrue();
        h.State.Path.Should().BeNull();
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf16LE);
        h.Window.Title.Should().StartWith("*" + ResxStrings.Instance.GetString(StringKeys.Untitled));
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Restored_text_keeps_nuls_a_leading_bom_character_and_gets_no_log_stamp()
    {
        var h = new Harness();
        const string text = "\uFEFF.LOG\0end";
        Seed(h, OtherId, Manifest(FilePath, hasText: true), text);

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Restored);

        h.Editor.Text.Should().Be(text);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_text_file_that_fails_to_load_keeps_the_data_and_the_untitled_document()
    {
        var h = new Harness();
        Seed(h, OtherId, Manifest(FilePath, hasText: true));

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Kept);

        h.Dialogs.Messages.Should().ContainSingle().Which.Kind.Should().Be(MessageKind.Warning);
        h.State.IsUntitled.Should().BeTrue();
        h.Editor.Modified.Should().BeFalse();
        h.AutosaveStore.Deleted.Should().BeEmpty();
        h.AutosaveStore.Manifests.Should().ContainKey(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_text_the_editor_refuses_keeps_the_data()
    {
        var h = new Harness();
        Seed(h, OtherId, Manifest(FilePath, hasText: true), "text");
        h.Editor.FailNextLoad = true;

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Kept);

        h.State.IsUntitled.Should().BeTrue();
        h.AutosaveStore.Manifests.Should().ContainKey(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_clean_document_is_opened_again_by_path_with_its_encoding()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = Encoding.UTF8.GetBytes("on disk");
        Seed(h, OtherId, Manifest(FilePath, hasText: false, TextEncodingKind.Ansi));

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Reopened);

        h.Editor.Text.Should().Be("on disk");
        h.Editor.Modified.Should().BeFalse();
        h.State.Path.Should().Be(FilePath);
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Ansi);
        h.AutosaveStore.Deleted.Should().Equal(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_clean_document_whose_file_is_gone_shows_the_file_error()
    {
        var h = new Harness();
        h.Files.ReadErrors[FilePath] = 2;
        h.SystemMessages.Messages[2] = "The system cannot find the file specified.";
        Seed(h, OtherId, Manifest(FilePath, hasText: false));

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Reopened);

        h.Dialogs.Messages.Should().Equal(("The system cannot find the file specified.", MessageKind.Warning));
        h.AutosaveStore.Deleted.Should().Equal(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_clean_untitled_document_opens_nothing()
    {
        var h = new Harness();
        Seed(h, OtherId, Manifest(null, hasText: false, directory: null));

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Nothing);

        h.Editor.Calls.Should().NotContain("LoadBuffer");
        h.ProcessDirectory.Changes.Should().BeEmpty();
        h.AutosaveStore.Deleted.Should().Equal(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void A_directory_that_cannot_be_made_current_does_not_stop_the_restore()
    {
        var h = new Harness();
        h.ProcessDirectory.Refuse = true;
        Seed(h, OtherId, Manifest(null, hasText: true, directory: @"Z:\gone"), "draft");

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Restored);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Nothing_is_read_for_an_invalid_or_unknown_id()
    {
        var h = new Harness();

        h.Autosave.Restore(@"..\..\..\..\..\..\..\..\..\..\x.txt").Should().Be(AutosaveRestoreOutcome.InvalidId);
        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.NotFound);
        h.AutosaveStore.Deleted.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void An_unreadable_manifest_is_kept()
    {
        var h = new Harness();
        h.AutosaveStore.Manifests[OtherId] = "{broken";

        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Unreadable);
        h.AutosaveStore.FailReadManifest = true;
        h.Autosave.Restore(OtherId).Should().Be(AutosaveRestoreOutcome.Unreadable);

        h.AutosaveStore.Manifests.Should().ContainKey(OtherId);
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Saved_state_restores_in_a_new_run()
    {
        var first = DirtyNamed("work in progress", TextEncodingKind.Utf16BE);
        first.Autosave.EndSession(ending: true, closeApp: true);
        var second = new Harness();
        second.Files.Files[first.AutosaveStore.TextPath(Id)] = first.Files.Files[first.AutosaveStore.TextPath(Id)];
        second.AutosaveStore.Manifests[Id] = first.AutosaveStore.Manifests[Id];

        second.Autosave.Restore(Id).Should().Be(AutosaveRestoreOutcome.Restored);

        second.Editor.Text.Should().Be("work in progress");
        second.State.Path.Should().Be(FilePath);
        second.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf16BE);
    }

    // ---- Orphans ----

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Old_entries_are_deleted_at_startup_and_recent_ones_are_kept()
    {
        var h = new Harness();
        h.AutosaveStore.Entries.AddRange(
        [
            new("00000000-0000-0000-0000-000000000001", true, TimeSpan.FromDays(30)),
            new("00000000-0000-0000-0000-000000000002", true, TimeSpan.FromDays(29)),
            new("00000000-0000-0000-0000-000000000003", false, TimeSpan.FromDays(1)),
            new("00000000-0000-0000-0000-000000000004", false, TimeSpan.FromHours(23)),
            new(Id, false, TimeSpan.FromDays(400)),
        ]);

        h.Autosave.CleanOrphans().Should().Be(2);

        h.AutosaveStore.Deleted.Should().Equal("00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000003");
    }

    [Fact]
    [Trait("Doc", "parity.md#autosave-va-restart-manager")]
    public void Cleanup_failures_are_ignored()
    {
        var h = new Harness();
        h.AutosaveStore.Entries.Add(new("00000000-0000-0000-0000-000000000001", true, TimeSpan.FromDays(90)));
        h.AutosaveStore.FailDelete = true;

        h.Autosave.CleanOrphans().Should().Be(0);
        h.AutosaveStore.FailList = true;
        h.Autosave.CleanOrphans().Should().Be(0);
    }
}
