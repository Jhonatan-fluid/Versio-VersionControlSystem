using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The reader for <c>*.libraries.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// This file format has two hand-written halves that never meet in the same process:
/// <c>versio_yaml.py</c> writes it inside CODESYS's IronPython 2.7, and
/// <see cref="LibraryCatalog.Parse"/> reads it here. Nothing but a test forces them to agree,
/// so the important cases below parse <b>the committed fixture the real exporter produced</b>
/// rather than a document written by hand in this file. A hand-written sample would only ever
/// prove the parser agrees with whoever wrote the sample — the same mental model, twice.
/// </para>
/// <para>
/// The hand-written documents that do appear here are for the shapes a real export cannot
/// easily be made to produce: a truncated file, a file from a newer writer, junk.
/// </para>
/// </remarks>
public sealed class LibraryCatalogTests
{
    private static string FixtureYaml(string relative) =>
        File.ReadAllText(Path.Combine(TestPaths.Project("RefrigeratorControl"), relative));

    private static LibraryCatalog ApplicationManager() =>
        LibraryCatalog.Parse(FixtureYaml(
            Path.Combine("Device", "Plc Logic", "Application", "Library Manager.libraries.yaml")));

    [Fact]
    public void The_real_export_parses_and_declares_its_schema()
    {
        var catalog = ApplicationManager();

        Assert.True(catalog.IsValid);
        Assert.Equal(1, catalog.Schema);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.Equal("Library Manager", catalog.Manager);

        // The object's place in the project, not the file it landed in: the point of
        // exporting per manager is that a library change shows up under the device that
        // owns it.
        Assert.Equal("Device/Plc Logic/Application/Library Manager", catalog.Path);
    }

