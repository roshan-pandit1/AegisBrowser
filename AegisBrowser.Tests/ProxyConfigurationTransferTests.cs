using System.Security.Cryptography;
using System.Text.Json;
using AegisBrowser.Core;

internal static class ProxyConfigurationTransferTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL proxy configuration transfer: " + name); checks++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { checks++; return; }
            throw new Exception("FAIL proxy configuration transfer (accepted): " + name);
        }
        string Envelope(PortableProxyConfiguration content)
        {
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
            return JsonSerializer.Serialize(new PortableProxyConfigurationEnvelope(content, hash));
        }
        var workspace = Path.Combine(Path.GetTempPath(), "aegis-proxy-config-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(workspace, "profiles");
            var profilePath = BrowserPolicy.ProfilePath(root, "transfer"); Directory.CreateDirectory(profilePath);
            var now = DateTimeOffset.UtcNow;
            var profile = new ProfileDefinition(Guid.NewGuid(), "transfer", "Transfer", "", [], [], 1, 1, now, now);
            var draft = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, true, Guid.NewGuid(), "fixture", true);
            var configuration = new ProxyConfiguration(Guid.NewGuid(), profile.Id, draft, 1, 1, now, now);
            var repo = new FakeRepository { Current = configuration };
            var transfer = new ProxyConfigurationTransfer(root, repo);
            var path = Path.Combine(workspace, "proxy.json"); transfer.Export(path, profile, configuration);
            var file = File.ReadAllText(path);
            Check(!file.Contains("CredentialId", StringComparison.OrdinalIgnoreCase) && !file.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
                !file.Contains("Username", StringComparison.OrdinalIgnoreCase) && !file.Contains(profile.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                !file.Contains(configuration.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                !file.Contains(draft.CredentialId!.Value.ToString("D"), StringComparison.OrdinalIgnoreCase), "export excludes identities and secrets");
            var preview = transfer.Preview(path);
            Check(preview.AuthenticationRequired && preview.Host == draft.Host && preview.Port == draft.Port && preview.Protocol == draft.Protocol,
                "preview preserves required proxy and authentication intent without credentials");
            Check(!preview.ToString().Contains(draft.Host, StringComparison.Ordinal), "portable record string does not expose endpoint data");
            Reject(() => transfer.Export(path, profile, configuration), "existing file is not overwritten");
            Check(File.ReadAllText(path) == file, "existing export remains unchanged");
            Reject(() => transfer.Export(Path.Combine(root, "inside.json"), profile, configuration), "export inside profile storage rejected");
            Reject(() => transfer.Export(Path.Combine(workspace, "wrong.json"), profile with { Id = Guid.NewGuid() }, configuration), "profile identity mismatch rejected");
            repo.Current = configuration with { Revision = 2 };
            var stale = Path.Combine(workspace, "stale.json");
            Reject(() => transfer.Export(stale, profile, configuration), "stale configuration rejected");
            Check(!File.Exists(stale), "stale export creates no file");
            repo.Current = configuration;
            using (var lease = new ProfileLease(profilePath))
            {
                Reject(() => transfer.Export(Path.Combine(workspace, "busy.json"), profile, configuration), "running profile lease blocks export");
            }
            var direct = configuration with { Settings = new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false), Revision = 2 };
            repo.Current = direct;
            Reject(() => transfer.Export(Path.Combine(workspace, "direct.json"), profile, direct), "browser defaults are not exported as a proxy");
            repo.Current = configuration;

            var bad = Path.Combine(workspace, "bad.json");
            var tampered = file.Replace("proxy.example", "other.example", StringComparison.Ordinal); File.WriteAllText(bad, tampered);
            Reject(() => transfer.Preview(bad), "checksum mismatch rejected");
            Check(File.ReadAllText(bad) == tampered, "corrupt import is preserved");
            File.WriteAllText(bad, Envelope(preview with { Version = 99 }));
            Reject(() => transfer.Preview(bad), "future version rejected");
            File.WriteAllText(bad, Envelope(preview with { Kind = "other" }));
            Reject(() => transfer.Preview(bad), "wrong document kind rejected");
            File.WriteAllText(bad, Envelope(preview with { Host = "user:password@proxy.example" }));
            Reject(() => transfer.Preview(bad), "credential-bearing host rejected");
            File.WriteAllText(bad, Envelope(preview with { Protocol = ProxyProtocol.Socks5, AuthenticationRequired = true }));
            Reject(() => transfer.Preview(bad), "unsupported SOCKS5 authentication rejected");
            File.WriteAllText(bad, Envelope(preview with { Protocol = (ProxyProtocol)99 }));
            Reject(() => transfer.Preview(bad), "unknown protocol rejected");
            File.WriteAllText(bad, Envelope(preview with { Protocol = ProxyProtocol.Https, Host = "2001:db8::1", Port = 443 }));
            Check(transfer.Preview(bad).Host == "2001:db8::1", "IPv6 proxy endpoint round-trips without credentials");
            File.WriteAllText(bad, Envelope(preview with { Protocol = ProxyProtocol.Direct }));
            Reject(() => transfer.Preview(bad), "direct mode rejected");
            File.WriteAllText(bad, Envelope(preview with { Port = 0 }));
            Reject(() => transfer.Preview(bad), "invalid port rejected");
            File.WriteAllText(bad, "{\"Content\":{},\"content\":{},\"Sha256\":\"00\"}");
            Reject(() => transfer.Preview(bad), "duplicate fields rejected");
            File.WriteAllText(bad, Envelope(preview).Replace("\"Content\":{", "\"Content\":{\"Password\":\"fixture\",", StringComparison.Ordinal));
            Reject(() => transfer.Preview(bad), "password field rejected");
            File.WriteAllText(bad, Envelope(preview).Replace("\"Content\":{", "\"Content\":{\"CredentialId\":\"" + Guid.NewGuid().ToString("D") + "\",", StringComparison.Ordinal));
            Reject(() => transfer.Preview(bad), "credential reference field rejected");
            File.WriteAllText(bad, new string('x', 16_385));
            Reject(() => transfer.Preview(bad), "oversized input rejected");
            File.WriteAllText(bad, "{ invalid");
            Reject(() => transfer.Preview(bad), "malformed JSON rejected");
            var presetRepo = new ProxyPresetRepository(Path.Combine(workspace, "preset-root"));
            var presetTransfer = new ProxyPresetTransfer(Path.Combine(workspace, "preset-root"), presetRepo);
            Reject(() => presetTransfer.Preview(path), "configuration cannot be imported as a preset");
            Check(repo.Current == configuration, "preview and rejected exports never mutate proxy settings");
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
        Console.WriteLine($"PASS: {checks} proxy configuration transfer checks");
    }

    private sealed class FakeRepository : IProxyRepository
    {
        public ProxyConfiguration? Current { get; set; }
        public ProxyConfiguration? Get(ProfileDefinition profile, ProfileLease lease) => Current;
        public ProxyConfiguration Save(ProfileDefinition profile, ProfileLease lease, ProxyDraft draft, long expectedRevision) => throw new NotSupportedException();
    }
}
