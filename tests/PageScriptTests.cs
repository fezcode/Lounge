using Lounge.Core.Web;
using Xunit;

namespace Lounge.Tests;

public class PageScriptTests
{
    [Fact]
    public void Extracts_a_function_past_braces_in_strings_and_comments()
    {
        const string source = """
            const x = 1;
            // function run() { not this one }
            async function run(a) {
              const s = "}{"; const t = '}'; const u = `${a} }`;
              /* } */ if (a) { return { ok: true }; }
              return {};
            }
            function after() {}
            """;
        var fn = PageScripts.ExtractFunction(source, "run");
        Assert.NotNull(fn);
        Assert.StartsWith("async function run(a) {", fn);
        Assert.EndsWith("return {};\n}", fn!.Replace("\r\n", "\n"));
        Assert.DoesNotContain("after", fn);
        Assert.Null(PageScripts.ExtractFunction(source, "missing"));
    }

    [Fact]
    public void AllOffs_own_popup_still_has_the_function_Lounge_runs()
    {
        // The subtree Lounge embeds (extensions/AllOff); an update that renamed the function
        // would silently drop AllOff from the web view's Extensions menu.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "extensions", "AllOff", "popup", "popup.js"))) dir = dir.Parent;
        Assert.True(dir is not null, "extensions/AllOff was not found");
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "extensions", "AllOff", "popup", "popup.js"));
        var fn = PageScripts.ExtractFunction(source, PageScripts.AllOffFunction);
        Assert.NotNull(fn);
        Assert.Contains("return { toggled, remaining };", fn);
        Assert.DoesNotContain("addEventListener(\"click\"", fn);   // stops before the popup's own code
    }

    [Theory]
    [InlineData(0, 0, 0, "AllOff couldn't reach this page.")]
    [InlineData(0, 0, 2, "Nothing was switched on.")]
    [InlineData(5, 0, 1, "Switched off 5. All clear. Save your choices on the page if it asks.")]
    [InlineData(3, 2, 1, "Switched off 3, but 2 still on.")]
    public void AllOff_summary(int toggled, int remaining, int frames, string expected) =>
        Assert.Equal(expected, PageScripts.AllOffSummary(toggled, remaining, frames));
}
