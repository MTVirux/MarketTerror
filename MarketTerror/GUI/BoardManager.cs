// <copyright file="BoardManager.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror.GUI
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Numerics;
  using Dalamud.Bindings.ImGui;
  using Dalamud.Interface.ManagedFontAtlas;
  using Dalamud.Interface.Windowing;
  using MarketTerror.Models;
  using MarketTerror.Services;

  /// <summary>
  /// Owns the main board and every torn-off one, and moves lists between them.
  /// </summary>
  /// <remarks>
  /// Detach and reattach are queued rather than done on the spot. They are asked for from inside
  /// <see cref="WindowSystem.Draw"/>: a reattach comes out of the closing window's own
  /// <see cref="Window.OnClose"/>, which Dalamud raises at the top of that window's draw, so doing it
  /// there would dispose the board and its market data view while the frame still going on is about to
  /// use them. The walk is over a snapshot of the window list, so a window removed part-way through can
  /// still be drawn later in the same frame - which is the same hazard from the other end.
  /// </remarks>
  public sealed class BoardManager : IDisposable
  {
    private readonly MarketTerrorPlugin plugin;

    private readonly WindowSystem windowSystem;

    private readonly List<DetachedBoardWindow> detached = new List<DetachedBoardWindow>();

    private readonly List<ItemListTab> pendingDetach = new List<ItemListTab>();

    private readonly List<ItemListTab> pendingReattach = new List<ItemListTab>();

    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoardManager"/> class.
    /// </summary>
    /// <param name="plugin">The plugin instance.</param>
    /// <param name="defaultFont">The body font.</param>
    /// <param name="titleFont">The 1.5x font used for headings.</param>
    /// <param name="windowSystem">The window system the boards register with.</param>
    public BoardManager(
      MarketTerrorPlugin plugin,
      IFontHandle defaultFont,
      IFontHandle titleFont,
      WindowSystem windowSystem)
    {
      this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
      this.windowSystem = windowSystem ?? throw new ArgumentNullException(nameof(windowSystem));

      this.Services = new BoardServices(plugin, defaultFont, titleFont);
      this.MainWindow = new MarketBoardWindow(this);
      this.MainWindow.Board.Manager = this;

      this.RestoreDetachedBoards();
    }

    /// <summary>
    /// Gets the services shared by every board.
    /// </summary>
    public BoardServices Services { get; }

    /// <summary>
    /// Gets the main window.
    /// </summary>
    public MarketBoardWindow MainWindow { get; }

    /// <summary>
    /// Gets the windows currently torn off.
    /// </summary>
    public IReadOnlyList<DetachedBoardWindow> Detached => this.detached;

    /// <summary>
    /// Queues a list to be torn off into its own window.
    /// </summary>
    /// <param name="tab">The list to tear off.</param>
    public void Detach(ItemListTab tab)
    {
      if (this.detached.Any(w => w.Tab == tab) || this.pendingDetach.Contains(tab))
      {
        return;
      }

      this.pendingDetach.Add(tab);
    }

    /// <summary>
    /// Queues a list to go back into the main window.
    /// </summary>
    /// <param name="tab">The list to send home.</param>
    public void Reattach(ItemListTab tab)
    {
      if (this.pendingReattach.Contains(tab))
      {
        return;
      }

      this.pendingReattach.Add(tab);
    }

    /// <summary>
    /// Applies the queued detaches and reattaches. Call this after the window system has drawn.
    /// </summary>
    public void ApplyPendingChanges()
    {
      this.CheckDockBack();

      if (this.pendingDetach.Count == 0 && this.pendingReattach.Count == 0)
      {
        return;
      }

      // Emptied before anything is acted on, so a throw here does not come back every frame.
      var detaching = this.pendingDetach.ToArray();
      var reattaching = this.pendingReattach.ToArray();

      this.pendingDetach.Clear();
      this.pendingReattach.Clear();

      foreach (var tab in detaching)
      {
        this.DetachNow(tab, grabbed: true);
      }

      foreach (var tab in reattaching)
      {
        this.ReattachNow(tab);
      }

      this.SaveLayout();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
      if (this.isDisposed)
      {
        return;
      }

      foreach (var window in this.detached)
      {
        this.windowSystem.RemoveWindow(window);
        window.Dispose();
      }

      this.detached.Clear();
      this.MainWindow.Dispose();
      this.Services.Dispose();
      this.isDisposed = true;
    }

    /// <summary>
    /// Writes every detached window's current state into its stored entry and saves the config.
    /// </summary>
    internal void SaveLayout()
    {
      foreach (var window in this.detached)
      {
        window.Board.Context.SaveTo(window.State);
      }

      this.plugin.PluginInterface.SavePluginConfig(this.plugin.Config);
    }

    /// <summary>
    /// Shows every detached window that is hidden.
    /// </summary>
    /// <remarks>Detached windows left hidden on start come back with the main window.</remarks>
    internal void ShowDetached()
    {
      foreach (var window in this.detached)
      {
        window.IsOpen = true;
      }
    }

    private void DetachNow(ItemListTab tab, bool grabbed, bool open = true)
    {
      var state = this.plugin.Config.DetachedBoards.FirstOrDefault(s => s.Tab == tab);

      if (state == null)
      {
        // A tab torn off mid-search takes what the board it came from was showing with it.
        state = new DetachedBoardState { Tab = tab };
        this.MainWindow.Board.Context.SaveTo(state);
        this.plugin.Config.DetachedBoards.Add(state);
      }

      var context = new MarketBoardContext(
        this.Services,
        WorldSelection.ForDetachedBoard(this.plugin, state));

      var board = new MarketBoard(this.Services, context, isMainBoard: false)
      {
        Manager = this,
      };

      board.Tabs.Add(tab);
      context.ItemListTab = tab;
      context.LoadFrom(state);

      var window = new DetachedBoardWindow(this.Services, board, tab, state, this.Reattach)
      {
        IsOpen = open,
        Grabbed = grabbed,
      };

      if (grabbed)
      {
        // Sit the cursor on the title bar, a little in from the left, so the grab reads as natural.
        var titleBarHeight = ImGui.GetFrameHeight();
        window.GrabOffset = new Vector2(titleBarHeight * 2.0f, titleBarHeight * 0.5f);
      }

      this.detached.Add(window);
      this.windowSystem.AddWindow(window);
      this.MainWindow.Board.Tabs.Remove(tab);
    }

    private void ReattachNow(ItemListTab tab)
    {
      var window = this.detached.FirstOrDefault(w => w.Tab == tab);

      if (window == null)
      {
        return;
      }

      this.detached.Remove(window);
      this.windowSystem.RemoveWindow(window);
      window.Dispose();

      this.plugin.Config.DetachedBoards.RemoveAll(s => s.Tab == tab);

      if (!this.MainWindow.Board.Tabs.Contains(tab))
      {
        this.MainWindow.Board.Tabs.Add(tab);
        this.SortMainTabs();
      }
    }

    /// <summary>
    /// Highlights the main tab bar while a window is dragged over it, and docks the window
    /// when the drag is released there.
    /// </summary>
    /// <remarks>Runs after the window system has drawn, so the rects are this frame's.</remarks>
    private void CheckDockBack()
    {
      var target = this.MainWindow.Board.TabBarScreenRect;

      if (!this.MainWindow.IsOpen || target.Max.X <= target.Min.X)
      {
        return;
      }

      var dragging = this.detached.FirstOrDefault(w => w.IsBeingDragged);

      if (dragging == null)
      {
        return;
      }

      var mouse = ImGui.GetMousePos();
      var over = mouse.X >= target.Min.X && mouse.X <= target.Max.X
        && mouse.Y >= target.Min.Y && mouse.Y <= target.Max.Y;

      if (!over)
      {
        return;
      }

      ImGui.GetForegroundDrawList().AddRectFilled(target.Min, target.Max, this.Services.Theme.AccentHover & 0x40FFFFFFu);
      ImGui.GetForegroundDrawList().AddRect(target.Min, target.Max, this.Services.Theme.AccentHover);

      if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
      {
        this.Reattach(dragging.Tab);
      }
    }

    /// <summary>
    /// Puts the main board's list of tabs back in enum order.
    /// </summary>
    /// <remarks>
    /// This is the membership list, not the bar: ImGui appends a returning tab at the end of the bar
    /// wherever this list says it sits, and the user is free to drag it elsewhere from there. Sorting it
    /// only keeps <c>FallbackTab</c> deterministic, so the board falls back to the whole catalogue rather
    /// than whichever list happened to come home last.
    /// </remarks>
    private void SortMainTabs()
    {
      var ordered = this.MainWindow.Board.Tabs.OrderBy(t => (int)t).ToList();
      this.MainWindow.Board.Tabs.Clear();

      foreach (var tab in ordered)
      {
        this.MainWindow.Board.Tabs.Add(tab);
      }
    }

    /// <summary>
    /// Reopens every window that was detached when the plugin last saved its config.
    /// </summary>
    private void RestoreDetachedBoards()
    {
      var seen = new HashSet<ItemListTab>();

      // A tab is either docked or detached, so a duplicate entry is dropped rather than opened twice.
      foreach (var state in this.plugin.Config.DetachedBoards.ToList())
      {
        if (!Enum.IsDefined(state.Tab) || !seen.Add(state.Tab))
        {
          this.plugin.Config.DetachedBoards.Remove(state);
          continue;
        }

        this.DetachNow(state.Tab, grabbed: false, open: this.plugin.ShowWindowsOnStart);
      }
    }
  }
}
