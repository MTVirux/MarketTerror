// <copyright file="ShoppingListRequest.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI.ShoppingList
{
  using Lumina.Excel.Sheets;
  using MarketTerror.Models.ShoppingList;

  /// <summary>
  /// What a click on one of the tree's rows asks the window to open.
  /// </summary>
  /// <remarks>
  /// A popup opened from inside the table goes down again with the row it was opened from, so the tree
  /// only writes down what was asked for. The window reads this straight after the tree has drawn and
  /// clears it again.
  /// </remarks>
  public sealed class ShoppingListRequest
  {
    /// <summary>
    /// Gets or sets the entry whose conditions are to be edited, or null when none was asked for.
    /// </summary>
    public ListingEntry? Edit { get; set; }

    /// <summary>
    /// Gets or sets the entry a listing is to be chosen for, or null when none was asked for.
    /// </summary>
    public ListingEntry? PickListing { get; set; }

    /// <summary>
    /// Gets or sets the item and market a new conditional entry is to be made in, or null when none
    /// was asked for.
    /// </summary>
    public (Item Item, ListingScope Scope)? NewConditional { get; set; }

    /// <summary>
    /// Gets or sets the item and market a new direct entry is to be picked in, or null when none was
    /// asked for.
    /// </summary>
    public (Item Item, ListingScope Scope)? NewDirect { get; set; }
  }
}
