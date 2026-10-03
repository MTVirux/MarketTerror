// <copyright file="MarketTerrorPlugin.cs" company="MTVirux">
// Copyright (c) MTVirux. All rights reserved.
// </copyright>

namespace MarketTerror
{
  using System;
  using System.Collections.Generic;
  using System.ComponentModel;
  using System.Diagnostics.CodeAnalysis;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using Dalamud.Bindings.ImPlot;
  using Dalamud.Game.ClientState.Objects.Enums;
  using Dalamud.Game.Command;
  using Dalamud.Game.Gui.ContextMenu;
  using Dalamud.Game.Text;
  using Dalamud.Interface;
  using Dalamud.Interface.ManagedFontAtlas;
  using Dalamud.Interface.Windowing;
  using Dalamud.Plugin;
  using Dalamud.Plugin.Services;
  using Dalamud.Utility;
  using FFXIVClientStructs.FFXIV.Client.UI.Agent;
  using Lumina.Excel.Sheets;

  using MarketTerror.GUI;
  using MarketTerror.GUI.ShoppingList;
  using MarketTerror.GUI.Theme;
  using MarketTerror.Helpers;
  using MarketTerror.Models;
  using MarketTerror.Models.ItemLists;
  using MarketTerror.Models.ShoppingList;
  using MarketTerror.Services;

