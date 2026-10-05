using System.ComponentModel;
using ModelContextProtocol.Server;
using ZMS.MCP.Resource.Browser;

namespace ZMS.MCP.Resource.Tools;

/// <summary>浏览器：把 JS 跑完的页面拿回来；人机验证交给用户过。</summary>
[McpServerToolType]
public static class BrowseTools
{
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Open a page in a real browser and return its rendered text, so pages that build themselves with JavaScript come " +
        "back as what a person would see. It drives a Chrome-based browser that is already installed (Edge first, then " +
        "Chrome) - nothing extra is downloaded; 360 / QQ / Quark style shells are not supported and the call fails " +
        "plainly if no usable browser is found. The window is visible on purpose: when a human check (CAPTCHA / browser " +
        "verification) is detected, this waits for the user to pass it, up to 60 seconds, and says so plainly when it " +
        "was not passed in time. The result does not enter the cache.")]
    public static Task<string> Browse(
        [Description("Absolute URL, must start with http:// or https://.")] string url,
        [Description("'open' = return once the page is up; 'wait' = also wait for the page to settle. Empty = open.")] string? action = null,
        [Description("What to wait for when action = 'wait': 'networkidle', 'load', 'domcontentloaded', or a duration like '5s'.")] string? waitFor = null,
        [Description("How long to allow, in seconds. Default 60.")] int timeout = BrowseService.DefaultTimeoutSeconds)
    {
        return BrowseService.RunAsync(url, action, waitFor, timeout);
    }
}
