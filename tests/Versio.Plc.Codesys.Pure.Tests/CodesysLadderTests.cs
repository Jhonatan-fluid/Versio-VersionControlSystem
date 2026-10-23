using Versio.Core.Iec.Ladder;
using Versio.Plc.Codesys.Ladder;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The CODESYS LD/FBD network list into the neutral ladder model, from the fixture's real
/// <c>Signals</c> block in both of its containers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Real output, one block.</b> <c>Signals</c> is CODESYS's own export, written twice: the
/// classic mirror's <c>Signals.xml</c> and File-Based Storage's <c>Signals.pou.xml.v3</c>. It has
/// three networks: a contact into a TON whose output drives a coil and a jump, a chain of two boxes
/// in series with two contacts, and a labelled contact and coil. Everything it lacks (OR,
/// negation, edges, set/reset, EN/ENO) is listed in <c>docs/fbs/PLAN.md</c> §5. The parser draws
/// anything it cannot name as unrecognised rather than guessing, and these tests pin that too.
/// </para>
/// <para>
/// <b>Approved drawings.</b> As in the TIA tests: a difference writes <c>*.received.html</c>
/// beside <c>Specimens/Ladder/Approved/*.approved.html</c> and fails. Approving is copying it over
/// after looking at it in a browser.
/// </para>
/// </remarks>
public sealed class CodesysLadderTests
{
    private static readonly string Approved =
        Path.Combine(TestPaths.Root, "tests", "Versio.Plc.Codesys.Pure.Tests", "Specimens", "Ladder", "Approved");

    private const string FbsPath =
        "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Signals.pou.xml.v3";

    private static readonly string FbsFile = Path.Combine(
        TestPaths.Project("RefrigeratorControl_FBS"), "RefrigeratorControl.fbsproj",
        FbsPath.Replace('/', Path.DirectorySeparatorChar));

    private static readonly string ClassicFile = Path.Combine(
        TestPaths.Project("RefrigeratorControl"), "Device", "Plc Logic", "Application", "Signals.xml");

    private static LadderDocument Fbs(string? content = null)
    {
        var document = CodesysProjectFormat.Instance.TryParseLadder(FbsPath, content ?? File.ReadAllText(FbsFile));
        Assert.NotNull(document);
        return document;
    }

    private static LadderDocument Classic()
    {
        var document = CodesysProjectFormat.Instance.TryParseLadder(
            "Device/Plc Logic/Application/Signals.xml", File.ReadAllText(ClassicFile));
        Assert.NotNull(document);
        return document;
    }

    private static string Shape(LadderNetwork network) => ShapeOf(LadderStructure.Of(network).Root);

    private static string ShapeOf(LdBlock? block) => block switch
    {
        null => "",
        LdLeaf leaf => leaf.Node.Id,
        LdSeries s => "S(" + string.Join(",", s.Items.Select(ShapeOf)) + ")",
        LdParallel p => "P(" + string.Join("|", p.Branches.Select(ShapeOf)) + ")",
        _ => "?",
    };

    private static T Node<T>(LadderNetwork network, string id) where T : LdNode =>
        Assert.IsType<T>(network.Nodes.Single(n => n.Id == id));

    private static string Fbd() =>
        File.ReadAllText(FbsFile).Replace(
            "<Single Name=\"DefaultViewMode\" Type=\"string\">Ld</Single>",
            "<Single Name=\"DefaultViewMode\" Type=\"string\">Fbd</Single>",
            StringComparison.Ordinal);

    [Fact]
    public void Signals_is_three_ladder_networks_named_after_its_file()
    {
        var document = Fbs();

        Assert.Equal("Signals", document.Body.BlockName);
        Assert.Equal([1, 2, 3], document.Body.Networks.Select(n => n.Number));
        Assert.All(document.Body.Networks, n => Assert.Equal(LdLanguage.Ladder, n.Language));
        Assert.Same(CodesysLadderParser.Style, document.Style);
    }

