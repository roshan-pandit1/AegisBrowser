using System.Text;
using AegisBrowser.Testing;

// In-memory negative cases only. No operation under consideration is executed.
internal static class InventoryValidationTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool result)
        {
            checks++;
            if (!result) throw new InvalidOperationException("Inventory decision regression " + checks);
        }

        var a = new string('A', 64);
        var b = new string('B', 64);
        var paths = new[] { "sample/one.cs", "sample/two.cs" };
        var observed = new Dictionary<string, string> { [paths[0]] = a, [paths[1]] = b };
        string Entry(string path, string hash) =>
            "{\"path\":\"" + path + "\",\"category\":\"SAFE_WORKSPACE\",\"profile\":\"A\"," +
            "\"required_environment\":\"reviewed\",\"required_authorization\":\"normal\"," +
            "\"expected_filesystem_changes\":\"none\",\"process_operations\":\"none\"," +
            "\"network_operations\":\"none\",\"privileges\":\"none\",\"basis\":\"reviewed\"," +
            "\"execution_status\":\"PERMITTED_FIXED_SOURCE_DECISION_TEST\",\"evidence\":[\"STATIC-ANALYSIS\"]," +
            "\"observed_sha256\":\"" + hash + "\"}";
        var operation = "{\"id\":\"governance-self-test\",\"profile\":\"A\",\"category\":\"SAFE_WORKSPACE\"," +
            "\"scope\":\"fixed-source-decision-test\",\"dependencies\":[\"sample/one.cs\",\"sample/two.cs\"]}";
        var first = Entry(paths[0], a);
        var second = Entry(paths[1], b);
        string Fixture(string ops, string entries, int version = 1) =>
            "{\"schema_version\":" + version + ",\"operations\":[" + ops + "],\"entries\":[" + entries + "]}";
        var valid = Fixture(operation, first + "," + second);
        bool Accept(string json, string op = "governance-self-test", string profile = "A",
            IReadOnlyList<string>? dependencies = null, IReadOnlyDictionary<string, string>? actual = null) =>
            InventoryValidation.Validate(Encoding.UTF8.GetBytes(json), op, profile,
                dependencies ?? paths, actual ?? observed, out _);

        Check(Accept(valid));
        Check(!Accept(""));
        Check(!Accept("{"));
        Check(!Accept(Fixture(operation, first + "," + second, 2)));
        Check(!Accept(Fixture("", first + "," + second)));
        Check(!Accept(Fixture(operation.Replace("governance-self-test", "different"), first + "," + second)));
        Check(!Accept(Fixture(operation + "," + operation, first + "," + second)));
        Check(!Accept(Fixture(operation.Replace("\"id\":\"governance-self-test\"", "\"id\":\"   \""), first + "," + second)));
        Check(!Accept(valid.Replace("\"required_environment\":\"reviewed\",", "")));
        Check(!Accept(valid.Replace("\"required_environment\":\"reviewed\"", "\"required_environment\":\"   \"")));
        Check(!Accept(valid.Replace("\"required_environment\":\"reviewed\"", "\"required_environment\":\"UNKNOWN\"")));
        Check(!Accept(valid.Replace("\"required_environment\":\"reviewed\"", "\"required_environment\":7")));
        Check(!Accept(valid.Replace("\"category\":\"SAFE_WORKSPACE\"", "\"category\":\"UNSUPPORTED\"")));
        Check(!Accept(valid.Replace("\"scope\":\"fixed-source-decision-test\"", "\"scope\":\"broader\"")));
        Check(!Accept(valid, profile: "B"));
        Check(!Accept(valid, profile: "C"));
        Check(!Accept(valid, profile: "invalid"));
        Check(!Accept(valid, op: "different"));
        Check(!Accept(valid.Replace("\"sample/two.cs\"]", "\"sample/two.cs\",\"sample/extra.cs\"]")));
        Check(!Accept(valid.Replace("\"sample/two.cs\"]", "\"sample/one.cs\"]")));
        Check(!Accept(valid, dependencies: new[] { paths[0] }));
        Check(!Accept(Fixture(operation, first)));
        Check(!Accept(Fixture(operation, first + "," + second + "," + Entry("SAMPLE/ONE.CS", a))));
        Check(!Accept(valid.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1")));
        Check(!Accept(valid.Replace("\"evidence\":[\"STATIC-ANALYSIS\"]", "\"evidence\":[]")));
        Check(!Accept(valid.Replace("\"observed_sha256\":\"" + a + "\"", "\"observed_sha256\":\"short\"")));
        Check(!Accept(valid.Replace("\"observed_sha256\":\"" + a + "\"", "\"identity_algorithm\":\"unsupported\",\"observed_sha256\":\"" + a + "\"")));
        Check(!Accept(valid, actual: new Dictionary<string, string> { [paths[0]] = b, [paths[1]] = b }));
        Check(!Accept(valid, actual: new Dictionary<string, string> { [paths[0]] = a }));
        Check(!Accept(valid, actual: new Dictionary<string, string> { [paths[0]] = a, [paths[1]] = b, ["sample/extra.cs"] = b }));
        Check(!Accept(valid.Replace("sample/one.cs", "../outside.cs")));

        var request = new[] { "--governance-run", "A", "governance-self-test" };
        var rejected = TestGovernance.Evaluate(TestGovernance.ProgramKind.Core, request, () => Accept("{"));
        var permitted = TestGovernance.Evaluate(TestGovernance.ProgramKind.Core, request, () => Accept(valid));
        var invoked = 0;
        if (rejected.Disposition == TestGovernance.Disposition.RunSelfTest) invoked++;
        Check(rejected.Disposition == TestGovernance.Disposition.Refuse && invoked == 0);
        if (permitted.Disposition == TestGovernance.Disposition.RunSelfTest) invoked++;
        Check(permitted.Disposition == TestGovernance.Disposition.RunSelfTest && invoked == 1);
        var validationInvoked = false;
        var blocked = TestGovernance.Evaluate(TestGovernance.ProgramKind.Core,
            new[] { "--governance-run", "B", "core-suite" }, () => { validationInvoked = true; return true; });
        Check(blocked.Disposition == TestGovernance.Disposition.Refuse && !validationInvoked);

        var jsonLf = Encoding.UTF8.GetBytes("{\n\"a\": 1\n}");
        var jsonCrLf = Encoding.UTF8.GetBytes("{\r\n\"a\": 1\r\n}");
        var lfHash = InventoryValidation.ComputeDigest(jsonLf, InventoryValidation.JsonLfSha256);
        Check(lfHash is not null && lfHash == InventoryValidation.ComputeDigest(jsonCrLf, InventoryValidation.JsonLfSha256));
        Check(lfHash != InventoryValidation.ComputeDigest(Encoding.UTF8.GetBytes("{\n\"a\": 2\n}"), InventoryValidation.JsonLfSha256));
        Check(InventoryValidation.ComputeDigest(jsonLf, InventoryValidation.RawSha256) != InventoryValidation.ComputeDigest(jsonCrLf, InventoryValidation.RawSha256));
        Check(InventoryValidation.ComputeDigest(new byte[] { 0, 1 }, InventoryValidation.RawSha256) != InventoryValidation.ComputeDigest(new byte[] { 0, 2 }, InventoryValidation.RawSha256));
        Check(InventoryValidation.ComputeDigest(jsonLf, "unsupported") is null);
        Check(InventoryValidation.ComputeDigest(Encoding.UTF8.GetBytes("{\r\"a\":1}"), InventoryValidation.JsonLfSha256) is null);
        Check(InventoryValidation.ComputeDigest(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"), InventoryValidation.JsonLfSha256) is null);
        Check(InventoryValidation.ComputeDigest(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D }, InventoryValidation.JsonLfSha256) is null);
        Check(InventoryValidation.ComputeDigest(new byte[] { 0xFF }, InventoryValidation.JsonLfSha256) is null);
        Check(InventoryValidation.ComputeDigest(Encoding.UTF8.GetBytes("{"), InventoryValidation.JsonLfSha256) is null);
        return checks;
    }
}
