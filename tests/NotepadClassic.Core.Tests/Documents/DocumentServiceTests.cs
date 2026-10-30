using System.Text;
using NotepadClassic.Core.Documents;
using NotepadClassic.Core.Editing;
using NotepadClassic.Core.IO;
using NotepadClassic.Core.Services;
using NotepadClassic.Core.Tests.Fakes;
using NotepadClassic.Core.Text;

namespace NotepadClassic.Core.Tests.Documents;

/// <summary>Document commands: open, save, save changes question, <c>.LOG</c>.</summary>
public sealed class DocumentServiceTests
{
    private const string FilePath = @"C:\docs\Note.txt";

    private static FileDialogResult Chosen(string path, TextEncodingKind encoding = TextEncodingKind.Utf8) => FileDialogResult.Ok(path, encoding);

    // ---- Dirty state and title ----

    [Fact]
    [Trait("Doc", "parity.md#tieu-de-status-bar")]
    public void Untitled_empty_document_is_never_dirty_even_when_modified()
    {
        var h = new Harness();
        h.Editor.Type("abc");
        h.Documents.IsDirty.Should().BeTrue();

        h.Editor.Type("");

        h.Editor.Modified.Should().BeTrue();
        h.Documents.IsDirty.Should().BeFalse();
    }

    [Fact]
    [Trait("Doc", "parity.md#tieu-de-status-bar")]
    public void Named_empty_document_follows_the_modified_flag()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "x");

        h.Editor.Type("");

        h.Documents.IsDirty.Should().BeTrue();
    }

    [Fact]
    [Trait("Doc", "parity.md#tieu-de-status-bar")]
    public void Title_shows_star_when_dirty_and_is_only_rewritten_when_the_star_changes()
    {
        var h = new Harness();
        h.Documents.UpdateTitle();
        h.Window.Title.Should().Be("Untitled - Notepad Classic Port");

        h.Editor.Type("a");
        h.Editor.Type("ab");

        h.Window.Titles.Should().Equal("Untitled - Notepad Classic Port", "*Untitled - Notepad Classic Port");

        h.Editor.Type("");

        h.Window.Title.Should().Be("Untitled - Notepad Classic Port");
    }

    [Fact]
    [Trait("Doc", "parity.md#tieu-de-status-bar")]
    public void Title_of_a_saved_file_uses_the_file_title_provider()
    {
        var h = new Harness();

        h.OpenSaved(FilePath, "x");

        h.Window.Title.Should().Be("Note.txt - Notepad Classic Port");
    }

    // ---- CheckSave ----

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void CheckSave_does_not_ask_when_clean()
    {
        var h = new Harness();

        h.Documents.CheckSave().Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().BeEmpty();
    }

    [Theory]
    [Trait("Doc", "parity.md#lenh")]
    [InlineData(SaveChangesChoice.DontSave, true)]
    [InlineData(SaveChangesChoice.Cancel, false)]
    public void CheckSave_answers(SaveChangesChoice choice, bool proceed)
    {
        var h = new Harness();
        h.Editor.Type("text");
        h.Dialogs.SaveChangesAnswers.Enqueue(choice);

        h.Documents.CheckSave().Should().Be(proceed);

        h.Dialogs.SaveChangesQuestions.Should().Equal("Do you want to save changes to Untitled?");
        h.Files.Saves.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void CheckSave_names_the_full_path_and_saves_a_named_file_directly()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "old");
        h.Editor.Type("new");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Save);
        var dialogFlag = false;
        h.Dialogs.DuringSaveChanges = () => dialogFlag = h.State.IsSaveDialogOpen;

        h.Documents.CheckSave().Should().BeTrue();

        h.Dialogs.SaveChangesQuestions.Should().Equal($"Do you want to save changes to {FilePath}?");
        dialogFlag.Should().BeTrue("WM_QUERYENDSESSION refuses shutdown while the question is up");
        h.State.IsSaveDialogOpen.Should().BeFalse();
        h.Dialogs.SaveAsRequests.Should().BeEmpty();
        h.Files.TextOf(FilePath, Encoding.UTF8).Should().Be("new");
        h.Editor.Modified.Should().BeFalse();
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void CheckSave_on_untitled_goes_to_save_as_with_txt_suggestion()
    {
        var h = new Harness();
        h.Editor.Type("text");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Save);
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\new.txt"));

        h.Documents.CheckSave().Should().BeTrue();

        h.Dialogs.SaveAsRequests.Should().Equal((@"*.txt", TextEncodingKind.Utf8));
        h.State.Path.Should().Be(@"C:\new.txt");
        h.State.IsUntitled.Should().BeFalse();
        h.Window.Title.Should().Be("new.txt - Notepad Classic Port");
        h.Files.Saves.Should().Equal((@"C:\new.txt", true));
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void CheckSave_falls_back_to_save_as_when_the_direct_save_fails_and_loops_after_errors()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "old");
        h.Editor.Type("new");
        h.Files.SaveErrors[FilePath] = FileErrors.AccessDenied;
        h.Files.SaveErrors[@"C:\locked.txt"] = FileErrors.SharingViolation;
        h.SystemMessages.Messages[FileErrors.SharingViolation] = "in use";
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Save);
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\locked.txt"));
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\ok.txt"));

        h.Documents.CheckSave().Should().BeTrue();

        h.Dialogs.Messages.Should().Equal(("in use", MessageKind.Warning));
        h.Dialogs.SaveAsRequests.Select(r => r.Suggested).Should().Equal(FilePath, @"C:\locked.txt");
        h.State.Path.Should().Be(@"C:\ok.txt");
    }

    [Theory]
    [Trait("Doc", "parity.md#lenh")]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void CheckSave_save_as_cancel_or_failure_returns_false(bool dialogFails, int messages)
    {
        var h = new Harness();
        h.Editor.Type("text");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Save);
        h.Dialogs.SaveAsAnswers.Enqueue(dialogFails ? FileDialogResult.Fail(FileDialogResult.OutOfMemory) : FileDialogResult.Cancel());

        h.Documents.CheckSave().Should().BeFalse();

        h.Dialogs.Messages.Should().HaveCount(messages);
        if (dialogFails)
        {
            h.Dialogs.Messages[0].Text.Should().StartWith("Not enough memory");
            h.Dialogs.Messages[0].Kind.Should().Be(MessageKind.SystemError);
        }
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void CheckSave_save_as_loops_without_error_when_the_loss_warning_is_cancelled()
    {
        var h = new Harness("Ā");
        h.Editor.Type("Ā");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Save);
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\a.txt", TextEncodingKind.Ansi));
        h.Dialogs.MessageAnswers.Enqueue(MessageResult.Cancel);
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\a.txt", TextEncodingKind.Utf8));

        h.Documents.CheckSave().Should().BeTrue();

        h.Dialogs.Messages.Should().ContainSingle().Which.Kind.Should().Be(MessageKind.WarningOkCancel);
        h.Files.TextOf(@"C:\a.txt", Encoding.UTF8).Should().Be("Ā");
    }

    // ---- Save / Save As ----

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Save_of_a_named_file_writes_with_the_current_encoding_and_read_sharing_only()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "old text");
        h.State.CurrentEncoding = TextEncodingKind.Utf16LE;
        h.Editor.Type("new");

        h.Documents.Save();

        h.Files.Saves.Should().Equal((FilePath, false));
        h.Files.Files[FilePath].Should().Equal(0xFF, 0xFE, (byte)'n', 0, (byte)'e', 0, (byte)'w', 0);
        h.Window.Title.Should().Be("Note.txt - Notepad Classic Port");
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Failed_save_falls_through_to_save_as_without_an_error()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "old");
        h.Editor.Type("new");
        h.Files.SaveErrors[FilePath] = FileErrors.AccessDenied;
        h.Dialogs.SaveAsAnswers.Enqueue(FileDialogResult.Cancel());

        h.Documents.Save();

        h.Dialogs.SaveAsRequests.Should().Equal((FilePath, TextEncodingKind.Utf8));
        h.Dialogs.Messages.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Untitled_save_becomes_save_as()
    {
        var h = new Harness();
        h.Editor.Type("hello");
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\out.txt", TextEncodingKind.Utf8Bom));

        h.Documents.Save();

        h.Files.Files[@"C:\out.txt"].Should().Equal(0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o');
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf8Bom);
        h.Window.Title.Should().Be("out.txt - Notepad Classic Port");
        h.StatusView.Parts[4].Should().Be(" UTF-8 with BOM");
    }

    [Fact]
    [Trait("Doc", "parity.md#hop-thoai-he-thong")]
    public void Save_as_dialog_sets_the_dialog_open_flag_while_shown()
    {
        var h = new Harness();
        var during = false;
        h.Dialogs.DuringSaveAs = () => during = h.State.IsSaveDialogOpen;
        h.Dialogs.SaveAsAnswers.Enqueue(FileDialogResult.Cancel());

        h.Documents.SaveAs();

        during.Should().BeTrue();
        h.State.IsSaveDialogOpen.Should().BeFalse();
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Save_as_failure_shows_the_file_error()
    {
        var h = new Harness();
        h.Editor.Type("x");
        h.Files.SaveErrors[@"C:\ro.txt"] = FileErrors.AccessDenied;
        h.Dialogs.SaveAsAnswers.Enqueue(Chosen(@"C:\ro.txt"));

        h.Documents.SaveAs();

        h.Dialogs.Messages.Should().ContainSingle().Which.Text.Should().StartWith(@"C:\ro.txt" + "\nYou do not have permission");
        h.State.IsUntitled.Should().BeTrue();
    }

    [Theory]
    [Trait("Doc", "parity.md#hop-thoai-he-thong")]
    [InlineData(TextEncodingKind.Detect, TextEncodingKind.Utf8)]
    [InlineData(TextEncodingKind.Utf16BE, TextEncodingKind.Utf16BE)]
    public void Save_as_preselects_current_or_default_encoding(TextEncodingKind current, TextEncodingKind expected)
    {
        var h = new Harness();
        h.State.CurrentEncoding = current;

        h.Documents.InitialSaveAsEncoding().Should().Be(expected);
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Save_overwrites_in_place_and_truncates_the_old_tail()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "a much longer old text");
        h.Editor.Type("short");

        h.Documents.SaveFile(FilePath, false, TextEncodingKind.Utf8).Succeeded.Should().BeTrue();

        h.Files.TextOf(FilePath, Encoding.UTF8).Should().Be("short");
        h.Editor.Modified.Should().BeFalse();
        h.AccessPolicy.Saved.Should().Equal(FilePath);
        h.Editor.OpenLeases.Should().Be(0);
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Failed_write_deletes_only_a_file_this_save_created()
    {
        var h = new Harness();
        h.Files.Files[@"C:\exists.txt"] = [1, 2, 3, 4, 5, 6];
        h.Editor.Type("abc");
        h.Files.FailingWrite = (1, FileErrors.DiskFull);

        var existing = h.Documents.SaveFile(@"C:\exists.txt", true, TextEncodingKind.Utf8);
        var created = h.Documents.SaveFile(@"C:\new.txt", true, TextEncodingKind.Utf8);

        existing.Should().Be(new SaveResult(false, FileErrors.DiskFull));
        created.Succeeded.Should().BeFalse();
        h.Files.Deleted.Should().Equal(@"C:\new.txt");
        // UTF-8 has no BOM, so the failing write is the body and nothing was written.
        h.Files.Files[@"C:\exists.txt"].Should().Equal(1, 2, 3, 4, 5, 6);
        h.Editor.Modified.Should().BeTrue();
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Bom_write_failure_is_ignored()
    {
        var h = new Harness();
        h.Editor.Type("ab");
        h.Files.FailingWrite = (0, FileErrors.DiskFull);

        var result = h.Documents.SaveFile(@"C:\bom.txt", true, TextEncodingKind.Utf16LE);

        result.Succeeded.Should().BeTrue();
        h.Files.Files[@"C:\bom.txt"].Should().Equal((byte)'a', 0, (byte)'b', 0);
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Open_failure_reports_the_error_and_leaves_nothing_behind()
    {
        var h = new Harness();
        h.Files.SaveErrors[@"C:\x.txt"] = FileErrors.InvalidName;

        h.Documents.SaveFile(@"C:\x.txt", true, TextEncodingKind.Utf8).Should().Be(new SaveResult(false, FileErrors.InvalidName));

        h.Files.Deleted.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Word_wrap_selection_is_parked_during_the_write_and_restored()
    {
        var h = new Harness();
        h.Editor.Type("hello world");
        h.Editor.WordWrap = true;
        h.Editor.SetSelection(2, 5);
        h.Editor.Calls.Clear();

        h.Documents.SaveFile(@"C:\w.txt", true, TextEncodingKind.Utf8);

        h.Editor.Calls.Should().StartWith(["SetSelection(0,0)"]).And.Contain("SetSelection(2,5)");
        h.Editor.Selection.Should().Be(new TextRange(2, 5));
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Word_wrap_selection_is_restored_after_a_failed_save_too()
    {
        var h = new Harness();
        h.Editor.Type("hello world");
        h.Editor.WordWrap = true;
        h.Editor.SetSelection(1, 3);
        h.Files.FailingWrite = (1, FileErrors.DiskFull);

        h.Documents.SaveFile(@"C:\w.txt", true, TextEncodingKind.Utf8).Succeeded.Should().BeFalse();

        h.Editor.Selection.Should().Be(new TextRange(1, 3));
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Lossy_ansi_save_asks_and_cancel_deletes_a_new_file()
    {
        var h = new Harness();
        h.Editor.Type("Ā");
        h.Dialogs.MessageAnswers.Enqueue(MessageResult.Cancel);

        var result = h.Documents.SaveFile(@"C:\a.txt", true, TextEncodingKind.Ansi);

        result.Should().Be(new SaveResult(false, 0, CancelledByUser: true));
        h.Dialogs.Messages.Should().ContainSingle().Which.Text.Should().StartWith(@"C:\a.txt" + "\n This file contains characters in Unicode format");
        h.Files.Deleted.Should().Equal(@"C:\a.txt");
    }

    [Fact]
    [Trait("Doc", "parity.md#luu-file")]
    public void Accepted_lossy_ansi_save_uses_best_fit_and_reloads_the_file_with_another_log_stamp()
    {
        var h = new Harness();
        h.Editor.Type(".LOG Ā");
        h.Dialogs.MessageAnswers.Enqueue(MessageResult.Ok);

        h.Documents.SaveFile(@"C:\log.txt", true, TextEncodingKind.Ansi).Succeeded.Should().BeTrue();

        h.Converter.EncodeCalls.Should().Contain((1252, false), "the second conversion drops WC_NO_BEST_FIT_CHARS");
        h.Editor.Calls.Should().Contain("LoadBuffer");
        h.Editor.Text.Should().EndWith("\r\n2:44 PM 10/29/2026\r\n");
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Ansi);
        h.Shell.RecentDocuments.Should().Equal(@"C:\log.txt");
    }

    // ---- Load ----

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Load_hands_the_text_to_the_editor_and_updates_state()
    {
        var h = new Harness();
        h.Editor.Type("previous");
        h.Files.Files[FilePath] = [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0];

        h.Documents.Load(FilePath, TextEncodingKind.Detect).Should().Be(LoadOutcome.Loaded);

        h.Editor.Text.Should().Be("hi");
        h.Editor.Modified.Should().BeFalse();
        h.State.Path.Should().Be(FilePath);
        h.State.IsUntitled.Should().BeFalse();
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf16LE);
        h.Window.Title.Should().Be("Note.txt - Notepad Classic Port");
        h.StatusView.Parts[4].Should().Be(" UTF-16 LE");
        h.Shell.RecentDocuments.Should().Equal(FilePath);
    }

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Files_of_one_gibibyte_are_refused_with_string_8()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = [1];
        h.Files.ReportedLength = DocumentLoader.MaxFileSize;

        h.Documents.Load(FilePath, TextEncodingKind.Detect).Should().Be(LoadOutcome.FailedAndReported);

        h.Dialogs.Messages.Should().Equal(($"The {FilePath} file is too large for Notepad Classic Port.\n\nUse another editor to edit the file.", MessageKind.Warning));
        h.Editor.Calls.Should().NotContain("LoadBuffer");
    }

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Out_of_space_while_loading_fails_with_string_8_and_a_new_document()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = "big"u8.ToArray();
        h.Editor.FailNextLoad = true;

        h.Documents.Load(FilePath, TextEncodingKind.Detect).Should().Be(LoadOutcome.FailedAndReported);

        h.Dialogs.Messages.Should().ContainSingle().Which.Text.Should().Contain("too large");
        h.State.IsUntitled.Should().BeTrue();
        h.State.Path.Should().BeNull();
        h.Window.Title.Should().Be("Untitled - Notepad Classic Port");
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Out_of_space_outside_a_load_shows_string_7()
    {
        var h = new Harness();

        h.Editor.RaiseOutOfSpace();

        h.Dialogs.Messages.Should().ContainSingle().Which.Should().Be((
            "Not enough memory available to complete this operation. Quit one or more applications to increase available memory, and then try again.",
            MessageKind.SystemError));
    }

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Access_policy_can_refuse_a_load()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = "x"u8.ToArray();
        h.AccessPolicy.Allow = false;

        h.Documents.Load(FilePath, TextEncodingKind.Detect).Should().Be(LoadOutcome.FailedAndReported);

        h.State.IsUntitled.Should().BeTrue();
    }

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Log_file_gets_a_stamp_at_the_end_with_undo()
    {
        var h = new Harness();
        h.Files.Files[FilePath] = ".LOG\r\nold\r\n"u8.ToArray();

        h.Documents.Load(FilePath, TextEncodingKind.Detect);

        h.Editor.Text.Should().Be(".LOG\r\nold\r\n\r\n2:44 PM 10/29/2026\r\n");
        h.Editor.Calls.Should().ContainInOrder("LoadBuffer", "SetSelection(11,11)", "ScrollCaret", @"ReplaceSelection(\r\n2:44 PM 10/29/2026\r\n,True)");
        h.Documents.IsDirty.Should().BeTrue();
        h.Window.Title.Should().Be("*Note.txt - Notepad Classic Port");
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Load_reports_open_errors_to_the_caller()
    {
        var h = new Harness();

        h.Documents.Load(@"C:\missing.txt", TextEncodingKind.Detect).Should().Be(LoadOutcome.OpenFailed);

        h.Documents.LastOpenError.Should().Be(FileErrors.FileNotFound);
    }

    // ---- File errors ----

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void File_errors_use_string_26_the_system_message_or_string_1()
    {
        var h = new Harness();

        h.Documents.ShowFileError(@"C:\a.txt", FileErrors.AccessDenied);
        h.Documents.ShowFileError(@"C:\a.txt", FileErrors.FileNotFound);
        h.Documents.ShowFileError(@"C:\a.txt", 9999);

        h.Dialogs.Messages.Select(m => m.Text).Should().Equal(
            "C:\\a.txt\nYou do not have permission to open this file.  See the owner of the file or an administrator to obtain permission.",
            "The system cannot find the file specified.",
            "Cannot open the C:\\a.txt file.\n\nMake sure a disk is in the drive you specified.");
        h.Dialogs.Messages.Should().OnlyContain(m => m.Kind == MessageKind.Warning);
    }

    // ---- Open ----

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Open_with_auto_detect_forces_the_sniffed_encoding()
    {
        var h = new Harness();
        var bytes = new byte[1100];
        bytes.AsSpan().Fill((byte)'a');
        bytes[1050] = 0xFF; // invalid UTF-8 after the first KiB
        h.Files.Files[FilePath] = bytes;
        h.Dialogs.OpenAnswers.Enqueue(Chosen(FilePath, TextEncodingKind.Detect));

        h.Documents.Open();

        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf8, "the 1 KiB pre-scan saw valid UTF-8 and the load is forced to it");
        h.Editor.TextLength.Should().Be(1100);
    }

    [Fact]
    [Trait("Doc", "parity.md#encoding-va-nap-file")]
    public void Sniff_returns_the_default_when_the_file_cannot_be_read()
    {
        var h = new Harness();
        h.State.DefaultEncoding = TextEncodingKind.Ansi;

        h.Documents.Sniff(@"C:\missing.txt").Should().Be(TextEncodingKind.Ansi);
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Open_failure_restores_the_encoding_and_shows_the_file_error()
    {
        var h = new Harness();
        h.State.CurrentEncoding = TextEncodingKind.Utf16BE;
        h.Files.ReadErrors[FilePath] = FileErrors.SharingViolation;
        h.SystemMessages.Messages[FileErrors.SharingViolation] = "in use";
        h.Dialogs.OpenAnswers.Enqueue(Chosen(FilePath, TextEncodingKind.Ansi));

        h.Documents.Open();

        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf16BE);
        h.Dialogs.Messages.Should().Equal(("in use", MessageKind.Warning));
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Open_is_abandoned_when_save_changes_is_cancelled_or_the_dialog_is_closed()
    {
        var h = new Harness();
        h.Editor.Type("x");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);

        h.Documents.Open();

        h.Dialogs.OpenShown.Should().Be(0);

        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.DontSave);
        h.Dialogs.OpenAnswers.Enqueue(FileDialogResult.Cancel());
        h.Documents.Open();

        h.Dialogs.OpenShown.Should().Be(1);
        h.Dialogs.Messages.Should().BeEmpty();
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void Dialog_failure_other_than_cancel_shows_string_10()
    {
        var h = new Harness();
        h.Dialogs.OpenAnswers.Enqueue(FileDialogResult.Fail(unchecked((int)0x80004005)));

        h.Documents.Open();

        h.Dialogs.Messages.Should().ContainSingle().Which.Text.Should().StartWith("Failed to initialize file dialogs");
    }

    // ---- New ----

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void New_returns_to_untitled_with_the_default_encoding_keeping_zoom_and_wrap()
    {
        var h = new Harness();
        h.OpenSaved(FilePath, "text", TextEncodingKind.Utf16LE);
        h.Editor.Zoom = new(150, 100);
        h.Editor.WordWrap = true;

        h.Documents.New().Should().BeTrue();

        h.State.IsUntitled.Should().BeTrue();
        h.State.Path.Should().BeNull();
        h.State.CurrentEncoding.Should().Be(TextEncodingKind.Utf8);
        h.Editor.Text.Should().BeEmpty();
        h.Editor.Zoom.Should().Be(new ZoomRatio(150, 100));
        h.Editor.WordWrap.Should().BeTrue();
        h.Window.Title.Should().Be("Untitled - Notepad Classic Port");
        h.StatusView.Parts[4].Should().Be(" UTF-8");
    }

    [Fact]
    [Trait("Doc", "parity.md#lenh")]
    public void New_always_asks_but_only_a_requested_question_can_cancel()
    {
        var h = new Harness();
        h.Editor.Type("x");
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);
        h.Dialogs.SaveChangesAnswers.Enqueue(SaveChangesChoice.Cancel);

        h.Documents.New().Should().BeFalse();
        h.Editor.Text.Should().Be("x");

        h.Documents.New(askSave: false).Should().BeTrue();
        h.Dialogs.SaveChangesQuestions.Should().HaveCount(2);
        h.Editor.Text.Should().BeEmpty();
    }
}
