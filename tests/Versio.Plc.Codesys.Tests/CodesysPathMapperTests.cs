using Versio.Core.Bindings;
using Versio.Plc.Codesys;
using Versio.Core.Serialization;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// CODESYS object names are far freer than Windows file names. A collision or a lossy
/// encoding here means importing into the wrong object (plan R12).
/// </summary>
public class PathMapperTests
{
    [Theory]
    [InlineData("Main")]
    [InlineData("Conveyor_FB")]
    [InlineData("With Space")]
    [InlineData("Kühlschrank")]
    [InlineData("Кириллица")]
    [InlineData("dotted.name")]
    [InlineData("emoji_\U0001F600")]
    public void Ordinary_names_round_trip(string name)
    {
        Assert.Equal(name, PathMapper.DecodeStem(PathMapper.EncodeStem(name)));
    }

    [Theory]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>c")]
    [InlineData("a|b")]
    public void Characters_Windows_forbids_are_encoded_reversibly(string name)
    {
        // Everything EXCEPT a separator or a colon. Those three make the name look like a
        // path, which is handled below and deliberately not reversible.
        var encoded = PathMapper.EncodeStem(name);

        Assert.DoesNotContain('*', encoded);
        Assert.DoesNotContain('?', encoded);
        Assert.DoesNotContain('"', encoded);
        Assert.DoesNotContain('|', encoded);
        Assert.Equal(name, PathMapper.DecodeStem(encoded));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public void A_name_that_looks_like_a_path_is_shortened_rather_than_encoded(string name)
    {
        // These USED to assert reversibility, and the assertion was the divergence: the
        // exporter that actually runs - safe_file_stem in versio_text.py - has shortened
        // path-like names since a real mirror was found carrying somebody's username and
        // home directory in a file name. This file specified the opposite, and nothing
        // called it, so nobody found out.
        var encoded = PathMapper.EncodeStem(name);

        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('\\', encoded);
        Assert.DoesNotContain(':', encoded);
        Assert.Contains('~', encoded);
        Assert.NotEqual(name, PathMapper.DecodeStem(encoded));
    }

    [Fact]
    public void A_path_like_name_carries_no_username_and_no_directory_layout()
    {
        // The measured case, from a real project: CODESYS gives an external-file reference
        // this shape of identity, and percent-encoding it wrote 120 characters of somebody
        // else's machine into a repository they share.
        var encoded = PathMapper.EncodeStem(
            @"{00000000-0000-0000-0000-000000000000}|C:\Users\m.lamp\Documents\license.txt");

        Assert.StartsWith("license.txt~", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("m.lamp", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", encoded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_external_files_with_the_same_basename_do_not_collide()
    {
        // Why the shortened form carries a digest at all. One object per file is what makes
        // the manifest's path mapping mean anything.
        var first = PathMapper.EncodeStem(@"{0000}|C:\Users\a\Documents\license.txt");
        var second = PathMapper.EncodeStem(@"{0000}|D:\shared\legal\license.txt");

        Assert.NotEqual(first, second);
        Assert.StartsWith("license.txt~", first, StringComparison.Ordinal);
        Assert.StartsWith("license.txt~", second, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_nobody_could_open_is_shortened_rather_than_written()
    {
        // Windows still enforces 260 characters in most configurations, and a deep CODESYS
        // folder chain plus a percent-encoded name reaches it.
        var encoded = PathMapper.EncodeStem(new string('A', 300));

        // 87, PINNED, where this read `< 100` until 2026-08-16. The loose bound was the
        // reason nobody noticed the cap is not the 80 MaxStem advertises: the shortened
        // branch appends '~' plus six hex characters. Seven characters do not sound like
        // much until FileNameFor puts two shortened stems in one name — see the test below.
        Assert.Equal(87, encoded.Length);
        Assert.Contains('~', encoded);
    }

    [Fact]
    public void The_longest_legal_file_name_leaves_no_room_for_an_ordinary_checkout()
    {
        // G9's arithmetic, DERIVED rather than asserted, so that changing MaxStem or the
        // parent-prefix rule re-measures instead of leaving a stale number behind. Deriving
        // it has already been worth it twice: the first version of this test enumerated
        // impossible type/form pairs and reported 201, and the number quoted in the plan
        // before that was a hand count of 189. The measured answer is 190.
        //
        // The gap was written up as needing "a deep folder chain". It does not — one legal
        // file name plus the chain every CODESYS project has leaves 19 characters for the
        // repository root, and that is with a ONE-CHARACTER project name. C:\GIT\Versio is
        // 13 and fits by six; anything under a user profile does not.
        const int MaxPath = 260;
        var longName = new string('A', 300);

        var worst = 0;
        var worstLabel = string.Empty;

        foreach (var type in Enum.GetValues<CodesysObjectType>())
        {
            // ONLY THE FORMS THIS TYPE CAN ACTUALLY TAKE. Enumerating the two enums against
            // each other was the first version, and it reported Transition/Libraries at 201
            // characters — a combination the exporter cannot produce, because .libraries.yaml
            // belongs to a library manager and a library manager is not parent-prefixed. A
            // worst case built from impossible inputs is not a worst case, and it inflated
            // this number by twelve before ExportFormRules was asked instead.
            foreach (var impl in new[] { true, false })
            {
                foreach (var decl in new[] { true, false })
                {
                    foreach (var kids in new[] { true, false })
                    {
                        var form = ExportFormRules.Decide(type, impl, decl, kids);

                        // A parent is supplied for every type; FileNameFor uses it only for
                        // the five that are parent-prefixed, so this finds those without
                        // naming them and keeps working if that set changes.
                        var fileName = PathMapper.FileNameFor(longName, type, form, longName);
                        if (fileName.Length > worst)
                        {
                            worst = fileName.Length;
                            worstLabel = $"{type}/{form}";
                        }
                    }
                }
            }
        }

        // The shallowest chain a real project has. Every CODESYS project has it.
        const string Chain = @"Device\Plc Logic\Application";

        // What the export adds above the mirror, before the repository root: the binding's
        // folder and the staging suffix the swap needs. Measured against staging because it
        // is the longer of the two — a name that fits the mirror by fifteen characters still
        // fails on the way in. The project name is the shortest plausible one, so this is
        // the FRIENDLIEST case rather than a representative one.
        var above = $@"\plc\M{BindingConventions.StagingSuffix}";

        var belowRoot = above.Length + 1 + Chain.Length + 1 + worst;
        var leftForRoot = MaxPath - belowRoot;

        // NOT "it overflows from any root", which is what this asserted first and is only
        // true for a longer project name. The robust claim is that what remains cannot hold
        // an ordinary checkout: C:\Users\somebody\Documents\GitHub\Repo is 38 characters, and
        // a repository nobody can put in their own Documents folder is the hazard.
        Assert.True(leftForRoot < 38,
            $"the longest file name is {worst} characters ({worstLabel}); with {Chain} and "
            + $"{above} that leaves {leftForRoot} characters for the repository root, which "
            + "is now enough for an ordinary checkout. If MaxStem or the parent-prefix rule "
            + "was tightened deliberately, this test has done its job — re-derive G9's "
            + "numbers in CODESYS-IMPORT-PLAN.md and versio_text, which quote them.");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("con")]
    public void Reserved_windows_device_names_are_escaped(string name)
    {
        var encoded = PathMapper.EncodeStem(name);

        Assert.NotEqual(name, encoded);
        Assert.Equal(name, PathMapper.DecodeStem(encoded));
    }

    [Theory]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    public void Trailing_space_and_dot_are_encoded_because_windows_strips_them(string name)
    {
        // Windows silently drops these, which would make two distinct objects collide.
        var encoded = PathMapper.EncodeStem(name);

        Assert.False(encoded.EndsWith(' ') || encoded.EndsWith('.'));
        Assert.Equal(name, PathMapper.DecodeStem(encoded));
    }

    [Fact]
    public void Distinct_names_never_collide_after_encoding()
    {
        string[] names = ["a/b", "a\\b", "a:b", "a|b", "a?b", "a*b", "ab", "a b", "CON", "con"];

        var encoded = names.Select(PathMapper.EncodeStem).ToList();

        Assert.Equal(names.Length, encoded.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Empty_name_gets_a_placeholder_rather_than_an_empty_file_name()
    {
        Assert.Equal("_unnamed", PathMapper.EncodeStem(""));
        Assert.Equal("_unnamed", PathMapper.EncodeStem(null));
    }

    [Fact]
    public void A_visualisation_does_not_collide_with_a_same_named_pou()
    {
        // "Main" the program plus "Main" the visualisation is a common real project shape.
        var pou = PathMapper.FileNameFor("Main", CodesysObjectType.Pou, ExportForm.StFull);
        var visu = PathMapper.FileNameFor("Main", CodesysObjectType.Visualisation, ExportForm.Native);

        Assert.Equal("Main.st", pou);
        Assert.Equal("Main.vis.xml", visu);
        Assert.NotEqual(pou, visu);
    }

    [Fact]
    public void A_gvl_writes_both_forms_under_distinct_names()
    {
        var st = PathMapper.FileNameFor("GVL_IO", CodesysObjectType.Gvl, ExportForm.StDeclaration);
        var xml = PathMapper.FileNameFor("GVL_IO", CodesysObjectType.Gvl, ExportForm.Native);

        Assert.Equal("GVL_IO.gvl.st", st);
        Assert.Equal("GVL_IO.gvl.xml", xml);
    }

    [Theory]
    [InlineData(CodesysObjectType.Method, ExportForm.StFull, "Conveyor.Execute.st")]
    [InlineData(CodesysObjectType.Action, ExportForm.StImplementation, "Conveyor.Execute.action.st")]
    [InlineData(CodesysObjectType.Transition, ExportForm.StImplementation, "Conveyor.Execute.transition.st")]
    [InlineData(CodesysObjectType.Property, ExportForm.NativeRecursive, "Conveyor.Execute.xml")]
    public void Sub_pou_members_are_prefixed_with_their_owner(
        CodesysObjectType type, ExportForm form, string expected)
    {
        Assert.Equal(expected, PathMapper.FileNameFor("Execute", type, form, "Conveyor"));
    }

    [Fact]
    public void An_action_and_a_transition_of_the_same_name_do_not_collide()
    {
        // "action" and "transition" are reserved IEC keywords, so they cannot collide with
        // a method name either.
        var a = PathMapper.FileNameFor("Step", CodesysObjectType.Action, ExportForm.StImplementation, "P");
        var t = PathMapper.FileNameFor("Step", CodesysObjectType.Transition, ExportForm.StImplementation, "P");

        Assert.NotEqual(a, t);
    }

    [Fact]
    public void Top_level_objects_are_not_parent_prefixed()
    {
        Assert.Equal("Main.st",
            PathMapper.FileNameFor("Main", CodesysObjectType.Pou, ExportForm.StFull, "Application"));
    }

    [Theory]
    [InlineData(ExportForm.StFull, ".st")]
    [InlineData(ExportForm.StDeclaration, ".st")]
    [InlineData(ExportForm.StImplementation, ".st")]
    [InlineData(ExportForm.Native, ".xml")]
    [InlineData(ExportForm.NativeRecursive, ".xml")]
    public void Suffix_follows_the_form(ExportForm form, string expected)
    {
        Assert.Equal(expected, PathMapper.SuffixFor(form));
    }

    [Theory]
    [InlineData(@"a\b\c", "a/b/c")]
    [InlineData("/a/b", "a/b")]
    [InlineData(@"\a\b", "a/b")]
    [InlineData("a/b", "a/b")]
    public void Relative_paths_are_normalized_to_git_form(string input, string expected)
    {
        Assert.Equal(expected, PathMapper.NormalizeRelative(input));
    }
}
