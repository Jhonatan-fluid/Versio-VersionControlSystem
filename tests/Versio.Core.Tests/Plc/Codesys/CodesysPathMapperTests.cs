using Versio.Core.Plc.Codesys;
using Versio.Core.Serialization;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

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
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>c")]
    [InlineData("a|b")]
    public void Characters_Windows_forbids_are_encoded_reversibly(string name)
    {
        var encoded = PathMapper.EncodeStem(name);

        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('\\', encoded);
        Assert.DoesNotContain(':', encoded);
        Assert.DoesNotContain('*', encoded);
        Assert.DoesNotContain('?', encoded);
        Assert.Equal(name, PathMapper.DecodeStem(encoded));
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