    [Fact]
    public void Every_reference_in_the_real_export_is_read()
    {
        var catalog = ApplicationManager();

        // Measured against the fixture: 21 references, 18 of them pulled in by plug-ins.
        Assert.Equal(21, catalog.References.Count);
        Assert.Equal(18, catalog.References.Count(r => r.SystemLibrary));

        // Nothing nameless survived, and nothing was invented.
        Assert.All(catalog.References, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void A_quoted_value_containing_punctuation_survives_the_round_trip()
    {
        var standard = ApplicationManager().References.Single(r => r.Name == "#Standard");

        // "Standard, * (System)" is the reason quoting exists in this format at all: a
        // comma, an asterisk and brackets in one value. Read back with none of it mangled
        // and, in particular, without the writer's quotes left on.
        Assert.Equal("Standard", standard.Namespace);
        Assert.Equal("Standard", standard.Placeholder);
        Assert.Equal("Standard, * (System)", standard.DefaultResolution);
        Assert.Equal("Standard, 3.5.22.0 (System)", standard.Redirection);
        Assert.False(standard.SystemLibrary);
    }

    [Fact]
    public void Library_parameters_are_read_as_a_map()
    {
        var util = ApplicationManager().References.Single(r => r.Name == "#Util");

        // Real project state, and the reason parameters are exported at all: this number
        // changes what the machine does.
        Assert.Equal("22800", Assert.Contains("IBLOCKSIZE", util.Parameters));

        // Nothing leaked in from the reference before it.
        Assert.Single(util.Parameters);
    }

    [Fact]
    public void A_key_after_the_parameter_map_belongs_to_the_reference_again()
    {
        // Hand-written on purpose. `parameters` is last in versio_libraries.REF_KEYS, so
        // the real export never puts a sibling key after it — which means the fixture
        // cannot tell a parser that closes the nested mapping from one that does not.
        //
        // This was caught by breaking the reader deliberately and watching the fixture
        // tests pass anyway: the first version of this test asserted the map did not leak
        // into the NEXT reference, which the dash already prevents on its own. It proved
        // nothing. The property that actually needs a parser is this one, and the key
        // order it depends on is a list somebody could reorder tomorrow.
        var catalog = LibraryCatalog.Parse(
            """
            schema: 1
            references:
              - name: A
                parameters:
                  P: "1"
                optional: true
              - name: B
            """);

        var a = catalog.References[0];
        Assert.Equal("1", Assert.Contains("P", a.Parameters));
        Assert.True(a.Optional);
        Assert.Empty(catalog.References[1].Parameters);
    }

    [Fact]
    public void A_reference_with_no_parameters_gets_an_empty_map_not_a_null()
    {
        var catalog = ApplicationManager();
        Assert.All(catalog.References, r => Assert.NotNull(r.Parameters));
    }

    [Fact]
    public void Both_managers_in_one_project_are_read_independently()
    {
        // Two library managers is the normal case, not an edge case: the fixture has one
        // on the application and one on the project. It is also why this file is written
        // per manager rather than as a single config/libraries.yaml — one file could not
        // hold both without inventing a key to tell them apart.
        var project = LibraryCatalog.Parse(FixtureYaml("Library Manager.libraries.yaml"));

        Assert.Equal(13, project.References.Count);
        Assert.Equal("Library Manager", project.Path);
        Assert.NotEqual(ApplicationManager().Path, project.Path);
    }

    [Fact]
    public void The_version_shown_falls_back_through_what_the_reference_actually_declares()
    {
        var refs = ApplicationManager().References;

        // A placeholder has no version of its own. The pin is the answer when there is
        // one; the request is the answer when there is not. An empty string for a
        // perfectly well-specified placeholder would make the row look broken.
        Assert.Equal("Standard, 3.5.22.0 (System)",
            refs.Single(r => r.Name == "#Standard").DisplayVersion);
        Assert.Equal("VisuUserMgmt, * (System)",
            refs.Single(r => r.Name == "#VisuUserManagement").DisplayVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not yaml at all: [ { unterminated")]
    [InlineData("# only a comment\n")]
    public void Unreadable_input_yields_an_invalid_catalog_rather_than_throwing(string text)
    {
        // Same rule as the manifest: this is read to draw a tree, and a file can be
        // mid-write or truncated by a killed export. Taking the window down over it
        // would be the wrong trade.
        var catalog = LibraryCatalog.Parse(text);

        Assert.False(catalog.IsValid);
        Assert.Empty(catalog.References);
    }

    [Fact]
    public void A_file_truncated_mid_reference_keeps_what_it_can_and_drops_the_half_entry()
    {
        var whole = FixtureYaml("Library Manager.libraries.yaml");
        var truncated = whole[..(whole.Length / 2)];

        var catalog = LibraryCatalog.Parse(truncated);

        // The header survived, so the file still identifies itself...
        Assert.True(catalog.IsValid);

        // ...and every reference that came out is complete. A half-read entry with no
        // name is not information, and showing it as a nameless row would be worse than
        // showing nothing.
        Assert.NotEmpty(catalog.References);
        Assert.True(catalog.References.Count < 13);
        Assert.All(catalog.References, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void An_entry_with_no_name_is_dropped_rather_than_shown_as_a_blank_row()
    {
        // Truncating the fixture does not produce this, because `name` is the first key
        // of every item — so the rule that drops nameless entries survived a deliberate
        // break until this test existed. A file interrupted between the dash and the name
        // is the case that reaches it.
        var catalog = LibraryCatalog.Parse(
            """
            schema: 1
            references:
              - name: Real
              - optional: true
              - name: AlsoReal
            """);

        Assert.Equal(2, catalog.References.Count);
        Assert.All(catalog.References, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void A_newer_schema_is_read_rather_than_refused()
    {
        // Forward compatibility in the direction that matters: an older Versio opening a
        // repository somebody exported with a newer one should still draw the tree. The
        // schema is recorded so the caller can decide, not so the parser can refuse.
        var catalog = LibraryCatalog.Parse(
            "schema: 99\nvendor: something\nreferences:\n  - name: Thing\n");

        Assert.Equal(99, catalog.Schema);
        Assert.Equal("Thing", Assert.Single(catalog.References).Name);
    }

    [Fact]
    public void A_vendor_that_is_not_codesys_reads_the_same_way()
    {
        // The format is the seam. Nothing in the reader may key off the vendor name, or
        // the second toolchain has to write CODESYS's file to be understood.
        var catalog = LibraryCatalog.Parse(
            """
            schema: 1
            vendor: twincat
            manager: References
            path: PLC/References
            referenceCount: 1
            references:
              - name: "Tc2_Standard, 3.3.4.0 (Beckhoff Automation GmbH)"
                namespace: Tc2_Standard
                version: 3.3.4.0
                company: "Beckhoff Automation GmbH"
                systemLibrary: false
                optional: true
            """);

        var only = Assert.Single(catalog.References);
        Assert.Equal("twincat", catalog.Vendor);
        Assert.Equal("3.3.4.0", only.Version);
        Assert.Equal("Beckhoff Automation GmbH", only.Company);
        Assert.True(only.Optional);
        Assert.False(only.SystemLibrary);
        Assert.Equal("3.3.4.0", only.DisplayVersion);
    }

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("\"with \\\"quotes\\\"\"", "with \"quotes\"")]
    [InlineData("\"back \\\\ slash\"", "back \\ slash")]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"ctrl\\x07char\"", "ctrl\achar")]
    [InlineData("\"colon: inside\"", "colon: inside")]
    [InlineData("unquoted", "unquoted")]
    [InlineData("\"unterminated", "\"unterminated")]
    public void Unquoting_reverses_exactly_what_the_writer_escapes(string stored, string expected)
    {
        // Only the escapes versio_yaml.quote can produce are recognised. A reader that
        // invented others would eventually corrupt a name that legitimately contains a
        // backslash.
        //
        // Driven through Parse rather than through the helper: the quoting has to survive
        // the line splitter as well, and a colon inside a quoted value is exactly where a
        // naive "split on the first colon" reader falls over.
        var catalog = LibraryCatalog.Parse($"schema: 1\nreferences:\n  - name: {stored}\n");

        Assert.Equal(expected, Assert.Single(catalog.References).Name);
    }

    [Fact]
    public void A_flag_is_only_true_when_it_says_true()
    {
        // Absent must not read as true. Every flag here weakens a check — optional
        // suppresses a missing-library error, systemLibrary hides a row — so the failure
        // is quiet by construction.
        var catalog = LibraryCatalog.Parse(
            "schema: 1\nreferences:\n  - name: A\n  - name: B\n    optional: false\n");

        Assert.All(catalog.References, r =>
        {
            Assert.False(r.Optional);
            Assert.False(r.SystemLibrary);
            Assert.False(r.QualifiedOnly);
            Assert.False(r.PublishSymbols);
        });
    }
}
