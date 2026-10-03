// <copyright file="ResolvedListing.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.Models.ShoppingList
{
  using System;
  using MarketTerror.Models.Universalis;

  /// <summary>
  /// One Market Board listing a shopping list entry currently resolves to.
  /// </summary>
  /// <remarks>
  /// The game has no notion of the Universalis listing id, so a buy can only ask the board for a
  /// listing of this quality and stack size at this price or less. Two listings that look the same are
  /// therefore interchangeable, and resolving to both buys two listings of that shape.
  /// </remarks>
  public sealed class ResolvedListing
  {
    /// <summary>
    /// Initializes a new instance of the <see cref="ResolvedListing"/> class.
    /// </summary>
    /// <param name="price">The price per unit, with the gil sales tax folded in when the config asks for it.</param>
    /// <param name="quantity">The stack size of the listing.</param>
    /// <param name="hq">True when the listing is high quality.</param>
    /// <param name="world">The world the listing is on.</param>
    /// <param name="retainerName">The name of the retainer selling it.</param>
    /// <param name="listingId">The Universalis listing id, used to recognise the listing on a refresh.</param>
    public ResolvedListing(double price, long quantity, bool hq, string world, string retainerName, string listingId)
    {
      this.Price = price;
      this.Quantity = quantity;
      this.Hq = hq;
      this.World = world ?? string.Empty;
      this.RetainerName = retainerName ?? string.Empty;
      this.ListingId = listingId ?? string.Empty;
    }

    /// <summary>Gets or sets the price per unit.</summary>
    /// <remarks>A refresh writes the listing's current price here, which can be lower than the resolved one.</remarks>
    public double Price { get; set; }

    /// <summary>Gets the stack size of the listing.</summary>
    public long Quantity { get; }

    /// <summary>Gets a value indicating whether the listing is high quality.</summary>
    public bool Hq { get; }

    /// <summary>Gets the world the listing is on.</summary>
    public string World { get; }

    /// <summary>Gets the name of the retainer selling it.</summary>
    public string RetainerName { get; }

    /// <summary>Gets the Universalis listing id.</summary>
    public string ListingId { get; }

    /// <summary>Gets a value indicating whether the listing is being sold off a mannequin.</summary>
    /// <remarks>The shopping list never resolves to one, whatever the entry asks for.</remarks>
    public bool OnMannequin { get; init; }

    /// <summary>Gets a value indicating whether the listing was put up by a crafter.</summary>
    public bool IsCrafted { get; init; }

    /// <summary>Gets the name of the crafter, or an empty string when the listing names none.</summary>
    public string CreatorName { get; init; } = string.Empty;

    /// <summary>Gets how many materia are melded into the listing.</summary>
    public int MateriaCount { get; init; }

    /// <summary>Gets the dye on the listing, or 0 when it carries none.</summary>
    public uint StainId { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the last refresh could not find the listing any more.
    /// </summary>
    public bool Gone { get; set; }

    /// <summary>
    /// Gets or sets how the last buy attempt on this listing ended.
    /// </summary>
    /// <remarks>Not saved to the configuration; it only lasts until the row is priced again.</remarks>
    public BuyOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets why the last buy attempt on this listing bought nothing, or an empty string when
    /// it has not been tried or it went through.
    /// </summary>
    /// <remarks>Not saved to the configuration; it only lasts until the row is priced again.</remarks>
    public string FailReason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what was really paid per unit, or null when the listing has not been bought.
    /// </summary>
    /// <remarks>Not saved to the configuration; it only lasts until the row is priced again.</remarks>
    public double? Paid { get; set; }

    /// <summary>Gets a value indicating whether the listing has been bought.</summary>
    public bool Bought => this.Paid.HasValue;

    /// <summary>Gets the gil the whole listing costs.</summary>
    public double Total => this.Price * this.Quantity;

    /// <summary>
    /// Reads the price per unit of a listing the way the rest of the plugin shows it.
    /// </summary>
    /// <param name="listing">The listing to price.</param>
    /// <param name="includeSalesTax">True to fold the gil sales tax into the price.</param>
    /// <returns>The price per unit.</returns>
    public static double UnitPrice(MarketDataListing listing, bool includeSalesTax)
    {
      ArgumentNullException.ThrowIfNull(listing);

      return includeSalesTax && listing.Quantity > 0
        ? listing.PricePerUnit + ((double)listing.Tax / listing.Quantity)
        : listing.PricePerUnit;
    }

    /// <summary>
    /// Builds a resolved listing from a Market Board listing.
    /// </summary>
    /// <param name="listing">The listing to resolve to.</param>
    /// <param name="includeSalesTax">True to fold the gil sales tax into the price.</param>
    /// <param name="fallbackWorld">The world to record when the listing carries none.</param>
    /// <returns>The resolved listing.</returns>
    public static ResolvedListing FromListing(MarketDataListing listing, bool includeSalesTax, string fallbackWorld)
    {
      ArgumentNullException.ThrowIfNull(listing);

      return new ResolvedListing(
        UnitPrice(listing, includeSalesTax),
        listing.Quantity,
        listing.Hq,
        listing.WorldName ?? fallbackWorld,
        listing.RetainerName,
        listing.ListingId)
      {
        OnMannequin = listing.OnMannequin,
        IsCrafted = listing.IsCrafted,
        CreatorName = listing.CreatorName ?? string.Empty,
        MateriaCount = listing.Materia.Count,
        StainId = (uint)Math.Max(0, listing.StainId),
      };
    }

    /// <summary>
    /// Copies the listing so that whoever resolves to it owns its mutable state outright.
    /// </summary>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// The buy state - the outcome, the reason and what was paid - is deliberately left behind. A
    /// listing that has just been resolved to has not been attempted, and a refresh is exactly when
    /// that state should reset.
    /// </remarks>
    public ResolvedListing Copy()
    {
      return new ResolvedListing(this.Price, this.Quantity, this.Hq, this.World, this.RetainerName, this.ListingId)
      {
        OnMannequin = this.OnMannequin,
        IsCrafted = this.IsCrafted,
        CreatorName = this.CreatorName,
        MateriaCount = this.MateriaCount,
        StainId = this.StainId,
        Gone = this.Gone,
      };
    }

    /// <summary>
    /// Checks whether another resolved listing stands for the same Market Board listing as this one.
    /// </summary>
    /// <param name="other">The resolved listing to compare against.</param>
    /// <returns>True when both are the same listing.</returns>
    /// <remarks>
    /// The listing id is the sure answer, but Universalis hands out a new one whenever a retainer is
    /// uploaded by a client that hashes them differently, so one retainer's stack of a given size and
    /// quality on a given world counts as the same listing. The price is left out of that on purpose:
    /// it is the one thing a seller can change without the listing becoming a different one.
    /// </remarks>
    public bool SameAs(ResolvedListing other)
    {
      ArgumentNullException.ThrowIfNull(other);

      if (this.ListingId.Length > 0 && string.Equals(this.ListingId, other.ListingId, StringComparison.Ordinal))
      {
        return true;
      }

      return other.Quantity == this.Quantity
        && other.Hq == this.Hq
        && string.Equals(other.World, this.World, StringComparison.OrdinalIgnoreCase)
        && this.RetainerName.Length > 0
        && string.Equals(other.RetainerName, this.RetainerName, StringComparison.Ordinal);
    }
  }
}
