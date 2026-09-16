// Lockwell - local-only encrypted vault
// Copyright (C) 2026 Lockwell
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Xml.Linq;

namespace Lockwell.Tests;

/// <summary>
/// No WPF Effect sits on anything holding readable text.
///
/// This exists because the same bug has now been found three times.
///
/// In WPF an Effect on an element forces its entire subtree into an intermediate bitmap
/// before compositing. Text drawn that way loses ClearType and gets resampled, so every
/// label under an effect goes soft. The first time, it was a drop shadow on the Card
/// style, and since almost everything sits in a card, almost every label in the app was
/// blurry. The fix removed it from Card and stopped there. HeroCard still had one, which
/// covered the unlock screen, the profile gate, every dialog and the whole entry detail
/// pane. Three interaction states had the same problem in miniature: hovering a ghost
/// button, focusing a text box and selecting a row each blurred the one piece of text the
/// user was looking at, at the exact moment they looked at it.
///
/// HeroCard was worse than a soft label. An Effect casts its shadow from the alpha of
/// that intermediate bitmap, and HeroCard's own fill was white at 2-8%, so the silhouette
/// being shadowed was the content, not the card. Every title and every field row carried
/// its own black smear.
///
/// A reviewer cannot see any of this in a screenshot: RenderTargetBitmap rasterises text
/// without ClearType regardless, so captures of the broken and fixed builds look the
/// same. Reading the markup is the only reliable way to catch it, which is what this does.
///
/// Effects on shapes and on empty borders are fine and stay allowed. So is a single icon
/// glyph: a 14px chevron resampled costs nothing, and the accent glow behind the lock icon
/// on the unlock screen is a deliberate part of that design.
/// </summary>
internal static class EffectChecks
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Elements that present arbitrary content, so they can always hold text.</summary>
    private static readonly HashSet<string> TextHosts = new(StringComparer.Ordinal)
    {
        "ContentPresenter", "ItemsPresenter", "ContentControl", "AccessText",
        "TextBox", "PasswordBox", "RichTextBox", "Label", "ScrollViewer",
    };

    /// <summary>
    /// Style TargetTypes that can contain content. A keyed style for one of these gets
    /// applied to arbitrary containers, so an Effect on it is a blur waiting to happen.
    /// </summary>
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal)
    {
        "Border", "Control", "ContentControl", "Button", "RepeatButton", "ToggleButton",
        "CheckBox", "RadioButton", "Grid", "StackPanel", "Panel", "DockPanel",
        "WrapPanel", "UniformGrid", "ListBox", "ListBoxItem", "ItemsControl",
        "UserControl", "Window", "TextBlock",
    };

    public static void Run()
    {
        Check.Section("56. No effect is applied to anything holding text");
        Scan("Lockwell", "desktop", minimumFiles: 5);

        // The setup program had the same problem and worse: a drop shadow on the shell
        // border, which sits above every word in the window. Same rule, same check.
        Check.Section("64. The installer applies no effect to anything holding text");
        Scan("Lockwell.Installer", "installer", minimumFiles: 2);
    }

    private static void Scan(string projectFolder, string label, int minimumFiles)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            Check.Note("source tree not found next to the test binary; skipped");
            return;
        }

        string appDir = Path.Combine(root, projectFolder);
        if (!Directory.Exists(appDir))
        {
            Check.Note($"{label} project not found; skipped");
            return;
        }

        var offences = new List<string>();
        int scanned = 0;
        int effectsSeen = 0;

        foreach (string file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            XDocument doc;
            try { doc = XDocument.Load(file, LoadOptions.None); }
            catch (Exception ex)
            {
                offences.Add($"{Path.GetFileName(file)} could not be parsed ({ex.Message})");
                continue;
            }

            scanned++;
            string name = Path.GetFileName(file);

            foreach (XElement element in doc.Descendants())
            {
                // Effect="{StaticResource X}" written as an attribute.
                XAttribute? inline = element.Attribute("Effect");
                if (inline is not null && !IsNullLiteral(inline.Value))
                {
                    effectsSeen++;
                    if (HoldsText(element))
                        offences.Add($"{name}: <{element.Name.LocalName}> carries {Describe(inline.Value)} and holds text");
                }

                // <Ellipse.Effect> ... </Ellipse.Effect> property-element syntax.
                if (element.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal))
                {
                    effectsSeen++;
                    XElement? owner = element.Parent;
                    if (owner is not null && HoldsText(owner))
                        offences.Add($"{name}: <{owner.Name.LocalName}> sets an effect inline and holds text");
                }

                if (element.Name.LocalName != "Setter") continue;
                if (element.Attribute("Property")?.Value is not "Effect" and not "UIElement.Effect") continue;

                string? value = element.Attribute("Value")?.Value;
                if (value is not null && IsNullLiteral(value)) continue;   // clearing one is always fine
                effectsSeen++;

                string? targetName = element.Attribute("TargetName")?.Value;
                if (targetName is not null)
                {
                    // Scoped to a named part of a template. Resolve it and look inside.
                    XElement? template = element.AncestorsAndSelf()
                        .FirstOrDefault(a => a.Name.LocalName is "ControlTemplate" or "DataTemplate");
                    XElement? target = template?.Descendants()
                        .FirstOrDefault(d => (string?)d.Attribute(X + "Name") == targetName);

                    if (target is null)
                        offences.Add($"{name}: a setter targets \"{targetName}\", which is not in its template");
                    else if (HoldsText(target))
                        offences.Add($"{name}: \"{targetName}\" gets {Describe(value)} on a state change and holds text");

                    continue;
                }

                // Unscoped: the effect lands on whatever the style is applied to.
                XElement? style = element.Ancestors()
                    .FirstOrDefault(a => a.Name.LocalName is "Style");
                string targetType = Simplify(style?.Attribute("TargetType")?.Value) ?? "an unknown type";
                string key = style?.Attribute(X + "Key")?.Value ?? "an unkeyed style";

                if (ContainerTypes.Contains(targetType))
                {
                    offences.Add(
                        $"{name}: style \"{key}\" puts {Describe(value)} on every {targetType} that uses it");
                }
            }
        }

        Check.That($"the {label} markup was readable ({scanned} files, {effectsSeen} effect uses)",
            scanned >= minimumFiles && offences.All(o => !o.Contains("could not be parsed")));

        if (offences.Count == 0)
        {
            Check.That($"no effect covers readable text in the {label} UI", true);
        }
        else
        {
            foreach (string offence in offences.Distinct().OrderBy(o => o, StringComparer.Ordinal))
                Check.That(offence, false);
        }

        // The specific regressions this was written for, named so a failure says which
        // one came back rather than only that something did.
        string theme = Path.Combine(appDir, "Themes", "Theme.xaml");
        if (File.Exists(theme))
        {
            string text = File.ReadAllText(theme);
            Check.That("the Card style still has no shadow", !StyleSetsEffect(text, "Card"));
            Check.That("the HeroCard style still has no shadow", !StyleSetsEffect(text, "HeroCard"));
        }

        string installerTheme = Path.Combine(appDir, "Themes", "InstallerTheme.xaml");
        if (File.Exists(installerTheme))
        {
            string text = File.ReadAllText(installerTheme);
            Check.That("the installer's primary button still has no shadow",
                !StyleSetsEffect(text, "InstallerPrimaryButton"));
            Check.That("the installer's card style still has no shadow",
                !StyleSetsEffect(text, "InstallerCard"));
        }
    }

    /// <summary>Does this element's content contain anything with readable text in it?</summary>
    private static bool HoldsText(XElement element)
    {
        foreach (XElement child in Content(element))
        {
            string local = child.Name.LocalName;

            if (TextHosts.Contains(local)) return true;

            if (local == "TextBlock")
            {
                // An icon glyph is text to WPF but not to a reader. Resampling a chevron
                // costs nothing, and several deliberate accents are exactly this.
                if (!IsIconGlyph(child)) return true;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(child.Value) && !child.HasElements && local != "Border")
                return true;
        }

        return false;
    }

    /// <summary>
    /// The content subtree, skipping property-element syntax such as
    /// &lt;Border.Style&gt; or &lt;ListBox.ItemContainerStyle&gt;. Those describe how a
    /// control looks somewhere else; they are not what this element draws.
    /// </summary>
    private static IEnumerable<XElement> Content(XElement element)
    {
        foreach (XElement child in element.Elements())
        {
            string local = child.Name.LocalName;
            bool isPropertyElement = local.Contains('.', StringComparison.Ordinal);

            if (isPropertyElement &&
                !local.EndsWith(".Child", StringComparison.Ordinal) &&
                !local.EndsWith(".Content", StringComparison.Ordinal) &&
                !local.EndsWith(".Children", StringComparison.Ordinal))
            {
                continue;
            }

            if (!isPropertyElement) yield return child;

            foreach (XElement deeper in Content(child))
                yield return deeper;
        }
    }

    private static bool IsIconGlyph(XElement textBlock)
    {
        string? font = textBlock.Attribute("FontFamily")?.Value;
        return font is not null && font.Contains("IconFont", StringComparison.Ordinal);
    }

    private static bool StyleSetsEffect(string themeXaml, string key)
    {
        int start = themeXaml.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        if (start < 0) return false;

        int end = themeXaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        if (end < 0) end = themeXaml.Length;

        string body = themeXaml[start..end];
        return body.Contains("Property=\"Effect\"", StringComparison.Ordinal);
    }

    private static bool IsNullLiteral(string value) =>
        value.Replace(" ", "").Equals("{x:Null}", StringComparison.Ordinal);

    /// <summary>"{StaticResource SoftShadow}" reads better as "SoftShadow".</summary>
    private static string Describe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "an effect";
        string trimmed = value.Trim();
        const string marker = "StaticResource ";
        int at = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return "an effect";
        return trimmed[(at + marker.Length)..].TrimEnd('}', ' ');
    }

    /// <summary>"{x:Type Border}" and "Border" are the same target type.</summary>
    private static string? Simplify(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType)) return null;
        string t = targetType.Trim();
        if (t.StartsWith("{", StringComparison.Ordinal))
            t = t.Trim('{', '}').Replace("x:Type", "", StringComparison.Ordinal).Trim();
        int colon = t.LastIndexOf(':');
        return colon >= 0 ? t[(colon + 1)..] : t;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Lockwell.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
