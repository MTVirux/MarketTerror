// <copyright file="MarketBoardWindow.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>
namespace MarketTerror.GUI
{
  using System;
  using System.Numerics;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Interface;
  using Dalamud.Interface.Windowing;
  using MarketTerror.GUI.Components;
  using MarketTerror.Services;

  /// <summary>
  /// The market board window.
  /// </summary>
  /// <remarks>
  /// This type hosts the main <see cref="MarketBoard"/>; the drawing itself lives in
  /// <see cref="MarketBoard"/> and the components under <see cref="MarketTerror.GUI.Components"/>.
  /// The services it draws with belong to the <see cref="BoardManager"/>.
  /// </remarks>
  public class MarketBoardWindow : Window, IDisposable
  {
    private readonly MarketTerrorPlugin plugin;

    private readonly BoardServices services;

    private readonly HoveredItemWatcher hoveredItemWatcher;

    private readonly MarketBoard board;

    private readonly TitleBarButton integrationsButton;

    private readonly TitleBarButton shoppingListButton;

    private IDisposable? themeScope;

    private bool isDisposed;

#if DEBUG
    private uint pendingItemId;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketBoardWindow"/> class.
    /// </summary>
    /// <param name="manager">The manager that owns this window and the torn-off ones.</param>
    public MarketBoardWindow(BoardManager manager)
      : base("Market Terror")
    {
      ArgumentNullException.ThrowIfNull(manager);

      this.services = manager.Services;
      this.plugin = this.services.Plugin;
      this.Flags = ImGuiWindowFlags.NoScrollbar;
      this.Size = new Vector2(800, 600);
      this.SizeCondition = ImGuiCond.FirstUseEver;
      this.SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new Vector2(350, 225),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
      };

      var context = new MarketBoardContext(this.services, WorldSelection.ForMainWindow(this.plugin));

      this.hoveredItemWatcher = new HoveredItemWatcher(this.plugin, this.services.Catalog, id => context.SelectItem(id));

      this.board = new MarketBoard(this.services, context, isMainBoard: true);

      foreach (var tab in Enum.GetValues<ItemListTab>())
      {
        this.board.Tabs.Add(tab);
      }

      this.board.DrawUnderList = () =>
      {
        this.hoveredItemWatcher.Tick();

        if (this.plugin.Config.WatchForHovered)
        {
          ImGui.ProgressBar(this.hoveredItemWatcher.Progress, new Vector2(-1, 0), string.Empty);
        }
      };

      this.integrationsButton = IntegrationsButton.Build(this.board.Context);
      this.TitleBarButtons.Add(this.integrationsButton);

      this.shoppingListButton = ShoppingListButton.Build(this.board.Context);
      this.TitleBarButtons.Add(this.shoppingListButton);

      this.TitleBarButtons.Add(new TitleBarButton
      {
        Icon = FontAwesomeIcon.Heart,
        IconOffset = new Vector2(2, 1),
        Click = _ => this.board.OpenLinksPopup(),
        ShowTooltip = () =>
        {
          ImGui.BeginTooltip();
          ImGui.Text("Links");
          ImGui.EndTooltip();
        },
      });

      this.TitleBarButtons.Add(new TitleBarButton
      {
        Icon = FontAwesomeIcon.Cog,
        IconOffset = new Vector2(2, 1),
        Click = _ => this.plugin.OpenConfigUi(),
        ShowTooltip = () =>
        {
          ImGui.BeginTooltip();
          ImGui.Text("Settings");
          ImGui.EndTooltip();
        },
      });

#if DEBUG
      if (this.plugin.Config.RememberLastItem)
      {
        this.pendingItemId = this.plugin.Config.LastOpenedItem;
      }
#endif
    }

    /// <summary>
    /// Gets the board this window hosts.
    /// </summary>
    public MarketBoard Board => this.board;

    /// <summary>
    /// Gets the state and services shared by every component of this window.
    /// </summary>
    public MarketBoardContext Context => this.board.Context;

    /// <summary>
    /// Gets or sets the current search string.
    /// </summary>
    public string SearchString
    {
      get => this.board.Context.SearchString;
      set => this.board.Context.SearchString = value;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reset the market data.
    /// </summary>
    public void ResetMarketData()
    {
      this.board.Context.ResetMarketData();
    }

    /// <inheritdoc/>
    public override void OnOpen()
    {
      this.board.Manager?.ShowDetached();
    }

    /// <inheritdoc/>
    public override void PreDraw()
    {
      // Draw is skipped while the window is collapsed, so the tab bar rect is dropped here, where
      // it always runs, rather than left standing as a drop target over nothing.
      this.board.TabBarScreenRect = default;

      IntegrationsButton.Refresh(this.integrationsButton, this.board.Context);
      ShoppingListButton.Refresh(this.shoppingListButton, this.board.Context);
      this.themeScope = this.board.Context.Theme.Push();
    }

    /// <inheritdoc/>
    public override void PostDraw()
    {
      this.themeScope?.Dispose();
      this.themeScope = null;
    }

    /// <summary>
    /// Draws the window.
    /// </summary>
    public override void Draw()
    {
#if DEBUG
      this.RestoreLastOpenedItem();
#endif

      using var fontDispose = this.services.DefaultFont.Push();

      this.board.Draw();
    }

    /// <summary>
    /// Selects an item and refreshes its market data.
    /// </summary>
    /// <param name="itemId">The item row id.</param>
    /// <param name="noHistory">True to leave the search history untouched.</param>
    internal void ChangeSelectedItem(uint itemId, bool noHistory = false)
    {
      this.board.Context.SelectItem(itemId, noHistory);
    }

    /// <summary>
    /// Protected implementation of Dispose pattern.
    /// </summary>
    /// <param name="disposing">A value indicating whether we are disposing.</param>
    protected virtual void Dispose(bool disposing)
    {
      if (this.isDisposed)
      {
        return;
      }

      if (disposing)
      {
        this.themeScope?.Dispose();
        this.themeScope = null;
        this.hoveredItemWatcher.Dispose();
        this.board.Dispose();
      }

      this.isDisposed = true;
    }

#if DEBUG
    /// <summary>
    /// Reselects the item that was open last, once a world is available to query it against.
    /// </summary>
    private void RestoreLastOpenedItem()
    {
      if (this.pendingItemId == 0 || !this.board.Context.Worlds.HasSelection)
      {
        return;
      }

      var itemId = this.pendingItemId;
      this.pendingItemId = 0;
      this.board.Context.SelectItem(itemId, true);
    }
#endif
  }
}
