using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnrealCommander
{
    public static class ScrollViewerFinder
    {
        public static ScrollViewer GetScrollViewer(UIElement element)
        {
            ScrollViewer result = null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element) && result == null; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is ScrollViewer)
                {
                    result = (ScrollViewer)(VisualTreeHelper.GetChild(element, i));
                }
                else
                {
                    result = GetScrollViewer(VisualTreeHelper.GetChild(element, i) as UIElement);
                }
            }
            return result;
        }
    }
}
