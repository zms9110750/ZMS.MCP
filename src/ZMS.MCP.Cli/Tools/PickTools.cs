using System.Buffers;
using System.Text.Json;

namespace zms9110750.ZMS_MCP.Cli.Tools;

[McpServerToolType]
public static partial class PickTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Interval-constrained random pick: pick N items from name:point pool so that count in (min,max) and total points in [min,max].")]
    public static string PickRandom(
        [Description("Expression format: '(2,4)[5,8]{A:3,B:2,C:4}'. Supports file://path.json")] string expression,
        [Description("No-replacement mode (default: with replacement)")] bool? single)
    {
        try
        {
            var s = expression.AsSpan().Trim();
            string expr = s.ToString();
            bool isSingle = single ?? false;

            var m = System.Text.RegularExpressions.Regex.Match(expr, @"^\((\d+),(\d+)\)\[(\d+),(\d+)\]\{(.+)\}$");
            int cMin, cMax, pMin, pMax;
            string poolStr;

            if (m.Success)
            {
                cMin = int.Parse(m.Groups[1].Value);
                cMax = int.Parse(m.Groups[2].Value);
                pMin = int.Parse(m.Groups[3].Value);
                pMax = int.Parse(m.Groups[4].Value);
                poolStr = m.Groups[5].Value;
            }
            else
            {
                m = System.Text.RegularExpressions.Regex.Match(expr, @"^\((\d+)\)\[(\d+)\]\{(.+)\}$");
                if (!m.Success)
                {
                    return "Invalid format. Expected: '(2,4)[5,8]{Name:Points,...}'";
                }

                int cv = int.Parse(m.Groups[1].Value);
                int pv = int.Parse(m.Groups[2].Value);
                cMin = cMax = cv; pMin = pMax = pv;
                poolStr = m.Groups[3].Value;
            }

            var pool = ParsePool(poolStr.AsSpan().Trim());
            if (pool.Count == 0)
            {
                return "Pool is empty.";
            }

            BasePicker<string> picker = isSingle ? new NonReplacementPicker<string>(pool) : new ReplacementPicker<string>(pool);
            picker.SetConstraints(cMin, cMax, pMin, pMax);

            var result = new List<string>();
            while (picker.CountMin > 0 && picker.PointMin > 0)
            {
                result.Add(picker.Pick());
            }

            int total = result.Sum(n => pool[n]);
            return $"{string.Join(", ", result)} = {total}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    static Dictionary<string, int> ParsePool(ReadOnlySpan<char> arg)
    {
        var s = arg.Trim();
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = s["file://".Length..].Trim().ToString();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }

            return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                ?? throw new FormatException("Empty JSON.");
        }
        return ParseInline(s);
    }

    static Dictionary<string, int> ParseInline(ReadOnlySpan<char> s)
    {
        var result = new Dictionary<string, int>();
        var text = s.ToString().Replace('\uFF0C', ',');
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var colonIdx = trimmed.IndexOfAny(':', '\uFF1A');
            if (colonIdx < 0)
            {
                continue;
            }

            var name = trimmed[..colonIdx].Trim();
            var valStr = trimmed[(colonIdx + 1)..].Trim();
            if (name.Length > 0 && int.TryParse(valStr, out int val))
            {
                result[name] = val;
            }
        }
        return result;
    }

    abstract class BasePicker<T>
    {
        protected List<T> Items { get; }
        protected SortedList<int, ValueRange> Ranges { get; }
        public int CountMin { get; protected set; }
        public int CountMax { get; protected set; }
        public int PointMin { get; protected set; }
        public int PointMax { get; protected set; }
        public abstract int Low { get; }
        public abstract int High { get; }

        protected record ValueRange(int Start, int End, int Count)
        {
            public int GetRandomIndex()
            {
                return Start + Random.Shared.Next(Count);
            }
        }

        protected BasePicker(IReadOnlyDictionary<T, int> items)
        {
            var groups = items.ToLookup(k => k.Value, k => k.Key);
            Ranges = new SortedList<int, ValueRange>(groups.Count);
            Items = new List<T>(items.Count);
            int start = 0;
            foreach (var group in groups.OrderBy(g => g.Key))
            {
                var count = Items.Count;
                Items.AddRange(group);
                count = Items.Count - count;
                Ranges[group.Key] = new ValueRange(start, start + count, count);
                start += count;
            }
        }

        public void SetConstraints(int cMin, int cMax, int pMin, int pMax)
        { CountMin = cMin; CountMax = cMax; PointMin = pMin; PointMax = pMax; }

        protected int SelectKey()
        {
            int lo = Low, hi = High;
            var buffer = ArrayPool<int>.Shared.Rent(Ranges.Count);
            try
            {
                int count = Ranges.Count;
                Ranges.Keys.CopyTo(buffer, 0);
                var candidates = buffer.AsSpan(0, count);
                int start = candidates.BinarySearch(lo);
                if (start < 0)
                {
                    start = ~start;
                }

                int end = candidates.BinarySearch(hi);
                if (end < 0)
                {
                    end = ~end - 1;
                }

                return start > end ? throw new InvalidOperationException($"No candidates [Low={lo}, High={hi}]") : candidates[start + Random.Shared.Next(end - start + 1)];
            }
            finally { ArrayPool<int>.Shared.Return(buffer); }
        }

        public virtual T Pick()
        {
            int key = SelectKey();
            var range = Ranges[key];
            T picked = Items[range.GetRandomIndex()];
            CountMin--; CountMax--; PointMin -= key; PointMax -= key;
            return picked;
        }
    }

    sealed class ReplacementPicker<T>(IReadOnlyDictionary<T, int> items) : BasePicker<T>(items)
    {
        public override int Low
        {
            get
            {
                var remain = CountMin;
                if (remain <= 0)
                {
                    return Ranges.Keys[0];
                }

                return Math.Clamp(Math.Max((int)Math.Ceiling((double)PointMin / CountMax), (int)Math.Ceiling((double)PointMin / remain)), Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }
        public override int High
        {
            get
            {
                var remain = CountMin;
                if (remain <= 0)
                {
                    return Ranges.Keys[^1];
                }

                return Math.Clamp(Math.Min((int)Math.Floor((double)PointMax / CountMin), (int)Math.Floor((double)PointMax / remain)), Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }
    }

    sealed class NonReplacementPicker<T>(IReadOnlyDictionary<T, int> items) : BasePicker<T>(items)
    {
        public override int Low
        {
            get
            {
                var remain = CountMin;
                if (remain <= 0)
                {
                    return Ranges.Keys[0];
                }

                return Math.Clamp(Math.Max((int)Math.Ceiling((double)PointMin / CountMax), (int)Math.Ceiling((double)PointMin / remain)), Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }
        public override int High
        {
            get
            {
                var remain = CountMin;
                if (remain <= 0)
                {
                    return Ranges.Keys[^1];
                }

                return Math.Clamp(Math.Min((int)Math.Floor((double)PointMax / CountMin), (int)Math.Floor((double)PointMax / remain)), Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }
        public override T Pick()
        {
            int key = SelectKey();
            var range = Ranges[key];
            int idx = range.GetRandomIndex();
            T picked = Items[idx];
            Items.RemoveAt(idx);

            var newRanges = new SortedList<int, ValueRange>(Ranges.Count);
            foreach (var kv in Ranges)
            {
                var old = kv.Value;
                if (idx < old.Start)
                {
                    newRanges[kv.Key] = new ValueRange(old.Start - 1, old.End - 1, old.Count);
                }
                else if (idx >= old.End)
                {
                    newRanges[kv.Key] = old;
                }
                else
                {
                    int nc = old.Count - 1; if (nc > 0)
                    {
                        newRanges[kv.Key] = new ValueRange(old.Start, old.End - 1, nc);
                    }
                }
            }
            Ranges.Clear();
            foreach (var kv in newRanges)
            {
                Ranges[kv.Key] = kv.Value;
            }

            CountMin--; CountMax--; PointMin -= key; PointMax -= key;
            return picked;
        }
    }
}
