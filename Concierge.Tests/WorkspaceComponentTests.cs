using Bunit;
using Concierge.Shared.Components.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Component tests for the workspace UI.
///
/// Why these exist: every UI claim made while rebuilding this app was checked by
/// launching the desktop app, clicking through it and photographing the screen —
/// about two minutes a cycle, and it still missed a regression that a rendered
/// assertion catches instantly. The project's own coverage comment says the
/// thresholds were lowered because Razor components "can't be exercised without
/// bUnit"; this is the start of closing that.
/// </summary>
public sealed class AssistantContentTests : BunitContext
{
    /// <summary>Multi-paragraph Markdown stays split into readable paragraphs.</summary>
    [Fact]
    public void A_multi_paragraph_reply_renders_as_separate_paragraphs()
    {
        var reply = "First paragraph, which stands alone.\n\nSecond paragraph, which must not be glued to the first.\n\nThird.";

        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, reply));

        var paragraphs = cut.FindAll(".assistant-markdown p");
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("First paragraph, which stands alone.", paragraphs[0].TextContent);
        Assert.Equal("Third.", paragraphs[2].TextContent);
    }

    [Fact]
    public void A_single_line_reply_still_renders()
    {
        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, "Just the one line."));

        var paragraph = cut.Find(".assistant-markdown p");
        Assert.Equal("Just the one line.", paragraph.TextContent);
    }

    /// <summary>
    /// An empty reply must render nothing rather than an empty paragraph — a
    /// stray blank &lt;p&gt; shows as a gap in the thread with no cause.
    /// </summary>
    [Fact]
    public void An_empty_reply_renders_nothing()
    {
        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, string.Empty));

        Assert.Empty(cut.FindAll(".assistant-markdown p"));
    }

    /// <summary>
    /// Windows line endings arrive from pasted content and from some providers.
    /// They must split paragraphs the same way \n\n does.
    /// </summary>
    [Fact]
    public void Windows_line_endings_split_paragraphs_too()
    {
        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, "One.\r\n\r\nTwo."));

        Assert.Equal(2, cut.FindAll(".assistant-markdown p").Count);
    }

    /// <summary>
    /// The model's output is data, not markup. Rendering it as HTML would let a
    /// reply inject into the page; Razor escapes it, and this pins that.
    /// </summary>
    [Fact]
    public void Markup_in_a_reply_is_shown_as_text_not_rendered()
    {
        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, "<script>alert(1)</script>"));

        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("<script>alert(1)</script>", cut.Find(".assistant-markdown p").TextContent);
    }

    [Fact]
    public void Common_answer_formatting_renders_as_rich_text()
    {
        const string reply = "## First steps\n\nUse **bold** and `code`.\n\n- One\n- Two\n\n```csharp\nConsole.WriteLine(\"hi\");\n```";

        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content, reply));

        Assert.Equal("First steps", cut.Find("h2").TextContent);
        Assert.Equal("bold", cut.Find("strong").TextContent);
        Assert.Equal("code", cut.Find("code").TextContent);
        Assert.Equal(2, cut.FindAll("ul li").Count);
        Assert.Contains("Console.WriteLine", cut.Find("pre code").TextContent);
    }

    [Fact]
    public void Unsafe_html_and_link_schemes_do_not_become_active_content()
    {
        var cut = Render<AssistantContent>(ps => ps.Add(p => p.Content,
            "<script>alert(1)</script>\n\n[run](javascript:alert(1))"));

        Assert.Empty(cut.FindAll("script"));
        Assert.Contains("<script>alert(1)</script>", cut.Find(".assistant-markdown").TextContent);
        Assert.DoesNotContain("javascript:", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
