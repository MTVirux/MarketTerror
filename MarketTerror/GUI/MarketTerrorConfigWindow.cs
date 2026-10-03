// <copyright file="MarketTerrorConfigWindow.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI
{
  using System;
  using System.Numerics;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Interface;
  using Dalamud.Interface.Windowing;
  using MarketTerror.GUI.Theme;
  using MarketTerror.Helpers;
  using MarketTerror.Models;
  using MarketTerror.Services;

  /// <summary>
  /// The market board config window.
  /// </summary>
  public class MarketTerrorConfigWindow : Window
  {
    private readonly TerrorTheme theme;

    private IDisposable? themeScope;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketTerrorConfigWindow"/> class.
    /// </summary>
    /// <param name="plugin">The <see cref="MarketTerrorPlugin"/>.</param>
    public MarketTerrorConfigWindow(MarketTerrorPlugin plugin)
      : base("Market Terror Config")
    {
      this.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
      this.Size = new Vector2(0, 0);

      this.Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
      this.theme = new TerrorTheme(this.Plugin.Config);
    }

    private MarketTerrorPlugin Plugin { get; init; }

    /// <inheritdoc/>
    public override void PreDraw()
    {
      this.themeScope = this.theme.Push();
    }

    /// <inheritdoc/>
    public override void PostDraw()
    {
      this.themeScope?.Dispose();
      this.themeScope = null;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
      this.SectionHeading("General");
      this.Checkbox("Context menu integration", "Toggles whether context menu integration is enabled", this.Plugin.Config.ContextMenuIntegration, (v) => this.Plugin.Config.ContextMenuIntegration = v);
      this.Checkbox("Gil Icon Shown", "Toggles whether the Gil icon is shown", this.Plugin.Config.PriceIconShown, (v) => this.Plugin.Config.PriceIconShown = v);

      this.Checkbox("No Gil Sales Tax", "Toggles whether the Gil Sales Tax is included", this.Plugin.Config.NoGilSalesTax, (v) =>
      {
        this.Plugin.Config.NoGilSalesTax = v;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
        this.Plugin.ResetMarketData();
      });

      this.Checkbox("World overrides", "Show the world pickers, so the market board and the buy list can price around a world other than your own. Turn it off to always follow your current world.", this.Plugin.Config.WorldOverridesEnabled, (v) =>
      {
        this.Plugin.Config.WorldOverridesEnabled = v;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
        this.Plugin.ResetMarketData();
      });

      ImGui.NewLine();

      this.SectionHeading("Teleport / Integration");

      // Auto-teleport to world setting (only enabled if Lifestream is installed and switched on)
      var lifestreamAvailable = this.Plugin.IsLifestreamAvailable;
      if (!lifestreamAvailable)
      {
        ImGui.BeginDisabled();
      }

      this.Checkbox("Auto-teleport to world", lifestreamAvailable ? "Automatically teleport to the listing's world when clicked (requires Lifestream plugin)" : "Automatically teleport to the listing's world when clicked (Lifestream plugin not installed or disabled)", this.Plugin.Config.AutoTeleportToWorld, (v) => this.Plugin.Config.AutoTeleportToWorld = v);

      if (!lifestreamAvailable)
      {
        ImGui.EndDisabled();
      }

      ImGui.SameLine();
      this.LifestreamMark(lifestreamAvailable);

      this.Checkbox("Auto-search item on Market Board", "When clicking a listing, automatically type the item's name into the Market Board search when applicable.", this.Plugin.Config.AutoSearchOnMarketBoard, (v) => this.Plugin.Config.AutoSearchOnMarketBoard = v);

      ImGui.Indent();
      if (!this.Plugin.Config.AutoSearchOnMarketBoard)
      {
        ImGui.BeginDisabled();
      }

      this.Checkbox("Open the matching result", "Once the auto-search returns, select the searched item in the results so its listings open.", this.Plugin.Config.AutoOpenSearchResult, (v) => this.Plugin.Config.AutoOpenSearchResult = v);

      this.Checkbox("Buy from the shopping list", "Let the shopping list buy an item when the Market Board still has the listing it was priced from, at or below that price.", this.Plugin.Config.ShoppingListBuyEnabled, (v) => this.Plugin.Config.ShoppingListBuyEnabled = v);

      ImGui.Indent();
      if (!this.Plugin.Config.ShoppingListBuyEnabled)
      {
        ImGui.BeginDisabled();
      }

      this.Checkbox("Refresh listings after buying", "Once an item has been bought, open its listings and sales history again so both are uploaded without the listing that has just gone.", this.Plugin.Config.RefreshListingsAfterBuy, (v) => this.Plugin.Config.RefreshListingsAfterBuy = v);

      if (!this.Plugin.Config.ShoppingListBuyEnabled)
      {
        ImGui.EndDisabled();
      }

      ImGui.Unindent();

      if (!this.Plugin.Config.AutoSearchOnMarketBoard)
      {
        ImGui.EndDisabled();
      }

      ImGui.Unindent();

      ImGui.NewLine();

      this.SectionHeading("Clipboard");
      this.Checkbox("Clipboard notifications", "Show a chat message when something is copied to the clipboard", this.Plugin.Config.ClipboardNotificationsEnabled, (v) => this.Plugin.Config.ClipboardNotificationsEnabled = v);
      this.Checkbox("Copy item name on listing click", "Copy the selected item's name to the clipboard when clicking a current listing", this.Plugin.Config.CopyItemNameOnListingClick, (v) => this.Plugin.Config.CopyItemNameOnListingClick = v);

      ImGui.NewLine();

      this.SectionHeading("Others");
      this.Checkbox("Watch for hovered item", "Automatically select the item hovered in any of the in-game inventory window after 1 second.", this.Plugin.Config.WatchForHovered, (v) => this.Plugin.Config.WatchForHovered = v);

      this.Checkbox("Skip already unlocked items when buying", "Never buy a shopping list row for a minion, mount, orchestrion roll or card this character has already unlocked.", this.Plugin.Config.SkipUnlockedWhenBuying, (v) => this.Plugin.Config.SkipUnlockedWhenBuying = v);

      var grouping = (int)this.Plugin.Config.ShoppingListGrouping;

      if (ImGui.Combo("Buy list grouping", ref grouping, "Items, then scopes\0Scopes, then items\0"))
      {
        this.Plugin.Config.ShoppingListGrouping = (ShoppingListGrouping)grouping;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }

      Utilities.HoverTooltip("Whether the buy list hangs each item's markets under the item, or each market's items under the market.");

      this.Checkbox("Terror skin", "Apply the Market Terror colour scheme to this plugin's windows. Turn it off to use your Dalamud theme.", this.Plugin.Config.TerrorSkinEnabled, (v) => this.Plugin.Config.TerrorSkinEnabled = v);

#if DEBUG
      if (ImGui.Button("Edit theme colours"))
      {
        this.Plugin.OpenThemeEditor();
      }

      Utilities.HoverTooltip("Open the theme editor to change any of the skin's colours and see the result live.");

      ImGui.NewLine();

      this.SectionHeading("Debug");
      this.Checkbox("Open window on start", "Toggles whether the plugin's windows open automatically on plugin start in debug builds", this.Plugin.Config.OpenOnStart, (v) => this.Plugin.Config.OpenOnStart = v);
      this.Checkbox("Remember last opened item", "Toggles whether the item that was open last is reselected on plugin start in debug builds", this.Plugin.Config.RememberLastItem, (v) => this.Plugin.Config.RememberLastItem = v);
#endif

      ImGui.NewLine();

      this.SectionHeading("Data");

      var itemRefreshTimeout = this.Plugin.Config.ItemRefreshTimeout;
      ImGui.Text("Item buffer Timeout (ms) :");
      ImGui.InputInt("###refreshTimeout", ref itemRefreshTimeout);
      if (this.Plugin.Config.ItemRefreshTimeout != itemRefreshTimeout)
      {
        this.Plugin.Config.ItemRefreshTimeout = itemRefreshTimeout;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }

      var listingCount = this.Plugin.Config.ListingCount;
      ImGui.Text("Listing count :");
      ImGui.InputInt("###listingCount", ref listingCount);
      if (this.Plugin.Config.ListingCount != listingCount)
      {
        this.Plugin.Config.ListingCount = listingCount;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }

      var historyCount = this.Plugin.Config.HistoryCount;
      ImGui.Text("History count :");
      ImGui.InputInt("###historyCount", ref historyCount);
      if (this.Plugin.Config.HistoryCount != historyCount)
      {
        this.Plugin.Config.HistoryCount = historyCount;
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }
    }

    private void SectionHeading(string label)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, this.theme.TextDim);
      ImGui.Text(label);
      ImGui.PopStyleColor();
      ImGui.Separator();
    }

    private void LifestreamMark(bool available)
    {
      var icon = available ? FontAwesomeIcon.Check : FontAwesomeIcon.Times;
      var color = Integration.ColorFor(available ? IntegrationState.Ok : IntegrationState.Idle);

      ImGui.PushFont(UiBuilder.IconFont);
      ImGui.TextColored(color, $"{(char)icon}");
      ImGui.PopFont();

      string tooltip;
      if (available)
      {
        tooltip = "Lifestream is installed and enabled.";
      }
      else if (this.Plugin.IsLifestreamDisabled)
      {
        tooltip = "Lifestream is installed but switched off.";
      }
      else
      {
        tooltip = "Lifestream is not installed.";
      }

      Utilities.HoverTooltip(tooltip);
    }

    private void Checkbox(string label, string description, bool oldValue, Action<bool> setter)
    {
      if (Utilities.Checkbox(label, description, oldValue, setter))
      {
        this.Plugin.PluginInterface.SavePluginConfig(this.Plugin.Config);
      }
    }
  }
}
