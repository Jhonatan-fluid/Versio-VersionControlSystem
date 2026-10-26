using System.Collections;
using System.Text;
using System.Text.Json;
using Versio.Core.Plc;
using Versio.Plc.TiaPortal;
using Versio.Plc.TiaPortal.Host;
using Xunit;

// The host finds a device item's PLC software through GetService<SoftwareContainer>() with
// the type looked up by this exact full name (HostExport.WalkPlcSoftware), so a fake that
// wants to be found has to carry it. It exists only inside this test assembly; the vendor
// seam scanner reads the neutral assemblies, not the vendor's own tests.
namespace Siemens.Engineering.HW.Features
{
    public sealed class SoftwareContainer
    {
        public object? Software { get; init; }
    }
}

namespace Versio.Plc.TiaPortal.Tests
{
    /// <summary>
    /// The host's import, driven without a Portal: fakes shaped like the Openness objects
    /// the host reads by name, a plan serialised through the real job, and the host's own
    /// protocol lines parsed the way the runner parses them.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> The real gate skips on every machine whose account is not in
    /// the Openness group, so until 2026-10-25 nothing exercised <c>Import</c> short of
    /// Portal — and a plan targeting <c>Device2/PLC2</c> with that device renamed in the
    /// live project wrote Device2's payload into Device1 and saved (<c>MatchPlc(...) ??
    /// plcs[0]</c>, the fifth review's P1). A fake PLC that records what is written to it is
    /// how that is pinned.
    /// </remarks>
    public sealed class TiaPortalHostImportTests
    {
        // Console.Out is process-wide and the host writes its protocol there.
        private static readonly object ConsoleGate = new();

        [Fact]
        public void A_file_whose_PLC_is_not_in_the_project_is_refused_and_nothing_is_written_elsewhere()
        {
            // The reviewer's reproduction: Device2 was renamed in Portal after the export.
            var plc1 = new FakePlc("PLC1");
            var plc2 = new FakePlc("PLC2");
            var project = new FakeProject(new FakeDevice("Device1", plc1), new FakeDevice("Device2_renamed", plc2));
            using var src = new MirrorDir(("Device2/PLC2/Program blocks/Pump.scl", "FUNCTION_BLOCK \"Pump\"\nEND_FUNCTION_BLOCK\n"));
            var plan = Plan(ApplyText("Device2/PLC2/Program blocks/Pump.scl", "Device2/PLC2/FB/Pump#1"));

            var (output, ex) = Import(project, src.Path, plan);

            // The first PLC in device order is the one the old fallback wrote into.
            Assert.Empty(plc1.Writes);
            Assert.Empty(plc2.Writes);
            Assert.Null(ex);
            Assert.Equal(0, project.Saves);
            Assert.DoesNotContain(output.Events, e => e.Type == "object");
            var fatal = Assert.IsType<TiaPortalEvent>(output.Fatal);
            var msg = fatal.String("msg") ?? "";
            Assert.Contains("Device2/PLC2/Program blocks/Pump.scl", msg, StringComparison.Ordinal);
            Assert.Contains("Device2/PLC2", msg, StringComparison.Ordinal);
            Assert.Contains("Device1/PLC1", msg, StringComparison.Ordinal);
            Assert.Contains("Device2_renamed/PLC2", msg, StringComparison.Ordinal);
            Assert.Contains("export again", msg, StringComparison.Ordinal);
        }

        [Fact]
        public void A_plan_with_one_unresolved_file_applies_none_of_its_files()
        {
            // A plan is one unit. Applying the half that resolves and refusing the rest would
            // leave the project in a state no plan described, and the remedy — export again,
            // re-plan — is the same either way, so nothing is written before the refusal.
            var plc1 = new FakePlc("PLC1");
            var plc2 = new FakePlc("PLC2");
            var project = new FakeProject(new FakeDevice("Device1", plc1), new FakeDevice("Device2_renamed", plc2));
            using var src = new MirrorDir(
                ("Device1/PLC1/Program blocks/Valve.scl", "FUNCTION_BLOCK \"Valve\"\nEND_FUNCTION_BLOCK\n"),
                ("Device2/PLC2/Program blocks/Pump.scl", "FUNCTION_BLOCK \"Pump\"\nEND_FUNCTION_BLOCK\n"));
            var plan = Plan(
                ApplyText("Device1/PLC1/Program blocks/Valve.scl", "Device1/PLC1/FB/Valve#1"),
                ApplyText("Device2/PLC2/Program blocks/Pump.scl", "Device2/PLC2/FB/Pump#1"));

            var (output, ex) = Import(project, src.Path, plan);

            Assert.Null(ex);
            Assert.Empty(plc1.Writes);
            Assert.Empty(plc2.Writes);
            Assert.Equal(0, project.Saves);
            Assert.NotNull(output.Fatal);
            Assert.DoesNotContain(output.Events, e => e.Type == "object");
        }