  /// <summary>
  /// The entry point of the plugin.
  /// </summary>
  [SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Plugin entry point")]
  public class MarketTerrorPlugin : IDalamudPlugin
  {
    /// <summary>
    /// The config type name written by builds from before the plugin was renamed to Market Terror.
    /// </summary>
    private const string LegacyConfigTypeName = "MarketBoardPlugin.MBPluginConfig, MarketBoardPlugin";

    /// <summary>
    /// The config type name written after the assembly was renamed but before the namespace and class were.
    /// </summary>
    private const string RenamedAssemblyConfigTypeName = "MarketBoardPlugin.MBPluginConfig, MarketTerror";

    /// <summary>
    /// The config type name this build writes.
    /// </summary>
    private const string CurrentConfigTypeName = "MarketTerror.MarketTerrorConfig, MarketTerror";

    /// <summary>
    /// The internal name Lifestream is installed under.
    /// </summary>
    private const string LifestreamInternalName = "Lifestream";

    /// <summary>
    /// The item buffer timeout configs written before version 2 defaulted to.
    /// </summary>
    private const int LegacyItemRefreshTimeout = 30000;

    /// <summary>
    /// The chat commands that open the main window.
    /// </summary>
    private static readonly string[] OpenCommands = { "/pmb", "/mt", "/marketterror" };

    /// <summary>
    /// The arguments that toggle the buy list window instead of searching.
    /// </summary>
    private static readonly string[] BuyListCommands = { "buylist", "shoppinglist" };

    private readonly IFontHandle defaultFontHandle;

    private readonly IFontHandle titleFontHandle;

    private readonly BoardManager boardManager;

    private readonly MarketTerrorConfigWindow marketBoardConfigWindow;

    private readonly ShoppingListWindow shoppingListWindow;

    private readonly ThemeEditorWindow themeEditorWindow;

    private readonly IntegrationsWindow integrationsWindow;

    /// <summary>
    /// Gets the window system.
    /// </summary>
    private readonly WindowSystem windowSystem = new(typeof(MarketTerrorPlugin).AssemblyQualifiedName);

    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketTerrorPlugin"/> class.
    /// This is the plugin's entry point.
    /// </summary>
    /// <param name="pluginInterface">The Dalamud plugin interface.</param>
    /// <param name="dataManager">The data manager.</param>
    /// <param name="commandManager">The command manager.</param>
    /// <param name="framework">The framework.</param>
    /// <param name="clientState">The client state.</param>
    /// <param name="gameGui">The game GUI.</param>
    /// <param name="chatGui">The chat GUI.</param>
    /// <param name="textureProvider">The texture provider.</param>
    /// <param name="log">The plugin log.</param>
    /// <param name="contextMenu">The context menu.</param>
    /// <param name="playerState">The player state.</param>
    /// <param name="addonLifecycle">The addon lifecycle.</param>
    /// <param name="objectTable">The object table.</param>
    /// <param name="marketBoard">The market board events.</param>
    public MarketTerrorPlugin(
      IDalamudPluginInterface pluginInterface,
      IDataManager dataManager,
      ICommandManager commandManager,
      IFramework framework,
      IClientState clientState,
      IGameGui gameGui,
      IChatGui chatGui,
      ITextureProvider textureProvider,
      IPluginLog log,
      IContextMenu contextMenu,
      IPlayerState playerState,
      IAddonLifecycle addonLifecycle,
      IObjectTable objectTable,
      IMarketBoard marketBoard)
    {
      this.PluginInterface = pluginInterface;
      this.DataManager = dataManager;
      this.CommandManager = commandManager;
      this.Framework = framework;
      this.ClientState = clientState;
      this.GameGui = gameGui;
      this.ChatGui = chatGui;
      this.TextureProvider = textureProvider;
      this.Log = log;
      this.ContextMenu = contextMenu;
      this.PlayerState = playerState;
      this.AddonLifecycle = addonLifecycle;
      this.ObjectTable = objectTable;
      this.MarketBoard = marketBoard;

      this.UniversalisClient = new UniversalisClient(this);
      this.FFXIVMTClient = new FFXIVMTClient(this);
      this.ShoppingListBulkAdd = new ShoppingListBulkAdd(this);

      MigrateLegacyConfigTypeName(this.PluginInterface, this.Log);

      this.Config = this.PluginInterface.GetPluginConfig() as MarketTerrorConfig ?? new MarketTerrorConfig();

      this.MigrateConfig();

      this.ShoppingList = new ShoppingListStore(this);
      this.ItemLists = new ItemListStore(this);
      this.WorldCatalogue = new WorldCatalogue(this);
      this.ShoppingListScope = new ShoppingListScope(this);

      this.AutoSearch = new MarketBoardAutoSearch(
        this.PluginInterface,
        this.Framework,
        this.GameGui,
        this.AddonLifecycle,
        this.Log,
        () => this.Config.AutoSearchOnMarketBoard,
        () => this.Config.AutoOpenSearchResult);

      this.defaultFontHandle = this.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        e.OnPreBuild(toolkit =>
        {
          var fontStream = typeof(MarketTerrorPlugin).Assembly.GetManifestResourceStream("MarketTerror.Resources.NotoSans-Medium-NNBSP.otf");

          if (fontStream == null)
          {
            this.Log.Warning("Failed to load embedded font MarketTerror.Resources.NotoSans-Medium-NNBSP.otf");
            return;
          }

          toolkit.AddFontFromStream(
            fontStream,
            new SafeFontConfig()
            {
              SizePx = UiBuilder.DefaultFontSizePx,
              GlyphRanges = FontAtlasBuildToolkitUtilities.ToGlyphRange(char.ConvertFromUtf32(0x202F)),
              MergeFont = toolkit.AddDalamudDefaultFont(-1),
            },
            false,
            "NNBSP");
        }));

      this.titleFontHandle = this.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        e.OnPreBuild(toolkit =>
          toolkit.AddDalamudDefaultFont(this.PluginInterface.UiBuilder.DefaultFontSpec.SizePx * 1.5f)));

      var imPlotStylePtr = ImPlot.GetStyle();

      imPlotStylePtr.Use24HourClock = DateTimeFormatInfo.CurrentInfo.ShortTimePattern.Contains('H', StringComparison.InvariantCulture);
      imPlotStylePtr.UseISO8601 = DateTimeFormatInfo.CurrentInfo.ShortDatePattern != "M/d/yyyy";
      imPlotStylePtr.UseLocalTime = true;

      this.boardManager = new BoardManager(this, this.defaultFontHandle, this.titleFontHandle, this.windowSystem);
      this.marketBoardConfigWindow = new MarketTerrorConfigWindow(this);
      this.ShoppingListBuyer = new ShoppingListBuyer(this);
      this.shoppingListWindow = new ShoppingListWindow(this);
      this.themeEditorWindow = new ThemeEditorWindow(this);
      this.integrationsWindow = new IntegrationsWindow(this.boardManager.MainWindow.Context);

      this.windowSystem.AddWindow(this.boardManager.MainWindow);
      this.windowSystem.AddWindow(this.marketBoardConfigWindow);
      this.windowSystem.AddWindow(this.shoppingListWindow);
      this.windowSystem.AddWindow(this.themeEditorWindow);
      this.windowSystem.AddWindow(this.integrationsWindow);

      // Set up command handlers
      foreach (var command in OpenCommands)
      {
        this.CommandManager.AddHandler(command, new CommandInfo(this.OnOpenMarketBoardCommand)
        {
          HelpMessage = "Open the Market Terror window. Add \"buylist\" to toggle the buy list window.",
        });
      }

      this.PluginInterface.UiBuilder.Draw += this.DrawUi;
      this.PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
      this.PluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;

      // Set up context menu
      this.ContextMenu.OnMenuOpened += this.OnContextMenuOpened;

      // The remembered unlock states belong to one character, so drop them when the character changes
      this.ClientState.Login += ItemUnlock.Forget;
      this.ClientState.Logout += this.OnLogout;

      // Set up number format
      if (this.NumberFormatInfo != null)
      {
        this.NumberFormatInfo.CurrencySymbol = SeIconChar.Gil.ToIconString();
        this.NumberFormatInfo.CurrencyDecimalDigits = 0;
      }

#if DEBUG
      if (this.Config.OpenOnStart)
      {
        this.boardManager.MainWindow.IsOpen = true;
      }
#endif
    }

