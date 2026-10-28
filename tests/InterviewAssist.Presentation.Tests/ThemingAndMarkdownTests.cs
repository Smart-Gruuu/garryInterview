using System.Text.RegularExpressions;
using InterviewAssist.Core.Models;
using InterviewAssist.Presentation.Markdown;
using InterviewAssist.Presentation.Theming;

namespace InterviewAssist.Presentation.Tests;

public class ThemingAndMarkdownTests
{
    [Fact]
    public void Every_theme_setting_value_has_a_palette()
    {
        Assert.Equal(SettingOptions.Themes, ThemePalettes.Names);
        Assert.Equal(ThemePalettes.Names.Count, ThemePalettes.Names.Select(ThemePalettes.Get).Distinct().Count());
    }

    [Fact]
    public void Theme_lookup_ignores_case_and_falls_back_to_dark()
    {
        Assert.Same(ThemePalettes.Get("Nord"), ThemePalettes.Get("nord"));
        Assert.False(ThemePalettes.Get("Light").IsDark);
        Assert.Same(ThemePalettes.Dark, ThemePalettes.Get("No such theme"));
        Assert.Same(ThemePalettes.Dark, ThemePalettes.Get(null));
    }

    [Fact]
    public void Color_resources_cover_every_key_with_hex_colors()
    {
        foreach (var name in ThemePalettes.Names)
        {
            var resources = ThemePalettes.ColorResources(ThemePalettes.Get(name));

            Assert.Equal(15, resources.Count);
            Assert.Equal(15, resources.Select(r => r.Key).Distinct().Count());
            Assert.All(resources, r =>
            {
                Assert.EndsWith("Color", r.Key);
                Assert.Matches(new Regex("^#[0-9A-F]{6}$"), r.Hex);
            });
        }
    }

    [Theory]
    [InlineData(5, FontSizeLimits.Min)]
    [InlineData(40, FontSizeLimits.Max)]
    [InlineData(16, 16)]
    [InlineData(double.NaN, FontSizeLimits.Default)]
    public void Font_size_is_clamped(double value, double expected)
    {
        Assert.Equal(expected, FontSizeLimits.Clamp(value));
    }

    [Fact]
    public void Markdown_inline_styles_become_runs()
    {
        var runs = MarkdownParser.Parse("Say **this** and *that* with `code`.");

        Assert.Contains(runs, r => r.Style == RunStyle.Bold && r.Text == "this");
        Assert.Contains(runs, r => r.Style == RunStyle.Italic && r.Text == "that");
        Assert.Contains(runs, r => r.Style == RunStyle.Code && r.Text == "code");
        Assert.Equal("Say this and that with code.", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void Markdown_blocks_get_heading_bullet_and_code_styles()
    {
        var runs = MarkdownParser.Parse("# Plan\n- first step\n```\nvar x = 1;\n");

        Assert.Contains(runs, r => r.Style.HasFlag(RunStyle.Heading1) && r.Text.Contains("Plan"));
        Assert.Contains(runs, r => r.Style == RunStyle.Accent && r.Text.Contains('•'));
        Assert.Contains(runs, r => r.Text.Contains("first step"));
        Assert.Contains(runs, r => r.Style == RunStyle.CodeBlock && r.Text.Contains("var x = 1;"));
    }
}
