// <copyright file="ListingEntry.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.Models.ShoppingList
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics.CodeAnalysis;
  using System.Globalization;
  using System.Linq;
  using Lumina.Excel.Sheets;

  /// <summary>
  /// One line of the shopping list: an item, the market to shop for it in, and how to choose which
  /// of that market's listings to buy.
  /// </summary>
  /// <remarks>
  /// Everything an entry reports - price, stack size, total, world - is read off the listings it
  /// currently resolves to, and only the ones still on sale count, so a sold out listing adds nothing.
  /// </remarks>
  public sealed class ListingEntry
  {
    /// <summary>
    /// What the world reads as when an entry's listings are spread over more than one of them.
    /// </summary>
    private const string ManyWorldsSuffix = " worlds";

    /// <summary>
    /// Initializes a new instance of the <see cref="ListingEntry"/> class.
    /// </summary>
    /// <param name="sourceItem">The item to buy.</param>
    /// <param name="scope">The market to buy it in.</param>
    /// <param name="kind">How the entry chooses its listings.</param>
    public ListingEntry(Item sourceItem, ListingScope scope, ListingKind kind)
    {
      this.SourceItem = sourceItem;
      this.Scope = scope ?? throw new ArgumentNullException(nameof(scope));
      this.Kind = kind;
    }

    /// <summary>Gets or sets the item to buy.</summary>
    public Item SourceItem { get; set; }

    /// <summary>Gets or sets the market to buy it in.</summary>
    public ListingScope Scope { get; set; }

    /// <summary>Gets or sets how the entry chooses its listings.</summary>
    public ListingKind Kind { get; set; }

    /// <summary>Gets or sets how many listings a <see cref="ListingKind.Lowest"/> entry takes.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Gets or sets which qualities a <see cref="ListingKind.Lowest"/> entry takes.</summary>
    /// <remarks>A conditional entry reads its quality off its rule instead, and a direct one is
    /// whatever quality the listing it stands for is.</remarks>
    public QualityFilter Quality { get; set; }

    /// <summary>Gets which qualities the entry buys, whichever kind it is.</summary>
    public QualityFilter EffectiveQuality => this.Kind switch
    {
      ListingKind.Lowest => this.Quality,
      ListingKind.Conditional => this.Conditions?.Quality ?? QualityFilter.Any,
      _ => QualityFilter.Any,
    };

    /// <summary>Gets or sets the listing a <see cref="ListingKind.Direct"/> entry stands for.</summary>
    public ResolvedListing? Target { get; set; }

    /// <summary>Gets or sets the rule a <see cref="ListingKind.Conditional"/> entry buys by.</summary>
    public ListingConditions? Conditions { get; set; }

    /// <summary>Gets the listings the last refresh resolved the entry to.</summary>
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Rewritten wholesale by every refresh")]
    public List<ResolvedListing> Matches { get; } = new List<ResolvedListing>();

    /// <summary>Gets the resolved listings still on sale, which a buy run may still try.</summary>
    public IEnumerable<ResolvedListing> Live => this.Matches.Where(m => !m.Gone && !m.Bought);

    /// <summary>Gets the cheapest price per unit the entry buys at.</summary>
    public double Price => this.Live.Select(m => m.Price).DefaultIfEmpty(0).Min();

    /// <summary>Gets how many items the entry buys in all.</summary>
    public long Quantity => this.Live.Sum(m => m.Quantity);

    /// <summary>Gets how many of them come from high quality listings.</summary>
    public long QuantityHq => this.Live.Where(m => m.Hq).Sum(m => m.Quantity);

    /// <summary>Gets how many of them come from normal quality listings.</summary>
    public long QuantityNq => this.Quantity - this.QuantityHq;

    /// <summary>Gets the gil the entry costs in all.</summary>
    public double Total => this.Live.Sum(m => m.Total);

    /// <summary>Gets a value indicating whether every listing the entry buys is high quality.</summary>
    public bool Hq => this.Live.Any() && this.Live.All(m => m.Hq);

    /// <summary>Gets the distinct worlds the entry's listings sit on.</summary>
    public IReadOnlyList<string> Worlds =>
      this.Live
        .Select(m => m.World)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    /// <summary>Gets the world the entry buys on, or a count when its listings are spread over several.</summary>
    public string World
    {
      get
      {
        var worlds = this.Worlds;

        return worlds.Count switch
        {
          0 => string.Empty,
          1 => worlds[0],
          _ => worlds.Count.ToString(CultureInfo.CurrentCulture) + ManyWorldsSuffix,
        };
      }
    }

    /// <summary>Gets or sets a value indicating whether the entry is waiting for a new price.</summary>
    public bool Refreshing { get; set; }

    /// <summary>Gets a value indicating whether the entry has nothing left to buy.</summary>
    public bool Unlisted => !this.Refreshing && !this.Live.Any();

    /// <summary>Gets or sets how the last buy attempt on the entry ended.</summary>
    /// <remarks>Not saved to the configuration; it only lasts until the entry is priced again.</remarks>
    public BuyOutcome Outcome { get; set; }

    /// <summary>Gets or sets why the last buy attempt bought nothing, or an empty string otherwise.</summary>
    /// <remarks>Not saved to the configuration; it only lasts until the entry is priced again.</remarks>
    public string FailReason { get; set; } = string.Empty;

    /// <summary>Gets the label that says what kind of entry this is on its own row.</summary>
    public string Summary => this.Kind switch
    {
      ListingKind.Lowest => LowestSummary(this.Quality, this.Count),
      ListingKind.Direct => this.Target == null
        ? "Direct"
        : "Direct - " + this.Target.RetainerName,
      _ => this.Conditions?.Summary() ?? "no conditions",
    };

    /// <summary>
    /// Replaces the listings the entry resolves to.
    /// </summary>
    /// <param name="matches">The listings, which can be none.</param>
    public void SetMatches(IEnumerable<ResolvedListing> matches)
    {
      ArgumentNullException.ThrowIfNull(matches);

      var replacement = matches.ToArray();

      this.Matches.Clear();
      this.Matches.AddRange(replacement);
      this.Outcome = BuyOutcome.None;
      this.FailReason = string.Empty;
    }

    /// <summary>
    /// Rolls the outcomes of the entry's listings up into the entry's own.
    /// </summary>
    public void RollUpOutcome()
    {
      var tried = this.Matches.Where(m => m.Outcome != BuyOutcome.None).ToArray();

      if (tried.Length == 0)
      {
        return;
      }

      var bought = tried.Where(m => m.Outcome is BuyOutcome.Bought or BuyOutcome.BoughtCheaper).ToArray();

      this.Outcome = bought.Length switch
      {
        0 => BuyOutcome.Failed,
        _ when bought.Length < tried.Length => BuyOutcome.PartlyBought,
        _ when bought.Any(m => m.Outcome == BuyOutcome.BoughtCheaper) => BuyOutcome.BoughtCheaper,
        _ => BuyOutcome.Bought,
      };

      // Every listing carries its own reason, so the entry only repeats one when they all say the same thing.
      var reasons = tried
        .Where(m => m.FailReason.Length > 0)
        .Select(m => m.FailReason)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

      this.FailReason = reasons.Length == 1 ? reasons[0] : string.Empty;
    }

    /// <summary>
    /// Writes out what a lowest entry buys.
    /// </summary>
    /// <param name="quality">The qualities it takes.</param>
    /// <param name="count">How many listings it takes.</param>
    /// <returns>The label.</returns>
    private static string LowestSummary(QualityFilter quality, int count)
    {
      var name = quality == QualityFilter.Any ? "Lowest" : "Lowest " + quality.Label();

      return count > 1 ? FormattableString.Invariant($"{name} x{count}") : name;
    }
  }
}
