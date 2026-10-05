using Microsoft.Playwright;

namespace ZMS.MCP.Resource.Browser;

/// <summary>
/// 浏览器：<c>browse</c>。
///
/// 驱动**系统上本来就有的** Chrome 内核浏览器（先试 Edge，再试 Chrome），**不额外下载浏览器**。
/// 起的是**可见**窗口 —— 因为人机验证得让人亲自过；验证产物（这个会话）留在服务进程手里。
/// 360 / QQ / 夸克这类套壳改过内核，驱动不上：**驱动不起来就如实报错**，不猜、不降级。
/// </summary>
public static class BrowseService
{
    /// <summary>默认最多等多久（秒）—— 浏览器启动要时间，给少了不够。</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>人机验证最多同步等多久（秒）。</summary>
    public const int VerificationSeconds = 60;

    private static readonly string[] Channels = ["msedge", "chrome"];

    private static readonly string[] ChallengeMarks =
        ["just a moment", "checking your browser", "verify you are human", "正在检查您的浏览器", "cf-challenge"];

    /// <summary>打开一个页面，把 JS 跑完的内容交出来。</summary>
    public static async Task<string> RunAsync(string url, string? action, string? waitFor, int timeoutSeconds)
    {
        Uri target = Validate(url, action, timeoutSeconds);
        string mode = (action ?? "open").Trim().ToLowerInvariant();

        using IPlaywright playwright = await Playwright.CreateAsync();
        (IBrowser? browser, string channel) = await LaunchAsync(playwright);
        if (browser == null)
        {
            throw new ArgumentException(
                "机器上没有能驱动的 Chrome 内核浏览器（Edge 与 Chrome 都试过了）。"
                + "请装一个标准的 Microsoft Edge 120+ 或 Google Chrome 120+ —— "
                + "360 / QQ / 夸克这类套壳浏览器改过内核，驱动不上。");
        }

        await using IBrowser session = browser;
        IBrowserContext context = await session.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(timeoutSeconds * 1000);

        await page.GotoAsync(target.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = timeoutSeconds * 1000,
        });

        bool passed = true;
        if (await LooksLikeChallengeAsync(page).ConfigureAwait(false))
        {
            passed = await WaitForHumanAsync(page, DateTimeOffset.UtcNow.AddSeconds(VerificationSeconds)).ConfigureAwait(false);
        }

        if (passed && mode == "wait")
        {
            await WaitAsync(page, waitFor, timeoutSeconds).ConfigureAwait(false);
        }

        string text = await page.InnerTextAsync("body").ConfigureAwait(false);
        string title = await page.TitleAsync().ConfigureAwait(false);

        StringBuilder builder = new();
        builder.AppendLine($"# {title}（{channel}）");
        builder.AppendLine($"- 地址：{page.Url}");
        builder.AppendLine($"- 方式：{mode}" + (mode == "wait" && !string.IsNullOrWhiteSpace(waitFor) ? $"（等：{waitFor}）" : ""));
        if (!passed)
        {
            builder.AppendLine($"- **{VerificationSeconds} 秒内没有通过人机验证** —— 页面还停在验证页。");
        }

        builder.AppendLine("- 结果不进缓存。");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(text.Length <= 16000 ? text : text[..16000]);
        builder.AppendLine("```");

        return builder.ToString();
    }

    private static Uri Validate(string url, string? action, int timeoutSeconds)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"url 必须是 http:// 或 https:// 开头的绝对地址：{url}");
        }

        string mode = (action ?? "open").Trim().ToLowerInvariant();
        if (mode is not ("open" or "wait"))
        {
            throw new ArgumentException($"action 只认 open / wait，给的是：{action}");
        }

        if (timeoutSeconds <= 0)
        {
            throw new ArgumentException($"timeout 要大于 0，给的是：{timeoutSeconds}");
        }

        return target;
    }

    private static async Task<(IBrowser? Browser, string Channel)> LaunchAsync(IPlaywright playwright)
    {
        foreach (string channel in Channels)
        {
            try
            {
                IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = channel,
                    Headless = false,
                }).ConfigureAwait(false);

                return (browser, channel);
            }
            catch (PlaywrightException)
            {
                // 这个内核没有或驱动不上，试下一个
            }
        }

        return (null, "");
    }

    private static async Task<bool> LooksLikeChallengeAsync(IPage page)
    {
        string title = await page.TitleAsync().ConfigureAwait(false);
        string body = await page.InnerTextAsync("body").ConfigureAwait(false);
        string probe = (title + "\n" + (body.Length > 2000 ? body[..2000] : body)).ToLowerInvariant();

        return ChallengeMarks.Any(mark => probe.Contains(mark, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> WaitForHumanAsync(IPage page, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            if (!await LooksLikeChallengeAsync(page).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitAsync(IPage page, string? waitFor, int timeoutSeconds)
    {
        string condition = (waitFor ?? "networkidle").Trim();

        if (condition.EndsWith('s')
            && double.TryParse(condition[..^1], out double seconds)
            && seconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, timeoutSeconds))).ConfigureAwait(false);
            return;
        }

        LoadState state = condition.ToLowerInvariant() switch
        {
            "load" => LoadState.Load,
            "domcontentloaded" => LoadState.DOMContentLoaded,
            _ => LoadState.NetworkIdle,
        };

        try
        {
            await page.WaitForLoadStateAsync(state, new PageWaitForLoadStateOptions
            {
                Timeout = timeoutSeconds * 1000,
            }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 等不到就算了：把现在的样子交出去，别把整次调用判成失败
        }
    }
}