    /// <summary>
    /// Gets the plugin's name.
    /// </summary>
    public static string Name => "Market Terror";

    /// <summary>
    /// Gets the plugin's configuration.
    /// </summary>
    public MarketTerrorConfig Config { get; private set; }

    /// <summary>
    /// Gets a value indicating whether windows may show themselves when the plugin starts.
    /// </summary>
    /// <remarks>Only debug builds have the setting, so release builds keep showing what was left open.</remarks>
    public bool ShowWindowsOnStart =>
#if DEBUG
      this.Config.OpenOnStart;
#else
      true;
#endif

    /// <summary>
    /// Gets a value indicating whether the Lifestream plugin can be used right now.
    /// </summary>
    public bool IsLifestreamAvailable => this.FindPlugin(LifestreamInternalName)?.IsLoaded == true;

    /// <summary>
    /// Gets a value indicating whether the Lifestream plugin is installed but switched off.
    /// </summary>
    public bool IsLifestreamDisabled => this.FindPlugin(LifestreamInternalName) is { IsLoaded: false };

    /// <summary>
    /// Gets the shopping list.
    /// </summary>
    public ShoppingListStore ShoppingList { get; init; }

    /// <summary>
    /// Gets the item lists the user has made.
    /// </summary>
    public ItemListStore ItemLists { get; init; }

    /// <summary>
    /// Gets the service that adds a whole category to the shopping list.
    /// </summary>
    public ShoppingListBulkAdd ShoppingListBulkAdd { get; init; }

    /// <summary>
    /// Gets the service that buys shopping list rows off the Market Board.
    /// </summary>
    public ShoppingListBuyer ShoppingListBuyer { get; init; }

    /// <summary>
    /// Gets every world the market board can be priced at.
    /// </summary>
    public WorldCatalogue WorldCatalogue { get; init; }

    /// <summary>
    /// Gets the world and scope the shopping list prices its items against.
    /// </summary>
    public ShoppingListScope ShoppingListScope { get; init; }

    /// <summary>
    /// Gets the state and services shared by the main window's components.
    /// </summary>
    public MarketBoardContext MarketBoardContext => this.boardManager.MainWindow.Context;

    /// <summary>
    /// Gets the number format info.
    /// </summary>
    public NumberFormatInfo NumberFormatInfo { get; init; } = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();

    /// <summary>
    /// Gets the Dalamud plugin interface.
    /// </summary>
    public IDalamudPluginInterface PluginInterface { get; init; }

    /// <summary>
    /// Gets the data manager.
    /// </summary>
    public IDataManager DataManager { get; init; }

    /// <summary>
    /// Gets the command manager.
    /// </summary>
    public ICommandManager CommandManager { get; init; }

    /// <summary>
    /// Gets the framework.
    /// </summary>
    public IFramework Framework { get; init; }

    /// <summary>
    /// Gets the client state.
    /// </summary>
    public IClientState ClientState { get; init; }

    /// <summary>
    /// Gets the game GUI.
    /// </summary>
    public IGameGui GameGui { get; init; }

    /// <summary>
    /// Gets the chat GUI.
    /// </summary>
    public IChatGui ChatGui { get; init; }

    /// <summary>
    /// Gets the texture provider.
    /// </summary>
    public ITextureProvider TextureProvider { get; init; }

    /// <summary>
    /// Gets the plugin log.
    /// </summary>
    public IPluginLog Log { get; init; }

    /// <summary>
    /// Gets the context menu.
    /// </summary>
    public IContextMenu ContextMenu { get; init; }

    /// <summary>
    /// Gets the player state.
    /// </summary>
    public IPlayerState PlayerState { get; init; }

