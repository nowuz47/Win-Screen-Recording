using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;

namespace Glide.App;

internal static class NumberBoxInput
{
    // WinUI commits NumberBox.Text/Value on Enter or focus loss. An Apply click
    // can precede that commit. Read the pinned WinUI template's actual InputBox
    // and use its own regional number parser. If the template changes, reject
    // the edit instead of silently saving the last committed number.
    internal static double? Read(NumberBox box)
    {
        box.ApplyTemplate();
        static TextBox? Input(DependencyObject element)
        {
            if (element is TextBox text && text.Name == "InputBox") return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); ++i)
                if (Input(VisualTreeHelper.GetChild(element, i)) is TextBox found) return found;
            return null;
        }
        var input = Input(box);
        if (input is null || box.NumberFormatter is not INumberParser parser) return null;
        double? value = parser.ParseDouble(input.Text.Trim());
        return value is double number && double.IsFinite(number) && number >= box.Minimum && number <= box.Maximum ? number : null;
    }
}
