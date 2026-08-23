// <copyright file="ListingPicker.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI.Components
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Net.Http;
  using System.Numerics;
  using System.Text.Json;
  using System.Threading;
  using System.Threading.Tasks;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Game.Text;
  using Lumina.Excel.Sheets;
  using MarketTerror.Extensions;
  using MarketTerror.GUI.Theme;
  using MarketTerror.Models.ShoppingList;
  using MarketTerror.Models.Universalis;

  /// <summary>
  /// The popup that points a shopping list entry at one particular Market Board listing.
  /// </summary>
  /// <remarks>
  /// It shows what is on sale in the entry's own market, and clicking a listing is the whole of the
  /// choice: the entry buys that one and nothing else until it is pointed somewhere else. Mannequin
  /// listings are left out, since no entry is ever allowed to buy one.
  /// </remarks>
  public sealed class ListingPicker : IDisposable
  {
    /// <summary>
    /// The popup's ImGui id. Only one entry is picked at a time, so it does not need the entry in it.
    /// </summary>
    private const string PopupId = "shoppingListPicker";

    /// <summary>
    /// How many listings to ask Universalis for. More than a market board can show in one go.
    /// </summary>
    private const int ListingCount = 100;

    private readonly MarketTerrorPlugin plugin;

    private readonly List<ResolvedListing> candidates = new List<ResolvedListing>();

    private ListingEntry? entry;

    private bool opening;

    private bool loading;

    private string failure = string.Empty;

    private CancellationTokenSource? cancellation;

    private bool isDisposed;

    /// <summary>
    /// True while the open entry is one the popup made rather than one already on the list, so
    /// closing the popup without picking leaves nothing behind.
    /// </summary>
    private bool adding;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListingPicker"/> class.
    /// </summary>
    /// <param name="plugin">The plugin instance.</param>
    public ListingPicker(MarketTerrorPlugin plugin)
    {
      this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <summary>
    /// Gets the label of the market the popup fetches from.
    /// </summary>
    private string ScopeLabel =>
      this.entry?.Scope.Display(this.plugin.WorldCatalogue) ?? string.Empty;

    /// <summary>
    /// Opens the popup for an entry and starts fetching the listings to choose from.
    /// </summary>
    /// <param name="entry">The entry to pick a listing for.</param>
    public void Open(ListingEntry entry)
    {
      ArgumentNullException.ThrowIfNull(entry);

      this.Start(entry, false);
    }

    /// <summary>
    /// Opens the popup to add a direct entry, which only reaches the list once a listing is picked.
    /// </summary>
    /// <param name="item">The item to buy.</param>
    /// <param name="scope">The market to pick a listing from.</param>
    public void Open(Item item, ListingScope scope)
    {
      ArgumentNullException.ThrowIfNull(scope);

      this.Start(new ListingEntry(item, scope, ListingKind.Direct), true);
    }

    /// <summary>
    /// Draws the popup when an entry has it open.
    /// </summary>
    /// <param name="theme">The theme to draw in.</param>
    public void Draw(TerrorTheme theme)
    {
      ArgumentNullException.ThrowIfNull(theme);

      var open = this.entry;

      if (open == null)
      {
        this.opening = false;
        return;
      }

      // The name is what the title bar shows; the id after it is what ImGui matches the popup on,
      // so the title can change with the entry without the popup counting as a different one.
      var label = $"{open.SourceItem.Name.ExtractText()} Listings##{PopupId}";

      if (this.opening)
      {
        ImGui.OpenPopup(label);
        this.opening = false;
      }

      var scale = ImGui.GetIO().FontGlobalScale;
      ImGui.SetNextWindowSize(new Vector2(720 * scale, 480 * scale), ImGuiCond.Appearing);

      var stayOpen = true;

      if (!ImGui.BeginPopupModal(label, ref stayOpen, ImGuiWindowFlags.NoSavedSettings))
      {
        // The popup is gone, so nothing is being picked any more.
        this.Close();
        return;
      }

      ModalDim.Mark();

      this.DrawScope(theme);

      ImGui.Separator();

      var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;

      this.DrawTable(theme, footerHeight);

      ImGui.Separator();
      this.DrawFooter(theme);

      ImGui.EndPopup();

      if (!stayOpen)
      {
        this.Close();
      }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      if (this.isDisposed)
      {
        return;
      }

      this.CancelFetch();
      this.isDisposed = true;
    }

    private static void RightAligned(string text)
    {
      var padding = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;

      if (padding > 0)
      {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
      }

      ImGui.Text(text);
    }

    private static float CenterOffset(string text)
    {
      var width = ImGui.GetContentRegionAvail().X + ImGui.GetStyle().CellPadding.X;

      return Math.Max((width - ImGui.CalcTextSize(text).X) * 0.5f, 0.0f);
    }

    /// <summary>
    /// Points the popup at an entry and starts fetching the listings to choose from.
    /// </summary>
    /// <param name="open">The entry to pick a listing for.</param>
    /// <param name="isNew">True when the entry is not on the list yet.</param>
    private void Start(ListingEntry open, bool isNew)
    {
      this.CancelFetch();

      this.entry = open;
      this.adding = isNew;
      this.opening = true;
      this.failure = string.Empty;
      this.candidates.Clear();

      this.StartFetch();
    }

    private void CancelFetch()
    {
      this.cancellation?.Cancel();
      this.cancellation?.Dispose();
      this.cancellation = null;
    }

    /// <summary>
    /// Fetches the listings of the open entry across its market, dropping any fetch still in flight.
    /// </summary>
    private void StartFetch()
    {
      if (this.entry == null)
      {
        return;
      }

      this.CancelFetch();

      var open = this.entry;
      var targets = open.Scope.Targets(this.plugin.WorldCatalogue).ToArray();

      this.loading = true;
      this.cancellation = new CancellationTokenSource();
      var token = this.cancellation.Token;

      _ = Task.Run(() => this.Fetch(open, targets, token), token);
    }

    private void Close()
    {
      this.CancelFetch();
      this.entry = null;
      this.adding = false;
      this.loading = false;
      this.candidates.Clear();
      this.failure = string.Empty;
    }

    /// <summary>
    /// Fetches every listing of the entry's item across its market.
    /// </summary>
    /// <param name="open">The entry being picked for.</param>
    /// <param name="targets">The worlds, data centres or regions to fetch from.</param>
    /// <param name="token">Cancels the fetch when the popup closes.</param>
    /// <returns>A task that completes once the candidates have been handed to the framework thread.</returns>
    private async Task Fetch(ListingEntry open, IReadOnlyList<string> targets, CancellationToken token)
    {
      var found = new List<(MarketDataListing Listing, string Target)>();
      var error = string.Empty;

      foreach (var target in targets)
      {
        try
        {
          var data = await this.plugin.UniversalisClient
            .GetMarketData(open.SourceItem.RowId, target, ListingCount, 0, token)
            .ConfigureAwait(false);

          foreach (var listing in data.Listings)
          {
            found.Add((listing, data.WorldName ?? target));
          }
        }
        catch (OperationCanceledException)
        {
          return;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
          this.plugin.Log.Warning(ex, $"Could not fetch the listings of item {open.SourceItem.RowId} on {target}.");
          error = "Universalis could not be reached for part of the scope.";
        }
      }

      if (token.IsCancellationRequested)
      {
        return;
      }

      // The candidates are read while the popup draws, so they may only be swapped on the framework thread.
      await this.plugin.Framework
        .RunOnFrameworkThread(() => this.Apply(open, found, error))
        .ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the fetched listings into the candidates the popup offers.
    /// </summary>
    /// <param name="open">The entry being picked for.</param>
    /// <param name="found">The listings, paired with the target they came back from.</param>
    /// <param name="error">What went wrong, or an empty string when nothing did.</param>
    private void Apply(ListingEntry open, List<(MarketDataListing Listing, string Target)> found, string error)
    {
      if (!ReferenceEquals(this.entry, open))
      {
        // The popup moved on to another entry while the fetch was in flight.
        return;
      }

      var withTax = !this.plugin.Config.NoGilSalesTax;
      var seen = new HashSet<string>(StringComparer.Ordinal);
      var fresh = new List<ResolvedListing>();

      // A scope can ask a world and its data centre in the same breath, so the same listing comes twice.
      foreach (var (listing, target) in found.OrderBy(f => ResolvedListing.UnitPrice(f.Listing, withTax)))
      {
        if (listing.ListingId.Length > 0 && !seen.Add(listing.ListingId))
        {
          continue;
        }

        var resolved = ResolvedListing.FromListing(listing, withTax, target);

        if (resolved.OnMannequin)
        {
          continue;
        }

        fresh.Add(resolved);
      }

      this.candidates.Clear();
      this.candidates.AddRange(fresh);
      this.loading = false;
      this.failure = error;
    }

    /// <summary>
    /// Points the entry at a listing and closes the popup, putting a new entry on the list first.
    /// </summary>
    /// <param name="listing">The listing the entry now buys.</param>
    private void Choose(ResolvedListing listing)
    {
      var open = this.entry;

      if (open == null)
      {
        return;
      }

      if (this.adding)
      {
        this.plugin.ShoppingList.AddDirect(open.SourceItem, open.Scope, listing);
      }
      else
      {
        open.Target = listing;
        this.plugin.ShoppingList.ApplyMatches(open, new[] { listing });
      }

      this.Close();
      ImGui.CloseCurrentPopup();
    }

    private void DrawScope(TerrorTheme theme)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, theme.TextDim);
      ImGui.Text(this.ScopeLabel);
      ImGui.PopStyleColor();
    }

    private void DrawTable(TerrorTheme theme, float footerHeight)
    {
      if (this.candidates.Count == 0)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, theme.TextDim);
        ImGui.TextWrapped(this.loading ? "Loading listings..." : "Nothing of this item is on sale in the scope.");
        ImGui.PopStyleColor();
        return;
      }

      var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable
        | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

      if (!ImGui.BeginTable("shoppingListPickerTable", 5, flags, new Vector2(0, -footerHeight)))
      {
        return;
      }

      ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeightWithSpacing() * 1.5f);
      ImGui.TableSetupColumn("Price");
      ImGui.TableSetupColumn("Qty");
      ImGui.TableSetupColumn("Total");
      ImGui.TableSetupColumn("Retainer");
      ImGui.TableSetupScrollFreeze(0, 1);

      ImGui.PushStyleColor(ImGuiCol.Text, theme.TextDim);
      ImGui.TableHeadersRow();
      ImGui.PopStyleColor();

      ResolvedListing? chosen = null;

      for (var index = 0; index < this.candidates.Count; index++)
      {
        if (this.DrawCandidate(theme, this.candidates[index], index))
        {
          chosen = this.candidates[index];
        }
      }

      ImGui.EndTable();

      // Closing the popup is left until the table is done, so nothing draws into a row that is going away.
      if (chosen != null)
      {
        this.Choose(chosen);
      }
    }

    private bool DrawCandidate(TerrorTheme theme, ResolvedListing listing, int index)
    {
      ImGui.TableNextRow();

      ImGui.TableSetColumnIndex(0);

      var cursor = ImGui.GetCursorPos();
      var hqOffset = CenterOffset(SeIconChar.HighQuality.AsString());
      var target = this.entry?.Target;

      var clicked = ImGui.Selectable(
        $"##candidate{index}",
        target != null && target.SameAs(listing),
        ImGuiSelectableFlags.SpanAllColumns);

      if (listing.Hq)
      {
        ImGui.SetCursorPos(new Vector2(cursor.X + hqOffset, cursor.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, theme.TextBright);
        ImGui.Text(SeIconChar.HighQuality.AsString());
        ImGui.PopStyleColor();
      }

      ImGui.TableSetColumnIndex(1);
      this.DrawGil(theme, listing.Price);

      ImGui.TableSetColumnIndex(2);
      ImGui.PushStyleColor(ImGuiCol.Text, theme.Text);
      RightAligned(listing.Quantity.ToString("N0", CultureInfo.CurrentCulture));
      ImGui.PopStyleColor();

      ImGui.TableSetColumnIndex(3);
      this.DrawGil(theme, listing.Total);

      ImGui.TableSetColumnIndex(4);
      ImGui.PushStyleColor(ImGuiCol.Text, theme.Text);
      ImGui.Text($"{listing.RetainerName} {SeIconChar.CrossWorld.ToChar()} {listing.World}");
      ImGui.PopStyleColor();

      return clicked;
    }

    private void DrawGil(TerrorTheme theme, double value)
    {
      var text = this.plugin.Config.PriceIconShown
        ? value.ToString("C", this.plugin.NumberFormatInfo)
        : value.ToString("N0", CultureInfo.CurrentCulture);

      ImGui.PushStyleColor(ImGuiCol.Text, theme.GilText);
      RightAligned(text);
      ImGui.PopStyleColor();
    }

    private void DrawFooter(TerrorTheme theme)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, theme.TextDim);
      ImGui.Text("Click a listing to buy that one and nothing else.");
      ImGui.PopStyleColor();

      if (this.failure.Length > 0)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, theme.BuyFailed);
        ImGui.Text(this.failure);
        ImGui.PopStyleColor();
      }

      if (ImGui.Button("Cancel"))
      {
        this.Close();
        ImGui.CloseCurrentPopup();
      }
    }
  }
}
