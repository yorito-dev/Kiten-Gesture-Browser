using System.Windows;
using System.Windows.Media;

namespace TabBrowser.Views;

internal static class VisualTreeTraversal
{
    internal static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
