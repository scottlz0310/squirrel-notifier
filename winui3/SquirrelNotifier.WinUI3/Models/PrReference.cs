// <copyright file="PrReference.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

internal sealed record PrReference(string Owner, string Repo, int PrNumber);