        [Fact]
        public void A_file_whose_PLC_resolves_reaches_that_PLC_and_no_other()
        {
            // The positive control for the two above: the same fakes, a path that does name
            // a live PLC, and the write lands on it. The fake cannot finish a generate (there
            // is no block to read back), so the host's own read-back refuses — that throw is
            // the existing "generate did not apply" gate doing its job, not this test's subject.
            var plc1 = new FakePlc("PLC1");
            var plc2 = new FakePlc("PLC2");
            var project = new FakeProject(new FakeDevice("Device1", plc1), new FakeDevice("Device2", plc2));
            using var src = new MirrorDir(("Device2/PLC2/Program blocks/Pump.scl", "FUNCTION_BLOCK \"Pump\"\nEND_FUNCTION_BLOCK\n"));
            var plan = Plan(ApplyText("Device2/PLC2/Program blocks/Pump.scl", "Device2/PLC2/FB/Pump#1"));

            var (output, ex) = Import(project, src.Path, plan);

            Assert.Null(output.Fatal);
            Assert.Empty(plc1.Writes);
            Assert.Equal(new[] { "Pump.scl" }, plc2.Writes);
            var thrown = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("generate did not apply", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_path_is_matched_against_the_names_the_export_sanitised()
        {
            // Export writes Sanitize(DeviceName)/Sanitize(PlcName) — invalid file-name
            // characters become '_' — so a device called "Line:2" is the directory "Line_2".
            // Matching the raw name against the directory would refuse every file of such a
            // PLC now that a miss refuses the plan (and wrote them all into the first PLC before).
            var plc1 = new FakePlc("PLC1");
            var plc2 = new FakePlc("PLC2");
            var project = new FakeProject(new FakeDevice("Device1", plc1), new FakeDevice("Line:2", plc2));
            using var src = new MirrorDir(("Line_2/PLC2/Program blocks/Pump.scl", "FUNCTION_BLOCK \"Pump\"\nEND_FUNCTION_BLOCK\n"));
            var plan = Plan(ApplyText("Line_2/PLC2/Program blocks/Pump.scl", "Line:2/PLC2/FB/Pump#1"));

            var (output, _) = Import(project, src.Path, plan);

            Assert.Null(output.Fatal);
            Assert.Empty(plc1.Writes);
            Assert.Equal(new[] { "Pump.scl" }, plc2.Writes);
        }

        [Fact]
        public void With_one_PLC_left_a_file_for_a_PLC_that_is_gone_is_still_refused()
        {
            // The sixth review round's item 2. The first fix kept a one-PLC fallback — "with a
            // single PLC there is nowhere else the file could belong" — and that reasoning
            // mistook the common case for the only case: it is also what a project looks like
            // after its OTHER PLC was removed in Portal, and an old import for the removed
            // Device2 then wrote Device2's blocks into Device1 and saved. A path that names no
            // live PLC is refused however many PLCs are left; a renamed device in a one-PLC
            // project costs a re-export, which is the remedy the fatal already names.
            var only = new FakePlc("PLC1");
            var project = new FakeProject(new FakeDevice("Device1", only));
            using var src = new MirrorDir(("Device2/PLC2/Program blocks/Pump.scl", "FUNCTION_BLOCK \"Pump\"\nEND_FUNCTION_BLOCK\n"));
            var plan = Plan(ApplyText("Device2/PLC2/Program blocks/Pump.scl", "Device2/PLC2/FB/Pump#1"));

            var (output, ex) = Import(project, src.Path, plan);

            Assert.Empty(only.Writes);
            Assert.Null(ex);
            Assert.Equal(0, project.Saves);
            var fatal = Assert.IsType<TiaPortalEvent>(output.Fatal);
            Assert.Contains("Device2/PLC2", fatal.String("msg") ?? "", StringComparison.Ordinal);
            Assert.Contains("Device1/PLC1", fatal.String("msg") ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public void A_device_catalog_targets_the_project_and_is_not_refused_for_naming_no_PLC()
        {
            // "Device/Coupler.devices.yaml" has two segments and neither is a PLC. It is applied
            // to the project's DeviceItems, never to a PLC, so the PLC check must not see it.
            // The fake catalog carries no Comment rows, so ApplyDeviceComments refuses it with
            // its own sentence — reaching that sentence is the proof the pre-flight let it by.
            var plc1 = new FakePlc("PLC1");
            var plc2 = new FakePlc("PLC2");
            var project = new FakeProject(new FakeDevice("Device1", plc1), new FakeDevice("Device2", plc2));
            using var src = new MirrorDir(("Device2/Coupler.devices.yaml", "schema: 1\n"));
            var plan = Plan(ApplyText("Device2/Coupler.devices.yaml", "Device2/Coupler"));

            var (output, ex) = Import(project, src.Path, plan);

            Assert.Null(output.Fatal);
            var thrown = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("no Comment parameters", thrown.Message, StringComparison.Ordinal);
            Assert.Empty(plc1.Writes);
            Assert.Empty(plc2.Writes);
        }

        // ---- driving the host ------------------------------------------------------------

        private static (TiaPortalOutput Output, Exception? Thrown) Import(FakeProject project, string srcDir, JsonElement plan)
        {
            lock (ConsoleGate)
            {
                var writer = new StringWriter();
                var original = Console.Out;
                Console.SetOut(writer);
                Exception? thrown;
                try
                {
                    thrown = Record.Exception(() => HostApp.Import(project, srcDir, plan, save: true));
                }
                finally
                {
                    Console.SetOut(original);
                }

                return (TiaPortalProtocol.Parse(writer.ToString()), thrown);
            }
        }

        /// <summary>The plan as the host receives it: through <see cref="TiaPortalJob.ToJson"/>, not hand-written JSON.</summary>
        private static JsonElement Plan(params PlannedFile[] files)
        {
            var job = new TiaPortalJob
            {
                Operation = "import",
                ProjectPath = @"C:\plc\Station\Station.ap21",
                Path = @"C:\plc\mirror",
                ApiFolder = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21",
                Plan = new ImportPlan { Files = files },
            };
            using var doc = JsonDocument.Parse(job.ToJson());
            return doc.RootElement.GetProperty("plan").Clone();
        }

        private static PlannedFile ApplyText(string path, string guid) => new()
        {
            Path = path,
            Action = PlannedAction.ApplyText,
            Guid = guid,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Resolve = guid.Split('/'),
        };

        private sealed class MirrorDir : IDisposable
        {
            public MirrorDir(params (string Rel, string Text)[] files)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "versio-tia-host-test-" + Guid.NewGuid().ToString("N")[..8]);
                foreach (var (rel, text) in files)
                {
                    var abs = System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
                    File.WriteAllText(abs, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Temp.
                }
            }
        }

        // ---- fakes shaped like what the host reads by name ----------------------------------
        //
        // The host never references an Openness type: it walks Project.Devices, each device's
        // DeviceItems, asks each item GetService<SoftwareContainer>(), takes .Software when
        // its type is named PlcSoftware, and writes through PlcSoftware.ExternalSourceGroup.
        // ExternalSources.CreateFromFile(name, FileInfo) then source.GenerateBlocksFromSource().
        // These fakes answer exactly those questions and record the writes.

        private sealed class FakeProject(params FakeDevice[] devices)
        {
            public IReadOnlyList<object> Devices { get; } = devices;

            public int Saves { get; private set; }

            public void Save() => Saves++;
        }

        private sealed class FakeDevice(string name, FakePlc plc)
        {
            public string Name { get; } = name;

            public IReadOnlyList<object> DeviceItems { get; } = [];

            public T? GetService<T>()
                where T : class =>
                typeof(T) == typeof(Siemens.Engineering.HW.Features.SoftwareContainer)
                    ? new Siemens.Engineering.HW.Features.SoftwareContainer { Software = plc.Software } as T
                    : null;
        }

        private sealed class FakePlc
        {
            public FakePlc(string name)
            {
                Software = new PlcSoftware(name, this);
            }

            public PlcSoftware Software { get; }

            /// <summary>External-source file names created on this PLC, in order.</summary>
            public List<string> Writes { get; } = [];
        }

        // Named PlcSoftware because WalkPlcSoftware accepts software by type name alone.
        private sealed class PlcSoftware(string name, FakePlc owner)
        {
            public string Name { get; } = name;

            public object? BlockGroup => null;

            public object? TypeGroup => null;

            public object? TagTableGroup => null;

            public FakeExternalSourceGroup ExternalSourceGroup { get; } = new(owner);
        }

        private sealed class FakeExternalSourceGroup(FakePlc owner)
        {
            public FakeExternalSources ExternalSources { get; } = new(owner);
        }

        private sealed class FakeExternalSources(FakePlc owner) : IEnumerable<object>
        {
            private readonly List<object> _sources = [];

            public object CreateFromFile(string name, FileInfo file)
            {
                owner.Writes.Add(name);
                var source = new FakeExternalSource(name);
                _sources.Add(source);
                return source;
            }

            public IEnumerator<object> GetEnumerator() => _sources.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class FakeExternalSource(string name)
        {
            public string Name { get; } = name;

            public void GenerateBlocksFromSource()
            {
            }

            public void Delete()
            {
            }
        }
    }
}
