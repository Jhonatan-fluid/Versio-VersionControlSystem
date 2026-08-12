using Versio.Core.Plc.Codesys;
using Versio.Core.Serialization;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

public class ExportFormRulesTests
{
    [Fact]
    public void A_textual_pou_is_written_as_readable_ST()
    {
        Assert.Equal(ExportForm.StFull,
            ExportFormRules.Decide(CodesysObjectType.Pou, hasTextualImplementation: true,
                                   hasTextualDeclaration: true));
    }

    [Fact]
    public void A_GRAPHICAL_pou_falls_back_to_native_XML()
    {
        // A POU drawn in CFC/LD/FBD has no textual implementation. Writing it as .st would
        // silently discard its graphical layout - the worst kind of data loss, because the
        // file would look fine.
        //
        // Confirmed against the real project: PLC_PRG and Signals have hasTextualImpl=false
        // while Simulation has it true.
        Assert.Equal(ExportForm.Native,
            ExportFormRules.Decide(CodesysObjectType.Pou, hasTextualImplementation: false,
                                   hasTextualDeclaration: true));
    }

    [Fact]
    public void An_interface_is_exported_as_ST()
    {
        // codescribe does not export interfaces at all - a real gap this closes.
        Assert.Equal(ExportForm.StFull,
            ExportFormRules.Decide(CodesysObjectType.Interface, true, true));
    }

    [Fact]
    public void A_GVL_is_written_in_both_forms()
    {
        Assert.Equal(ExportForm.GvlBoth,
            ExportFormRules.Decide(CodesysObjectType.Gvl, false, true));
    }

    [Fact]
    public void A_DUT_is_declaration_only()
    {
        Assert.Equal(ExportForm.StDeclaration,
            ExportFormRules.Decide(CodesysObjectType.Dut, false, true));
    }

    [Theory]
    [InlineData(CodesysObjectType.Action)]
    [InlineData(CodesysObjectType.Transition)]
    public void Actions_and_transitions_are_implementation_only(CodesysObjectType type)
    {
        // These objects have no textual declaration at all.
        Assert.Equal(ExportForm.StImplementation,
            ExportFormRules.Decide(type, hasTextualImplementation: true, hasTextualDeclaration: false));
    }

    [Fact]
    public void A_property_is_exported_recursively_because_it_owns_its_accessors()
    {
        Assert.Equal(ExportForm.NativeRecursive,
            ExportFormRules.Decide(CodesysObjectType.Property, false, false));
    }

    [Theory]
    [InlineData(CodesysObjectType.VisualisationManager)]
    [InlineData(CodesysObjectType.TargetVisualisation)]
    [InlineData(CodesysObjectType.WebVisualisation)]
    [InlineData(CodesysObjectType.VisualisationStyle)]
    public void Visualisation_service_objects_are_skipped(CodesysObjectType type)
    {
        // Exporting the manager makes every later import raise an interactive overwrite
        // dialog (codescribe's finding). The template carries them instead.
        Assert.Equal(ExportForm.Skip, ExportFormRules.Decide(type, false, false));
    }

    [Theory]
    [InlineData(CodesysObjectType.Task)]
    [InlineData(CodesysObjectType.CallToPou)]
    public void Task_children_are_skipped_because_the_task_config_covers_them(CodesysObjectType type)
    {
        Assert.Equal(ExportForm.Skip, ExportFormRules.Decide(type, false, false));
    }

    [Theory]
    [InlineData(CodesysObjectType.Folder)]
    [InlineData(CodesysObjectType.Device)]
    [InlineData(CodesysObjectType.PlcLogic)]
    [InlineData(CodesysObjectType.Application)]
    public void Structural_nodes_become_directories(CodesysObjectType type)
    {
        Assert.Equal(ExportForm.Container, ExportFormRules.Decide(type, false, false));
    }

    [Fact]
    public void An_UNKNOWN_type_falls_back_to_native_export_and_is_never_dropped()
    {
        // A newer CODESYS adding a type must not silently lose objects. codescribe hit
        // exactly this with an LD nested inside a CFC.
        Assert.Equal(ExportForm.Native,
            ExportFormRules.Decide(CodesysObjectType.Unknown, false, false));
    }

    [Fact]
    public void An_UNKNOWN_type_WITH_CHILDREN_keeps_its_subtree_shape()
    {
        // The type table is incomplete by nature: probing a 25-object toy project turned up
        // two missing GUIDs ("Plc Logic" and "Application"), so a real machine project will
        // find more. Both of those are structural. Treating an unknown container as a plain
        // leaf flattens its children into the parent folder and quietly destroys the tree.
        Assert.Equal(ExportForm.NativeWithChildren,
            ExportFormRules.Decide(CodesysObjectType.Unknown, false, false, hasChildren: true));
    }

    [Fact]
    public void A_KNOWN_type_with_children_is_unaffected_by_the_unknown_container_rule()
    {
        // The rule must apply only to unmapped types, or a POU with methods would suddenly
        // change form.
        Assert.Equal(ExportForm.StFull,
            ExportFormRules.Decide(CodesysObjectType.Pou, true, true, hasChildren: true));
        Assert.Equal(ExportForm.NativeRecursive,
            ExportFormRules.Decide(CodesysObjectType.TaskConfiguration, false, false, hasChildren: true));
    }

    [Theory]
    [InlineData(CodesysObjectType.TextList)]
    [InlineData(CodesysObjectType.GlobalTextList)]
    [InlineData(CodesysObjectType.ImagePool)]
    [InlineData(CodesysObjectType.ProjectInformation)]
    public void Gaps_codescribe_leaves_open_are_exported(CodesysObjectType type)
    {
        Assert.NotEqual(ExportForm.Skip, ExportFormRules.Decide(type, false, false));
    }

    [Fact]
    public void Every_defined_type_produces_a_defined_form()
    {
        foreach (var type in Enum.GetValues<CodesysObjectType>())
        {
            foreach (var impl in new[] { true, false })
            {
                foreach (var decl in new[] { true, false })
                {
                    foreach (var kids in new[] { true, false })
                    {
                        Assert.True(Enum.IsDefined(ExportFormRules.Decide(type, impl, decl, kids)));
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_type_in_the_GUID_table_has_an_export_rule()
    {
        // A GUID we can identify but have no rule for would fall through to the unmapped
        // branch and be exported as raw XML without anyone noticing the gap.
        foreach (var type in CodesysObjectTypes.All.Values)
        {
            var form = ExportFormRules.Decide(type, false, false);
            Assert.True(Enum.IsDefined(form), $"{type} has no export rule");
        }
    }

    [Theory]
    [InlineData(ExportForm.StFull, true)]
    [InlineData(ExportForm.StDeclaration, true)]
    [InlineData(ExportForm.StImplementation, true)]
    [InlineData(ExportForm.Native, false)]
    [InlineData(ExportForm.NativeRecursive, false)]
    [InlineData(ExportForm.Container, false)]
    [InlineData(ExportForm.Skip, false)]
    public void IsTextual_identifies_the_reviewable_forms(ExportForm form, bool expected)
    {
        Assert.Equal(expected, ExportFormRules.IsTextual(form));
    }
}
