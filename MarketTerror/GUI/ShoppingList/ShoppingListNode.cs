// <copyright file="ShoppingListNode.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI.ShoppingList
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using Lumina.Excel.Sheets;
  using MarketTerror.Models.ShoppingList;

  /// <summary>
  /// One group of the shopping list tree: an item or a scope, holding either the groups under it or
  /// the entries filed against it.
  /// </summary>
  /// <remarks>
  /// A node reports the same things an entry does - price, stack size, total, world - worked out the
  /// same way over every listing below it, so a group reads like the entries it stands for.
  /// </remarks>
  public sealed class ShoppingListNode
  {
    /// <summary>
    /// What the world reads as when a node's listings are spread over more than one of them.
    /// </summary>
    private const string ManyWorldsSuffix = " worlds";

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListNode"/> class holding other groups.
    /// </summary>
    /// <param name="label">What the node's row reads as.</param>
    /// <param name="item">The item the node groups by, or null when it groups by scope.</param>
    /// <param name="scope">The scope the node groups by, or null when it groups by item.</param>
    /// <param name="children">The groups underneath it.</param>
    public ShoppingListNode(string label, Item? item, ListingScope? scope, IReadOnlyList<ShoppingListNode> children)
    {
      ArgumentNullException.ThrowIfNull(children);

      this.Label = label ?? string.Empty;
      this.Item = item;
      this.Scope = scope;
      this.Children = children;
      this.Entries = Array.Empty<ListingEntry>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListNode"/> class holding the entries of
    /// one kind, which can be none of them.
    /// </summary>
    /// <param name="label">What the node's row reads as.</param>
    /// <param name="item">The item the entries under it buy.</param>
    /// <param name="scope">The market the entries under it shop in.</param>
    /// <param name="kind">The kind of entry the node stands for.</param>
    /// <param name="entries">The entries of that kind, which can be none.</param>
    public ShoppingListNode(string label, Item item, ListingScope scope, ListingKind kind, IReadOnlyList<ListingEntry> entries)
    {
      ArgumentNullException.ThrowIfNull(scope);
      ArgumentNullException.ThrowIfNull(entries);

      this.Label = label ?? string.Empty;
      this.Item = item;
      this.Scope = scope;
      this.Kind = kind;
      this.Children = Array.Empty<ShoppingListNode>();
      this.Entries = entries;
    }

    /// <summary>Gets what the node's row reads as.</summary>
    public string Label { get; }

    /// <summary>Gets the item the node groups by, or null when it groups by scope.</summary>
    public Item? Item { get; }

    /// <summary>Gets the scope the node groups by, or null when it groups by item.</summary>
    public ListingScope? Scope { get; }

    /// <summary>Gets the kind of entry the node stands for, or null when it is not a kind group.</summary>
    /// <remarks>
    /// A kind group is always drawn, whether it has entries or not, so every item and market on the
    /// list offers each kind of entry in the same place.
    /// </remarks>
    public ListingKind? Kind { get; }

    /// <summary>Gets the groups underneath the node, which is empty when the node holds entries.</summary>
    public IReadOnlyList<ShoppingListNode> Children { get; }

    /// <summary>Gets the entries filed under the node, which is empty when the node holds groups.</summary>
    public IReadOnlyList<ListingEntry> Entries { get; }

    /// <summary>Gets every entry anywhere below the node, so a whole group can be bought or priced at once.</summary>
    public IEnumerable<ListingEntry> AllEntries =>
      this.Children.SelectMany(c => c.AllEntries).Concat(this.Entries);

    /// <summary>Gets the resolved listings under the node a buy run may still try.</summary>
    public IEnumerable<ResolvedListing> Live => this.AllEntries.SelectMany(e => e.Live);

    /// <summary>Gets the cheapest price per unit anything under the node buys at.</summary>
    public double Price => this.Live.Select(l => l.Price).DefaultIfEmpty(0).Min();

    /// <summary>Gets how many items the node buys in all.</summary>
    public long Quantity => this.Live.Sum(l => l.Quantity);

    /// <summary>Gets how many of them come from high quality listings.</summary>
    public long QuantityHq => this.Live.Where(l => l.Hq).Sum(l => l.Quantity);

    /// <summary>Gets how many of them come from normal quality listings.</summary>
    public long QuantityNq => this.Quantity - this.QuantityHq;

    /// <summary>Gets the gil everything under the node costs.</summary>
    public double Total => this.Live.Sum(l => l.Total);

    /// <summary>Gets the world the node buys on, or a count when its listings are spread over several.</summary>
    public string World
    {
      get
      {
        var worlds = this.Live
          .Select(l => l.World)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

        return worlds.Length switch
        {
          0 => string.Empty,
          1 => worlds[0],
          _ => worlds.Length.ToString(CultureInfo.CurrentCulture) + ManyWorldsSuffix,
        };
      }
    }

    /// <summary>Gets a value indicating whether anything under the node is waiting for a new price.</summary>
    public bool Refreshing => this.AllEntries.Any(e => e.Refreshing);

    /// <summary>Gets how the last buy attempt on the entries under the node ended.</summary>
    public BuyOutcome Outcome
    {
      get
      {
        var tried = this.AllEntries.Where(e => e.Outcome != BuyOutcome.None).ToArray();

        if (tried.Length == 0)
        {
          return BuyOutcome.None;
        }

        var bought = tried.Where(e => e.Outcome is BuyOutcome.Bought or BuyOutcome.BoughtCheaper).ToArray();

        if (bought.Length == tried.Length)
        {
          return bought.Any(e => e.Outcome == BuyOutcome.BoughtCheaper)
            ? BuyOutcome.BoughtCheaper
            : BuyOutcome.Bought;
        }

        // An entry already saying it only partly went through counts as something bought, so a group
        // over it never reads as if nothing was.
        return bought.Length == 0 && !tried.Any(e => e.Outcome == BuyOutcome.PartlyBought)
          ? BuyOutcome.Failed
          : BuyOutcome.PartlyBought;
      }
    }
  }
}
