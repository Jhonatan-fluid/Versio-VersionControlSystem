using Versio.Core.Updates;
using Xunit;

namespace Versio.Core.Tests.Updates;

/// <summary>
/// When the launch check asks about an update, and what of the notes it shows.
/// </summary>
public sealed class UpdatePolicyTests
{
    private static UpdateOffer Offer(string version) => new(version, string.Empty);

    [Fact]
    public void A_version_never_dismissed_is_asked_about()
    {
        Assert.True(UpdatePolicy.ShouldAskAtLaunch(Offer("0.5.0"), string.Empty));
    }

    [Fact]
    public void Later_silences_the_launch_check_for_that_version()
    {
        Assert.False(UpdatePolicy.ShouldAskAtLaunch(Offer("0.5.0"), "0.5.0"));
    }

    [Fact]
    public void A_newer_release_asks_again_after_an_older_one_was_dismissed()
    {
        // The dismissal is for one version. One that silenced every later release would leave
        // a user on an old build for good after one busy morning.
        Assert.True(UpdatePolicy.ShouldAskAtLaunch(Offer("0.5.1"), "0.5.0"));
    }

    [Fact]
    public void Notes_lose_their_markdown_markers_but_keep_their_bullets()
    {
        var notes = UpdatePolicy.NotesForPrompt("## What's new\n\n- **Faster** export\n- `git` bundled");

        Assert.Equal("What's new\n\n- Faster export\n- git bundled", notes);
    }

    [Fact]
    public void Notepads_markdown_save_reads_as_plain_text()
    {
        // The first real publish (2026-10-27): Windows 11 Notepad's Markdown mode saved the
        // prefilled "- " items as "* " and escaped every underscore.
        var notes = UpdatePolicy.NotesForPrompt(
            "* Publish\\_Release.bat: build, note, upload\n* App: check for updates");

        Assert.Equal("- Publish_Release.bat: build, note, upload\n- App: check for updates", notes);
    }

    [Fact]
    public void Blank_runs_fold_and_the_ends_are_trimmed()
    {
        var notes = UpdatePolicy.NotesForPrompt("\r\n\r\nOne\r\n\r\n\r\n\r\nTwo\r\n\r\n");

        Assert.Equal("One\n\nTwo", notes);
    }

    [Fact]
    public void Long_notes_are_cut_and_say_how_much_was_left_out()
    {
        var markdown = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"- item {i}"));

        var notes = UpdatePolicy.NotesForPrompt(markdown, maxLines: 5).Split('\n');

        Assert.Equal(6, notes.Length);
        Assert.Equal("- item 5", notes[4]);
        Assert.Equal("…and 15 more lines", notes[5]);
    }

    [Fact]
    public void No_notes_is_an_empty_string_rather_than_a_blank_line()
    {
        Assert.Equal(string.Empty, UpdatePolicy.NotesForPrompt("  \n \n"));
    }
}
