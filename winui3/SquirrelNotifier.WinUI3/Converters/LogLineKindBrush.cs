// <copyright file="LogLineKindBrush.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Converters;

/// <summary>
/// ログ行の由来（stdout / stderr / progress）を表示色へ変換する x:Bind 静的関数（#144）。
/// Window ルートの DataTemplate では StaticResource ベースの IValueConverter が使えない
/// （生成コードが Window を FrameworkElement として扱えない）ため、静的関数バインドにする.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class LogLineKindBrush
{
    private static readonly SolidColorBrush _stderrBrush = new(Microsoft.UI.Colors.OrangeRed);
    private static readonly SolidColorBrush _progressBrush = new(Microsoft.UI.Colors.DodgerBlue);

    /// <summary>
    /// ログ行の種別に対応する文字色を返す。stdout はテーマの既定文字色を使う.
    /// </summary>
    /// <param name="kind">ログ行の由来.</param>
    /// <returns>表示色.</returns>
    public static Brush For(AgentExecutionEventKind kind)
    {
        return kind switch
        {
            AgentExecutionEventKind.Stderr => _stderrBrush,
            AgentExecutionEventKind.Progress => _progressBrush,
            _ => (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
    }
}
