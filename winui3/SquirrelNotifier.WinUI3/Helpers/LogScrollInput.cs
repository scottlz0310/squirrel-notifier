// <copyright file="LogScrollInput.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>ログ領域のポインター入力がスクロールバーから来たかを判定する.</summary>
internal static class LogScrollInput
{
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            ScrollViewer? descendant = FindScrollViewer(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    public static bool IsScrollBarSource(object source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ScrollBar)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
