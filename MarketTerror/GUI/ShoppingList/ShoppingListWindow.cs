// <copyright file="ShoppingListWindow.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI.ShoppingList
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Numerics;
  using System.Text;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Interface.Windowing;
  using MarketTerror.GUI.Components;
  using MarketTerror.GUI.Theme;
  using MarketTerror.Helpers;
  using MarketTerror.Models;
  using MarketTerror.Models.ShoppingList;

  /// <summary>
  /// The buy list window: the bars around the tree, the progress lines, and the popups the tree asks for.
  /// </summary>
  /// <remarks>
  /// The rows themselves belong to <see cref="ShoppingListTree"/>. The window hands it the frame's
  /// nodes and a request object, then acts on whatever the tree wrote into that request, so nothing
  /// is opened halfway through drawing the table.
  /// </remarks>
  public sealed class ShoppingListWindow : Window, IDisposable
  {
    /// <summary>
    /// What a copied line shows in place of a price or a world it does not have.
    /// </summary>
    private const string NoValue = "-";

    /// <summary>
    /// What the scope pickers are labelled with, so it is clear they file rather than filter.
    /// </summary>
    private const string ScopeLabel = "New entries:";

    /// <summary>
    /// What the scope pickers say for themselves.
    /// </summary>
    private const string ScopeNote =
      "The market new entries are filed under.\nIt does not filter what the list already shows.";

    /// <summary>
    /// The unscaled size the window will not go below.
    /// </summary>
    private static readonly Vector2 MinWindowSize = new Vector2(560, 150);

    private readonly TerrorTheme theme;

    private readonly WorldPicker worldPicker = new WorldPicker("shoppingListWorld");

    private readonly ListingPicker picker;

    private readonly ShoppingListTree tree;

    private readonly ConditionEditor editor;

    private readonly ShoppingListRequest request = new ShoppingListRequest();

    /// <summary>
    /// The grouped list the frame draws, rebuilt only when the list or the grouping setting moves.
    /// </summary>
    private IReadOnlyList<ShoppingListNode> nodes = Array.Empty<ShoppingListNode>();

    private int nodesRevision = -1;

    private ShoppingListGrouping nodesGrouping;

    private IDisposable? themeScope;

    private bool isDisposed;

    private bool forceShown;

    private bool hidden;

    private int lastCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingListWindow"/> class.
    /// </summary>
    /// <param name="plugin">The <see cref="MarketTerrorPlugin"/>.</param>
    public ShoppingListWindow(MarketTerrorPlugin plugin)
      : base("Market Terror Shopping List")
    {
      this.Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

      this.Flags = ImGuiWindowFlags.NoScrollbar;
      this.IsOpen = true;
      this.RespectCloseHotkey = false;
      this.ShowCloseButton = true;
      this.Size = MinWindowSize;
      this.SizeCondition = ImGuiCond.FirstUseEver;

      this.theme = new TerrorTheme(this.Plugin.Config);
      this.picker = new ListingPicker(this.Plugin);
      this.tree = new ShoppingListTree(this.Plugin);
      this.editor = new ConditionEditor(this.Plugin);
    }

    /// <summary>
    /// Gets a value indicating whether the window is currently on screen.
    /// </summary>
    public bool IsShown =>
      !this.hidden
      && (this.Plugin.ShoppingList.Count > 0 || this.forceShown || this.Plugin.ShoppingListBulkAdd.IsRunning || this.Plugin.ShoppingListBuyer.IsRunning);

    private MarketTerrorPlugin Plugin { get; init; }

    /// <summary>
    /// Shows the window, or hides it when it is already shown.
    /// </summary>
    /// <remarks>The window is always open; what it draws is decided by <see cref="DrawConditions"/>.</remarks>
    public void ToggleShown()
    {
      this.hidden = this.IsShown;
      this.forceShown = !this.hidden;
    }

    /// <summary>
    /// Brings the window up, whether or not it was hidden.
    /// </summary>
    /// <remarks>
    /// What put an entry on the list is not always a change in how many there are, so this is how a
    /// click that landed on an entry already there still shows it.
    /// </remarks>
    public void Show()
    {
      this.hidden = false;
      this.forceShown = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      if (this.isDisposed)
      {
        return;
      }

      this.themeScope?.Dispose();
      this.themeScope = null;
      this.picker.Dispose();
      this.editor.Dispose();
      this.isDisposed = true;
    }

    /// <inheritdoc/>
    public override void OnClose()
    {
      this.hidden = true;
      this.forceShown = false;

      // The window stays open so a newly added entry can bring it back.
      this.IsOpen = true;
    }

    /// <inheritdoc/>
    public override void PreDraw()
    {
      // The action buttons grow with the font scale, so the width the window may not go below grows with it too.
      var scale = ImGui.GetIO().FontGlobalScale;

      this.SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = MinWindowSize * scale,
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
      };

      this.themeScope = this.theme.Push();
    }

    /// <inheritdoc/>
    public override void PostDraw()
    {
      this.themeScope?.Dispose();
      this.themeScope = null;
    }

    /// <inheritdoc/>
    public override bool DrawConditions()
    {
      var count = this.Plugin.ShoppingList.Count;

      // A newly added entry brings the window back even after it was hidden.
      if (count > this.lastCount)
      {
        this.hidden = false;
      }

      this.lastCount = count;

      return this.IsShown;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
      Utilities.RainbowText("EXPERIMENTAL");
      ImGui.SameLine(0, 0);
      ImGui.TextDisabled(" - use at your own risk");

      this.DrawBulkAddProgress();
      this.DrawBuyProgress();

      // Drawn before the table so a frame the table cannot open does not take the popups down with it.
      this.picker.Draw(this.theme);
      this.editor.Draw(this.theme);

      if (this.Plugin.ShoppingList.Count == 0)
      {
        this.DrawScopePickers(false);
        ImGui.Separator();

        if (!this.Plugin.ShoppingListBulkAdd.IsRunning)
        {
          ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
          ImGui.TextWrapped("Your buy list is empty. Add items from the item list right click menu.");
          ImGui.PopStyleColor();
        }

        return;
      }

      this.DrawActionBar();

      var bought = this.Plugin.ShoppingList.Count(WasBought);
      var failed = this.Plugin.ShoppingList.Count(BuyFailed);

      // The footer keeps its own row pinned under the table, so it stays put while the list scrolls.
      var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;

      if (bought + failed > 0)
      {
        footerHeight += ImGui.GetFrameHeightWithSpacing();
      }

      var nodes = this.Nodes();

      this.tree.Draw(this.theme, nodes, this.request, -footerHeight);

      if (this.request.Edit != null)
      {
        this.editor.Open(this.request.Edit.SourceItem, this.request.Edit.Scope, this.request.Edit.Conditions, this.request.Edit);
        this.request.Edit = null;
      }

      if (this.request.PickListing != null)
      {
        this.picker.Open(this.request.PickListing);
        this.request.PickListing = null;
      }

      if (this.request.NewConditional != null)
      {
        var (item, scope) = this.request.NewConditional.Value;
        this.editor.Open(item, scope, null, null);
        this.request.NewConditional = null;
      }

      if (this.request.NewDirect != null)
      {
        var (item, scope) = this.request.NewDirect.Value;
        this.picker.Open(item, scope);
        this.request.NewDirect = null;
      }

      ImGui.Separator();

      if (bought + failed > 0)
      {
        this.DrawOutcomeBar(bought, failed);
      }

      this.DrawFooter();
    }

    /// <summary>
    /// Sums up the last pricing job, or an empty string while none has run.
    /// </summary>
    /// <param name="stats">The stats of the last finished job.</param>
    /// <returns>The summary text.</returns>
    private static string QuerySummary(QueryStats? stats)
    {
      if (stats == null || stats.Items == 0)
      {
        return string.Empty;
      }

      var items = stats.Items == 1 ? "1 item" : $"{stats.Items.ToString("N0", CultureInfo.CurrentCulture)} items";
      var queries = stats.Queries == 1 ? "1 query" : $"{stats.Queries.ToString("N0", CultureInfo.CurrentCulture)} queries";
      var milliseconds = stats.Milliseconds.ToString("N0", CultureInfo.CurrentCulture);

      return $"{items} over {queries} @ {stats.Scope} in {milliseconds} ms";
    }

    /// <summary>
    /// Checks whether an entry was bought by the last buy run.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns>True when the entry was bought.</returns>
    private static bool WasBought(ListingEntry entry)
    {
      return entry.Outcome is BuyOutcome.Bought or BuyOutcome.BoughtCheaper;
    }

    /// <summary>
    /// Checks whether an entry was tried by the last buy run and did not come away with everything.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns>True when the entry was not bought, or only partly bought.</returns>
    private static bool BuyFailed(ListingEntry entry)
    {
      return entry.Outcome is BuyOutcome.Failed or BuyOutcome.PartlyBought;
    }

    /// <summary>
    /// Gets the grouped list the frame draws, building it again only when it can have changed.
    /// </summary>
    /// <returns>The top level groups, in the order they are drawn.</returns>
    private IReadOnlyList<ShoppingListNode> Nodes()
    {
      var store = this.Plugin.ShoppingList;
      var grouping = this.Plugin.Config.ShoppingListGrouping;

      if (this.nodesRevision == store.Revision && this.nodesGrouping == grouping)
      {
        return this.nodes;
      }

      this.nodes = ShoppingListNodes.Build(store, this.Plugin.WorldCatalogue, grouping);
      this.nodesRevision = store.Revision;
      this.nodesGrouping = grouping;

      return this.nodes;
    }

    /// <summary>
    /// Draws the world and scope combos that say where new entries go.
    /// </summary>
    /// <param name="sameLine">
    /// True to right-align them at the end of the action bar, false to give them a row of their own.
    /// </param>
    private void DrawScopePickers(bool sameLine)
    {
      var scope = this.Plugin.ShoppingListScope;
      var scale = ImGui.GetIO().FontGlobalScale;
      var showWorld = this.Plugin.Config.WorldOverridesEnabled;
      var spacing = showWorld ? ImGui.GetStyle().ItemSpacing.X : 0.0f;

      if (sameLine)
      {
        ImGui.SameLine();
      }
      else
      {
        // A row to itself has the space for the combos to say what they are for.
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
        ImGui.Text(ScopeLabel);
        ImGui.PopStyleColor();
        Utilities.HoverTooltip(ScopeNote);
        ImGui.SameLine();
      }

      var available = ImGui.GetContentRegionAvail().X;

      // The scope combo names the world, data centre or region new entries are filed under, so it has to fit that.
      var scopeWidth = Math.Max(
        140 * scale,
        ImGui.CalcTextSize(scope.SelectedDisplayName).X + ImGui.GetFrameHeight() + (ImGui.GetStyle().FramePadding.X * 2));

      var worldWidth = 0.0f;

      if (showWorld)
      {
        worldWidth = sameLine ? 150 * scale : available - scopeWidth - spacing;
      }
      else if (!sameLine)
      {
        scopeWidth = available;
      }

      if (scopeWidth + worldWidth + spacing > available)
      {
        // Share what is left rather than spilling out of the window.
        var share = Math.Max(available - spacing, 80 * scale);
        scopeWidth = showWorld ? share * 0.5f : share;
        worldWidth = share - scopeWidth;
      }

      var padding = available - worldWidth - scopeWidth - spacing;
      if (padding > 0)
      {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
      }

      ImGui.BeginDisabled(this.Plugin.ShoppingListBulkAdd.IsRunning);

      if (showWorld)
      {
        ImGui.SetNextItemWidth(worldWidth);
        this.worldPicker.Draw(scope, this.theme);

        ImGui.SameLine();
      }

      ImGui.SetNextItemWidth(scopeWidth);
      ScopePicker.Draw("##shoppingListScope", scope);
      Utilities.HoverTooltip(ScopeNote, ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.EndDisabled();
    }

    private void DrawActionBar()
    {
      var busy = this.Plugin.ShoppingListBulkAdd.IsRunning;
      var buyer = this.Plugin.ShoppingListBuyer;

      ImGui.BeginDisabled(busy || buyer.IsRunning);

      if (ImGui.Button("Refresh all"))
      {
        this.Plugin.ShoppingListBulkAdd.StartRefresh(this.Plugin.ShoppingList.ToArray());
      }

      ImGui.EndDisabled();
      Utilities.HoverTooltip("Price every entry on the list again, each in the market it shops in.");

      ImGui.SameLine();

      var loggedIn = this.Plugin.ClientState.IsLoggedIn;

      ImGui.BeginDisabled(busy || buyer.IsRunning || !this.Plugin.Config.ShoppingListBuyEnabled || !loggedIn);

      if (ImGui.Button("Buy all"))
      {
        buyer.BuyAll(this.Plugin.ShoppingList.ToArray());
      }

      ImGui.EndDisabled();
      Utilities.HoverTooltip(
        loggedIn
          ? "Buy every listing still on sale under every entry, closest worlds first."
          : "Log in to a character to buy.",
        ImGuiHoveredFlags.AllowWhenDisabled);

      ImGui.SameLine();

      if (ImGui.Button("Copy"))
      {
        ImGui.OpenPopup("shoppingListCopy");
      }

      Utilities.HoverTooltip("Copy the list to the clipboard.");
      this.DrawCopyPopup();

      ImGui.SameLine();

      ImGui.BeginDisabled(busy);

      if (ImGui.Button("Clear"))
      {
        this.Plugin.ShoppingList.Clear();
      }

      ImGui.EndDisabled();
      Utilities.HoverTooltip("Remove every entry from the list.");

      this.DrawScopePickers(true);

      ImGui.Separator();
    }

    /// <summary>
    /// Draws what to do with the entries a buy run bought or could not buy, pinned above the total.
    /// </summary>
    /// <param name="bought">How many entries were bought.</param>
    /// <param name="failed">How many entries were not.</param>
    private void DrawOutcomeBar(int bought, int failed)
    {
      var list = this.Plugin.ShoppingList;
      var busy = this.Plugin.ShoppingListBulkAdd.IsRunning || this.Plugin.ShoppingListBuyer.IsRunning;

      ImGui.BeginDisabled(busy);

      if (bought > 0)
      {
        if (ImGui.Button("Clear successful"))
        {
          list.RemoveAll(WasBought);
        }

        Utilities.HoverTooltip("Remove every entry that was bought from the list.");

        ImGui.SameLine();

        if (ImGui.Button("Reset successful"))
        {
          list.ClearOutcomes(WasBought);
        }

        Utilities.HoverTooltip("Take the colour off the entries that were bought, leaving them on the list.");
      }

      if (failed > 0)
      {
        if (bought > 0)
        {
          ImGui.SameLine();
        }

        if (ImGui.Button("Refresh failed"))
        {
          this.Plugin.ShoppingListBulkAdd.StartRefresh(
            list.Where(BuyFailed).ToArray(),
            "the entries that did not buy");
        }

        Utilities.HoverTooltip("Price every entry that did not buy again.");

        ImGui.SameLine();

        if (ImGui.Button("Reset failed"))
        {
          list.ClearOutcomes(BuyFailed);
        }

        Utilities.HoverTooltip("Take the colour off the entries that did not buy.");
      }

      ImGui.EndDisabled();
    }

    private void DrawFooter()
    {
      var total = this.Plugin.ShoppingList.Sum(e => e.Total);
      var text = "Total Cost: " + (this.Plugin.Config.PriceIconShown
        ? total.ToString("C", this.Plugin.NumberFormatInfo)
        : total.ToString("N0", CultureInfo.CurrentCulture));

      var totalWidth = ImGui.CalcTextSize(text).X;
      var summary = QuerySummary(this.Plugin.Config.ShoppingListLastQuery);

      ImGui.AlignTextToFramePadding();

      // The total keeps the row to itself rather than being pushed off it when both do not fit.
      if (summary.Length > 0 &&
          ImGui.CalcTextSize(summary).X + ImGui.GetStyle().ItemSpacing.X + totalWidth <= ImGui.GetContentRegionAvail().X)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
        ImGui.Text(summary);
        ImGui.PopStyleColor();
        ImGui.SameLine();
      }

      var padding = ImGui.GetContentRegionAvail().X - totalWidth;
      if (padding > 0)
      {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
      }

      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.GilText);
      ImGui.Text(text);
      ImGui.PopStyleColor();
    }

    private void DrawCopyPopup()
    {
      if (!ImGui.BeginPopup("shoppingListCopy"))
      {
        return;
      }

      var config = this.Plugin.Config;

      this.DrawCopyOption("Item name", config.ShoppingListCopyName, v => config.ShoppingListCopyName = v);
      this.DrawCopyOption("Price", config.ShoppingListCopyPrice, v => config.ShoppingListCopyPrice = v);
      this.DrawCopyOption("World", config.ShoppingListCopyWorld, v => config.ShoppingListCopyWorld = v);

      ImGui.Separator();

      ImGui.BeginDisabled(!config.ShoppingListCopyName && !config.ShoppingListCopyPrice && !config.ShoppingListCopyWorld);

      if (ImGui.Button("Copy to clipboard"))
      {
        this.CopyList();
        ImGui.CloseCurrentPopup();
      }

      ImGui.EndDisabled();
      ImGui.EndPopup();
    }

    private void DrawCopyOption(string label, bool value, Action<bool> setter)
    {
      var current = value;

      if (ImGui.Checkbox(label, ref current))
      {
        setter(current);
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }
    }

    private void CopyList()
    {
      var config = this.Plugin.Config;

      // Copied in the order the tree draws them, so the text reads the way the window does.
      var entries = this.Nodes().SelectMany(n => n.AllEntries).ToArray();

      var builder = new StringBuilder();

      foreach (var entry in entries)
      {
        var parts = new List<string>();

        if (config.ShoppingListCopyName)
        {
          parts.Add(entry.SourceItem.Name.ExtractText());
        }

        if (config.ShoppingListCopyPrice)
        {
          parts.Add(entry.Unlisted ? NoValue : entry.Price.ToString("N0", CultureInfo.CurrentCulture));
        }

        if (config.ShoppingListCopyWorld)
        {
          parts.Add(entry.Unlisted ? NoValue : entry.World);
        }

        builder.AppendLine(string.Join(" - ", parts));
      }

      var text = builder.ToString().TrimEnd();

      if (text.Length == 0)
      {
        return;
      }

      ImGui.SetClipboardText(text);

      if (config.ClipboardNotificationsEnabled)
      {
        this.Plugin.NotifyClipboardCopied($"{entries.Length} shopping list entries");
      }
    }

    private void DrawBulkAddProgress()
    {
      var bulkAdd = this.Plugin.ShoppingListBulkAdd;

      if (!bulkAdd.IsRunning)
      {
        return;
      }

      var processed = bulkAdd.Counted;
      var total = bulkAdd.Total;

      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
      ImGui.TextWrapped($"Pricing {bulkAdd.CategoryName}...");
      ImGui.PopStyleColor();

      var cancelWidth = ImGui.CalcTextSize("Cancel").X + (ImGui.GetStyle().FramePadding.X * 2);
      var barWidth = ImGui.GetContentRegionAvail().X - cancelWidth - ImGui.GetStyle().ItemSpacing.X;

      ImGui.ProgressBar(
        total > 0 ? processed / (float)total : 0f,
        new Vector2(barWidth, 0),
        $"{processed} / {total}");

      ImGui.SameLine();

      if (ImGui.Button("Cancel"))
      {
        bulkAdd.Cancel();
      }

      ImGui.Separator();
    }

    /// <summary>
    /// Draws how far a buy run has got, and the button that stops it.
    /// </summary>
    private void DrawBuyProgress()
    {
      var buyer = this.Plugin.ShoppingListBuyer;

      if (!buyer.IsRunning)
      {
        return;
      }

      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
      ImGui.TextWrapped($"Buying {buyer.CurrentItemName}...");
      ImGui.PopStyleColor();

      var cancelWidth = ImGui.CalcTextSize("Cancel").X + (ImGui.GetStyle().FramePadding.X * 2);
      var barWidth = ImGui.GetContentRegionAvail().X - cancelWidth - ImGui.GetStyle().ItemSpacing.X;

      ImGui.ProgressBar(
        buyer.Total > 0 ? buyer.Done / (float)buyer.Total : 0f,
        new Vector2(barWidth, 0),
        $"{buyer.Done} / {buyer.Total}");

      ImGui.SameLine();

      if (ImGui.Button("Cancel##buyRun"))
      {
        buyer.Cancel();
      }

      ImGui.Separator();
    }
  }
}