    [Fact]
    public void Network_1_is_a_contact_into_a_timer_driving_a_coil_and_a_jump()
    {
        var network = Fbs().Body.Networks[0];

        Assert.Equal("S(13,11,P(7|141))", Shape(network));
        Assert.Equal("Glob_Var.Compressor", Node<LdContact>(network, "13").Operand);

        var timer = Node<LdBox>(network, "11");
        Assert.Equal("TON", timer.TypeName);
        Assert.Equal("TON_0", timer.Instance);
        Assert.Equal(["IN", "PT", "Q"], timer.Pins.Select(p => p.Name));
        Assert.Equal("Glob_Var.AlarmThreshold", timer.Pins.Single(p => p.Name == "PT").Operand);
        Assert.Null(timer.Pins.Single(p => p.Name == "IN").Operand);

        Assert.Equal("Glob_Var.Signal", Node<LdCoil>(network, "7").Operand);
        Assert.Equal("DoorIsOpen", Node<LdJump>(network, "141").Target);
    }

    [Fact]
    public void Network_2s_and_puts_the_nested_timer_and_blink_in_series_with_two_contacts()
    {
        var network = Fbs().Body.Networks[1];

        // AND(BLINK(ENABLE := TON(IN := DoorOpen, ...), ...), TON_1.Q, DoorOpen) => Signal: the
        // innermost first input is where the rung starts, and AND is series, not a box.
        Assert.Equal("S(25,23,98,139,113,102)", Shape(network));
        Assert.Equal("Glob_Var.DoorOpen", Node<LdContact>(network, "25").Operand);
        Assert.Equal("TON_1", Node<LdBox>(network, "23").Instance);

        var blink = Node<LdBox>(network, "98");
        Assert.Equal("blink", blink.TypeName);
        Assert.Equal(["ENABLE", "TIMELOW", "TIMEHIGH", "OUT"], blink.Pins.Select(p => p.Name));
        Assert.Equal("tSignalTime", blink.Pins.Single(p => p.Name == "TIMELOW").Operand);

        Assert.Equal("TON_1.Q", Node<LdContact>(network, "139").Operand);
        Assert.DoesNotContain(network.Nodes, n => n is LdBox { TypeName: "AND" });
    }

    [Fact]
    public void Network_3_carries_its_label_and_is_one_contact_and_one_coil()
    {
        var network = Fbs().Body.Networks[2];

        Assert.Equal("S(119,125)", Shape(network));
        Assert.Equal("DoorIsOpen", Assert.Single(LadderStructure.Of(network).Labels).Name);
        Assert.Equal("Glob_Var.Lamp", Node<LdCoil>(network, "125").Operand);
    }

    [Fact]
    public void The_file_based_copy_and_the_classic_export_are_the_same_networks()
    {
        // The same block, written into two containers. If this fails, one of the two is being
        // read differently, and a project moved to File-Based Storage would show a change that
        // is not one.
        var fbs = Fbs().Body;
        var classic = Classic().Body;

        Assert.Equal(classic.BlockName, fbs.BlockName);
        Assert.Equal(
            classic.Networks.Select(LadderStructure.Fingerprint),
            fbs.Networks.Select(LadderStructure.Fingerprint));
    }