    /// <summary>
    /// Gets the addon lifecycle.
    /// </summary>
    public IAddonLifecycle AddonLifecycle { get; init; }

    /// <summary>
    /// Gets the object table.
    /// </summary>
    public IObjectTable ObjectTable { get; init; }

    /// <summary>
    /// Gets the market board events the game raises as it receives listings.
    /// </summary>
    public IMarketBoard MarketBoard { get; init; }

    /// <summary>
    /// Gets the Universalis client used for accessing market board data.
    /// </summary>
    public UniversalisClient UniversalisClient { get; init; }

    /// <summary>
    /// Gets the FFXIVMT client used for accessing gilflux ranking data.
    /// </summary>
    public FFXIVMTClient FFXIVMTClient { get; init; }

    /// <summary>
    /// Gets the Market Board auto-search service.
    /// </summary>
    public MarketBoardAutoSearch AutoSearch { get; init; }

    /// <inheritdoc/>
    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resets the market data.
    /// </summary>
    public void ResetMarketData()
    {
      this.boardManager.MainWindow.ResetMarketData();

      foreach (var window in this.boardManager.Detached)
      {
        window.Board.Context.ResetMarketData();
      }
    }

    /// <summary>
    /// Opens the main UI.
    /// </summary>
    public void OpenMainUi()
    {
      this.boardManager.MainWindow.IsOpen = true;
    }

    /// <summary>
    /// Opens the config UI.
    /// </summary>
    public void OpenConfigUi()
    {
      this.marketBoardConfigWindow.IsOpen = true;
    }

    /// <summary>
    /// Opens the theme editor.
    /// </summary>
    public void OpenThemeEditor()
    {
      this.themeEditorWindow.IsOpen = true;
    }

    /// <summary>
    /// Opens the integrations window.
    /// </summary>
    public void OpenIntegrations()
    {
      this.integrationsWindow.IsOpen = true;
    }

    /// <summary>
    /// Shows the shopping list window, or hides it when it is already shown.
    /// </summary>
    public void ToggleShoppingList()
    {
      this.shoppingListWindow.ToggleShown();
    }

    /// <summary>
    /// Brings the shopping list window up, whether or not it was hidden.
    /// </summary>
    public void ShowShoppingList()
    {
      this.shoppingListWindow.Show();
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
        // Save config
        this.PluginInterface.SavePluginConfig(this.Config);

        // Remove windows - the board manager unregisters its own before the rest go, since
        // the window system rejects a window that is no longer registered with it.
        this.boardManager.SaveLayout();
        this.boardManager.Dispose();
        this.windowSystem.RemoveAllWindows();
        this.shoppingListWindow.Dispose();
        this.defaultFontHandle.Dispose();
        this.titleFontHandle.Dispose();

        // Remove command handlers
        foreach (var command in OpenCommands)
        {
          this.CommandManager.RemoveHandler(command);
        }

        // Remove context menu handler
        this.ContextMenu.OnMenuOpened -= this.OnContextMenuOpened;

        // Remove character change handlers
        this.ClientState.Login -= ItemUnlock.Forget;
        this.ClientState.Logout -= this.OnLogout;

        // Dispose clients
        this.FFXIVMTClient.Dispose();

        // Dispose the auto-search service
        this.AutoSearch.Dispose();

        // Stop any category still being added to the shopping list
        this.ShoppingListBulkAdd.Dispose();

        // Stop any buy run and drop its purchase state machine
        this.ShoppingListBuyer.Dispose();
      }

