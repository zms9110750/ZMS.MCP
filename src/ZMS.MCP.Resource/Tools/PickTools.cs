using System.Buffers;
using System.Text.RegularExpressions;

namespace ZMS.MCP.Resource.Tools;

/// <summary>
/// 区间约束随机抽取：从 <c>名称:点数</c> 池子里反复抽，直到抽出的
/// **项数**落在 (cMin,cMax)、**点数和**落在 [pMin,pMax]。
///
/// 池子按点数分组、每组的项连续存放，每次只在"还够得着"的点数区间里随机 ——
/// 不是先抽再检查（那会抽不出来）。<c>single</c> 打开不放回：抽过的项不再出现。
/// </summary>
[McpServerToolType]
public static partial class PickTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Interval-constrained random pick: repeatedly draw items from a name:point pool until the number of items " +
        "lands in (countMin,countMax) and their total points land in [pointMin,pointMax]. " +
        "Expression form: '(2,4)[5,8]{A:3,B:2,C:4}', or '(3)[7]{...}' when both bounds of a pair are the same. " +
        "The pool may also come from a JSON file: '(2,4)[5,8]{file://pool.json}'. " +
        "single = true means no-replacement (a drawn item never comes back); default is with-replacement.")]
    public static string PickRandom(
        [Description("Expression, e.g. '(2,4)[5,8]{A:3,B:2,C:4}' or '(2,4)[5,8]{file://pool.json}'.")] string expression,
        [Description("No-replacement mode (default: with replacement).")] bool? single = null)
    {
        string expr = expression.Trim();
        bool noReplacement = single ?? false;

        Regex interval = IntervalPattern();
        Regex fixedBounds = FixedPattern();
        Match match = interval.Match(expr);

        int countMin;
        int countMax;
        int pointMin;
        int pointMax;
        string poolText;

        if (match.Success)
        {
            countMin = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            countMax = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            pointMin = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            pointMax = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            poolText = match.Groups[5].Value;
        }
        else
        {
            match = fixedBounds.Match(expr);
            if (!match.Success)
            {
                throw new ArgumentException("表达式认不出来。要写成 '(2,4)[5,8]{名称:点数, ...}'；两项各自相等时可以写 '(3)[7]{...}'。");
            }

            int count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int points = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            countMin = countMax = count;
            pointMin = pointMax = points;
            poolText = match.Groups[3].Value;
        }

        Dictionary<string, int> pool = ParsePool(poolText);
        if (pool.Count == 0)
        {
            throw new ArgumentException("池子是空的：至少要给一项「名称:点数」。");
        }

        BasePicker<string> picker = noReplacement
            ? new NonReplacementPicker<string>(pool)
            : new ReplacementPicker<string>(pool);
        picker.SetConstraints(countMin, countMax, pointMin, pointMax);

        List<string> drawn = [];
        while (picker.CountMin > 0 && picker.PointMin > 0)
        {
            drawn.Add(picker.Pick());
        }

        int total = drawn.Sum(name => pool[name]);
        return $"{string.Join(", ", drawn)} = {total}";
    }

    /// <summary><c>(2,4)[5,8]{...}</c>：项数与点数各给一个区间。</summary>
    [GeneratedRegex(@"^\((\d+),(\d+)\)\[(\d+),(\d+)\]\{(.+)\}$")]
    private static partial Regex IntervalPattern();

    /// <summary><c>(3)[7]{...}</c>：两项都写成定值。</summary>
    [GeneratedRegex(@"^\((\d+)\)\[(\d+)\]\{(.+)\}$")]
    private static partial Regex FixedPattern();

    private static Dictionary<string, int> ParsePool(string text)
    {
        string trimmed = text.Trim();

        if (!trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseInline(trimmed);
        }

        string path = trimmed["file://".Length..].Trim();
        if (!File.Exists(path))
        {
            throw new ArgumentException($"池子文件不存在：{path}");
        }

        return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
            ?? throw new ArgumentException($"池子文件读不出「名称:点数」这种键值对：{path}");
    }

    /// <summary>行内写法：<c>名称:点数</c> 用逗号分开；全角逗号与全角冒号也认。</summary>
    private static Dictionary<string, int> ParseInline(string text)
    {
        Dictionary<string, int> pool = [];

        foreach (string part in text.Replace('\uFF0C', ',').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            int colon = trimmed.IndexOfAny(':', '\uFF1A');
            if (colon < 0)
            {
                continue;
            }

            string name = trimmed[..colon].Trim();
            string points = trimmed[(colon + 1)..].Trim();
            if (name.Length > 0 && int.TryParse(points, CultureInfo.InvariantCulture, out int value))
            {
                pool[name] = value;
            }
        }

        return pool;
    }

    /// <summary>抽取器：池子按点数分组，每组的项在内部连续存放，于是"随机取一个点数"是二分 + 随机。</summary>
    private abstract class BasePicker<T>
        where T : notnull
    {
        protected List<T> Items { get; }

        protected SortedList<int, ValueRange> Ranges { get; set; }

        public int CountMin { get; private set; }

        public int CountMax { get; private set; }

        public int PointMin { get; private set; }

        public int PointMax { get; private set; }

        /// <summary>还能取的点数下界（由"还剩多少点、还能抽几项"反推）。</summary>
        public abstract int Low { get; }

        /// <summary>还能取的点数上界。</summary>
        public abstract int High { get; }

        protected BasePicker(IReadOnlyDictionary<T, int> items)
        {
            Items = [];
            Ranges = [];

            int start = 0;
            foreach (IGrouping<int, T> group in items.ToLookup(pair => pair.Value, pair => pair.Key).OrderBy(group => group.Key))
            {
                int count = Items.Count;
                Items.AddRange(group);
                count = Items.Count - count;
                Ranges[group.Key] = new ValueRange(start, start + count, count);
                start += count;
            }
        }

        public void SetConstraints(int countMin, int countMax, int pointMin, int pointMax)
        {
            CountMin = countMin;
            CountMax = countMax;
            PointMin = pointMin;
            PointMax = pointMax;
        }

        public abstract T Pick();

        /// <summary>在"可行点数区间"里随机挑一个点数。</summary>
        protected int SelectPoints()
        {
            int low = Low;
            int high = High;

            int[] buffer = ArrayPool<int>.Shared.Rent(Ranges.Count);
            try
            {
                Span<int> candidates = buffer.AsSpan(0, Ranges.Count);
                Ranges.Keys.CopyTo(buffer, 0);

                int start = candidates.BinarySearch(low);
                if (start < 0)
                {
                    start = ~start;
                }

                int end = candidates.BinarySearch(high);
                if (end < 0)
                {
                    end = ~end - 1;
                }

                if (start > end)
                {
                    throw new ArgumentException(
                        $"抽不出来：按当前约束，下一次要抽的点数得落在 [{low}, {high}]，而池子里一个都没有。"
                        + "放宽区间，或者换一个池子。");
                }

                return candidates[start + Random.Shared.Next(end - start + 1)];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buffer);
            }
        }

        /// <summary>按一个点数把项抽出来（抽完要把点数从账上扣掉）。</summary>
        protected (T Item, int Points, int Index) Draw()
        {
            int points = SelectPoints();
            int index = Ranges[points].RandomIndex();
            return (Items[index], points, index);
        }

        protected void Spend(int points)
        {
            CountMin--;
            CountMax--;
            PointMin -= points;
            PointMax -= points;
        }

        protected record struct ValueRange(int Start, int End, int Count)
        {
            public readonly int RandomIndex()
            {
                return Start + Random.Shared.Next(Count);
            }
        }
    }

    /// <summary>有放回：抽过的项还在池子里。</summary>
    private sealed class ReplacementPicker<T>(IReadOnlyDictionary<T, int> items) : BasePicker<T>(items)
        where T : notnull
    {
        public override int Low
        {
            get
            {
                if (CountMin <= 0)
                {
                    return Ranges.Keys[0];
                }

                // 还要抽 CountMin 项、每项至少 1 点，所以下一次至少要给 PointMin / CountMin 点
                return Math.Clamp(
                    (int)Math.Ceiling((double)PointMin / CountMin),
                    Ranges.Keys[0],
                    Ranges.Keys[^1]);
            }
        }

        public override int High
        {
            get
            {
                if (CountMin <= 0)
                {
                    return Ranges.Keys[^1];
                }

                // 除了这一次，其余 CountMin-1 项至少各要点数最小值，
                // 所以这一次至多能给 PointMax - (CountMin-1) * 最小点数。
                // （不能拿 PointMax / CountMin 当上界：池子里都是大项时那会小于下界。）
                int smallest = Ranges.Keys[0];
                int room = PointMax - ((CountMin - 1) * smallest);

                return Math.Clamp(room, Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }

        public override T Pick()
        {
            (T item, int points, _) = Draw();
            Spend(points);
            return item;
        }
    }

    /// <summary>不放回：抽过的项从池子里拿掉，余下的顺序与区间要重排。</summary>
    private sealed class NonReplacementPicker<T>(IReadOnlyDictionary<T, int> items) : BasePicker<T>(items)
        where T : notnull
    {
        public override int Low
        {
            get
            {
                if (CountMin <= 0)
                {
                    return Ranges.Keys[0];
                }

                return Math.Clamp(
                    (int)Math.Ceiling((double)PointMin / CountMin),
                    Ranges.Keys[0],
                    Ranges.Keys[^1]);
            }
        }

        public override int High
        {
            get
            {
                if (CountMin <= 0)
                {
                    return Ranges.Keys[^1];
                }

                // 同 ReplacementPicker：上界是"扣掉其余各项的最小点数后还剩多少"，
                // 不是 PointMax / CountMin（池子里都是大项时那会小于下界）。
                int smallest = Ranges.Keys[0];
                int room = PointMax - ((CountMin - 1) * smallest);

                return Math.Clamp(room, Ranges.Keys[0], Ranges.Keys[^1]);
            }
        }

        public override T Pick()
        {
            (T item, int points, int index) = Draw();
            Items.RemoveAt(index);

            SortedList<int, ValueRange> moved = [];
            foreach ((int key, ValueRange range) in Ranges)
            {
                if (index < range.Start)
                {
                    moved[key] = new ValueRange(range.Start - 1, range.End - 1, range.Count);
                }
                else if (index >= range.End)
                {
                    moved[key] = range;
                }
                else if (range.Count > 1)
                {
                    moved[key] = new ValueRange(range.Start, range.End - 1, range.Count - 1);
                }
            }

            Ranges = moved;
            Spend(points);
            return item;
        }
    }
}
