using Versio.Core.Projects;
using Versio.Plc.TiaPortal;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Native data-block XML becomes a member table; everything else stays null.
/// </summary>
/// <remarks>
/// Inline fixtures, no LFS. The FB document exists so widening the local-name gate
/// to any <c>SW.Blocks.*</c> goes red — that object has an Interface too.
/// </remarks>
public sealed class TiaPortalMemberTableTests
{
    private static MemberTable? Parse(string path, string xml) =>
        TiaPortalMemberTableParser.TryParse(path, xml);

    private static MemberTable MustParse(string xml) =>
        Assert.IsType<MemberTable>(Parse("block.xml", xml));

    [Fact]
    public void A_global_data_block_tables_a_start_value()
    {
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                      <Section Name="Static">
                        <Member Name="High" Datatype="Real">
                          <StartValue>2900.0</StartValue>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <MemoryLayout>Optimized</MemoryLayout>
                  <Name>Limits</Name>
                  <Number>58</Number>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.Equal("Limits", table.Name);
        Assert.Equal("58", table.Number);
        Assert.Equal(string.Empty, table.InstanceOf);
        Assert.Equal("Optimized", table.MemoryLayout);

        var row = Assert.Single(table.Members);
        Assert.Equal("High", row.Name);
        Assert.Equal("High", row.Path);
        Assert.Equal("Real", row.Datatype);
        Assert.Equal("Static", row.Section);
        Assert.Equal("2900.0", row.StartValue);
        Assert.Equal("Read/Write", row.ExternalAccess);
        Assert.Equal(0, row.Depth);
    }

    [Fact]
    public void An_instance_data_block_flattens_nested_members_and_fills_instance_of()
    {
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.InstanceDB ID="0">
                <AttributeList>
                  <InstanceOfName>PidCtrl</InstanceOfName>
                  <InstanceOfType>FB</InstanceOfType>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="Pid" Datatype="&quot;PidCtrl&quot;">
                          <Comment>
                            <MultiLanguageText Lang="en-US">controller</MultiLanguageText>
                          </Comment>
                          <Sections>
                            <Section Name="Input">
                              <Member Name="Gain" Datatype="&quot;AnaVal&quot;">
                                <Sections>
                                  <Section Name="None">
                                    <Member Name="Value" Datatype="Real">
                                      <StartValue>1.5</StartValue>
                                    </Member>
                                  </Section>
                                </Sections>
                              </Member>
                            </Section>
                          </Sections>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Fic</Name>
                  <Number>13</Number>
                </AttributeList>
              </SW.Blocks.InstanceDB>
            </Document>
            """);

        Assert.Equal("Fic", table.Name);
        Assert.Equal("PidCtrl", table.InstanceOf);
        Assert.Equal("Instance of PidCtrl", table.InstanceCaption);

        Assert.Equal(3, table.Members.Count);
        Assert.Equal("Pid", table.Members[0].Path);
        Assert.Equal("controller", table.Members[0].Comment);
        Assert.Equal("\"PidCtrl\"", table.Members[0].Datatype);
        Assert.Equal("Pid.Gain", table.Members[1].Path);
        Assert.Equal(1, table.Members[1].Depth);

        var leaf = table.Members[2];
        Assert.Equal("Pid.Gain.Value", leaf.Path);
        Assert.Equal("Value", leaf.Name);
        Assert.Equal("1.5", leaf.StartValue);
        Assert.Equal(2, leaf.Depth);
        Assert.Equal("Real", leaf.Datatype);
    }

    [Fact]
    public void Array_subelements_become_indexed_rows()
    {
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="Values" Datatype="Array[0..1] of Real">
                          <Subelement Path="0">
                            <StartValue>1.0</StartValue>
                          </Subelement>
                          <Subelement Path="1">
                            <StartValue>2.0</StartValue>
                          </Subelement>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Samples</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.Equal(3, table.Members.Count);
        Assert.Equal("Values", table.Members[0].Path);
        Assert.Equal("Values[0]", table.Members[1].Path);
        Assert.Equal("1.0", table.Members[1].StartValue);
        Assert.Equal(1, table.Members[1].Depth);
        Assert.Equal("Values[1]", table.Members[2].Path);
        Assert.Equal("2.0", table.Members[2].StartValue);
        Assert.Equal("Read/Write", table.Members[0].ExternalAccess);
        Assert.Equal("Read/Write", table.Members[1].ExternalAccess);
    }

    [Theory]
    [InlineData("false", "false", "None")]
    [InlineData("true", "false", "Read")]
    [InlineData("true", "true", "Read/Write")]
    [InlineData("false", "true", "None")]
    public void External_access_flags_collapse_to_one_column(
        string accessible, string writable, string expected)
    {
        // Writable without accessible is still None: there is nothing to write.
        var table = MustParse($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="PV" Datatype="Bool">
                          <AttributeList>
                            <BooleanAttribute Name="ExternalAccessible">{accessible}</BooleanAttribute>
                            <BooleanAttribute Name="ExternalWritable">{writable}</BooleanAttribute>
                          </AttributeList>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Flags</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.Equal(expected, Assert.Single(table.Members).ExternalAccess);
    }

    [Fact]
    public void Omitted_external_flags_default_to_read_write()
    {
        // This vendor writes the flags only when they are not the IDE default.
        // Nested UDT members on FIT100Liter_Type omit them; they are not None.
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.InstanceDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="Scale" Datatype="&quot;ScaVal&quot;">
                          <AttributeList>
                            <BooleanAttribute Name="ExternalAccessible">false</BooleanAttribute>
                            <BooleanAttribute Name="ExternalWritable">false</BooleanAttribute>
                          </AttributeList>
                          <Sections>
                            <Section Name="None">
                              <Member Name="High" Datatype="Real">
                                <StartValue>3600.0</StartValue>
                              </Member>
                            </Section>
                          </Sections>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Fit</Name>
                </AttributeList>
              </SW.Blocks.InstanceDB>
            </Document>
            """);