    [Fact]
    public void Every_network_has_a_rung_shape_and_draws_within_the_shared_invariants()
    {
        foreach (var network in Fbs().Body.Networks)
        {
            var drawing = LadderLayout.Lay(network, CodesysLadderParser.Style);
            Assert.Null(drawing.FallbackReason);
            var problems = LadderInvariants.Violations(drawing);
            Assert.True(problems.Count == 0, $"network {network.Number}: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void The_same_body_in_fbd_view_is_boxes_with_operands_on_their_pins_and_no_rail()
    {
        // CODESYS stores one network list and lets the IDE show it as LD or FBD, so flipping the
        // view mode is a real FBD specimen, not an invented one.
        var document = Fbs(Fbd());
        var network = document.Body.Networks[1];

        Assert.All(document.Body.Networks, n => Assert.Equal(LdLanguage.FunctionBlock, n.Language));
        Assert.DoesNotContain(network.Nodes, n => n is LdLeftRail or LdContact);

        var and = Node<LdBox>(network, "112");
        Assert.Equal("AND", and.TypeName);
        Assert.Equal(["IN1", "IN2", "IN3", "OUT"], and.Pins.Select(p => p.Name));
        Assert.Equal("TON_1.Q", and.Pins.Single(p => p.Name == "IN2").Operand);
        Assert.Equal("Glob_Var.DoorOpen", Node<LdBox>(network, "23").Pins.Single(p => p.Name == "IN").Operand);

        // The boxes are chained by wires: TON.Q into BLINK.ENABLE into AND.IN1 into the coil.
        Assert.Contains(network.Nets, n => n.Ends.Contains(new LdPinRef("23", "Q")) && n.Ends.Contains(new LdPinRef("98", "ENABLE")));
        Assert.Contains(network.Nets, n => n.Ends.Contains(new LdPinRef("98", "OUT")) && n.Ends.Contains(new LdPinRef("112", "IN1")));
        Assert.Contains(network.Nets, n => n.Ends.Contains(new LdPinRef("112", "OUT")) && n.Ends.Contains(new LdPinRef("102", LdPins.In)));

        foreach (var each in document.Body.Networks)
        {
            var problems = LadderInvariants.Violations(LadderLayout.Lay(each, CodesysLadderParser.Style));
            Assert.True(problems.Count == 0, $"network {each.Number}: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void An_output_flag_nobody_has_measured_is_drawn_as_unrecognised_not_as_a_plain_coil()
    {
        // Network 3's coil, given a flag value the fixture never shows. A set or reset coil drawn
        // as a normal one would show a reviewer logic the PLC does not run.
        var edited = File.ReadAllText(FbsFile);
        var coil = edited.IndexOf("<Single Name=\"Operand\" Type=\"string\">Glob_Var.Lamp</Single>", StringComparison.Ordinal);
        var flag = edited.IndexOf("<Single Name=\"Flags\" Type=\"int\">0</Single>", coil, StringComparison.Ordinal);
        edited = string.Concat(edited.AsSpan(0, flag), "<Single Name=\"Flags\" Type=\"int\">8</Single>",
            edited.AsSpan(flag + "<Single Name=\"Flags\" Type=\"int\">0</Single>".Length));

        var network = Fbs(edited).Body.Networks[2];

        var element = Node<LdUnsupported>(network, "125");
        Assert.Equal("output (flags 8)", element.VendorName);
        Assert.Equal("Glob_Var.Lamp", element.Pins.Single(p => p.Name == "operand").Operand);
        Assert.Equal("S(119,125)", Shape(network));
    }

    [Fact]
    public void An_operand_codesys_does_not_call_boolean_is_not_drawn_as_a_contact()
    {
        // Network 3's contact, marked non-Boolean. Where power flows that is a shape the fixture
        // never shows, and a contact on a TIME or an INT is an element nobody placed.
        var edited = File.ReadAllText(FbsFile);
        var operand = edited.LastIndexOf("<Single Name=\"Operand\" Type=\"string\">Glob_Var.DoorOpen</Single>", StringComparison.Ordinal);
        var boolean = edited.IndexOf("<Single Name=\"Boolean\" Type=\"bool\">True</Single>", operand, StringComparison.Ordinal);
        edited = string.Concat(edited.AsSpan(0, boolean), "<Single Name=\"Boolean\" Type=\"bool\">False</Single>",
            edited.AsSpan(boolean + "<Single Name=\"Boolean\" Type=\"bool\">True</Single>".Length));

        var network = Fbs(edited).Body.Networks[2];

        var element = Node<LdUnsupported>(network, "119");
        Assert.Equal("operand (not Boolean)", element.VendorName);
        Assert.Equal("Glob_Var.DoorOpen", element.Pins.Single(p => p.Name == "operand").Operand);
        Assert.Equal("S(119,125)", Shape(network));
    }

    [Fact]
    public void A_commented_out_network_says_so_in_its_title_and_an_il_network_is_a_placeholder()
    {
        var text = File.ReadAllText(FbsFile);
        var first = text.IndexOf("<Single Name=\"OutCommented\" Type=\"bool\">False</Single>", StringComparison.Ordinal);
        text = string.Concat(text.AsSpan(0, first), "<Single Name=\"OutCommented\" Type=\"bool\">True</Single>",
            text.AsSpan(first + "<Single Name=\"OutCommented\" Type=\"bool\">False</Single>".Length));
        var il = text.LastIndexOf("<Single Name=\"ILActive\" Type=\"bool\">False</Single>", StringComparison.Ordinal);
        text = string.Concat(text.AsSpan(0, il), "<Single Name=\"ILActive\" Type=\"bool\">True</Single>",
            text.AsSpan(il + "<Single Name=\"ILActive\" Type=\"bool\">False</Single>".Length));

        var networks = Fbs(text).Body.Networks;

        Assert.Equal("(commented out)", networks[0].Title);
        Assert.NotEmpty(networks[0].Nodes);
        Assert.Equal("IL", networks[2].UnsupportedLanguage);
        Assert.Empty(networks[2].Nodes);
    }

    [Fact]
    public void A_contact_renamed_in_a_copy_is_that_one_network_changed()
    {
        // Network 2, six elements. Network 3 has two, and one of two changed reads as the network
        // replaced, which is LadderDiff's answer and not this parser's question.
        var original = File.ReadAllText(FbsFile);
        var edited = original.Replace(">TON_1.Q<", ">TON_1.ET<", StringComparison.Ordinal);

        var comparison = LadderDiff.Compare(Fbs(original).Body, Fbs(edited).Body);

        var row = Assert.Single(comparison.Rows, r => r.Change != LadderChange.Same);
        Assert.Equal(LadderChange.Changed, row.Change);
        Assert.Equal(2, row.New!.Network.Number);
        Assert.Equal(LdMark.Modified, row.New.Marks["139"]);
    }

    [Theory]
    [InlineData("x/PLC_PRG.prg.st", "PROGRAM PLC_PRG\nEND_PROGRAM")]
    [InlineData("x/Signals.xml", "not xml")]
    [InlineData("x/Signals.pou.xml.v3", "<?xml version=\"1.0\"?><Single />")]
    [InlineData("x/Signals.pou.xml.v3", "__V3_META__\n{}\n__V3_CONTENT__\nnot xml")]
    [InlineData("x/Signals.pou.xml.v3", "")]
    public void Anything_that_is_not_a_network_list_is_null(string path, string content)
    {
        Assert.Null(CodesysProjectFormat.Instance.TryParseLadder(path, content));
    }

    [Theory]
    [InlineData("RefrigeratorControl_FBS", "RefrigeratorControl.fbsproj/Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Task Configuration.taskconfig.xml.v3^/MainTask.task.xml.v3")]
    [InlineData("RefrigeratorControl_FBS", "RefrigeratorControl.fbsproj/Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/PersistentVars.varpersistent.xml.v3")]
    [InlineData("RefrigeratorControl", "Device/Plc Logic/Application/Glob_Var.gvl.xml")]
    [InlineData("MachineWithMembers", "Device/Plc Logic/Application/PLC_PRG.xml")]
    public void Real_objects_that_are_not_ld_or_fbd_are_null(string project, string relative)
    {
        // The last one is CFC: graphical, and not drawn until CFC has a layout of its own.
        var path = Path.Combine(TestPaths.Project(project), relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Null(CodesysProjectFormat.Instance.TryParseLadder(relative, File.ReadAllText(path)));
    }

    [Theory]
    [InlineData(false, "Signals")]
    [InlineData(true, "Signals_fbd")]
    public void Signals_draws_as_approved(bool fbd, string approvedAs)
    {
        var document = Fbs(fbd ? Fbd() : null);
        var html = LadderHtml.Fragment(LadderDiff.Compare(document.Body, document.Body), document.Style);

        var approved = Path.Combine(Approved, approvedAs + ".approved.html");
        var received = Path.Combine(Approved, approvedAs + ".received.html");
        if (File.Exists(approved) && File.ReadAllText(approved) == html)
        {
            File.Delete(received);
            return;
        }

        Directory.CreateDirectory(Approved);
        File.WriteAllText(received, html);
        Assert.Fail($"{approvedAs}: the drawing differs from {approved}. Look at {received} in a browser; if it is right, copy it over the approved file.");
    }

    /// <summary>The fixture's first network with <paramref name="boxes"/> nested And boxes
    /// spliced into an input list.</summary>
    private static string Nested(int boxes)
    {
        var open = "<Single Type=\"{acfc6f68-8e3a-4af5-bf81-3dd512095a46}\" Method=\"IArchivable\">"
                   + "<Single Name=\"CallType\" Type=\"string\">And</Single><List2 Name=\"InputItems\">";
        var deep = string.Concat(Enumerable.Repeat(open, boxes))
                   + "<Null />"
                   + string.Concat(Enumerable.Repeat("</List2></Single>", boxes));

        const string marker = "<List2 Name=\"InputItems\">";
        var text = File.ReadAllText(ClassicFile);
        return text.Insert(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length, deep);
    }

    [Fact]
    public void Boxes_nested_past_the_expression_cap_draw_a_cut_mark_instead_of_overflowing()
    {
        // Power and Data recurse once per expression level, and a StackOverflowException
        // is the one failure no catch can keep: the web viewer hands every repository
        // .xml to this parser, and its "throwing parser degrades to plain text" rule
        // cannot catch an overflow. Past the cap the drawing carries an element saying
        // something was cut, like every other shape this parser cannot draw. 180 boxes
        // stays under SafeXml's document depth cap, so this exercises the parser's own
        // guard rather than the one below it.
        var document = CodesysProjectFormat.Instance.TryParseLadder(
            "Device/Plc Logic/Application/Signals.xml", Nested(180));

        Assert.NotNull(document);
        Assert.Contains(
            document.Body.Networks.SelectMany(n => n.Nodes),
            n => n is LdUnsupported { VendorName: "too deeply nested" }
                 || (n is LdBox box && box.Pins.Any(p => p.Operand == "(too deeply nested)")));
    }

    [Fact]
    public void A_document_nested_past_the_xml_cap_is_refused_in_milliseconds_not_minutes()
    {
        // XDocument's tree building is quadratic in nesting depth — 30,000 levels was
        // measured at 70+ s of CPU before any parser code ran — so SafeXml refuses the
        // shape in a streaming pre-pass and the parser answers null: the Raw view, which
        // is where an undrawable file always lands.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var document = CodesysProjectFormat.Instance.TryParseLadder(
            "Device/Plc Logic/Application/Signals.xml", Nested(30_000));
        watch.Stop();

        Assert.Null(document);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
            $"the refusal took {watch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void A_gallery_of_signals_is_written_for_a_human_to_look_at()
    {
        var page = new System.Text.StringBuilder($"<!doctype html><meta charset=utf-8><style>{LadderPageAssets.Css} body{{background:var(--ld-bg)}}</style>");
        foreach (var (name, document) in new[] { ("Signals (LD)", Fbs()), ("Signals (FBD view)", Fbs(Fbd())) })
        {
            page.Append("<h2 style=\"font:600 15px sans-serif\">").Append(name).Append("</h2>")
                .Append(LadderHtml.Fragment(LadderDiff.Compare(document.Body, document.Body), document.Style));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "codesys-ladder-gallery.html");
        File.WriteAllText(path, page.ToString());
        Assert.True(new FileInfo(path).Length > 1000);
    }

    /// <summary>
    /// Each network names the file lines it came from, in both containers — and in File-Based
    /// Storage the XML starts after a header the XML's own numbering does not count, so a line
    /// right in the XML is wrong in the file unless the header is added (docs/web/PLAN.md, C3).
    /// </summary>
    [Fact]
    public void Every_network_names_the_file_line_its_element_starts_on_in_both_containers()
    {
        foreach (var (document, file) in new[] { (Classic(), ClassicFile), (Fbs(), FbsFile) })
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            Assert.NotEmpty(document.Body.Networks);
            Assert.All(document.Body.Networks, n =>
            {
                Assert.NotNull(n.Lines);
                Assert.Contains("{d9a99d73-b633-47db-b876-a752acb25871}", lines[n.Lines!.Value.Start - 1], StringComparison.Ordinal);
            });
        }
    }

    /// <summary>A graphical POU's table rows name their lines inside the XML's declaration text, which starts on its element's own line.</summary>
    [Fact]
    public void A_graphical_pous_table_rows_name_their_lines_in_the_xml()
    {
        var text = File.ReadAllText(ClassicFile);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var table = CodesysProjectFormat.Instance.TryParseMemberTable("Device/Plc Logic/Application/Signals.xml", text)!;

        var row = table.Members.Single(m => m.Path == "TON_0");
        Assert.Contains("TON_0: TON;", lines[row.Lines!.Value.Start - 1], StringComparison.Ordinal);
    }

    private static int LineOf(string[] lines, string text) => Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal)) + 1;

    /// <summary>
    /// The ladder's framing for review coverage (docs/web/PLAN.md, C4): the envelope and the markup
    /// outside its networks, with the Interface left to the table drawn over it. In a classic export
    /// only the object's own element counts, because the file can hold its children below it.
    /// </summary>
    [Fact]
    public void A_ladders_framing_is_the_envelope_and_the_markup_outside_its_networks_and_its_interface()
    {
        var fbsLines = File.ReadAllText(FbsFile).Replace("\r\n", "\n").Split('\n');
        var fbs = Fbs();
        bool FramedFbs(int line) => fbs.Structural.Any(s => s.Contains(line));

        Assert.True(FramedFbs(1));
        Assert.True(FramedFbs(LineOf(fbsLines, "__V3_CONTENT__")));
        Assert.True(FramedFbs(LineOf(fbsLines, "<List2 Name=\"NetworkList\">")));
        Assert.True(FramedFbs(LineOf(fbsLines, "Name=\"Implementation\"")));
        // Inside a network, markup or not: the network's own span vouches for it.
        Assert.False(FramedFbs(LineOf(fbsLines, "<List2 Name=\"NetworkItems\">")));
        // Inside the Interface: the table's.
        Assert.False(FramedFbs(LineOf(fbsLines, "Name=\"TextDocument\"")));
        Assert.False(FramedFbs(LineOf(fbsLines, "TON_0: TON;")));
        // A value is not framing.
        Assert.False(FramedFbs(LineOf(fbsLines, "Name=\"DefaultViewMode\"")));

        var classicLines = File.ReadAllText(ClassicFile).Replace("\r\n", "\n").Split('\n');
        var classic = Classic();
        bool FramedClassic(int line) => classic.Structural.Any(s => s.Contains(line));

        // The export's wrapper is outside the object and stays a gap when it changes; the object's own markup is framing.
        Assert.False(FramedClassic(LineOf(classicLines, "<ExportFile>")));
        Assert.False(FramedClassic(LineOf(classicLines, "<List2 Name=\"EntryList\">")));
        Assert.True(FramedClassic(LineOf(classicLines, "<List2 Name=\"NetworkList\">")));
        Assert.False(FramedClassic(LineOf(classicLines, "<List2 Name=\"NetworkItems\">")));
    }
}
