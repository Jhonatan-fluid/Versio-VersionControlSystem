using Versio.Core.Settings;
using Xunit;

namespace Versio.Core.Tests.Settings;

/// <summary>
/// The repository dropdown's memory: what it keeps, in what order, and what it refuses to
/// throw away.
/// </summary>
/// <remarks>
/// The dropdown held only the repositories opened in the current session, and nothing wrote
/// them anywhere — so on a fresh start it listed exactly one entry, the repository the app had
/// just opened by itself, and the control whose whole purpose is jumping between repositories
/// could not do it. These are the rules that list follows now.
/// </remarks>
public sealed class RecentRepositoriesTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static RecentRepository Repo(string path, string url = "", int minutesAgo = 0) =>
        new() { Path = path, RemoteUrl = url, LastOpenedUtc = Now.AddMinutes(-minutesAgo) };

    [Fact]
    public void The_repository_just_opened_is_first()
    {
        var list = RecentRepositories.Promote(
            [Repo(@"C:\a"), Repo(@"C:\b")], Repo(@"C:\c"));

        Assert.Equal([@"C:\c", @"C:\a", @"C:\b"], list.Select(r => r.Path));
    }

    [Fact]
    public void Opening_one_already_in_the_list_moves_it_rather_than_copying_it()
    {
        var list = RecentRepositories.Promote(
            [Repo(@"C:\a"), Repo(@"C:\b"), Repo(@"C:\c")], Repo(@"C:\c"));

        Assert.Equal([@"C:\c", @"C:\a", @"C:\b"], list.Select(r => r.Path));
    }

    [Fact]
    public void The_same_path_cased_differently_is_the_same_repository()
    {
        // Windows is the only platform this ships on and it treats these as one directory.
        // An ordinal comparison would list the repository twice, once per way it was typed.
        var list = RecentRepositories.Promote([Repo(@"C:\Git\Repo")], Repo(@"c:\git\repo"));

        Assert.Equal(@"c:\git\repo", Assert.Single(list).Path);
    }

    [Fact]
    public void A_remembered_remote_survives_being_opened_without_one()
    {
        // Opening while offline, or after `git remote remove origin`, must not erase the URL
        // that is the only thing making "clone it again" possible later.
        var list = RecentRepositories.Promote(
            [Repo(@"C:\a", "https://example.com/a.git")], Repo(@"C:\a"));

        Assert.Equal("https://example.com/a.git", Assert.Single(list).RemoteUrl);
    }

    [Fact]
    public void A_remote_that_changed_is_taken_from_the_repository_that_just_opened()
    {
        var list = RecentRepositories.Promote(
            [Repo(@"C:\a", "https://old.example/a.git")],
            Repo(@"C:\a", "https://new.example/a.git"));

        Assert.Equal("https://new.example/a.git", Assert.Single(list).RemoteUrl);
    }

    [Fact]
    public void The_list_is_capped_and_the_oldest_falls_off_the_end()
    {
        IReadOnlyList<RecentRepository> list = [];
        for (var i = 0; i < RecentRepositories.Limit + 5; i++)
        {
            list = RecentRepositories.Promote(list, Repo($@"C:\r{i}"));
        }

        Assert.Equal(RecentRepositories.Limit, list.Count);
        Assert.Equal($@"C:\r{RecentRepositories.Limit + 4}", list[0].Path);
        Assert.DoesNotContain(list, r => r.Path == @"C:\r0");
    }

    [Fact]
    public void Removing_takes_out_one_entry_and_leaves_the_order_alone()
    {
        var list = RecentRepositories.Remove(
            [Repo(@"C:\a"), Repo(@"C:\b"), Repo(@"C:\c")], @"C:\B");

        Assert.Equal([@"C:\a", @"C:\c"], list.Select(r => r.Path));
    }

    [Fact]
    public void Removing_something_that_is_not_there_changes_nothing()
    {
        var list = RecentRepositories.Remove([Repo(@"C:\a")], @"C:\z");

        Assert.Equal(@"C:\a", Assert.Single(list).Path);
    }
    // ---- one repository, one entry, whatever spelling it arrived in --------------------

    [Fact]
    public void The_same_repository_typed_two_ways_is_one_entry()
    {
        // FOUND IN THE RUNNING APP. Git answers rev-parse --show-toplevel with forward
        // slashes and the Windows folder picker hands back backslashes, so opening one
        // repository both ways put two rows in the dropdown - same name, same location,
        // no way to tell them apart.
        var list = RecentRepositories.Promote([], Repo("C:/GIT/Versio"));
        list = RecentRepositories.Promote(list, Repo(@"C:\GIT\Versio"));

        Assert.Single(list);
    }

    [Fact]
    public void The_stored_spelling_is_the_one_Windows_uses()
    {
        // Stored canonical, not merely compared canonical: the folder picker is handed this
        // string as its starting directory, and settings.json is a file people open.
        var list = RecentRepositories.Promote([], Repo("C:/GIT/Versio"));

        Assert.Equal(@"C:\GIT\Versio", list[0].Path);
    }

    [Fact]
    public void An_entry_already_stored_the_old_way_is_tidied_as_it_moves()
    {
        // Everybody who has run Versio already has a settings.json with both spellings in it.
        // Promoting anything rewrites the rest, so the duplicate clears itself.
        var mixed = RecentRepositories.Promote([], Repo("C:/GIT/Other"));
        var list = RecentRepositories.Promote(mixed, Repo(@"C:\GIT\Versio"));

        Assert.All(list, r => Assert.DoesNotContain('/', r.Path));
    }

    [Fact]
    public void Removing_works_whichever_way_the_path_is_spelled()
    {
        var list = RecentRepositories.Promote([], Repo("C:/GIT/Versio"));

        Assert.Empty(RecentRepositories.Remove(list, @"C:\GIT\Versio"));
    }

    [Theory]
    [InlineData("C:/GIT/Versio", @"C:\GIT\Versio")]
    [InlineData(@"C:\GIT\Versio\", @"C:\GIT\Versio")]
    [InlineData("C:/GIT/Versio/", @"C:\GIT\Versio")]
    [InlineData(@"  C:\GIT\Versio  ", @"C:\GIT\Versio")]
    [InlineData(@"\server\share\repo", @"\server\share\repo")]
    [InlineData("", "")]
    public void A_path_has_one_spelling(string given, string expected) =>
        Assert.Equal(expected, RecentRepositories.Canonical(given));

    [Fact]
    public void A_bare_root_keeps_its_separator()
    {
        // "C:" alone means "the current directory on C" to Windows, which is a different
        // thing from "C:\" entirely.
        Assert.Equal(@"C:\", RecentRepositories.Canonical(@"C:\"));
        Assert.Equal(@"C:\", RecentRepositories.Canonical("C:/"));
    }

}

/// <summary>What to offer for a remembered repository that is no longer there.</summary>
/// <remarks>
/// The decision is entirely about whether a remote was recorded, which is why it is a pure
/// function and not an <c>if</c> inside a dialog: a <i>Clone it again</i> button on a
/// repository that never had a remote is a button that cannot work.
/// </remarks>
public sealed class MissingRepositoryTests
{
    private static RecentRepository Repo(string url = "") =>
        new() { Path = @"C:\gone", RemoteUrl = url };

    [Fact]
    public void With_a_remote_the_first_answer_is_to_clone_it_back()
    {
        var prompt = MissingRepository.Prompt(
            Repo("https://example.com/a.git"), MissingRepositoryReason.Gone);

        Assert.Equal(
            [MissingRepositoryChoice.CloneAgain, MissingRepositoryChoice.Forget,
             MissingRepositoryChoice.Cancel],
            prompt.Choices);

        Assert.Contains("https://example.com/a.git", prompt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_remote_the_option_is_absent_and_the_reason_is_stated()
    {
        var prompt = MissingRepository.Prompt(Repo(), MissingRepositoryReason.Gone);

        Assert.DoesNotContain(MissingRepositoryChoice.CloneAgain, prompt.Choices);

        // "Why is there no clone option" is a question the dialog should not make anybody ask.
        Assert.Contains("nothing to clone it back from", prompt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_is_always_offered_and_always_last()
    {
        foreach (var reason in Enum.GetValues<MissingRepositoryReason>())
        {
            foreach (var url in new[] { "", "https://example.com/a.git" })
            {
                var prompt = MissingRepository.Prompt(Repo(url), reason);
                Assert.Equal(MissingRepositoryChoice.Cancel, prompt.Choices[^1]);
            }
        }
    }

    [Fact]
    public void A_folder_that_is_no_longer_a_repository_says_so_rather_than_saying_it_is_gone()
    {
        var prompt = MissingRepository.Prompt(Repo(), MissingRepositoryReason.NotARepository);

        Assert.Contains("still there", prompt.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing at that path", prompt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_destructive_looking_option_says_remove_not_delete()
    {
        // Deleting the folder is what the user is afraid this does, and the label is the only
        // place that fear gets answered before they click.
        Assert.Equal("Remove from list", MissingRepository.Label(MissingRepositoryChoice.Forget));
    }
}