        Assert.Equal("None", table.Members[0].ExternalAccess);
        Assert.Equal("Read/Write", table.Members[1].ExternalAccess);
    }

    [Fact]
    public void External_visible_alone_is_not_access()
    {
        // Visibility in HMI engineering is a different flag. Painting it as None
        // would hide members that are still reachable from outside.
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="Hidden" Datatype="Bool">
                          <AttributeList>
                            <BooleanAttribute Name="ExternalVisible">false</BooleanAttribute>
                          </AttributeList>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Flags</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.Equal("Read/Write", Assert.Single(table.Members).ExternalAccess);
    }

    [Fact]
    public void Array_subelements_inherit_the_member_access()
    {
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Static">
                        <Member Name="Values" Datatype="Array[0..0] of Real">
                          <AttributeList>
                            <BooleanAttribute Name="ExternalAccessible">true</BooleanAttribute>
                            <BooleanAttribute Name="ExternalWritable">false</BooleanAttribute>
                          </AttributeList>
                          <Subelement Path="0">
                            <StartValue>1.0</StartValue>
                          </Subelement>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Samples</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.Equal("Read", table.Members[0].ExternalAccess);
        Assert.Equal("Read", table.Members[1].ExternalAccess);
    }

    [Fact]
    public void The_format_forwards_to_the_parser()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Name>Limits</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """;

        var table = TiaPortalProjectFormat.Instance.TryParseMemberTable("plc/Limits.xml", xml);
        Assert.NotNull(table);
        Assert.Equal("Limits", table.Name);
        Assert.True(table.IsValid);
    }

    [Theory]
    [InlineData("block.scl")]
    [InlineData("block.st")]
    public void A_non_xml_path_is_not_a_table(string path)
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList><Name>Limits</Name></AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """;

        Assert.Null(Parse(path, xml));
    }

    [Fact]
    public void A_function_block_tables_its_interface_by_section_with_type_references()
    {
        // Shaped on the fixture's Service_Interaction_FB: user types in quotes, an empty
        // Static section, and a Temp of a UDT. Refused here until 2026-10-04.
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.FB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Input">
                        <Member Name="Startbit" Datatype="Bool" />
                      </Section>
                      <Section Name="InOut">
                        <Member Name="Pump_100" Datatype="&quot;MotL&quot;" />
                        <Member Name="Levels" Datatype="Array[1..4] of &quot;AnaVal&quot;" />
                      </Section>
                      <Section Name="Static" />
                      <Section Name="Temp">
                        <Member Name="State" Datatype="&quot;Service_States&quot;" />
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Service_Interaction_FB</Name>
                  <Number>1</Number>
                </AttributeList>
              </SW.Blocks.FB>
            </Document>
            """);

        Assert.True(table.IsInterface);
        Assert.False(table.IsDataType);

        // Headings for the three non-empty sections, none for Static.
        Assert.Equal(
            ["Input", "Startbit", "InOut", "Pump_100", "Levels", "Temp", "State"],
            table.Members.Select(m => m.Name));
        Assert.Equal(["Input", "InOut", "Temp"],
            table.Members.Where(m => m.IsSectionHeading).Select(m => m.Name));

        var references = table.Members.ToDictionary(m => m.Name, m => m.TypeReference);
        Assert.Equal(string.Empty, references["Startbit"]);
        Assert.Equal("MotL", references["Pump_100"]);
        Assert.Equal("AnaVal", references["Levels"]);
        Assert.Equal("Service_States", references["State"]);
    }

    [Fact]
    public void A_function_has_no_external_access_and_no_void_return()
    {
        // The owner's RealToAnaVal_1: every parameter showed "Read/Write" and a Return section
        // held Ret_Val : Void. A function has no storage outside the call, and Void is not a
        // value.
        var table = MustParse("""
            <Document>
              <SW.Blocks.FC ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Input">
                        <Member Name="In" Datatype="Real" />
                        <Member Name="ST" Datatype="Byte" />
                      </Section>
                      <Section Name="Output">
                        <Member Name="Out" Datatype="&quot;AnaVal&quot;" />
                      </Section>
                      <Section Name="Return">
                        <Member Name="Ret_Val" Datatype="Void" />
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>RealToAnaVal_1</Name>
                  <Number>16</Number>
                </AttributeList>
              </SW.Blocks.FC>
            </Document>
            """);

        Assert.True(table.IsInterface);
        Assert.Equal(["Input", "In", "ST", "Output", "Out"], table.Members.Select(m => m.Name));
        Assert.All(table.Members, m => Assert.Equal(string.Empty, m.ExternalAccess));
    }

    [Fact]
    public void A_function_block_keeps_access_on_its_instance_data_only()
    {
        // Inputs, outputs, in-outs and statics live in the instance DB and carry the HMI
        // flags. A temporary does not exist outside the call.
        var table = MustParse("""
            <Document>
              <SW.Blocks.FB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Input"><Member Name="Start" Datatype="Bool" /></Section>
                      <Section Name="Static"><Member Name="Count" Datatype="Int" /></Section>
                      <Section Name="Temp"><Member Name="Scratch" Datatype="Int" /></Section>
                      <Section Name="Return"><Member Name="Ret_Val" Datatype="Int" /></Section>
                    </Sections>
                  </Interface>
                  <Name>Counter</Name>
                </AttributeList>
              </SW.Blocks.FB>
            </Document>
            """);

        var access = table.Members.Where(m => !m.IsSectionHeading).ToDictionary(m => m.Name, m => m.ExternalAccess);
        Assert.Equal("Read/Write", access["Start"]);
        Assert.Equal("Read/Write", access["Count"]);
        Assert.Equal(string.Empty, access["Scratch"]);

        // A return that IS a value stays.
        Assert.Equal(string.Empty, access["Ret_Val"]);
    }

    [Theory]
    // The body sits between the line after BEGIN and the closing keyword, both excluded.
    [InlineData("Block.scl", "FUNCTION \"F\" : Void\nVAR_INPUT\n  In : Real;\nEND_VAR\n\nBEGIN\n  #x := 1;\nEND_FUNCTION\n", 6, 7)]
    // An END_ keyword inside a comment is not the end; the last one alone on its line is.
    [InlineData("Block.scl", "FUNCTION_BLOCK \"B\"\nBEGIN\n  // END_FUNCTION_BLOCK is in here\n  #y := 2;\nEND_FUNCTION_BLOCK\n", 2, 4)]
    // No closing keyword: the body runs to the end rather than being refused.
    [InlineData("Block.scl", "FUNCTION \"F\" : Void\n// BEGIN is not here\nbegin\n  #z := 3;", 3, 4)]
    public void The_body_lies_between_begin_and_the_closing_keyword(string path, string content, int start, int end)
    {
        Assert.Equal((start, end), new TiaPortalProjectFormat().CodeBody(path, content));
    }

    [Theory]
    [InlineData("Block.scl", "FUNCTION \"F\" : Void\n// BEGIN is mentioned, never alone\n")]
    [InlineData("Block.xml", "BEGIN\n")]
    public void No_body_without_a_begin_line_or_outside_scl(string path, string content)
    {
        Assert.Null(new TiaPortalProjectFormat().CodeBody(path, content));
    }

    [Fact]
    public void A_byte_order_mark_in_front_of_the_document_is_not_a_reason_to_refuse_it()
    {
        // How the committed copy arrives from `git show`: the export's BOM as a character.
        var table = MustParse("\uFEFF" + """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Types.PlcStruct ID="0">
                <AttributeList><Name>AnaVal</Name></AttributeList>
              </SW.Types.PlcStruct>
            </Document>
            """);

        Assert.Equal("AnaVal", table.Name);
    }

    [Fact]
    public void An_organization_block_is_not_a_table()
    {
        // Same Interface schema as every block. The local-name gate is the whole of "the
        // blocks somebody reviews as a table"; widen it to any SW.Blocks.* and this goes
        // green for an OB's start information.
        const string ob = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.OB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Input">
                        <Member Name="Initial_Call" Datatype="Bool" />
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Main</Name>
                  <Number>1</Number>
                </AttributeList>
              </SW.Blocks.OB>
            </Document>
            """;

        Assert.Null(Parse("Main.xml", ob));
    }

    [Fact]
    public void A_plc_data_type_tables_its_members_as_a_data_type()
    {
        // Shaped on the fixture's AnaVal: a comment in two languages, a default value, and
        // a reserved bit locked from outside. It used to be refused here and shown as SCL
        // text; the owner asked for UDTs as tables on 2026-10-04.
        var table = MustParse("""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Types.PlcStruct ID="0">
                <AttributeList>
                  <Interface>
                    <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                      <Section Name="None">
                        <Member Name="Value" Datatype="Real">
                          <Comment>
                            <MultiLanguageText Lang="de-DE">Wert</MultiLanguageText>
                            <MultiLanguageText Lang="en-US">Value</MultiLanguageText>
                          </Comment>
                        </Member>
                        <Member Name="ST" Datatype="Byte">
                          <StartValue>BYTE#16#80</StartValue>
                        </Member>
                        <Member Name="InternalUseBit0" Datatype="Bool">
                          <AttributeList>
                            <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">false</BooleanAttribute>
                            <BooleanAttribute Name="ExternalWritable" SystemDefined="true">false</BooleanAttribute>
                          </AttributeList>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>AnaVal</Name>
                </AttributeList>
              </SW.Types.PlcStruct>
            </Document>
            """);

        Assert.Equal("AnaVal", table.Name);
        Assert.True(table.IsDataType);
        Assert.Equal("DEFAULT VALUE", table.ValueHeader);
        Assert.False(table.HasNumber);

        Assert.Collection(table.Members,
            r => Assert.Equal(("Value", "Real", "", "Read/Write", "Wert"),
                              (r.Name, r.Datatype, r.StartValue, r.ExternalAccess, r.Comment)),
            r => Assert.Equal(("ST", "Byte", "BYTE#16#80"), (r.Name, r.Datatype, r.StartValue)),
            r => Assert.Equal(("InternalUseBit0", "None"), (r.Name, r.ExternalAccess)));
    }

    [Fact]
    public void A_data_block_is_not_marked_a_data_type()
    {
        // The flag is what changes the pane's words; a block that set it would be captioned
        // with "default value" over its start values.
        var table = MustParse("""
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList><Name>Limits</Name></AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """);

        Assert.False(table.IsDataType);
        Assert.Equal("START VALUE", table.ValueHeader);
    }

    [Fact]
    public void Structured_text_is_not_a_table()
    {
        Assert.Null(Parse("Limits.xml", """
            DATA_BLOCK "Limits"
            VAR
              High : Real := 2900.0;
            END_VAR
            """));
    }

    [Fact]
    public void A_foreign_native_document_is_not_a_table()
    {
        Assert.Null(Parse("GVL.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <project>
              <pou name="GVL" />
            </project>
            """));
    }

    [Fact]
    public void An_empty_document_is_not_a_table()
    {
        Assert.Null(Parse("empty.xml", "<Document />"));
    }

    [Fact]
    public void Malformed_xml_returns_null_rather_than_throwing()
    {
        Assert.Null(Parse("broken.xml", "<Document><SW.Blocks.GlobalDB>"));
    }

    /// <summary>Each row names the file line its member element starts on (docs/web/PLAN.md, C3).</summary>
    [Fact]
    public void Every_row_names_the_file_line_its_member_starts_on()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                      <Section Name="Static">
                        <Member Name="Low" Datatype="Real" />
                        <Member Name="High" Datatype="Real">
                          <StartValue>2900.0</StartValue>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Limits</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """;
        var lines = xml.Replace("\r\n", "\n").Split('\n');

        var rows = MustParse(xml).Members.ToDictionary(m => m.Path);

        Assert.Contains("Name=\"Low\"", lines[rows["Low"].Lines!.Value.Start - 1], StringComparison.Ordinal);
        Assert.Contains("Name=\"High\"", lines[rows["High"].Lines!.Value.Start - 1], StringComparison.Ordinal);
        Assert.Equal(rows["High"].Lines!.Value.Start + 1, rows["High"].Lines!.Value.End);
    }

    /// <summary>
    /// A comment that spells its line breaks as <c>&amp;#xA;</c> — which Openness does for a
    /// multi-line comment — is one physical line. The span counted the value's newlines, so the
    /// first member ran over the second's line, and the smallest-span rule that pins a comment
    /// to a member handed the second member's line to the first whenever the second was the
    /// longer of the two (the 2026-10-24 review's item 18).
    /// </summary>
    [Fact]
    public void An_encoded_newline_in_a_comment_does_not_run_a_member_into_the_next()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.GlobalDB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                      <Section Name="Static">
                        <Member Name="Low" Datatype="Real"><Comment><MultiLanguageText Lang="en-US">first&#xA;second&#xA;third</MultiLanguageText></Comment></Member>
                        <Member Name="High" Datatype="Real">
                          <StartValue>2900.0</StartValue>
                          <Comment>
                            <MultiLanguageText Lang="en-US">upper</MultiLanguageText>
                          </Comment>
                        </Member>
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>Limits</Name>
                </AttributeList>
              </SW.Blocks.GlobalDB>
            </Document>
            """;

        var rows = MustParse(xml).Members.ToDictionary(m => m.Path);
        var low = rows["Low"].Lines!.Value;
        var high = rows["High"].Lines!.Value;

        Assert.Equal(low.Start, low.End);
        Assert.True(low.End < high.Start, $"Low {low} runs into High {high}");

        // What the anchor lookup does with them: the line High starts on is High's.
        var candidates = new[] { ("Low", rows["Low"].Lines), ("High", rows["High"].Lines) }
            .Select(c => new Versio.Core.Review.RenderedAnchor(
                "row-" + c.Item1, "row", "block.xml", Versio.Core.Review.ReviewSide.New, "Limits", c.Item1, "fp", c.Item2))
            .ToList();
        var found = Versio.Core.Review.AnchorMatcher.ByLine("block.xml", Versio.Core.Review.ReviewSide.New, high.Start, candidates);
        Assert.Equal("High", found?.Key);
    }

    /// <summary>
    /// The framing review coverage may vouch for through the table (docs/web/PLAN.md, C4): the markup
    /// around and between the rows. Not a row's line, not a value between tags, and nothing inside the
    /// block's ObjectList, whose compile units are the code and are no table's to vouch for.
    /// </summary>
    [Fact]
    public void The_markup_around_the_rows_is_framing_and_values_and_the_code_are_not()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.FB ID="0">
                <AttributeList>
                  <Interface>
                    <Sections>
                      <Section Name="Input">
                        <Member Name="Startbit" Datatype="Bool" Remanence="Retain" />
                      </Section>
                      <Section Name="Static" />
                    </Sections>
                  </Interface>
                  <Name>Pump</Name>
                  <Number>1</Number>
                </AttributeList>
                <ObjectList>
                  <SW.Blocks.CompileUnit ID="5" CompositionName="CompileUnits">
                    <AttributeList>
                      <NetworkSource />
                    </AttributeList>
                  </SW.Blocks.CompileUnit>
                </ObjectList>
              </SW.Blocks.FB>
            </Document>
            """;
        var lines = xml.Replace("\r\n", "\n").Split('\n');
        int LineOf(string text) => Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal)) + 1;

        var table = MustParse(xml);
        bool Framed(int line) => table.Structural.Any(s => s.Contains(line));

        Assert.True(Framed(LineOf("<?xml")));
        Assert.True(Framed(LineOf("<Document>")));
        Assert.True(Framed(LineOf("<Section Name=\"Input\">")));
        Assert.True(Framed(LineOf("</Section>")));
        Assert.True(Framed(LineOf("<Section Name=\"Static\" />")));
        Assert.True(Framed(LineOf("</Document>")));
        // The row's own line, attribute the table does not draw included: the row's to vouch for, or not.
        Assert.False(Framed(LineOf("Name=\"Startbit\"")));
        // Values between tags.
        Assert.False(Framed(LineOf("<Name>Pump</Name>")));
        Assert.False(Framed(LineOf("<Number>1</Number>")));
        // The code, markup or not.
        Assert.False(Framed(LineOf("<ObjectList>")));
        Assert.False(Framed(LineOf("<NetworkSource />")));
    }
}
