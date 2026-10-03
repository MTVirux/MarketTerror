// <copyright file="ShoppingListBuyer.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.Services
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using MarketTerror.Helpers;
  using MarketTerror.Models.ShoppingList;

  /// <summary>
  /// Buys the listings shopping list entries resolved to, off the Market Board, one at a time.
  /// </summary>
  /// <remarks>
  /// A run is ordered by how far it has to travel - the current world first, then the rest of the
  /// data centre, then the rest of the region, then anywhere else with Oceania last - and listings on
  /// the same world, and worlds on the same data centre, are kept together so each is only travelled
  /// to once. An entry's listings are spread through that order along with everyone else's, since
  /// the entry itself is never somewhere: only its listings are.
  /// </remarks>
  public sealed class ShoppingListBuyer : IDisposable
  {
    private readonly MarketTerrorPlugin plugin;

    private readonly MarketBoardPurchase purchase;

    private readonly MarketBoardRefresh refresh;

    private readonly Queue<BuyJob> queue = new Queue<BuyJob>();

    /// <summary>
    /// How many listings of each entry the run still has to get through, so an entry is only
    /// reported once it is finished with.
    /// </summary>
    private readonly Dictionary<ListingEntry, int> outstanding = new Dictionary<ListingEntry, int>();

    /// <summary>
    /// How many listings of each entry the run started with, so the progress line can count them off.
    /// A run started from one listing has fewer of them than the entry currently resolves to.
    /// </summary>
    private readonly Dictionary<ListingEntry, int> planned = new Dictionary<ListingEntry, int>();

    private string boardWorld = string.Empty;

    /// <summary>
    /// The item the board is showing, and whether anything of it has been bought there, so a
    /// refresh only runs once the run is finished with that item on that world.
    /// </summary>
    private string boardItemName = string.Empty;

    private uint boardItemId;

    private bool boughtOnBoard;

    /// <summary>
    /// Whether the listing in flight is being bought off a listings window the last purchase left up,
    /// and whether the board turned out to still be holding that purchase's listings.
    /// </summary>
    private bool reusingListings;

    private bool listingsWentStale;

    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListBuyer"/> class.
    /// </summary>
    /// <param name="plugin">The plugin instance.</param>
    public ShoppingListBuyer(MarketTerrorPlugin plugin)
    {
      this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

      this.purchase = new MarketBoardPurchase(
        this.plugin.Framework,
        this.plugin.GameGui,
        this.plugin.MarketBoard,
        this.plugin.Log,
        () => !this.plugin.Config.NoGilSalesTax);

      this.refresh = new MarketBoardRefresh(
        this.plugin.Framework,
        this.plugin.GameGui,
        this.plugin.MarketBoard,
        this.plugin.Log,
        this.plugin.AutoSearch);
    }

    /// <summary>
    /// Gets a value indicating whether a run is going.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Gets the name of the item being bought right now.
    /// </summary>
    public string CurrentItemName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the number of listings the run has finished with.
    /// </summary>
    public int Done { get; private set; }

    /// <summary>
    /// Gets the number of listings the run started with.
    /// </summary>
    public int Total { get; private set; }

    /// <summary>
    /// Gets the entry being bought right now, or null when no run is going.
    /// </summary>
    public ListingEntry? CurrentEntry { get; private set; }

    /// <summary>
    /// Gets the resolved listing being bought right now, or null when no run is going.
    /// </summary>
    public ResolvedListing? CurrentListing { get; private set; }

    /// <summary>
    /// Checks whether a run has still to get to a resolved listing.
    /// </summary>
    /// <param name="listing">The listing to look for.</param>
    /// <returns>True when the listing is waiting in the queue, the one in flight included.</returns>
    public bool IsQueued(ResolvedListing listing)
    {
      return this.IsRunning && this.queue.Any(job => ReferenceEquals(job.Listing, listing));
    }

    /// <summary>
    /// Checks whether an entry can be bought, and says why when it cannot.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <param name="reason">Why the entry cannot be bought, or an empty string when it can.</param>
    /// <returns>True when the entry can be bought.</returns>
    public bool CanBuy(ListingEntry entry, out string reason)
    {
      ArgumentNullException.ThrowIfNull(entry);

      if (!this.plugin.Config.ShoppingListBuyEnabled)
      {
        reason = "Buying from the shopping list is switched off in the settings.";
        return false;
      }

      if (this.IsRunning)
      {
        reason = "A buy run is already going.";
        return false;
      }

      if (this.plugin.ShoppingListBulkAdd.IsRunning)
      {
        reason = "The list is still being added to or priced.";
        return false;
      }

      // A run that is already going travels through the main menu, so only the start is guarded.
      if (!this.plugin.ClientState.IsLoggedIn)
      {
        reason = "No character is logged in.";
        return false;
      }

      if (entry.Refreshing)
      {
        reason = "This entry is being priced again.";
        return false;
      }

      if (this.plugin.Config.SkipUnlockedWhenBuying && ItemUnlock.IsUnlocked(this.plugin.PlayerState, entry.SourceItem) == true)
      {
        reason = "This character has already unlocked this item.";
        return false;
      }

      var live = entry.Live.ToArray();

      if (live.Length == 0)
      {
        reason = "Nothing on sale to buy.";
        return false;
      }

      // The board is asked for a stack of an exact size, so a listing whose size was never recorded
      // has nothing to ask for.
      if (live.All(listing => listing.Quantity <= 0))
      {
        reason = "Refresh to record the stack size.";
        return false;
      }

      reason = string.Empty;
      return true;
    }

    /// <summary>
    /// Buys every listing a single entry resolved to.
    /// </summary>
    /// <param name="entry">The entry to buy.</param>
    public void BuyOne(ListingEntry entry)
    {
      this.BuyAll(new[] { entry });
    }

    /// <summary>
    /// Buys one of an entry's listings, leaving the rest of the entry for later.
    /// </summary>
    /// <param name="entry">The entry the listing belongs to.</param>
    /// <param name="listing">The listing to buy.</param>
    public void BuyListing(ListingEntry entry, ResolvedListing listing)
    {
      ArgumentNullException.ThrowIfNull(entry);
      ArgumentNullException.ThrowIfNull(listing);

      if (listing.Gone || listing.Bought || !this.CanBuy(entry, out _))
      {
        return;
      }

      // Two buys without a refresh in between would otherwise report the first one's result again.
      listing.Outcome = BuyOutcome.None;
      listing.FailReason = string.Empty;
      listing.Paid = null;

      this.Start(new[] { new BuyJob(entry, listing) });
    }

    /// <summary>
    /// Buys every listing of every entry it is handed, cheapest travel first.
    /// </summary>
    /// <param name="entries">The entries to buy, which can be one entry, one node's worth, or the whole list.</param>
    public void BuyAll(IEnumerable<ListingEntry> entries)
    {
      ArgumentNullException.ThrowIfNull(entries);

      if (this.IsRunning)
      {
        return;
      }

      if (!this.plugin.ClientState.IsLoggedIn)
      {
        return;
      }

      var jobs = new List<BuyJob>();
      var skipped = new List<(string Name, string Reason)>();

      foreach (var entry in entries)
      {
        if (!this.CanBuy(entry, out var why))
        {
          skipped.Add((entry.SourceItem.Name.ExtractText(), why));
          continue;
        }

        jobs.AddRange(Jobs(entry));
      }

      if (skipped.Count > 0)
      {
        this.plugin.ChatGui.Print($"Skipped {skipped.Count} shopping list entries that cannot be bought yet:");

        foreach (var group in skipped.GroupBy(skip => skip.Reason, StringComparer.Ordinal))
        {
          var who = group.Count() == 1 ? group.First().Name : $"{group.Count()} entries";
          this.plugin.ChatGui.Print($"  {who}: {group.Key}");
        }
      }

      if (jobs.Count == 0)
      {
        return;
      }

      this.Start(jobs);
    }

    /// <summary>
    /// Stops the run after the listing that is in flight.
    /// </summary>
    public void Cancel()
    {
      this.queue.Clear();
      this.plugin.AutoSearch.Disarm();
      this.refresh.Cancel();
      this.purchase.Cancel();

      // Neither of those fires a continuation when nothing is in flight, so settle the run here.
      if (this.IsRunning)
      {
        this.StartNext();
      }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      if (this.isDisposed)
      {
        return;
      }

      this.queue.Clear();
      this.outstanding.Clear();
      this.planned.Clear();
      this.purchase.Dispose();
      this.refresh.Dispose();
      this.isDisposed = true;
    }

    private static string TieBreak(string region)
    {
      // Oceania is the most expensive hop, so it goes last inside the "somewhere else" bucket.
      return string.Equals(region, WorldRegions.Oceania, StringComparison.Ordinal) ? "1" : "0";
    }

    /// <summary>
    /// Breaks an entry into the listings a run has to go and buy for it.
    /// </summary>
    /// <param name="entry">The entry to break up.</param>
    /// <returns>One job per listing the entry still has on sale.</returns>
    private static BuyJob[] Jobs(ListingEntry entry)
    {
      var live = entry.Live.ToArray();

      // Two runs without a refresh in between would otherwise report the first run's results again.
      // Bought listings keep theirs, so they stay off the board.
      foreach (var listing in live)
      {
        listing.Outcome = BuyOutcome.None;
        listing.FailReason = string.Empty;
        listing.Paid = null;
      }

      // Cheapest first, so a run that is cancelled part way through has bought the best of them.
      return live
        .OrderBy(listing => listing.Price)
        .Select(listing => new BuyJob(entry, listing))
        .ToArray();
    }

    private static string Quality(bool hq)
    {
      return hq ? "HQ" : "NQ";
    }

    /// <summary>
    /// Names a listing by the retainer selling it and the world it is on.
    /// </summary>
    /// <param name="listing">The listing to name.</param>
    /// <returns>The text a chat line calls it.</returns>
    private static string Where(ResolvedListing listing)
    {
      return listing.RetainerName.Length > 0
        ? $"{listing.RetainerName} on {listing.World}"
        : $"the listing on {listing.World}";
    }

    private static string Gil(double value)
    {
      return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Queues up a run's listings and sets it going.
    /// </summary>
    /// <param name="jobs">The listings to buy.</param>
    private void Start(IEnumerable<BuyJob> jobs)
    {
      this.outstanding.Clear();
      this.planned.Clear();

      foreach (var job in this.Order(jobs))
      {
        this.queue.Enqueue(job);

        var count = this.outstanding.GetValueOrDefault(job.Entry) + 1;
        this.outstanding[job.Entry] = count;
        this.planned[job.Entry] = count;
      }

      this.IsRunning = true;
      this.Done = 0;
      this.Total = this.queue.Count;
      this.ForgetBoardItem();

      this.StartNext();
    }

    private IEnumerable<BuyJob> Order(IEnumerable<BuyJob> jobs)
    {
      // Going from one visited data centre to another passes through the home world, so worlds are
      // grouped by data centre before their names are compared.
      return jobs
        .Select(job =>
        {
          var world = this.plugin.WorldCatalogue.Find(job.World);

          return new
          {
            Job = job,
            Tier = this.TravelTier(job.World),
            Region = world?.Region ?? string.Empty,
            DataCentre = world?.DataCentre ?? string.Empty,
          };
        })
        .OrderBy(step => step.Tier)
        .ThenBy(step => TieBreak(step.Region), StringComparer.Ordinal)
        .ThenBy(step => step.Region, StringComparer.Ordinal)
        .ThenBy(step => step.DataCentre, StringComparer.Ordinal)
        .ThenBy(step => step.Job.World, StringComparer.Ordinal)
        .Select(step => step.Job);
    }

    /// <summary>
    /// Scores how far a world is from the one the player is standing on.
    /// </summary>
    /// <param name="world">The world to score.</param>
    /// <returns>0 for the current world, 1 for its data centre, 2 for its region, 3 for anywhere else.</returns>
    private int TravelTier(string world)
    {
      var current = PlayerWorld.CurrentName(this.plugin.PlayerState);

      if (string.Equals(world, current, StringComparison.OrdinalIgnoreCase))
      {
        return 0;
      }

      var target = this.plugin.WorldCatalogue.Find(world);
      var here = this.plugin.WorldCatalogue.Find(current);

      if (target == null || here == null)
      {
        return 3;
      }

      if (string.Equals(target.DataCentre, here.DataCentre, StringComparison.Ordinal))
      {
        return 1;
      }

      return string.Equals(target.Region, here.Region, StringComparison.Ordinal) ? 2 : 3;
    }

    private void StartNext()
    {
      if (this.TryRefreshBoardItem())
      {
        return;
      }

      // Travel goes through the main menu, so a run only stops when the next job comes round with nobody logged in.
      if (this.queue.Count > 0 && !this.plugin.ClientState.IsLoggedIn)
      {
        this.queue.Clear();
        this.plugin.ChatGui.Print("Stopped buying: no character is logged in.");
      }

      if (this.queue.Count == 0)
      {
        this.purchase.CloseBoard();
        this.IsRunning = false;
        this.CurrentItemName = string.Empty;
        this.CurrentEntry = null;
        this.CurrentListing = null;
        this.ForgetBoardItem();
        this.outstanding.Clear();
        this.planned.Clear();
        return;
      }

      var job = this.queue.Peek();

      if (this.boardWorld.Length > 0 && !string.Equals(this.boardWorld, job.World, StringComparison.OrdinalIgnoreCase))
      {
        this.purchase.CloseBoard();
      }

      this.CurrentItemName = this.Describe(job);
      this.CurrentEntry = job.Entry;
      this.CurrentListing = job.Listing;

      var sameBoardItem = this.boardItemId == job.Entry.SourceItem.RowId
        && string.Equals(this.boardWorld, job.World, StringComparison.OrdinalIgnoreCase);

      // Another listing of the same item, and the last purchase left the listings up: buy straight
      // off them. The buy only takes a listing the board has sent since, so nothing stale is bought.
      this.reusingListings = sameBoardItem && !this.listingsWentStale && this.purchase.IsListingsWindowOpen;
      this.listingsWentStale = false;

      if (this.reusingListings)
      {
        this.OnSearchFinished(job, true);
        return;
      }

      // Same item, but the listings have gone: they only have to be opened again rather than
      // searched for from scratch.
      if (sameBoardItem
        && this.plugin.MarketBoardContext.TryReopenListingsForBuy(job.World, job.Entry.SourceItem, opened => this.OnSearchFinished(job, opened)))
      {
        return;
      }

      this.plugin.MarketBoardContext.GoToMarketBoardForBuy(job.World, job.Entry.SourceItem, opened => this.OnSearchFinished(job, opened));
    }

    /// <summary>
    /// Opens the listings and sales history of the item the board is showing, once the run has bought
    /// everything it wanted of it on that world.
    /// </summary>
    /// <returns>True when a refresh was started, which calls back into <see cref="StartNext"/> when it ends.</returns>
    /// <remarks>
    /// Buying takes the listing off the board without anything asking for the board again, so a
    /// Universalis uploader would otherwise keep serving the listing that has just been bought.
    /// </remarks>
    private bool TryRefreshBoardItem()
    {
      if (!this.boughtOnBoard || !this.plugin.Config.RefreshListingsAfterBuy)
      {
        return false;
      }

      // More of the same item still to buy on the same world: that buy waits for the board to send
      // its listings again anyway, which is all an uploader needs.
      if (this.queue.Count > 0)
      {
        var next = this.queue.Peek();

        if (next.Entry.SourceItem.RowId == this.boardItemId
          && string.Equals(next.World, this.boardWorld, StringComparison.OrdinalIgnoreCase))
        {
          return false;
        }
      }

      var name = this.boardItemName;
      var id = this.boardItemId;

      this.boughtOnBoard = false;
      this.refresh.Start(name, id, this.StartNext);
      return true;
    }

    /// <summary>
    /// Forgets which item the board is showing, so nothing is refreshed once it has been closed.
    /// </summary>
    private void ForgetBoardItem()
    {
      this.boardWorld = string.Empty;
      this.boardItemName = string.Empty;
      this.boardItemId = 0;
      this.boughtOnBoard = false;
      this.reusingListings = false;
      this.listingsWentStale = false;
    }

    /// <summary>
    /// Names what is being bought, counting the listings off for an entry that has more than one.
    /// </summary>
    /// <param name="job">The job about to start.</param>
    /// <returns>The text the progress line shows.</returns>
    private string Describe(BuyJob job)
    {
      var name = job.Entry.SourceItem.Name.ExtractText();
      var queued = this.planned.GetValueOrDefault(job.Entry);

      if (queued <= 1)
      {
        return name;
      }

      var left = this.outstanding.GetValueOrDefault(job.Entry);

      return $"{name} (listing {queued - left + 1} of {queued})";
    }

    private void OnSearchFinished(BuyJob job, bool opened)
    {
      if (!opened)
      {
        this.Report(job, BuyResult.Failed($"the Market Board never opened its listings on {job.World}"));
        return;
      }

      this.boardWorld = job.World;
      this.boardItemId = job.Entry.SourceItem.RowId;
      this.boardItemName = job.Entry.SourceItem.Name.ExtractText();

      this.purchase.Start(
        new BuyRequest(job.Entry.SourceItem.RowId, job.Entry.SourceItem.Name.ExtractText(), job.Hq, job.Quantity, job.Price),
        result => this.Report(job, result),
        this.reusingListings);
    }

    private void Report(BuyJob job, BuyResult result)
    {
      // The board kept its window up but not its listings, so this job has not been tried yet.
      if (result.Stale)
      {
        this.listingsWentStale = true;
        this.StartNext();
        return;
      }

      var outcome = result.Success
        ? (result.UnitPrice < job.Price ? BuyOutcome.BoughtCheaper : BuyOutcome.Bought)
        : BuyOutcome.Failed;

      this.boughtOnBoard |= result.Success;

      job.Listing.Outcome = outcome;
      job.Listing.FailReason = result.Success ? string.Empty : result.Reason;
      job.Listing.Paid = result.Success ? result.UnitPrice : null;
      job.Entry.RollUpOutcome();

      var left = this.outstanding.GetValueOrDefault(job.Entry) - 1;
      this.outstanding[job.Entry] = left;

      if (left == 0)
      {
        this.PrintEntry(job.Entry);
      }

      if (this.queue.Count > 0)
      {
        this.queue.Dequeue();
      }

      this.Done++;
      this.StartNext();
    }

    /// <summary>
    /// Says in chat how an entry ended, once every listing the run wanted of it has been tried.
    /// </summary>
    /// <param name="entry">The finished entry.</param>
    private void PrintEntry(ListingEntry entry)
    {
      var name = entry.SourceItem.Name.ExtractText();
      var tried = entry.Matches.Where(m => m.Outcome != BuyOutcome.None).ToArray();
      var bought = tried.Where(m => m.Paid.HasValue).ToArray();

      if (tried.Length == 1)
      {
        var only = tried[0];

        if (bought.Length == 1)
        {
          this.plugin.ChatGui.Print($"Bought {name} x{only.Quantity} for {Gil(only.Paid!.Value * only.Quantity)} gil");
        }
        else
        {
          this.plugin.ChatGui.Print($"Did not buy {name} x{only.Quantity} {Quality(only.Hq)} from {Where(only)}: {only.FailReason}");
        }

        return;
      }

      if (bought.Length == 0)
      {
        this.plugin.ChatGui.Print($"Did not buy {name}: none of its {tried.Length} listings could be bought");
        this.PrintReasons(tried);
        return;
      }

      var units = bought.Sum(m => m.Quantity);
      var spent = bought.Sum(m => m.Paid!.Value * m.Quantity);
      var listings = bought.Length == tried.Length
        ? $"{bought.Length} listings"
        : $"{bought.Length} of {tried.Length} listings";

      this.plugin.ChatGui.Print($"Bought {name} x{units} for {Gil(spent)} gil over {listings}");
      this.PrintReasons(tried);
    }

    /// <summary>
    /// Says in chat why the listings of an entry that were not bought were left behind, one line a reason.
    /// </summary>
    /// <param name="tried">Every listing of the entry a buy was attempted on.</param>
    private void PrintReasons(IEnumerable<ResolvedListing> tried)
    {
      var failed = tried.Where(m => !m.Paid.HasValue && m.FailReason.Length > 0);

      foreach (var group in failed.GroupBy(m => m.FailReason, StringComparer.Ordinal))
      {
        var who = group.Count() == 1
          ? Where(group.First())
          : $"{group.Count()} listings";

        this.plugin.ChatGui.Print($"  {who}: {group.Key}");
      }
    }

    /// <summary>
    /// One resolved listing a buy run has to go and get.
    /// </summary>
    private sealed class BuyJob
    {
      /// <summary>
      /// Initializes a new instance of the <see cref="BuyJob"/> class.
      /// </summary>
      /// <param name="entry">The entry the listing belongs to.</param>
      /// <param name="listing">The listing to buy.</param>
      public BuyJob(ListingEntry entry, ResolvedListing listing)
      {
        this.Entry = entry;
        this.Listing = listing;
      }

      /// <summary>Gets the entry the listing belongs to.</summary>
      public ListingEntry Entry { get; }

      /// <summary>Gets the listing to buy.</summary>
      public ResolvedListing Listing { get; }

      /// <summary>Gets the world to buy on.</summary>
      public string World => this.Listing.World;

      /// <summary>Gets the stack size the listing has to have.</summary>
      public long Quantity => this.Listing.Quantity;

      /// <summary>Gets a value indicating whether the listing has to be high quality.</summary>
      public bool Hq => this.Listing.Hq;

      /// <summary>Gets the highest price per unit that may be paid.</summary>
      public double Price => this.Listing.Price;
    }
  }
}
