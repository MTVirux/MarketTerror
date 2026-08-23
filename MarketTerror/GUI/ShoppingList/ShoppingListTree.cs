// <copyright file="ShoppingListTree.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI.ShoppingList
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Numerics;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Game.Text;
  using Dalamud.Interface;
  using Dalamud.Interface.Textures;
  using Lumina.Excel.Sheets;
  using MarketTerror.Extensions;
  using MarketTerror.GUI.Theme;
  using MarketTerror.Helpers;
  using MarketTerror.Models.ShoppingList;

  /// <summary>
  /// The buy list table: two levels of groups, the entries filed under them, and the listings each
  /// entry currently resolves to.
  /// </summary>
  /// <remarks>
  /// The tree keeps nothing of its own but which rows are open and how the table is sorted. What it
  /// draws comes from the nodes it is handed each frame, and what a click leads to is either done on
  /// the store there and then or written into the <see cref="ShoppingListRequest"/> for the window to
  /// act on once the table has been closed.
  /// </remarks>
  public sealed class ShoppingListTree
  {
    /// <summary>
    /// What a row shows in place of a price or a world it does not have.
    /// </summary>
    private const string NoValue = "-";

    /// <summary>
    /// What a listing whose retainer has no name reads as.
    /// </summary>
    private const string UnnamedRetainer = "Unnamed retainer";

    /// <summary>
    /// The unscaled width of one of the icon buttons in the action column.
    /// </summary>
    private const float ActionButtonWidth = 32;

    /// <summary>
    /// How many icon buttons a row can show.
    /// </summary>
    private const int ActionButtonCount = 6;

    private const string PriceHeader = "Price";

    private const string QtyHeader = "Qty";

    private const string TotalHeader = "Total";

    private const string WorldHeader = "World";

    // Not resizable on purpose: that is what keeps every fixed column fitted to its content each frame.
    private const ImGuiTableFlags TableFlags =
      ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit |
      ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate;

    /// <summary>
    /// What the quantity column puts after a stack that is high quality.
    /// </summary>
    private static readonly string HqMark = SeIconChar.HighQuality.AsString();

    private readonly MarketTerrorPlugin plugin;

    /// <summary>
    /// The groups that have been closed, keyed by where they sit in the tree.
    /// </summary>
    /// <remarks>
    /// Groups are open until they are closed, so a list that has just been added to shows what is on
    /// it rather than a row that has to be opened first. Keying them by what a group groups rather
    /// than by node is what keeps them open across the rebuild every change to the list causes.
    /// </remarks>
    private readonly HashSet<string> collapsed = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The entries showing the listings they resolve to under them.
    /// </summary>
    private readonly HashSet<ListingEntry> expanded = new HashSet<ListingEntry>();

    /// <summary>
    /// The entries changed on their own row that are waiting to be priced again.
    /// </summary>
    /// <remarks>
    /// A set rather than one entry, so changing an entry's quality and then another's count prices
    /// both instead of the second change losing the first.
    /// </remarks>
    private readonly HashSet<ListingEntry> repriceWanted = new HashSet<ListingEntry>();

    /// <summary>
    /// The entry a buy run was last seen on, so each one is only opened once as the run walks the list.
    /// </summary>
    private ListingEntry? followed;

    /// <summary>
    /// The entry whose count box is open, or null when none is.
    /// </summary>
    private ListingEntry? countEditing;

    /// <summary>
    /// The count the open box is being set to, which only reaches the entry once the box closes.
    /// </summary>
    private int countDraft = 1;

    /// <summary>
    /// The entry whose quality button the pointer is still on, whose pricing therefore waits.
    /// </summary>
    private ListingEntry? repriceHeld;

    /// <summary>
    /// The skin the window is drawing in, kept for as long as the frame lasts.
    /// </summary>
    private TerrorTheme theme;

    private float priceWidth;

    private float qtyWidth;

    private float totalWidth;

    private float worldWidth;

    private int sortColumn = -1;

    private bool sortAscending = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListTree"/> class.
    /// </summary>
    /// <param name="plugin">The <see cref="MarketTerrorPlugin"/>.</param>
    public ShoppingListTree(MarketTerrorPlugin plugin)
    {
      this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

      // Replaced by the window's own skin on the first frame; here so no row ever has to check.
      this.theme = new TerrorTheme(this.plugin.Config);
    }

    /// <summary>
    /// Draws the table, from the group rows down to the listings under whatever is open.
    /// </summary>
    /// <param name="theme">The skin the window is drawing in.</param>
    /// <param name="nodes">The groups to draw.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    /// <param name="tableHeight">
    /// The height to give the table, which is negative to leave that much room under it.
    /// </param>
    public void Draw(TerrorTheme theme, IReadOnlyList<ShoppingListNode> nodes, ShoppingListRequest request, float tableHeight)
    {
      ArgumentNullException.ThrowIfNull(theme);
      ArgumentNullException.ThrowIfNull(nodes);
      ArgumentNullException.ThrowIfNull(request);

      this.theme = theme;
      this.FollowBuyRun();

      // An entry that has been taken off the list stops counting as open.
      this.expanded.IntersectWith(nodes.SelectMany(n => n.AllEntries));
      this.ForgetDroppedEntries(nodes);
      this.repriceHeld = null;

      if (!ImGui.BeginTable("shoppingList", 6, TableFlags | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
      {
        return;
      }

      ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn(PriceHeader, ImGuiTableColumnFlags.WidthFixed);
      ImGui.TableSetupColumn(QtyHeader, ImGuiTableColumnFlags.WidthFixed);
      ImGui.TableSetupColumn(TotalHeader, ImGuiTableColumnFlags.WidthFixed);
      ImGui.TableSetupColumn(WorldHeader, ImGuiTableColumnFlags.WidthFixed);
      ImGui.TableSetupColumn(
        "Action",
        ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed,
        ActionColumnWidth());
      ImGui.TableSetupScrollFreeze(0, 1);

      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
      ImGui.TableHeadersRow();
      ImGui.PopStyleColor();

      this.UpdateSort();

      // Every level that is on screen is measured, so the columns fit the listings under an open entry too.
      this.priceWidth = ColumnWidth(PriceHeader, this.ColumnValues(nodes, string.Empty, this.PriceText, l => this.Gil(ListingPrice(l))));
      this.qtyWidth = ColumnWidth(QtyHeader, this.ColumnValues(nodes, string.Empty, QtyText, ListingQtyText));
      this.totalWidth = ColumnWidth(TotalHeader, this.ColumnValues(nodes, string.Empty, this.TotalText, l => this.Gil(ListingPrice(l) * l.Quantity)));
      this.worldWidth = ColumnWidth(WorldHeader, this.ColumnValues(nodes, string.Empty, WorldText, l => l.World));

      foreach (var node in this.SortedNodes(nodes))
      {
        this.DrawNode(node, Key(string.Empty, NodeKey(node)), 0, request);
      }

      ImGui.EndTable();

      this.SettleReprice();
    }

    /// <summary>
    /// Works out the width the action column needs to hold every button on a row.
    /// </summary>
    /// <returns>The width, in pixels.</returns>
    private static float ActionColumnWidth()
    {
      var style = ImGui.GetStyle();

      return (ActionButtonCount * ActionButtonWidth * ImGui.GetIO().FontGlobalScale)
        + ((ActionButtonCount - 1) * style.ItemSpacing.X)
        + (2 * style.CellPadding.X);
    }

    /// <summary>
    /// Works out how big one of a row's buttons is.
    /// </summary>
    /// <returns>The size, in pixels.</returns>
    private static Vector2 ButtonSize()
    {
      return new Vector2(ActionButtonWidth * ImGui.GetIO().FontGlobalScale, 1.5f * ImGui.GetTextLineHeight());
    }

    /// <summary>
    /// Names a row by where it sits in the tree, which is what its open state and its buttons are
    /// identified by.
    /// </summary>
    /// <param name="parent">The key of the row above, or an empty string at the top level.</param>
    /// <param name="name">What the row is called among the rows beside it.</param>
    /// <returns>The key.</returns>
    private static string Key(string parent, string name)
    {
      return parent.Length == 0 ? name : parent + "/" + name;
    }

    /// <summary>
    /// Names a group by what it groups rather than by what it reads as, since two markets with
    /// different anchors are named the same and two items can share a name.
    /// </summary>
    /// <param name="node">The group to name.</param>
    /// <returns>The name, which no other group beside it shares.</returns>
    private static string NodeKey(ShoppingListNode node)
    {
      if (node.Scope != null)
      {
        return FormattableString.Invariant($"s{(int)node.Scope.Level}@{node.Scope.AnchorWorld}");
      }

      return node.Item.HasValue
        ? FormattableString.Invariant($"i{node.Item.Value.RowId}")
        : node.Label;
    }

    /// <summary>
    /// Pushes a row's name in to where its level starts.
    /// </summary>
    /// <param name="depth">How far down the tree the row sits.</param>
    private static void Indent(int depth)
    {
      if (depth > 0)
      {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (depth * ImGui.GetTreeNodeToLabelSpacing()));
      }
    }

    /// <summary>
    /// Leaves the room a twisty would have taken, so a row that has nothing to open still lines up.
    /// </summary>
    private static void NoExpander()
    {
      ImGui.Dummy(new Vector2(ImGui.GetTreeNodeToLabelSpacing(), ImGui.GetTextLineHeight()));
      ImGui.SameLine();
    }

    /// <summary>
    /// Checks whether an entry draws its listings as rows of their own.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns>True when the entry has a twisty and its listings hang off it.</returns>
    /// <remarks>
    /// One listing that is still on sale is drawn on the entry's own row, so a direct entry and a
    /// lowest entry taking a single listing stay one line each. A listing that has sold out keeps its
    /// own row even when it is the only one, since that row is the only place it can be dismissed from.
    /// </remarks>
    private static bool HasListingRows(ListingEntry entry)
    {
      return entry.Matches.Count > 1 || (entry.Matches.Count == 1 && entry.Matches[0].Gone);
    }

    /// <summary>
    /// Gets the one listing an entry draws on its own row.
    /// </summary>
    /// <param name="entry">The entry to read.</param>
    /// <returns>The listing, or null when the entry draws its listings under it instead.</returns>
    private static ResolvedListing? InlineListing(ListingEntry entry)
    {
      return HasListingRows(entry) || entry.Matches.Count == 0
        ? null
        : entry.Matches[0];
    }

    /// <summary>
    /// Gets the item a group is about, whether it says so itself or every entry under it agrees.
    /// </summary>
    /// <param name="node">The group to read.</param>
    /// <returns>The item, or null when the group holds several of them.</returns>
    private static Item? ContextItem(ShoppingListNode node)
    {
      if (node.Item.HasValue)
      {
        return node.Item;
      }

      var entries = node.AllEntries.ToArray();

      return entries.Length > 0 && entries.All(e => e.SourceItem.RowId == entries[0].SourceItem.RowId)
        ? entries[0].SourceItem
        : (Item?)null;
    }

    /// <summary>
    /// Gets the market a group shops in, whether it says so itself or every entry under it agrees.
    /// </summary>
    /// <param name="node">The group to read.</param>
    /// <returns>The market, or null when the group reaches into several of them.</returns>
    private static ListingScope? ContextScope(ShoppingListNode node)
    {
      if (node.Scope != null)
      {
        return node.Scope;
      }

      var scopes = node.AllEntries.Select(e => e.Scope).Distinct().ToArray();

      return scopes.Length == 1 ? scopes[0] : null;
    }

    /// <summary>
    /// Reads the price per unit a listing counts at.
    /// </summary>
    /// <param name="listing">The listing to price.</param>
    /// <returns>What was paid for it once it has been bought, and what it is asked at before that.</returns>
    private static double ListingPrice(ResolvedListing listing)
    {
      return listing.Paid ?? listing.Price;
    }

    /// <summary>
    /// Formats the stack size of one listing.
    /// </summary>
    /// <param name="listing">The listing to format.</param>
    /// <returns>The stack size as it reads in the table.</returns>
    private static string ListingQtyText(ResolvedListing listing)
    {
      return $"{Count(listing.Quantity)} {(listing.Hq ? "HQ" : "NQ")}";
    }

    /// <summary>
    /// Formats how many items a group or an entry buys, and of what quality.
    /// </summary>
    /// <param name="roll">The figures the row draws.</param>
    /// <returns>The quantity as it reads in the table.</returns>
    private static string QtyText(RollUp roll)
    {
      if (!roll.Listed)
      {
        return NoValue;
      }

      if (roll.QuantityHq > 0 && roll.QuantityNq > 0)
      {
        return $"{Count(roll.QuantityNq)} NQ / {Count(roll.QuantityHq)} HQ";
      }

      return Count(roll.Quantity, roll.QuantityHq > 0);
    }

    /// <summary>
    /// Formats which world a group or an entry buys on.
    /// </summary>
    /// <param name="roll">The figures the row draws.</param>
    /// <returns>The world as it reads in the table.</returns>
    private static string WorldText(RollUp roll)
    {
      return roll.World.Length == 0 ? NoValue : roll.World;
    }

    /// <summary>
    /// Formats a stack size the way the table shows it.
    /// </summary>
    /// <param name="quantity">The stack size to format.</param>
    /// <returns>The stack size as it reads in the table.</returns>
    private static string Count(long quantity)
    {
      return quantity.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Formats a stack size, marking it when it is high quality.
    /// </summary>
    /// <param name="quantity">The stack size to format.</param>
    /// <param name="hq">True when the stack is high quality.</param>
    /// <returns>The stack size as it reads in the table.</returns>
    private static string Count(long quantity, bool hq)
    {
      return hq ? $"{Count(quantity)} {HqMark}" : Count(quantity);
    }

    /// <summary>
    /// Counts an entry's listings, saying how many of them are still on sale.
    /// </summary>
    /// <param name="entry">The entry to count.</param>
    /// <returns>The count as it reads in a tooltip.</returns>
    private static string ListingCountText(ListingEntry entry)
    {
      var total = entry.Matches.Count;
      var live = entry.Live.Count();

      var listings = total == 1 ? "1 listing" : $"{total} listings";

      return live == total ? listings : $"{listings}, {total - live} gone";
    }

    /// <summary>
    /// Spells out why a row bought nothing, for the tooltip on its name.
    /// </summary>
    /// <param name="reason">The row's own reason, or an empty string when it has none.</param>
    /// <param name="listings">The listings under the row.</param>
    /// <returns>The tooltip, or an empty string when the row has nothing to explain.</returns>
    private static string FailTooltip(string reason, IEnumerable<ResolvedListing> listings)
    {
      // A row over several listings only keeps a reason of its own when every listing gave the same one.
      var reasons = reason.Length > 0
        ? new[] { reason }
        : listings
          .Where(l => l.Outcome == BuyOutcome.Failed && l.FailReason.Length > 0)
          .GroupBy(l => l.FailReason, StringComparer.Ordinal)
          .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} listings: {g.Key}")
          .ToArray();

      return reasons.Length switch
      {
        0 => string.Empty,
        1 => $"Not bought: {reasons[0]}",
        _ => "Not bought:\n  " + string.Join("\n  ", reasons),
      };
    }

    /// <summary>
    /// Measures how wide a column has to be to hold its heading and all of its values.
    /// </summary>
    /// <param name="header">The column heading.</param>
    /// <param name="values">The values the column draws.</param>
    /// <returns>The width, in pixels.</returns>
    private static float ColumnWidth(string header, IEnumerable<string> values)
    {
      // The heading also keeps room for the sort arrow, which is what the column ends up as wide as.
      var widest = ImGui.CalcTextSize(header).X + ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.X;

      foreach (var value in values)
      {
        widest = Math.Max(widest, ImGui.CalcTextSize(value).X);
      }

      return widest;
    }

    /// <summary>
    /// Draws text pushed to the right edge of its column.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="width">The width the column was measured at.</param>
    private static void RightAligned(string text, float width)
    {
      // Padding out to the full cell would count as content and stop the column from ever fitting its values.
      var edge = Math.Min(ImGui.GetContentRegionAvail().X, width);
      var padding = edge - ImGui.CalcTextSize(text).X;

      if (padding > 0)
      {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
      }

      ImGui.Text(text);
    }

    /// <summary>
    /// Opens the entry a buy run has reached, so its listings can be watched as they are bought.
    /// </summary>
    private void FollowBuyRun()
    {
      var entry = this.plugin.ShoppingListBuyer.CurrentEntry;

      if (ReferenceEquals(entry, this.followed))
      {
        return;
      }

      this.followed = entry;

      if (entry != null && HasListingRows(entry))
      {
        this.expanded.Add(entry);
      }
    }

    /// <summary>
    /// Reads which column the table is being sorted by.
    /// </summary>
    private void UpdateSort()
    {
      var specs = ImGui.TableGetSortSpecs();

      if (specs.IsNull)
      {
        return;
      }

      this.sortColumn = -1;
      this.sortAscending = true;

      if (specs.SpecsCount > 0)
      {
        var spec = specs.Specs;
        this.sortColumn = spec.ColumnIndex;
        this.sortAscending = spec.SortDirection != ImGuiSortDirection.Descending;
      }

      specs.SpecsDirty = false;
    }

    /// <summary>
    /// Walks the values a column draws, at every level that is currently on screen.
    /// </summary>
    /// <param name="nodes">The groups to walk.</param>
    /// <param name="parentKey">The key of the group above, or an empty string at the top level.</param>
    /// <param name="forRow">Reads the value a group or an entry draws.</param>
    /// <param name="forListing">Reads the value one of an entry's listings draws.</param>
    /// <returns>Every value the column has to be wide enough for.</returns>
    private IEnumerable<string> ColumnValues(
      IReadOnlyList<ShoppingListNode> nodes,
      string parentKey,
      Func<RollUp, string> forRow,
      Func<ResolvedListing, string> forListing)
    {
      foreach (var node in nodes)
      {
        var key = Key(parentKey, NodeKey(node));

        yield return forRow(RollUp.Of(node));

        if (this.collapsed.Contains(key))
        {
          continue;
        }

        foreach (var value in this.ColumnValues(node.Children, key, forRow, forListing))
        {
          yield return value;
        }

        foreach (var entry in node.Entries)
        {
          yield return forRow(RollUp.Of(entry));

          if (!this.ShowsListings(entry))
          {
            continue;
          }

          foreach (var listing in entry.Matches)
          {
            yield return forListing(listing);
          }
        }
      }
    }

    /// <summary>
    /// Checks whether an entry's listings are on screen under it.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns>True when the listings are drawn as rows of their own.</returns>
    private bool ShowsListings(ListingEntry entry)
    {
      return HasListingRows(entry) && this.expanded.Contains(entry);
    }

    /// <summary>
    /// Puts groups in the order the table is sorted in.
    /// </summary>
    /// <param name="nodes">The groups to sort.</param>
    /// <returns>The groups, in the order they are drawn.</returns>
    private ShoppingListNode[] SortedNodes(IReadOnlyList<ShoppingListNode> nodes)
    {
      return this.sortColumn switch
      {
        0 => this.Ordered(nodes, n => n.Label, StringComparer.CurrentCultureIgnoreCase),
        1 => this.Ordered(nodes, n => n.Price, null),
        2 => this.Ordered(nodes, n => n.Quantity, null),
        3 => this.Ordered(nodes, n => n.Total, null),
        4 => this.Ordered(nodes, n => n.World, StringComparer.CurrentCultureIgnoreCase),

        // Unsorted, so they stay in the order they were built in, which is by their label.
        _ => nodes.ToArray(),
      };
    }

    /// <summary>
    /// Puts a group's entries in the order the table is sorted in.
    /// </summary>
    /// <param name="entries">The entries to sort.</param>
    /// <returns>The entries, in the order they are drawn.</returns>
    private ListingEntry[] SortedEntries(IReadOnlyList<ListingEntry> entries)
    {
      return this.sortColumn switch
      {
        0 => this.Ordered(entries, e => e.Summary, StringComparer.CurrentCultureIgnoreCase),
        1 => this.Ordered(entries, e => e.Price, null),
        2 => this.Ordered(entries, e => e.Quantity, null),
        3 => this.Ordered(entries, e => e.Total, null),
        4 => this.Ordered(entries, e => e.World, StringComparer.CurrentCultureIgnoreCase),

        // Unsorted, so they stay in the order they were added to the list.
        _ => entries.ToArray(),
      };
    }

    /// <summary>
    /// Puts an entry's listings in the order the table is sorted in.
    /// </summary>
    /// <param name="listings">The listings to sort.</param>
    /// <returns>The listings, in the order they are drawn.</returns>
    private ResolvedListing[] SortedListings(IReadOnlyList<ResolvedListing> listings)
    {
      return this.sortColumn switch
      {
        0 => this.Ordered(listings, l => l.RetainerName, StringComparer.CurrentCultureIgnoreCase),
        1 => this.Ordered(listings, l => ListingPrice(l), null),
        2 => this.Ordered(listings, l => l.Quantity, null),
        3 => this.Ordered(listings, l => ListingPrice(l) * l.Quantity, null),
        4 => this.Ordered(listings, l => l.World, StringComparer.CurrentCultureIgnoreCase),

        // Unsorted, so the cheapest listing leads: the one a buy run gets to first.
        _ => listings.OrderBy(l => l.Price).ToArray(),
      };
    }

    /// <summary>
    /// Orders rows by one of their figures, the way round the table is sorted.
    /// </summary>
    /// <typeparam name="TRow">The kind of row being ordered.</typeparam>
    /// <typeparam name="TKey">The figure they are ordered by.</typeparam>
    /// <param name="rows">The rows to order.</param>
    /// <param name="key">Reads the figure off a row.</param>
    /// <param name="comparer">How to compare two figures, or null for their own order.</param>
    /// <returns>The ordered rows.</returns>
    private TRow[] Ordered<TRow, TKey>(IEnumerable<TRow> rows, Func<TRow, TKey> key, IComparer<TKey>? comparer)
    {
      return this.sortAscending
        ? rows.OrderBy(key, comparer).ToArray()
        : rows.OrderByDescending(key, comparer).ToArray();
    }

    /// <summary>
    /// Draws one group, and everything under it when it is open.
    /// </summary>
    /// <param name="node">The group to draw.</param>
    /// <param name="key">Where the group sits in the tree.</param>
    /// <param name="depth">How far down the tree it sits.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    private void DrawNode(ShoppingListNode node, string key, int depth, ShoppingListRequest request)
    {
      var roll = RollUp.Of(node);
      var item = ContextItem(node);

      ImGui.TableNextRow();

      ImGui.TableSetColumnIndex(0);
      Indent(depth);

      var open = this.DrawNodeExpander(node, key);

      if (item.HasValue)
      {
        this.DrawItemIcon(item.Value, roll.Listed && roll.QuantityNq == 0);
      }

      ImGui.PushStyleColor(ImGuiCol.Text, this.NameColour(node.Outcome, roll.Listed || node.Refreshing));
      ImGui.Text(node.Label);
      ImGui.PopStyleColor();
      Utilities.HoverTooltip(FailTooltip(string.Empty, node.AllEntries.SelectMany(e => e.Matches)));

      if (item.HasValue)
      {
        this.DrawItemMenu(item.Value, ContextScope(node), key, request);
      }

      this.DrawRollUp(roll, null);
      this.DrawNodeActions(node, key);

      if (!open)
      {
        return;
      }

      foreach (var child in this.SortedNodes(node.Children))
      {
        this.DrawNode(child, Key(key, NodeKey(child)), depth + 1, request);
      }

      var index = 0;

      foreach (var entry in this.SortedEntries(node.Entries))
      {
        this.DrawEntry(entry, $"{key}~{index}", depth + 1, request);
        index += 1;
      }
    }

    /// <summary>
    /// Draws the twisty that shows what is under a group, leaving the cursor where its label goes.
    /// </summary>
    /// <param name="node">The group to draw the twisty for.</param>
    /// <param name="key">Where the group sits in the tree.</param>
    /// <returns>True when what is under the group is shown.</returns>
    private bool DrawNodeExpander(ShoppingListNode node, string key)
    {
      if (node.Children.Count == 0 && node.Entries.Count == 0)
      {
        NoExpander();
        return false;
      }

      var open = !this.collapsed.Contains(key);

      // The tree keeps track of what is open itself, so a rebuild of the nodes leaves them as they were.
      ImGui.SetNextItemOpen(open);

      if (ImGui.TreeNodeEx($"##shoplistnode{key}", ImGuiTreeNodeFlags.NoTreePushOnOpen) != open)
      {
        open = !open;

        if (open)
        {
          this.collapsed.Remove(key);
        }
        else
        {
          this.collapsed.Add(key);
        }
      }

      Utilities.HoverTooltip(open ? "Hide what is under this group." : "Show what is under this group.");

      ImGui.SameLine();

      return open;
    }

    /// <summary>
    /// Draws what can be done with a whole group.
    /// </summary>
    /// <param name="node">The group the buttons belong to.</param>
    /// <param name="key">Where the group sits in the tree.</param>
    private void DrawNodeActions(ShoppingListNode node, string key)
    {
      var buttonSize = ButtonSize();
      var busy = this.plugin.ShoppingListBulkAdd.IsRunning || this.plugin.ShoppingListBuyer.IsRunning;

      ImGui.TableSetColumnIndex(5);

      ImGui.BeginDisabled(busy);
      ImGui.PushFont(UiBuilder.IconFont);
      var refresh = ImGui.Button($"{(char)FontAwesomeIcon.SyncAlt}##shoplistnoderefresh{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip("Price everything under this group again.", ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      var canBuy = this.CanBuyAny(node, out var buyBlockedReason);

      ImGui.BeginDisabled(!canBuy);
      ImGui.PushFont(UiBuilder.IconFont);
      var buy = ImGui.Button($"{(char)FontAwesomeIcon.ShoppingCart}##shoplistnodebuy{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        canBuy ? $"Buy everything under this group, for {this.Gil(node.Total)} in all." : buyBlockedReason,
        ImGuiHoveredFlags.AllowWhenDisabled);

      if (refresh)
      {
        this.plugin.ShoppingListBulkAdd.StartRefresh(node.AllEntries.ToArray(), node.Label);
      }

      if (buy)
      {
        this.plugin.ShoppingListBuyer.BuyAll(node.AllEntries.ToArray());
      }
    }

    /// <summary>
    /// Checks whether anything under a group can be bought, and says why when nothing can.
    /// </summary>
    /// <param name="node">The group to check.</param>
    /// <param name="reason">Why nothing under the group can be bought, or an empty string when something can.</param>
    /// <returns>True when at least one entry under the group can be bought.</returns>
    private bool CanBuyAny(ShoppingListNode node, out string reason)
    {
      var blocked = string.Empty;

      foreach (var entry in node.AllEntries)
      {
        if (this.plugin.ShoppingListBuyer.CanBuy(entry, out var why))
        {
          reason = string.Empty;
          return true;
        }

        if (blocked.Length == 0)
        {
          blocked = why;
        }
      }

      reason = blocked;
      return false;
    }

    /// <summary>
    /// Draws the right click menu a group about one item carries.
    /// </summary>
    /// <param name="item">The item the group is about.</param>
    /// <param name="scope">The market the group shops in, or null when it reaches into several.</param>
    /// <param name="key">Where the group sits in the tree.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    private void DrawItemMenu(Item item, ListingScope? scope, string key, ShoppingListRequest request)
    {
      if (!ImGui.BeginPopupContextItem($"shoplistName{key}"))
      {
        return;
      }

      if (ImGui.Selectable("Copy name to clipboard"))
      {
        this.plugin.MarketBoardContext.CopyToClipboard(item.Name.ExtractText());
      }

      if (ImGui.Selectable("Add a conditional listing"))
      {
        // A group that is already one market's own files the new entry there; one that is not files it
        // where the scope picker is pointing.
        request.NewConditional = (item, scope ?? this.CurrentScope());
      }

      this.plugin.MarketBoardContext.DrawListsMenu(item.RowId);

      ImGui.EndPopup();
    }

    /// <summary>
    /// Draws one entry, and its listings when they are shown under it.
    /// </summary>
    /// <param name="entry">The entry to draw.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <param name="depth">How far down the tree it sits.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    private void DrawEntry(ListingEntry entry, string key, int depth, ShoppingListRequest request)
    {
      var roll = RollUp.Of(entry);
      var inline = InlineListing(entry);

      ImGui.TableNextRow();

      ImGui.TableSetColumnIndex(0);
      Indent(depth);

      var open = this.DrawEntryExpander(entry, key);

      ImGui.PushStyleColor(ImGuiCol.Text, this.NameColour(entry.Outcome, !entry.Unlisted));
      ImGui.Text(entry.Summary);
      ImGui.PopStyleColor();
      Utilities.HoverTooltip(FailTooltip(entry.FailReason, entry.Matches));

      if (inline != null)
      {
        // A direct entry already names its retainer in its summary.
        if (entry.Kind != ListingKind.Direct)
        {
          ImGui.SameLine();
          ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
          ImGui.Text(inline.RetainerName.Length > 0 ? inline.RetainerName : UnnamedRetainer);
          ImGui.PopStyleColor();
        }

        this.DrawListingStatus(inline);
      }

      this.DrawRollUp(roll, open ? null : entry);
      this.DrawEntryActions(entry, key, request);

      if (!open)
      {
        return;
      }

      var index = 0;

      foreach (var listing in this.SortedListings(entry.Matches))
      {
        this.DrawListing(entry, listing, $"{key}_{index}", depth + 1);
        index += 1;
      }
    }

    /// <summary>
    /// Draws the twisty that shows the listings an entry buys, leaving the cursor where its label goes.
    /// </summary>
    /// <param name="entry">The entry to draw the twisty for.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <returns>True when the entry's listings are shown under it.</returns>
    private bool DrawEntryExpander(ListingEntry entry, string key)
    {
      if (!HasListingRows(entry))
      {
        // An entry standing for one listing has nothing to open, but its label still lines up.
        NoExpander();
        return false;
      }

      var open = this.expanded.Contains(entry);

      ImGui.SetNextItemOpen(open);

      if (ImGui.TreeNodeEx($"##shoplistentry{key}", ImGuiTreeNodeFlags.NoTreePushOnOpen) != open)
      {
        open = !open;

        if (open)
        {
          this.expanded.Add(entry);
        }
        else
        {
          this.expanded.Remove(entry);
        }
      }

      Utilities.HoverTooltip(open
        ? "Hide the listings this entry buys."
        : $"Show the listings this entry buys. {ListingCountText(entry)} chosen.");

      ImGui.SameLine();

      return open;
    }

    /// <summary>
    /// Draws what can be done with one entry.
    /// </summary>
    /// <param name="entry">The entry the buttons belong to.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    private void DrawEntryActions(ListingEntry entry, string key, ShoppingListRequest request)
    {
      var buttonSize = ButtonSize();
      var busy = this.plugin.ShoppingListBulkAdd.IsRunning || this.plugin.ShoppingListBuyer.IsRunning;

      ImGui.TableSetColumnIndex(5);

      ImGui.BeginDisabled(busy);
      ImGui.PushFont(UiBuilder.IconFont);
      var refresh = ImGui.Button($"{(char)FontAwesomeIcon.SyncAlt}##shoplistrefresh{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip("Price this entry again.", ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      var canBuy = this.plugin.ShoppingListBuyer.CanBuy(entry, out var buyBlockedReason);

      ImGui.BeginDisabled(!canBuy);
      ImGui.PushFont(UiBuilder.IconFont);
      var buy = ImGui.Button($"{(char)FontAwesomeIcon.ShoppingCart}##shoplistbuy{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        canBuy ? this.BuyTooltip(entry) : buyBlockedReason,
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      this.DrawKindActions(entry, key, request, buttonSize, busy);

      // An entry whose listings sit on more than one world has nowhere single to walk to, so the
      // button is there but dead rather than picking a world on the entry's behalf.
      var worlds = entry.Worlds;
      var canTravel = this.plugin.MarketBoardContext.CanTravel;

      ImGui.BeginDisabled(worlds.Count != 1 || !canTravel);
      ImGui.PushFont(UiBuilder.IconFont);
      var travel = ImGui.Button($"{(char)FontAwesomeIcon.Walking}##shoplistgo{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        (canTravel, worlds.Count) switch
        {
          (false, _) => "Log in to a character to travel.",
          (_, 1) => $"Go to the market board on {worlds[0]}.",
          _ => "This entry has no one world to travel to.",
        },
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      ImGui.PushFont(UiBuilder.IconFont);
      var remove = ImGui.Button($"{(char)FontAwesomeIcon.TrashAlt}##shoplistdel{key}", buttonSize);
      ImGui.PopFont();
      Utilities.HoverTooltip("Take this entry off the list.");

      if (travel)
      {
        this.plugin.MarketBoardContext.GoToMarketBoard(worlds[0], entry.SourceItem, true);
      }

      if (refresh)
      {
        this.plugin.ShoppingListBulkAdd.StartRefresh(new[] { entry }, entry.SourceItem.Name.ExtractText());
      }

      if (buy)
      {
        this.plugin.ShoppingListBuyer.BuyOne(entry);
      }

      if (remove)
      {
        this.expanded.Remove(entry);
        this.plugin.ShoppingList.Remove(entry);
      }
    }

    /// <summary>
    /// Draws the two buttons that only some kinds of entry have, so the bin sits in the same place on
    /// every row.
    /// </summary>
    /// <param name="entry">The entry the buttons belong to.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <param name="request">Where a click that has to open a popup is written down.</param>
    /// <param name="buttonSize">How big one button is.</param>
    /// <param name="busy">True while a pricing or buy run is going.</param>
    private void DrawKindActions(ListingEntry entry, string key, ShoppingListRequest request, Vector2 buttonSize, bool busy)
    {
      if (entry.Kind == ListingKind.Lowest)
      {
        this.DrawCountButton(entry, key, buttonSize, busy);
        ImGui.SameLine();

        this.DrawQualityButton(entry, key, buttonSize, busy);
        ImGui.SameLine();

        return;
      }

      var conditional = entry.Kind == ListingKind.Conditional;

      ImGui.BeginDisabled(busy);
      ImGui.PushFont(UiBuilder.IconFont);
      var icon = conditional ? FontAwesomeIcon.Filter : FontAwesomeIcon.ListUl;
      var opened = ImGui.Button($"{(char)icon}##shoplistedit{key}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        conditional ? "Change what this entry buys." : "Choose which listing this entry buys.",
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      // The kinds with one button keep the gap, so every row's bin lines up with the ones above it.
      ImGui.Dummy(buttonSize);
      ImGui.SameLine();

      if (!opened)
      {
        return;
      }

      if (conditional)
      {
        request.Edit = entry;
      }
      else
      {
        request.PickListing = entry;
      }
    }

    /// <summary>
    /// Draws how many listings a lowest entry takes, as a button opening the box that changes it.
    /// </summary>
    /// <param name="entry">The entry the button belongs to.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <param name="buttonSize">How big one button is.</param>
    /// <param name="busy">True while a pricing or buy run is going.</param>
    private void DrawCountButton(ListingEntry entry, string key, Vector2 buttonSize, bool busy)
    {
      var popupId = $"shoplistcountpop{key}";

      ImGui.BeginDisabled(busy);
      var open = ImGui.Button(FormattableString.Invariant($"x{entry.Count}##shoplistcount{key}"), buttonSize);
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        entry.Count == 1
          ? "Buys the cheapest listing. Click to buy more of them."
          : FormattableString.Invariant($"Buys the {entry.Count} cheapest listings. Click to change."),
        ImGuiHoveredFlags.AllowWhenDisabled);

      if (open)
      {
        this.countEditing = entry;
        this.countDraft = entry.Count;
        ImGui.OpenPopup(popupId);
      }

      if (ImGui.BeginPopup(popupId))
      {
        this.DrawCountPopup(popupId, buttonSize);
        ImGui.EndPopup();
      }
      else if (this.countEditing == entry)
      {
        // The box closing is what settles the number, so a count being typed is never read half
        // written and trimmed down to what its first digit said.
        var grew = this.countDraft > entry.Count;

        this.plugin.ShoppingList.SetCount(entry, this.countDraft);
        this.countEditing = null;

        // Asking for fewer is served out of what the entry already holds, so only asking for more
        // has anything to fetch.
        if (grew)
        {
          this.repriceWanted.Add(entry);
        }
      }
    }

    /// <summary>
    /// Draws the box that changes how many listings a lowest entry takes.
    /// </summary>
    /// <param name="popupId">What the box is identified by.</param>
    /// <param name="buttonSize">How big one button is.</param>
    private void DrawCountPopup(string popupId, Vector2 buttonSize)
    {
      ImGui.TextUnformatted("Listings to buy");

      ImGui.BeginDisabled(this.countDraft <= 1);
      var fewer = ImGui.Button($"-##{popupId}fewer", buttonSize);
      ImGui.EndDisabled();

      ImGui.SameLine();

      ImGui.SetNextItemWidth(2 * buttonSize.X);
      var typed = this.countDraft;

      if (ImGui.InputInt($"##{popupId}count", ref typed, 0, 0))
      {
        this.countDraft = typed;
      }

      ImGui.SameLine();

      if (ImGui.Button($"+##{popupId}more", buttonSize))
      {
        this.countDraft++;
      }

      if (fewer)
      {
        this.countDraft--;
      }

      this.countDraft = Math.Max(1, this.countDraft);
    }

    /// <summary>
    /// Draws which qualities a lowest entry takes, as a button cycling through them.
    /// </summary>
    /// <param name="entry">The entry the button belongs to.</param>
    /// <param name="key">Where the entry sits in the tree.</param>
    /// <param name="buttonSize">How big one button is.</param>
    /// <param name="busy">True while a pricing or buy run is going.</param>
    private void DrawQualityButton(ListingEntry entry, string key, Vector2 buttonSize, bool busy)
    {
      ImGui.BeginDisabled(busy);
      var cycled = ImGui.Button($"{entry.Quality.Label()}##shoplistquality{key}", buttonSize);
      ImGui.EndDisabled();

      if (this.repriceWanted.Contains(entry) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      {
        this.repriceHeld = entry;
      }

      Utilities.HoverTooltip(
        entry.Quality switch
        {
          QualityFilter.HqOnly => "Buys high quality listings only. Click for normal quality.",
          QualityFilter.NqOnly => "Buys normal quality listings only. Click for either quality.",
          _ => "Buys either quality. Click for high quality only.",
        },
        ImGuiHoveredFlags.AllowWhenDisabled);

      if (!cycled)
      {
        return;
      }

      this.plugin.ShoppingList.SetQuality(entry, entry.Quality.Next());
      this.repriceWanted.Add(entry);
      this.repriceHeld = entry;
    }

    /// <summary>
    /// Drops what is being held about entries that are no longer on the list.
    /// </summary>
    /// <param name="nodes">The groups being drawn.</param>
    private void ForgetDroppedEntries(IReadOnlyList<ShoppingListNode> nodes)
    {
      if (this.countEditing == null && this.repriceWanted.Count == 0)
      {
        return;
      }

      var live = nodes.SelectMany(n => n.AllEntries).ToHashSet();

      if (this.countEditing != null && !live.Contains(this.countEditing))
      {
        this.countEditing = null;
      }

      this.repriceWanted.IntersectWith(live);
    }

    /// <summary>
    /// Prices the entries changed on their own row again, once each has settled.
    /// </summary>
    /// <remarks>
    /// An entry whose quality button is still under the pointer waits, which is what makes a walk
    /// from either quality round to high and on to normal cost one pricing rather than three. A row
    /// scrolled out of sight settles too, since a button that is not drawn cannot be hovered.
    /// </remarks>
    private void SettleReprice()
    {
      if (this.repriceWanted.Count == 0)
      {
        return;
      }

      if (this.plugin.ShoppingListBulkAdd.IsRunning || this.plugin.ShoppingListBuyer.IsRunning)
      {
        // A run that started in the meantime holds the pricing back rather than dropping it.
        return;
      }

      var settled = this.repriceWanted.Where(e => e != this.repriceHeld).ToArray();

      if (settled.Length == 0)
      {
        return;
      }

      this.repriceWanted.ExceptWith(settled);
      this.plugin.ShoppingListBulkAdd.StartRefresh(
        settled,
        settled.Length == 1 ? settled[0].SourceItem.Name.ExtractText() : null);
    }

    /// <summary>
    /// Draws one of an entry's listings on a row of its own.
    /// </summary>
    /// <param name="entry">The entry the listing belongs to.</param>
    /// <param name="listing">The listing to draw.</param>
    /// <param name="id">What the listing's buttons are identified by.</param>
    /// <param name="depth">How far down the tree it sits.</param>
    private void DrawListing(ListingEntry entry, ResolvedListing listing, string id, int depth)
    {
      var price = ListingPrice(listing);
      var bargain = listing.Paid.HasValue && listing.Paid.Value < listing.Price;
      var dim = listing.Gone || listing.Outcome == BuyOutcome.Failed;
      var gil = dim ? this.theme.TextDim : bargain ? this.theme.BuyBargain : this.theme.GilText;

      ImGui.TableNextRow();

      ImGui.TableSetColumnIndex(0);
      Indent(depth);
      ImGui.PushStyleColor(ImGuiCol.Text, dim ? this.theme.TextDim : this.theme.Text);
      ImGui.Text(listing.RetainerName.Length > 0 ? listing.RetainerName : UnnamedRetainer);
      ImGui.PopStyleColor();

      this.DrawListingStatus(listing);

      ImGui.TableSetColumnIndex(1);
      ImGui.PushStyleColor(ImGuiCol.Text, gil);
      RightAligned(this.Gil(price), this.priceWidth);
      ImGui.PopStyleColor();

      if (bargain)
      {
        Utilities.HoverTooltip($"Picked at {this.Gil(listing.Price)}, bought at {this.Gil(price)}.");
      }

      ImGui.TableSetColumnIndex(2);
      ImGui.PushStyleColor(ImGuiCol.Text, dim ? this.theme.TextDim : this.theme.TextBright);
      RightAligned(ListingQtyText(listing), this.qtyWidth);
      ImGui.PopStyleColor();

      ImGui.TableSetColumnIndex(3);
      ImGui.PushStyleColor(ImGuiCol.Text, gil);
      RightAligned(this.Gil(price * listing.Quantity), this.totalWidth);
      ImGui.PopStyleColor();

      ImGui.TableSetColumnIndex(4);
      ImGui.PushStyleColor(ImGuiCol.Text, dim ? this.theme.TextDim : this.theme.Text);
      RightAligned(listing.World, this.worldWidth);
      ImGui.PopStyleColor();

      ImGui.TableSetColumnIndex(5);

      // A listing that has sold out from under the entry can only be dismissed.
      var drop = listing.Gone
        ? this.DrawGoneAction(id)
        : this.DrawListingActions(entry, listing, id);

      if (drop)
      {
        entry.Matches.Remove(listing);
        this.plugin.ShoppingList.Persist();
      }
    }

    /// <summary>
    /// Says where a listing has got to, from waiting its turn in a buy run to what it cost.
    /// </summary>
    /// <param name="listing">The listing to report on.</param>
    private void DrawListingStatus(ResolvedListing listing)
    {
      var status = this.ListingStatus(listing);

      if (status.Text.Length == 0)
      {
        return;
      }

      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, status.Colour);
      ImGui.Text(status.Text);
      ImGui.PopStyleColor();
      Utilities.HoverTooltip(listing.FailReason);
    }

    /// <summary>
    /// Works out what a listing's status reads as.
    /// </summary>
    /// <param name="listing">The listing to report on.</param>
    /// <returns>The text to draw and the colour to draw it in.</returns>
    private (string Text, uint Colour) ListingStatus(ResolvedListing listing)
    {
      var buyer = this.plugin.ShoppingListBuyer;

      if (ReferenceEquals(buyer.CurrentListing, listing))
      {
        return ("Buying...", this.theme.Accent);
      }

      if (buyer.IsQueued(listing))
      {
        return ("Queued", this.theme.TextDim);
      }

      return listing.Outcome switch
      {
        BuyOutcome.Bought => ("Bought", this.theme.BuySuccess),
        BuyOutcome.BoughtCheaper => ("Bought cheaper", this.theme.BuyBargain),
        BuyOutcome.Failed => ("Not bought", this.theme.BuyFailed),
        _ => listing.Gone ? ("Gone", this.theme.TextDim) : (string.Empty, this.theme.TextDim),
      };
    }

    /// <summary>
    /// Draws the button that takes a listing that has sold out off its entry.
    /// </summary>
    /// <param name="id">What the button is identified by.</param>
    /// <returns>True when the listing is to be taken off the entry.</returns>
    private bool DrawGoneAction(string id)
    {
      var buttonSize = ButtonSize();
      var busy = this.plugin.ShoppingListBulkAdd.IsRunning || this.plugin.ShoppingListBuyer.IsRunning;

      // Kept under the bin the listings that are still on sale have, since it does the same thing.
      ImGui.Dummy(buttonSize);
      ImGui.SameLine();
      ImGui.Dummy(buttonSize);
      ImGui.SameLine();

      ImGui.BeginDisabled(busy);
      ImGui.PushFont(UiBuilder.IconFont);
      var dismissed = ImGui.Button($"{(char)FontAwesomeIcon.Check}##shoplistlistinggone{id}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip("Take this sold out listing off the entry.", ImGuiHoveredFlags.AllowWhenDisabled);

      return dismissed;
    }

    /// <summary>
    /// Draws what can be done with one of an entry's listings on its own.
    /// </summary>
    /// <param name="entry">The entry the listing belongs to.</param>
    /// <param name="listing">The listing to draw the buttons for.</param>
    /// <param name="id">What the buttons are identified by.</param>
    /// <returns>True when the listing is to be taken off the entry.</returns>
    private bool DrawListingActions(ListingEntry entry, ResolvedListing listing, string id)
    {
      var buttonSize = ButtonSize();
      var busy = this.plugin.ShoppingListBulkAdd.IsRunning || this.plugin.ShoppingListBuyer.IsRunning;
      var canBuy = this.plugin.ShoppingListBuyer.CanBuy(entry, out var buyBlockedReason);

      ImGui.BeginDisabled(!canBuy);
      ImGui.PushFont(UiBuilder.IconFont);
      var buy = ImGui.Button($"{(char)FontAwesomeIcon.ShoppingCart}##shoplistlistingbuy{id}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        canBuy
          ? $"Buy just this listing, for {this.Gil(ListingPrice(listing) * listing.Quantity)}."
          : buyBlockedReason,
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      var canTravel = this.plugin.MarketBoardContext.CanTravel;

      ImGui.BeginDisabled(listing.World.Length == 0 || !canTravel);
      ImGui.PushFont(UiBuilder.IconFont);
      var travel = ImGui.Button($"{(char)FontAwesomeIcon.Walking}##shoplistlistinggo{id}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        canTravel ? $"Go to the market board on {listing.World}." : "Log in to a character to travel.",
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      ImGui.BeginDisabled(busy);
      ImGui.PushFont(UiBuilder.IconFont);
      var dropped = ImGui.Button($"{(char)FontAwesomeIcon.TrashAlt}##shoplistlistingdel{id}", buttonSize);
      ImGui.PopFont();
      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        entry.Kind == ListingKind.Direct
          ? "Stop buying this listing."
          : "Stop buying this listing until the entry is priced again and picks it up.",
        ImGuiHoveredFlags.AllowWhenDisabled);

      if (travel)
      {
        this.plugin.MarketBoardContext.GoToMarketBoard(listing.World, entry.SourceItem, true);
      }

      if (buy)
      {
        this.plugin.ShoppingListBuyer.BuyListing(entry, listing);
      }

      return dropped;
    }

    /// <summary>
    /// Draws the price, quantity, total and world a row rolled up.
    /// </summary>
    /// <param name="roll">The figures the row draws.</param>
    /// <param name="hovered">The entry whose listings to spell out on hover, or null for none.</param>
    private void DrawRollUp(RollUp roll, ListingEntry? hovered)
    {
      var dim = roll.Refreshing || !roll.Listed;
      var gil = dim ? this.theme.TextDim : this.theme.GilText;

      ImGui.TableSetColumnIndex(1);
      ImGui.PushStyleColor(ImGuiCol.Text, gil);
      RightAligned(this.PriceText(roll), this.priceWidth);
      ImGui.PopStyleColor();
      this.ListingTooltip(hovered);

      ImGui.TableSetColumnIndex(2);
      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
      RightAligned(QtyText(roll), this.qtyWidth);
      ImGui.PopStyleColor();
      this.ListingTooltip(hovered);

      ImGui.TableSetColumnIndex(3);
      ImGui.PushStyleColor(ImGuiCol.Text, gil);
      RightAligned(this.TotalText(roll), this.totalWidth);
      ImGui.PopStyleColor();
      this.ListingTooltip(hovered);

      ImGui.TableSetColumnIndex(4);
      ImGui.PushStyleColor(ImGuiCol.Text, dim ? this.theme.TextDim : this.theme.Text);
      RightAligned(WorldText(roll), this.worldWidth);
      ImGui.PopStyleColor();
      this.ListingTooltip(hovered);
    }

    /// <summary>
    /// Spells out an entry's listings while the cursor is over one of its figures.
    /// </summary>
    /// <param name="entry">The entry being hovered, or null when the row has nothing to spell out.</param>
    private void ListingTooltip(ListingEntry? entry)
    {
      // An open entry already has all of this under it.
      if (entry == null || !HasListingRows(entry) || !ImGui.IsItemHovered())
      {
        return;
      }

      ImGui.BeginTooltip();

      foreach (var listing in this.SortedListings(entry.Matches))
      {
        var price = listing.Price.ToString("N0", CultureInfo.CurrentCulture);

        // The entry only marks itself high quality when every listing is, so each one says for itself.
        var quality = listing.Hq ? $" {HqMark}" : string.Empty;
        var line = $"{listing.Quantity}{quality} @ {price} on {listing.World} ({listing.RetainerName})";

        ImGui.PushStyleColor(ImGuiCol.Text, listing.Gone ? this.theme.TextDim : this.theme.Text);
        ImGui.Text(listing.Gone ? $"{line} - gone" : line);
        ImGui.PopStyleColor();
      }

      ImGui.EndTooltip();
    }

    /// <summary>
    /// Draws an item's game icon at text height and leaves the cursor on the same line as the name.
    /// </summary>
    /// <param name="item">The item to draw the icon of.</param>
    /// <param name="hq">True to draw the high quality icon.</param>
    private void DrawItemIcon(Item item, bool hq)
    {
      var size = new Vector2(ImGui.GetTextLineHeight());

      using var icon = this.plugin.TextureProvider.GetFromGameIcon(new GameIconLookup
      {
        IconId = item.Icon,
        ItemHq = hq,
      }).GetWrapOrDefault();

      if (icon == null)
      {
        // The name still lines up with the rows that did get an icon.
        ImGui.Dummy(size);
      }
      else
      {
        ImGui.Image(icon.Handle, size);
      }

      ImGui.SameLine();
    }

    /// <summary>
    /// Works out what colour a row's name is drawn in.
    /// </summary>
    /// <param name="outcome">How the last buy attempt on the row ended.</param>
    /// <param name="listed">True when the row still has something to buy.</param>
    /// <returns>The colour.</returns>
    private uint NameColour(BuyOutcome outcome, bool listed)
    {
      return outcome switch
      {
        BuyOutcome.Bought => this.theme.BuySuccess,
        BuyOutcome.BoughtCheaper => this.theme.BuyBargain,

        // A row that only partly bought still wants looking at, so it reads like one that did not.
        BuyOutcome.Failed or BuyOutcome.PartlyBought => this.theme.BuyFailed,
        _ => listed ? this.theme.Text : this.theme.TextDim,
      };
    }

    /// <summary>
    /// Builds the market the scope picker is currently pointing at.
    /// </summary>
    /// <returns>The market a new entry would be filed under.</returns>
    private ListingScope CurrentScope()
    {
      return this.plugin.ShoppingListScope.ToListingScope();
    }

    /// <summary>
    /// Says what the buy button on an entry would do.
    /// </summary>
    /// <param name="entry">The entry the button belongs to.</param>
    /// <returns>The tooltip text.</returns>
    private string BuyTooltip(ListingEntry entry)
    {
      var live = entry.Live.Count();
      var listings = live == 1 ? "the 1 listing" : $"the {live} listings";

      return $"Buy {listings} this entry buys, for {this.Gil(entry.Total)} in all.";
    }

    /// <summary>
    /// Formats the cheapest price per unit a row buys at.
    /// </summary>
    /// <param name="roll">The figures the row draws.</param>
    /// <returns>The text to draw in the price cell.</returns>
    private string PriceText(RollUp roll)
    {
      if (roll.Refreshing)
      {
        return "Refreshing";
      }

      return roll.Listed ? this.Gil(roll.Price) : NoValue;
    }

    /// <summary>
    /// Formats the gil everything a row buys costs.
    /// </summary>
    /// <param name="roll">The figures the row draws.</param>
    /// <returns>The text to draw in the total cell.</returns>
    private string TotalText(RollUp roll)
    {
      if (roll.Refreshing)
      {
        return "Refreshing";
      }

      return roll.Listed ? this.Gil(roll.Total) : NoValue;
    }

    /// <summary>
    /// Formats an amount of gil the way the rest of the window shows it.
    /// </summary>
    /// <param name="value">The amount to format.</param>
    /// <returns>The amount as it reads in the table.</returns>
    private string Gil(double value)
    {
      return this.plugin.Config.PriceIconShown
        ? value.ToString("C", this.plugin.NumberFormatInfo)
        : value.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// What a group or an entry reads as in the price, quantity, total and world columns.
    /// </summary>
    /// <remarks>
    /// A group works its figures out the same way an entry does, so both kinds of row are drawn and
    /// measured by the same code once they have been read off into this.
    /// </remarks>
    private readonly struct RollUp
    {
      private RollUp(double price, long quantity, long quantityHq, long quantityNq, double total, string world, bool refreshing)
      {
        this.Price = price;
        this.Quantity = quantity;
        this.QuantityHq = quantityHq;
        this.QuantityNq = quantityNq;
        this.Total = total;
        this.World = world;
        this.Refreshing = refreshing;
      }

      /// <summary>Gets the cheapest price per unit the row buys at.</summary>
      public double Price { get; }

      /// <summary>Gets how many items the row buys in all.</summary>
      public long Quantity { get; }

      /// <summary>Gets how many of them come from high quality listings.</summary>
      public long QuantityHq { get; }

      /// <summary>Gets how many of them come from normal quality listings.</summary>
      public long QuantityNq { get; }

      /// <summary>Gets the gil the row costs in all.</summary>
      public double Total { get; }

      /// <summary>Gets the world the row buys on, or a count when it buys on several.</summary>
      public string World { get; }

      /// <summary>Gets a value indicating whether the row is waiting for a new price.</summary>
      public bool Refreshing { get; }

      /// <summary>Gets a value indicating whether the row has anything left to buy.</summary>
      public bool Listed => this.Quantity > 0;

      /// <summary>
      /// Reads a group's figures.
      /// </summary>
      /// <param name="node">The group to read.</param>
      /// <returns>The figures its row draws.</returns>
      public static RollUp Of(ShoppingListNode node)
      {
        return new RollUp(
          node.Price,
          node.Quantity,
          node.QuantityHq,
          node.QuantityNq,
          node.Total,
          node.World,
          node.Refreshing);
      }

      /// <summary>
      /// Reads an entry's figures.
      /// </summary>
      /// <param name="entry">The entry to read.</param>
      /// <returns>The figures its row draws.</returns>
      public static RollUp Of(ListingEntry entry)
      {
        return new RollUp(
          entry.Price,
          entry.Quantity,
          entry.QuantityHq,
          entry.QuantityNq,
          entry.Total,
          entry.World,
          entry.Refreshing);
      }
    }
  }
}
