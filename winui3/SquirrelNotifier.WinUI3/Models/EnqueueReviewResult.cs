// <copyright file="EnqueueReviewResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class EnqueueReviewResult
{
    public bool Success { get; set; }

    public int? ExitCode { get; set; }

    public bool IsAuthenticationRequired { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;
}