      this.isDisposed = true;
    }

    /// <summary>
    /// Rewrites the type name Dalamud stored in the config file so settings saved before the rename still load.
    /// </summary>
    /// <param name="pluginInterface">The Dalamud plugin interface.</param>
    /// <param name="log">The plugin log.</param>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed migration must not stop the plugin from loading.")]
    private static void MigrateLegacyConfigTypeName(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
      try
      {
        var configPath = pluginInterface.ConfigFile.FullName;

        if (!File.Exists(configPath))
        {
          return;
        }

        var json = File.ReadAllText(configPath);

        if (!json.Contains(LegacyConfigTypeName, StringComparison.Ordinal) && !json.Contains(RenamedAssemblyConfigTypeName, StringComparison.Ordinal))
        {
          return;
        }

        json = json
          .Replace(LegacyConfigTypeName, CurrentConfigTypeName, StringComparison.Ordinal)
          .Replace(RenamedAssemblyConfigTypeName, CurrentConfigTypeName, StringComparison.Ordinal);

        File.WriteAllText(configPath, json);
        log.Information("Migrated the saved configuration to the Market Terror type name.");
      }
      catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
      {
        log.Error(ex, "Failed to migrate the saved configuration; settings will fall back to defaults.");
      }
    }

    /// <summary>
    /// Brings a configuration written by an older build up to the current version.
    /// </summary>
    private void MigrateConfig()
    {
      if (this.Config.Version >= MarketTerrorConfig.CurrentVersion)
      {
        return;
      }

      // The old 30s buffer left prices looking stale, so anyone still on it follows the new default.
      if (this.Config.ItemRefreshTimeout == LegacyItemRefreshTimeout)
      {
        this.Config.ItemRefreshTimeout = MarketTerrorConfig.DefaultItemRefreshTimeout;
      }

      // The two cross-world flags became one scope, and the Oceania toggle folded into it at its old default.
      if (this.Config.CrossDataCenter)
      {
        this.Config.MarketBoardScope = MarketScope.RegionWithOceania;
      }
      else if (this.Config.CrossWorld)
      {
        this.Config.MarketBoardScope = MarketScope.DataCentre;
      }

      this.Config.CrossDataCenter = false;
      this.Config.CrossWorld = false;

      // The one favourites collection became the first of any number of lists. The version gate runs
      // this once, so deleting that list later does not bring it back on the next reload.
      if (this.Config.Favorites.Count > 0)
      {
        var favorites = new ItemList { Name = "Favorites" };
        favorites.ItemIds.AddRange(this.Config.Favorites);
        this.Config.ItemLists.Add(favorites);
        this.Config.Favorites.Clear();
      }

      // The buy list stopped being a row per item and became an entry per listing rule, so there is
      // nothing in the old shape worth reading back.
      if (this.Config.ShoppingList.Count > 0)
      {
        this.Config.ShoppingList.Clear();
        this.ChatGui.Print("MarketTerror: the buy list was reset by the rebuild of shopping lists.");
      }

      this.Config.Version = MarketTerrorConfig.CurrentVersion;
      this.PluginInterface.SavePluginConfig(this.Config);
    }

    /// <summary>
    /// Finds an installed plugin whether or not it is switched on, so callers can tell a plugin that
    /// is absent from one that is merely disabled.
    /// </summary>
    /// <param name="internalName">The internal name the plugin is installed under.</param>
    /// <returns>The installed plugin, or null when it is not installed.</returns>
    private IExposedPlugin? FindPlugin(string internalName)
    {
      return this.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == internalName);
    }

    /// <summary>
    /// Drops the remembered unlock states when the character logs out.
    /// </summary>
    /// <param name="type">The logout type.</param>
    /// <param name="code">The logout code.</param>
    private void OnLogout(int type, int code)
    {
      ItemUnlock.Forget();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
      if (!this.Config.ContextMenuIntegration)
      {
        return;
      }

      uint itemId;

      if (args.MenuType == ContextMenuType.Inventory)
      {
        itemId = (args.Target as MenuTargetInventory)?.TargetItem?.BaseItemId ?? 0u;
      }
      else
      {
        itemId = this.GetItemIdFromAgent(args.AddonName);

        if (itemId == 0u)
        {
          this.Log.Warning("Failed to get item ID from agent {0}. Attempting hovered item.", args.AddonName ?? "null");
          itemId = (uint)this.GameGui.HoveredItem % 500000;
        }
      }

      if (itemId == 0u)
      {
        this.Log.Warning("Failed to get item ID");
        return;
      }

      var item = this.DataManager.Excel.GetSheet<Item>().GetRowOrDefault(itemId);

      if (!item.HasValue)
      {
        this.Log.Warning("Failed to get item data for item ID {0}", itemId);
        return;
      }

      args.AddMenuItem(new MenuItem
      {
        Name = "Search in Market Board",
        OnClicked = this.GetMenuItemClickedHandler(itemId),
        Prefix = SeIconChar.BoxedLetterM,
        PrefixColor = 48,
        IsEnabled = !item.Value.IsUntradable,
      });

      args.AddMenuItem(new MenuItem
      {
        Name = "Add to shopping list",
        OnClicked = this.GetAddToShoppingListHandler(item.Value),
        Prefix = SeIconChar.BoxedLetterM,
        PrefixColor = 48,
        IsEnabled = !item.Value.IsUntradable,
      });
    }

    private unsafe uint GetItemIdFromAgent(string? addonName)
    {
      var itemId = addonName switch
      {
        "ChatLog" => AgentChatLog.Instance()->ContextItemId,
        "GatheringNote" => *(uint*)((IntPtr)AgentGatheringNote.Instance() + 0xA0),
        "GrandCompanySupplyList" => *(uint*)((IntPtr)AgentGrandCompanySupply.Instance() + 0x54),
        "ItemSearch" => (uint)AgentContext.Instance()->UpdateCheckerParam,
        "RecipeNote" => AgentRecipeNote.Instance()->ContextMenuResultItemId,
        _ => 0u,
      };

      return itemId % 500000;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Context menu callback should log and continue without breaking the plugin UI.")]
    private Action<IMenuItemClickedArgs> GetMenuItemClickedHandler(uint itemId)
    {
      return (IMenuItemClickedArgs args) =>
      {
        try
        {
          this.boardManager.MainWindow.IsOpen = true;
          this.boardManager.MainWindow.ChangeSelectedItem(itemId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
          this.Log.Error(ex, "Failed on context menu for itemId" + itemId);
        }
      };
    }

    /// <summary>
    /// Builds the click handler that puts an item on the shopping list from the game's own context menu.
    /// </summary>
    /// <param name="item">The item the menu was opened on.</param>
    /// <returns>The handler.</returns>
    /// <remarks>
    /// Nothing of the plugin is on screen when the menu is used, so a new entry is only visible as a
    /// chat line. One that was already there brings the window up on its own.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Context menu callback should log and continue without breaking the plugin UI.")]
    private Action<IMenuItemClickedArgs> GetAddToShoppingListHandler(Item item)
    {
      return (IMenuItemClickedArgs args) =>
      {
        try
        {
          if (this.MarketBoardContext.TryAddCheapestToShoppingList(item))
          {
            this.ChatGui.Print($"MarketTerror: added {item.Name.ExtractText()} to the shopping list.");
          }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
          this.Log.Error(ex, "Failed to add item {0} to the shopping list from the context menu.", item.RowId);
        }
      };
    }

    private void OnOpenMarketBoardCommand(string command, string arguments)
    {
      if (!string.IsNullOrEmpty(arguments))
      {
#if DEBUG
        if (ItemUnlockDebug.TryHandle(this.DataManager, this.PlayerState, this.Log, arguments))
        {
          return;
        }
#endif

        if (BuyListCommands.Contains(arguments.Trim(), StringComparer.OrdinalIgnoreCase))
        {
          this.shoppingListWindow.ToggleShown();
        }
        else if (uint.TryParse(arguments, out var itemId))
        {
          this.boardManager.MainWindow.ChangeSelectedItem(itemId);
          this.boardManager.MainWindow.IsOpen = true;
        }
        else
        {
          this.boardManager.MainWindow.SearchString = arguments;
          this.boardManager.MainWindow.IsOpen = true;
        }
      }
      else
      {
        this.boardManager.MainWindow.IsOpen = !this.boardManager.MainWindow.IsOpen;
      }
    }

    private void DrawUi()
    {
      this.windowSystem.Draw();
      this.boardManager.ApplyPendingChanges();

      // After every window, because the wash has to stay on the shared style until ImGui ends the frame.
      ModalDim.Apply(this.boardManager.Services.Theme);
    }

    /// <summary>
    /// Notify the chat that something was copied to the clipboard.
    /// </summary>
    /// <param name="text">The copied text.</param>
    #pragma warning disable SA1202 // Allow public method placement for readability
    public void NotifyClipboardCopied(string text)
    {
      try
      {
        if (string.IsNullOrEmpty(text))
        {
          this.ChatGui.Print("Copied text to clipboard.");
        }
        else
        {
          this.ChatGui.Print($"Copied to clipboard: {text}");
        }
      }
      catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
      {
        this.Log.Warning($"Failed to print clipboard notification to chat: {ex.Message}");
      }
    }

    /// <summary>
    /// Notify the chat about a clipboard action that is not a copy, so it is not worded as one.
    /// </summary>
    /// <param name="text">The message to print.</param>
    public void NotifyClipboard(string text)
    {
      if (string.IsNullOrEmpty(text))
      {
        return;
      }

      try
      {
        this.ChatGui.Print(text);
      }
      catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
      {
        this.Log.Warning($"Failed to print clipboard notification to chat: {ex.Message}");
      }
    }
    #pragma warning restore SA1202
  }
}
