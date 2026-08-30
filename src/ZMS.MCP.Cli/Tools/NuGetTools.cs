using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Text;
using System.Xml.Linq;

namespace zms9110750.ZMS_MCP.Cli.Tools;

[McpServerToolType]
public static partial class NuGetTools
{
    private static readonly string NuGetV3 = "https://api.nuget.org/v3/index.json";
    private static readonly SourceCacheContext Ctx = new() { NoCache = false };
    private static readonly NuGet.Common.ILogger Log = NullLogger.Instance;
    private static SourceRepository? _repo;
    private static readonly object _repoLock = new();
    private static SourceRepository Repo() { if (_repo == null) { lock (_repoLock) { _repo ??= Repository.Factory.GetCoreV3(NuGetV3); } } return _repo; }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Search NuGet packages. version=empty: fuzzy search; version set: list versions matching that range. online=true: nuget.org; false: local cache.")]
    public static async Task<string> SearchNuGet(
        [Description("Package name or keyword")] string packageName,
        [Description("Version range. empty=fuzzy; '*'=all; '*-*'=all+prerelease; '[1,3)'=range; '1.0'=exact")] string version = "",
        [Description("true=online nuget.org; false=local cache (default)")] bool online = false)
    {
        try
        {
            if (online)
            {
                return !string.IsNullOrEmpty(version)
                    ? await OnlineListVersions(packageName, version)
                    : await OnlineSearch(packageName);
            }

            var root = CacheRoot();
            var pkg = Path.Combine(root, packageName.ToLowerInvariant());
            if (!Directory.Exists(pkg))
            {
                return $"Package '{packageName}' not in cache.";
            }

            var cached = Directory.GetDirectories(pkg).Select(d => Path.GetFileName(d))
                .Where(v => v != null).Select(v => { NuGetVersion.TryParse(v, out var nv); return nv; })
                .Where(v => v != null).Select(v => v!).ToArray();
            if (cached.Length == 0)
            {
                return "No cached versions.";
            }

            if (!string.IsNullOrEmpty(version))
            {
                if (!VersionRange.TryParse(version, out var range))
                {
                    return $"Bad range: '{version}'.";
                }

                cached = cached.Where(v => range.Satisfies(v)).OrderByDescending(v => v).ToArray();
                if (cached.Length == 0)
                {
                    return $"No cached versions matching '{version}'.";
                }
            }
            else
            {
                cached = cached.OrderByDescending(v => v).ToArray();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# {packageName} (cache) — {cached.Length} versions\n");
            foreach (var v in cached)
            {
                sb.AppendLine($"- `{v.ToFullString()}`{(v.IsPrerelease ? " prerelease" : "")}");
            }

            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private static async Task<string> OnlineSearch(string query)
    {
        var repo = Repo();
        var search = await repo.GetResourceAsync<PackageSearchResource>();
        var results = await search.SearchAsync(query, new SearchFilter(false), 0, 15, Log, default);
        var items = results.ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"# Search: \"{query}\"  ({items.Length} results)\n");
        foreach (var item in items)
        {
            var id = item.Identity.Id;
            var ver = item.Identity.Version.ToFullString();
            sb.AppendLine($"## {id}  `{ver}`");
            sb.AppendLine($"> {Trunc(item.Description ?? "(no desc)", 120)}");
            sb.AppendLine($"  DL: {(item.DownloadCount.HasValue ? Fmt(item.DownloadCount.Value) : "?")}\n");
        }
        if (items.Any(i => i.Identity.Id.Equals(query, StringComparison.OrdinalIgnoreCase)))
        {
            var meta = await repo.GetResourceAsync<PackageMetadataResource>();
            var vers = await meta.GetMetadataAsync(query, true, false, Ctx, Log, default);
            var all = vers.OrderByDescending(v => v.Identity.Version).ToArray();
            sb.AppendLine("---");
            sb.AppendLine($"# {query}  ({all.Length} versions)\n");
            foreach (var v in all)
            {
                sb.AppendLine($"- `{v.Identity.Version.ToFullString()}`{(v.Identity.Version.IsPrerelease ? " prerelease" : "")}  {v.Published?.ToString("yyyy-MM-dd") ?? "?"}");
            }
        }
        return sb.ToString();
    }

    private static async Task<string> OnlineListVersions(string name, string ver)
    {
        if (!VersionRange.TryParse(ver, out var range))
        {
            return $"Bad range: '{ver}'.";
        }

        var repo = Repo();
        var meta = await repo.GetResourceAsync<PackageMetadataResource>();
        var vers = await meta.GetMetadataAsync(name, true, false, Ctx, Log, default);
        var all = vers.ToArray();
        if (all.Length == 0)
        {
            return $"Package '{name}' not found.";
        }

        var filtered = all.Where(v => range.Satisfies(v.Identity.Version))
            .OrderByDescending(v => v.Identity.Version).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"# {name}  — {all.Length} total, {filtered.Length} matching '{ver}'\n");
        foreach (var v in filtered)
        {
            sb.AppendLine($"- `{v.Identity.Version.ToFullString()}`{(v.Identity.Version.IsPrerelease ? " prerelease" : "")}  {v.Published?.ToString("yyyy-MM-dd") ?? "?"}  dl={Fmt(v.DownloadCount ?? 0)}");
        }

        return sb.ToString();
    }

    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Inspect API surface from local NuGet cache XML docs. path=null: type tree; 'Type'=members; 'Type.Member'=single doc.")]
    public static async Task<string> InspectNuGetXml(
        [Description("Package name")] string packageName,
        [Description("Version/range, empty=latest")] string packageVersion = "",
        [Description("Target framework, empty=auto")] string framework = "",
        [Description("empty=type tree; 'Type'=members; 'Type.Member'=single doc")] string path = "")
    {
        try
        {
            var version = ResolveVersion(packageName, string.IsNullOrEmpty(packageVersion) ? null : packageVersion);
            using var reader = new PackageFolderReader(PkgDir(packageName, version));
            var all = reader.GetFiles().ToArray();
            var lib = all.Where(f => f.StartsWith("lib/", StringComparison.Ordinal)).ToArray();
            if (lib.Length == 0)
            {
                return "No lib/ directory.";
            }

            var fw = BestTfm(lib, string.IsNullOrEmpty(framework) ? null : framework);
            var xmls = lib.Where(f => f.StartsWith($"lib/{fw}/", StringComparison.Ordinal) && f.EndsWith(".xml", StringComparison.Ordinal))
                .Select(f => Path.Combine(PkgDir(packageName, version), f.Replace('/', '\\'))).Where(File.Exists).ToArray();
            if (xmls.Length == 0)
            {
                return $"No XML docs under lib/{fw}.";
            }

            var docs = xmls.Select(XDocument.Load).ToArray();
            if (string.IsNullOrEmpty(path))
            {
                return XmlTree(docs);
            }

            var ld = path.LastIndexOf('.');
            if (ld <= 0)
            {
                return $"Invalid path: '{path}'.";
            }

            var mm = path[(ld + 1)..];
            var mt = path[..ld];

            var r = XmlMember(docs, mt, mm);
            if (r != null)
            {
                return r;
            }

            r = XmlType(docs, path);
            if (r != null)
            {
                return r;
            }

            return $"Type '{path}' not found.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private static string XmlTree(XDocument[] docs)
    {
        var ns = new Dictionary<string, List<string>>();
        foreach (var doc in docs)
        {
            foreach (var m in doc.Descendants("member"))
            {
                var n = m.Attribute("name")?.Value ?? "";
                if (!n.StartsWith("T:"))
                {
                    continue;
                }

                var full = n[2..];
                var ld = full.LastIndexOf('.');
                var nspace = ld > 0 ? full[..ld] : "(global)";
                var tn = ld > 0 ? full[(ld + 1)..] : full;
                if (!ns.ContainsKey(nspace))
                {
                    ns[nspace] = [];
                }

                if (!ns[nspace].Contains(tn))
                {
                    ns[nspace].Add(tn);
                }
            }
        }

        if (ns.Count == 0)
        {
            return "No types in XML.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# API — {ns.Sum(kv => kv.Value.Count)} types, {ns.Count} namespaces\n");
        foreach (var ns2 in ns.OrderBy(k => k.Key))
        {
            sb.AppendLine($"## {ns2.Key}  ({ns2.Value.Count})");
            foreach (var t in ns2.Value.OrderBy(t => t))
            {
                sb.AppendLine($"  - `{t}`  _(type)_");
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string? XmlType(XDocument[] docs, string tn)
    {
        var prefixes = new[] { $"M:{tn}.", $"P:{tn}.", $"F:{tn}.", $"E:{tn}." };
        var mems = new List<(char K, string N, XElement E)>();
        foreach (var doc in docs)
        {
            foreach (var m in doc.Descendants("member"))
            {
                var n = m.Attribute("name")?.Value ?? "";
                foreach (var p in prefixes)
                {
                    if (n.StartsWith(p)) { mems.Add((p[0], n, m)); break; }
                }
            }
        }

        if (mems.Count == 0)
        {
            return null;
        }

        var td = XFind(docs, "T", tn)?.Element("summary")?.Value.Trim() ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"## {tn}  (type)\n");
        if (!string.IsNullOrEmpty(td))
        {
            sb.AppendLine($"> {td}\n");
        }

        var meth = mems.Where(m => m.K == 'M').OrderBy(m => m.N).ToArray();
        var props = mems.Where(m => m.K == 'P').OrderBy(m => m.N).ToArray();
        var flds = mems.Where(m => m.K == 'F').OrderBy(m => m.N).ToArray();
        if (meth.Length > 0) { sb.AppendLine($"### Methods ({meth.Length})"); foreach (var (_, n, el) in meth) { var s = el.Element("summary")?.Value.Trim() ?? ""; sb.AppendLine($"- `{n[($"M:{tn}.").Length..]}`"); if (!string.IsNullOrEmpty(s)) { sb.AppendLine($"  > {Trunc(s, 200)}"); } } sb.AppendLine(); }
        if (props.Length > 0) { sb.AppendLine($"### Properties ({props.Length})"); foreach (var (_, n, el) in props) { var s = el.Element("summary")?.Value.Trim() ?? ""; sb.AppendLine($"- `{n[($"P:{tn}.").Length..]}`"); if (!string.IsNullOrEmpty(s)) { sb.AppendLine($"  > {Trunc(s, 200)}"); } } sb.AppendLine(); }
        if (flds.Length > 0) { sb.AppendLine($"### Fields ({flds.Length})"); foreach (var (_, n, el) in flds) { var s = el.Element("summary")?.Value.Trim() ?? ""; sb.AppendLine($"- `{n[($"F:{tn}.").Length..]}`"); if (!string.IsNullOrEmpty(s)) { sb.AppendLine($"  > {Trunc(s, 200)}"); } } sb.AppendLine(); }
        return sb.ToString();
    }

    private static string? XmlMember(XDocument[] docs, string tn, string mn)
    {
        foreach (var p in new[] { 'M', 'P', 'F', 'E' })
        {
            var xml = XFind(docs, p.ToString(), $"{tn}.{mn}");
            if (xml != null)
            {
                var lbl = p switch { 'M' => "Method", 'P' => "Property", 'F' => "Field", 'E' => "Event", _ => "Member" };
                var sb = new StringBuilder();
                sb.AppendLine($"### {lbl}: {mn}\n");
                XDoc(sb, xml);
                return sb.ToString();
            }
        }
        return null;
    }

    private static XElement? XFind(XDocument[] docs, string prefix, string path)
    {
        var id = $"{prefix}:{path}";
        foreach (var doc in docs)
        {
            var found = doc.Descendants("member").FirstOrDefault(m => {
                var n = m.Attribute("name")?.Value ?? "";
                return n == id || (prefix == "M" && n.StartsWith(id + "("));
            });
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static void XDoc(StringBuilder sb, XElement? doc)
    {
        if (doc == null) { sb.AppendLine("_(no doc)_"); return; }
        var summary = doc.Element("summary")?.Value.Trim();
        if (!string.IsNullOrEmpty(summary))
        {
            sb.AppendLine($"> {summary}");
        }

        sb.AppendLine();
        var remarks = doc.Element("remarks")?.Value.Trim();
        if (!string.IsNullOrEmpty(remarks)) { sb.AppendLine("**Remarks:**\n" + remarks + "\n"); }
        foreach (var param in doc.Elements("param"))
        {
            var pn = param.Attribute("name")?.Value ?? "";
            var pt = param.Value.Trim();
            if (!string.IsNullOrEmpty(pt))
            {
                sb.AppendLine($"- `{pn}`: {pt}");
            }
        }
        var returns = doc.Element("returns")?.Value.Trim();
        if (!string.IsNullOrEmpty(returns)) { sb.AppendLine(); sb.AppendLine($"**Returns:** {returns}"); }
    }

    private static string CacheRoot()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static string PkgDir(string name, NuGetVersion ver)
    {
        var d = Path.Combine(CacheRoot(), name.ToLowerInvariant(), ver.ToFullString());
        return Directory.Exists(d) ? d : throw new DirectoryNotFoundException($"Package '{name}' {ver.ToFullString()} not in cache.");
    }

    private static NuGetVersion ResolveVersion(string name, string? ver)
    {
        var root = CacheRoot();
        var pkg = Path.Combine(root, name.ToLowerInvariant());
        if (!Directory.Exists(pkg))
        {
            throw new DirectoryNotFoundException($"Package '{name}' not in cache.");
        }

        var all = Directory.GetDirectories(pkg).Select(d => Path.GetFileName(d)).Where(v => v != null)
            .Select(v => { NuGetVersion.TryParse(v, out var nv); return nv; }).Where(v => v != null).Select(v => v!).OrderByDescending(v => v).ToArray();
        if (all.Length == 0)
        {
            throw new DirectoryNotFoundException($"No versions for '{name}'.");
        }

        if (string.IsNullOrWhiteSpace(ver))
        {
            return all[0];
        }

        if (!VersionRange.TryParse(ver, out var range))
        {
            throw new ArgumentException($"Bad range '{ver}'.");
        }

        return range.FindBestMatch(all) ?? throw new DirectoryNotFoundException($"No version matching '{ver}'.");
    }

    private static string BestTfm(string[] libFiles, string? preferred)
    {
        var tfms = libFiles.Select(f => f.Split('/')).Where(p => p.Length >= 3).Select(p => p[1]).Distinct().ToArray();
        if (tfms.Length == 0)
        {
            throw new InvalidOperationException("No TFMs.");
        }

        if (!string.IsNullOrWhiteSpace(preferred)) { if (tfms.Contains(preferred)) { return preferred; } throw new DirectoryNotFoundException($"TFM '{preferred}' not found."); }
        return tfms.OrderByDescending(t => { var m = System.Text.RegularExpressions.Regex.Match(t ?? "", @"^net(\d+)\.(\d+)$"); return m.Success ? int.Parse(m.Groups[1].Value) * 100 + int.Parse(m.Groups[2].Value) : 0; }).First();
    }

    private static string Trunc(string s, int m)
    {
        return string.IsNullOrEmpty(s) || s.Length <= m ? s : s[..m] + "...";
    }

    private static string Fmt(long c)
    {
        return c >= 1_000_000 ? $"{c / 1_000_000.0:F1}M" : c >= 1_000 ? $"{c / 1_000.0:F1}K" : c.ToString();
    }
}
