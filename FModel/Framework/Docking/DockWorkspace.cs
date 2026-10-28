using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using FModel.Localization;
using Serilog;

namespace FModel.Framework.Docking;

public enum EDockTarget
{
    Document,
    Left,
    Right,
    Bottom,
    Float
}

/// <summary>
/// Optional hooks for content hosted in a dock panel (replaces Window.Closing / Window.Closed).
/// </summary>
public interface IDockPanel
{
    /// <returns>false to keep the panel open</returns>
    bool OnPanelClosing() => true;

    void OnPanelClosed() { }
}

/// <summary>
/// Owns the main window's <see cref="DockingManager"/>: opens tool panels and documents inside it instead of
/// separate windows, and lets any panel float into its own Windows window (own taskbar entry, system frame) or
/// stay on top of everything (sticky note).
/// </summary>
public static class DockWorkspace
{
    /// <summary>Where the layout and the window options are saved (a test points it somewhere else).</summary>
    public static string StorageDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel");

    private static string LayoutPath => Path.Combine(StorageDirectory, "DockLayout.xml");

    // panels declared in MainWindow.xaml (id -> title key), they are never closed (only hidden)
    private static readonly Dictionary<string, string> StaticContents = new()
    {
        ["Archives"] = "Main_Tab_Archives",
        ["Folders"] = "Main_Tab_Folders",
        ["Packages"] = "Main_Tab_Packages",
        ["Workspace"] = "Main_Tab_Workspace",
        ["Output"] = "Main_Tab_Output"
    };

    private static string FlagsPath => Path.Combine(StorageDirectory, "DockFlags.json");

    private static readonly HashSet<string> Pinned = [];
    private static readonly HashSet<string> Independent = [];
    private static readonly Dictionary<string, Window> Popups = [];
    private static readonly Dictionary<string, (double Width, double Height)> PopupSizes = [];
    private static readonly Dictionary<string, Func<FrameworkElement>> Restorable = [];
    private static string _defaultLayout;

    public static DockingManager Manager { get; private set; }

    private static bool IsStatic(LayoutContent content) => content.ContentId != null && StaticContents.ContainsKey(content.ContentId);

    public static void Attach(DockingManager manager)
    {
        Manager = manager;
        manager.AnchorableContextMenu = BuildContextMenu(true);
        manager.DocumentContextMenu = BuildContextMenu(false);
        manager.LayoutFloatingWindowControlCreated += (_, e) => OnFloatingWindowCreated(e.LayoutFloatingWindowControl);

        foreach (var content in AllContents())
            Hook(content);

        _defaultLayout = SerializeLayout();
    }

    /// <summary>
    /// Panels that can be recreated without arguments are reopened when the saved layout is restored.
    /// </summary>
    public static void RegisterRestorable(string contentId, Func<FrameworkElement> factory) => Restorable[contentId] = factory;

    public static IEnumerable<LayoutContent> AllContents()
    {
        if (Manager?.Layout == null)
            return [];

        return Manager.Layout.Descendents().OfType<LayoutContent>()
            .Concat(Manager.Layout.Hidden)
            .Distinct();
    }

    public static LayoutContent FindContent(string contentId) => AllContents().FirstOrDefault(c => c.ContentId == contentId);

    public static LayoutContent FindContent(FrameworkElement content) => AllContents().FirstOrDefault(c => ReferenceEquals(c.Content, content));

    public static T Find<T>(string contentId) where T : class => FindContent(contentId)?.Content as T;

    public static bool IsOpen(string contentId) => FindContent(contentId) is { } content && content is not LayoutAnchorable { IsHidden: true };

    /// <summary>
    /// Brings the panel with that id to front, or creates it with <paramref name="factory"/> when it isn't open yet.
    /// </summary>
    public static T Show<T>(string contentId, string title, Func<T> factory, EDockTarget target = EDockTarget.Right,
        double width = 450, double height = 350) where T : FrameworkElement
    {
        if (FindContent(contentId) is { Content: T existingContent } existing)
        {
            Activate(existing);
            return existingContent;
        }

        var content = factory();
        Open(contentId, title, content, target, width, height);
        return content;
    }

    public static LayoutContent Open(string contentId, string title, FrameworkElement content, EDockTarget target,
        double width = 450, double height = 350)
    {
        PrepareContent(content);

        LayoutContent layoutContent;
        var documentPane = Manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (target == EDockTarget.Document && documentPane != null)
        {
            var document = new LayoutDocument { ContentId = contentId, Title = title, Content = content };
            documentPane.Children.Add(document);
            layoutContent = document;
        }
        else
        {
            var anchorable = new LayoutAnchorable
            {
                ContentId = contentId,
                Title = title,
                Content = content,
                CanHide = false, // the close button disposes tool panels, the window menu brings them back
                AutoHideWidth = width,
                AutoHideHeight = height,
                FloatingWidth = width,
                FloatingHeight = height
            };

            anchorable.AddToLayout(Manager, target switch
            {
                EDockTarget.Left => AnchorableShowStrategy.Left,
                EDockTarget.Bottom => AnchorableShowStrategy.Bottom,
                _ => AnchorableShowStrategy.Right
            });

            // a pane created just for this panel gets the requested size instead of splitting the window in half
            if (anchorable.Parent is LayoutAnchorablePane { ChildrenCount: 1 } pane)
            {
                if (target == EDockTarget.Bottom)
                    pane.DockHeight = new GridLength(height);
                else
                    pane.DockWidth = new GridLength(width);
            }

            if (target == EDockTarget.Float)
            {
                anchorable.FloatingLeft = Math.Max(0, (SystemParameters.WorkArea.Width - width) / 2);
                anchorable.FloatingTop = Math.Max(0, (SystemParameters.WorkArea.Height - height) / 2);
                anchorable.Float();
            }

            layoutContent = anchorable;
        }

        Hook(layoutContent);
        layoutContent.IsSelected = true;
        layoutContent.IsActive = true;
        return layoutContent;
    }

    public static void Activate(LayoutContent content)
    {
        if (content is LayoutAnchorable { IsHidden: true } anchorable)
            anchorable.Show();

        content.IsSelected = true;
        content.IsActive = true;

        // a floating panel is a window of its own: it can be minimized or behind other applications
        if (FindFloatingWindow(content) is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
    }

    public static void Activate(string contentId)
    {
        if (Popups.TryGetValue(contentId, out var popup))
        {
            ActivatePopup(popup);
            return;
        }

        if (FindContent(contentId) is { } content)
            Activate(content);
    }

    public static void Close(string contentId) => OnUiThread(() => Close(FindContent(contentId)));

    public static void Close(FrameworkElement content) => OnUiThread(() =>
    {
        if (Popups.Values.FirstOrDefault(o => ReferenceEquals(o.Content, content)) is { } popup)
            popup.Close();
        else
            Close(FindContent(content));
    });

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private static void Close(LayoutContent content)
    {
        if (content != null && !content.Dispatcher.CheckAccess())
        {
            content.Dispatcher.Invoke(() => Close(content));
            return;
        }

        switch (content)
        {
            case null:
                return;
            case LayoutAnchorable anchorable when IsStatic(anchorable):
                anchorable.Hide();
                break;
            default:
                content.Close();
                break;
        }
    }

    public static void SetTitle(FrameworkElement content, string title)
    {
        if (Popups.Values.FirstOrDefault(o => ReferenceEquals(o.Content, content)) is { } popup)
        {
            popup.Title = title;
            return;
        }

        if (FindContent(content) is { } layoutContent)
            layoutContent.Title = title;
    }

    #region Popup windows

    /// <summary>
    /// Opens a tool as an ordinary dialog window (system frame, centered over the main window, always above it, no taskbar button) instead
    /// of a docked panel: for things you open, use and close (key manager, backups, settings...). One window per id; opening it again
    /// brings it to front. The content's <see cref="IDockPanel"/> hooks run when the window closes.
    /// </summary>
    public static T ShowPopup<T>(string id, string title, Func<T> factory, double width, double height, double minWidth = 420, double minHeight = 280,
        bool closeOnEscape = false) where T : FrameworkElement
    {
        if (Popups.TryGetValue(id, out var existing) && existing.Content is T existingContent)
        {
            ActivatePopup(existing);
            return existingContent;
        }

        var content = factory();
        PrepareContent(content);
        var owner = Application.Current?.MainWindow;
        if (PopupSizes.TryGetValue(id, out var saved))
            (width, height) = saved;

        var window = new Window
        {
            Title = title,
            Content = content,
            Width = Math.Min(width, SystemParameters.WorkArea.Width),
            Height = Math.Min(height, SystemParameters.WorkArea.Height),
            MinWidth = minWidth,
            MinHeight = minHeight,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Icon = owner?.Icon,
        };
        if (owner is { IsVisible: true })
            window.Owner = owner;
        window.SetResourceReference(Control.BackgroundProperty, AdonisUI.Brushes.Layer0BackgroundBrush);
        window.SetResourceReference(Control.ForegroundProperty, AdonisUI.Brushes.ForegroundBrush);
        NativeFrame.ApplyDarkTheme(window);

        window.Closing += (_, e) =>
        {
            if (content is IDockPanel panel && !panel.OnPanelClosing())
                e.Cancel = true;
        };
        window.Closed += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal)
                PopupSizes[id] = (window.Width, window.Height);
            Popups.Remove(id);
            window.Content = null; // the content may be shown again
            if (content is IDockPanel panel)
                panel.OnPanelClosed();
        };
        if (closeOnEscape)
        {
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && Keyboard.FocusedElement is not (TextBoxBase or ComboBox { IsDropDownOpen: true }))
                {
                    e.Handled = true;
                    window.Close();
                }
            };
        }

        Popups[id] = window;
        window.Show();
        window.Activate();
        return content;
    }

    private static void ActivatePopup(Window popup)
    {
        if (popup.WindowState == WindowState.Minimized)
            popup.WindowState = WindowState.Normal;
        popup.Activate();
    }

    public static bool IsPopupOpen(string id) => Popups.ContainsKey(id);

    #endregion

    #region Floating windows

    // A panel taken out of the main window is a real Windows window with the system frame and title bar. Like any Windows tool window it
    // is owned by the main window: it always stays above it (never hidden behind it, however often the main window is clicked) and is
    // minimized and restored with it. Two options per panel, both remembered: "Always on top" (above every application) and "Independent
    // window" (own taskbar button; may go behind the main window). AvalonDock still drags it back and docks it.

    private static Style _floatingStyle;

    private static Style FloatingStyle =>
        _floatingStyle ??= (Style) new ResourceDictionary { Source = new Uri("pack://application:,,,/Flora;component/Views/Resources/DockFloatingWindow.xaml") }["NativeFloatingWindowStyle"];

    public static bool IsPinned(LayoutContent content) => Pinned.Contains(content.ContentId);

    /// <summary>Floats the panel into its own window and keeps that window above every other window (like a sticky note).</summary>
    public static void SetPinned(LayoutContent content, bool pinned)
    {
        if (pinned)
            Pinned.Add(content.ContentId);
        else
            Pinned.Remove(content.ContentId);

        if (pinned)
            Float(content);
        if (FindFloatingWindow(content) is { } window)
            ApplyWindowFlagsLater(window);
    }

    public static bool IsIndependent(LayoutContent content) => Independent.Contains(content.ContentId);

    /// <summary>Gives the panel's window its own taskbar button and lets it go behind the main window (otherwise it is owned by it).</summary>
    public static void SetIndependent(LayoutContent content, bool independent)
    {
        if (independent)
            Independent.Add(content.ContentId);
        else
            Independent.Remove(content.ContentId);

        if (independent)
            Float(content);
        if (FindFloatingWindow(content) is { } window)
            ApplyWindowFlagsLater(window);
    }

    public static bool CanFloat(LayoutContent content) => content is { IsFloating: false, CanFloat: true };

    /// <summary>Moves the panel into its own window.</summary>
    public static void Float(LayoutContent content)
    {
        if (content == null || content.IsFloating || !content.CanFloat)
            return;

        if (content is LayoutAnchorable { IsAutoHidden: true } anchorable)
            anchorable.ToggleAutoHide();

        var command = Manager.GetLayoutItemFromModel(content)?.FloatCommand;
        if (command?.CanExecute(null) == true)
            command.Execute(null);
        else
            content.Float();

        if (FindFloatingWindow(content) is { } window)
            ApplyWindowFlagsLater(window);
    }

    /// <summary>Puts a floating panel back where it was docked.</summary>
    public static void DockBack(LayoutContent content)
    {
        if (content is not { IsFloating: true })
            return;

        if (content is LayoutAnchorable anchorable)
            anchorable.Dock();
        else
            content.DockAsDocument();
    }

    public static bool HasFloatingWindows => Manager?.FloatingWindows.Any() == true;

    /// <summary>Brings every floating panel back into the main window.</summary>
    public static void DockAllFloating()
    {
        foreach (var content in AllContents().Where(c => c.IsFloating).ToArray())
            DockBack(content);
    }

    private static LayoutContent[] ContentsOf(LayoutFloatingWindowControl window) =>
        window.Model is ILayoutElement model ? model.Descendents().OfType<LayoutContent>().ToArray() : [];

    /// <summary>Docks every panel of a floating window back into the main window.</summary>
    private static void DockWindow(LayoutFloatingWindowControl window)
    {
        foreach (var content in ContentsOf(window))
            DockBack(content);
    }

    private static void TogglePinned(LayoutFloatingWindowControl window)
    {
        var contents = ContentsOf(window);
        var pinned = !contents.Any(IsPinned);
        foreach (var content in contents)
        {
            if (pinned)
                Pinned.Add(content.ContentId);
            else
                Pinned.Remove(content.ContentId);
        }
        ApplyWindowFlagsLater(window);
    }

    private static void ToggleIndependent(LayoutFloatingWindowControl window)
    {
        var contents = ContentsOf(window);
        var independent = !contents.Any(IsIndependent);
        foreach (var content in contents)
        {
            if (independent)
                Independent.Add(content.ContentId);
            else
                Independent.Remove(content.ContentId);
        }
        ApplyWindowFlagsLater(window);
    }

    private static LayoutFloatingWindowControl FindFloatingWindow(LayoutContent content)
    {
        return Manager?.FloatingWindows.FirstOrDefault(w =>
            w.Model is ILayoutElement model && model.Descendents().OfType<LayoutContent>().Contains(content));
    }

    private static void OnFloatingWindowCreated(LayoutFloatingWindowControl window)
    {
        window.Style = FloatingStyle;
        // the docking theme draws its own title bar through a window chrome that removes the system frame
        Microsoft.Windows.Shell.WindowChrome.SetWindowChrome(window, null);
        NativeFrame.ApplyDarkTheme(window);
        window.Icon ??= Window.GetWindow(Manager)?.Icon;
        NativeFrame.AddSystemMenu(window,
        [
            new NativeFrame.SystemMenuItem(LocalizationManager.Instance.Get("Dock_Dock"), () => DockWindow(window)),
            new NativeFrame.SystemMenuItem(LocalizationManager.Instance.Get("Dock_Pin"), () => TogglePinned(window), () => window.Topmost),
            new NativeFrame.SystemMenuItem(LocalizationManager.Instance.Get("Dock_Independent"), () => ToggleIndependent(window), () => window.ShowInTaskbar),
        ]);
        TrackTitle(window);
        ApplyWindowFlagsLater(window, true);
        // the chrome may be put back while the window is initialized, and the frame size is measured when the window is created
        window.SourceInitialized += (_, _) => Microsoft.Windows.Shell.WindowChrome.SetWindowChrome(window, null);
        window.Dispatcher.BeginInvoke(() =>
        {
            Microsoft.Windows.Shell.WindowChrome.SetWindowChrome(window, null);
            NativeFrame.RefreshFrame(window);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The window title is the title of the selected panel of the window, and follows it.</summary>
    private static void TrackTitle(LayoutFloatingWindowControl window)
    {
        void Update()
        {
            if (window.Model is not ILayoutElement model)
                return;
            var contents = model.Descendents().OfType<LayoutContent>().ToArray();
            if (contents.Length > 0)
                window.Title = (contents.FirstOrDefault(c => c.IsSelected) ?? contents[0]).Title;
        }

        var titleDescriptor = DependencyPropertyDescriptor.FromProperty(LayoutContent.TitleProperty, typeof(LayoutContent));
        var watched = new List<LayoutContent>();
        void Watch()
        {
            if (window.Model is not ILayoutElement model)
                return;
            foreach (var content in model.Descendents().OfType<LayoutContent>().Except(watched).ToArray())
            {
                watched.Add(content);
                titleDescriptor?.AddValueChanged(content, OnTitleChanged);
                content.IsSelectedChanged += OnSelectionChanged;
            }
        }

        void OnTitleChanged(object s, EventArgs e) => Update();
        void OnSelectionChanged(object s, EventArgs e) => Update();

        window.Activated += (_, _) => { Watch(); Update(); };
        window.Closed += (_, _) =>
        {
            foreach (var content in watched)
            {
                titleDescriptor?.RemoveValueChanged(content, OnTitleChanged);
                content.IsSelectedChanged -= OnSelectionChanged;
            }
            watched.Clear();
        };

        // the model's children are only attached once the window finished its initialization
        window.Dispatcher.BeginInvoke(() => { Watch(); Update(); }, DispatcherPriority.Loaded);
    }

    private static void ApplyWindowFlagsLater(LayoutFloatingWindowControl window, bool created = false)
    {
        window.Dispatcher.BeginInvoke(() => ApplyWindowFlags(window, created), DispatcherPriority.Loaded);
    }

    private static void ApplyWindowFlags(LayoutFloatingWindowControl window, bool created)
    {
        if (window.Model is not ILayoutElement model)
            return;

        // panels that never floated before have no position yet and would pop up in the screen corner
        if (created && window.Left <= 0 && window.Top <= 0 && Window.GetWindow(Manager) is { } main && main.WindowState != WindowState.Minimized)
        {
            var mainLeft = main.WindowState == WindowState.Maximized ? 0 : main.Left;
            var mainTop = main.WindowState == WindowState.Maximized ? 0 : main.Top;
            window.Left = mainLeft + Math.Max(0, (main.ActualWidth - window.ActualWidth) / 2);
            window.Top = mainTop + Math.Max(0, (main.ActualHeight - window.ActualHeight) / 2);
        }

        var contents = model.Descendents().OfType<LayoutContent>().ToArray();
        if (contents.Length == 0)
            return;

        var independent = contents.Any(c => Independent.Contains(c.ContentId));
        window.Topmost = contents.Any(c => Pinned.Contains(c.ContentId));
        window.Title = (contents.FirstOrDefault(c => c.IsSelected) ?? contents[0]).Title;

        // the docking library sets and clears the owner itself (when the window loads and whenever this property changes);
        // setting the owner by hand races with it
        window.OwnedByDockingManagerWindow = !independent;
        window.ShowInTaskbar = independent;
    }

    #endregion

    #region Context menus

    private static ContextMenu BuildContextMenu(bool anchorable)
    {
        var menu = new ContextMenu();

        menu.Items.Add(CommandItem("Dock_Float", "FloatCommand"));
        menu.Items.Add(CommandItem("Dock_Dock", "DockCommand"));
        if (anchorable)
        {
            menu.Items.Add(CommandItem("Dock_DockAsDocument", "DockAsDocumentCommand"));
            menu.Items.Add(CommandItem("Dock_AutoHide", "AutoHideCommand"));
        }
        else
        {
            menu.Items.Add(CommandItem("Dock_DockAsDocument", "DockAsDocumentCommand"));
            menu.Items.Add(CommandItem("Dock_NewVerticalTabGroup", "NewVerticalTabGroupCommand"));
            menu.Items.Add(CommandItem("Dock_NewHorizontalTabGroup", "NewHorizontalTabGroupCommand"));
        }

        menu.Items.Add(new Separator());

        var pin = new MenuItem { IsCheckable = true };
        pin.SetBinding(HeaderedItemsControl.HeaderProperty, LocBinding("Dock_Pin"));
        pin.Click += (_, _) =>
        {
            if (ContentOf(menu, pin) is { } content)
                SetPinned(content, pin.IsChecked);
        };
        menu.Items.Add(pin);

        var independent = new MenuItem { IsCheckable = true };
        independent.SetBinding(HeaderedItemsControl.HeaderProperty, LocBinding("Dock_Independent"));
        independent.Click += (_, _) =>
        {
            if (ContentOf(menu, independent) is { } content)
                SetIndependent(content, independent.IsChecked);
        };
        menu.Items.Add(independent);

        menu.Items.Add(new Separator());
        if (anchorable)
            menu.Items.Add(CommandItem("Dock_Hide", "HideCommand"));
        else
            menu.Items.Add(CommandItem("Dock_CloseOthers", "CloseAllButThisCommand"));
        menu.Items.Add(CommandItem("Common_Close", "CloseCommand"));

        menu.Opened += (_, _) =>
        {
            var content = ContentOf(menu, pin);
            pin.IsChecked = content != null && IsPinned(content);
            independent.IsChecked = content != null && IsIndependent(content);
        };

        return menu;
    }

    private static LayoutContent ContentOf(ContextMenu menu, FrameworkElement item)
    {
        var content = Resolve(item.DataContext) ?? Resolve(menu.DataContext) ?? Resolve((menu.PlacementTarget as FrameworkElement)?.DataContext);
        if (content == null)
            Log.Warning("Dock context menu: no panel for {Item} / {Menu}", item.DataContext?.GetType(), menu.DataContext?.GetType());
        return content;
    }

    private static LayoutContent Resolve(object context) => context switch
    {
        LayoutContent content => content,
        LayoutItem item => item.LayoutElement,
        LayoutAnchorableControl control => control.Model,
        LayoutDocumentControl control => control.Model,
        _ => null
    };

    private static MenuItem CommandItem(string locKey, string commandPath)
    {
        var item = new MenuItem();
        item.SetBinding(HeaderedItemsControl.HeaderProperty, LocBinding(locKey));
        item.SetBinding(MenuItem.CommandProperty, new Binding(commandPath));
        return item;
    }

    private static Binding LocBinding(string key) => new($"[{key}]")
    {
        Source = LocalizationManager.Instance,
        Mode = BindingMode.OneWay,
        FallbackValue = key
    };

    #endregion

    #region Content lifetime

    private static void PrepareContent(FrameworkElement content)
    {
        // windows used to provide these, a floating dock window uses its own theme colors otherwise
        if (content is Control control)
        {
            if (control.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue)
                control.SetResourceReference(Control.BackgroundProperty, AdonisUI.Brushes.Layer0BackgroundBrush);
            if (control.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
                control.SetResourceReference(Control.ForegroundProperty, AdonisUI.Brushes.ForegroundBrush);
        }
    }

    private static void Hook(LayoutContent content)
    {
        content.Closing -= OnContentClosing;
        content.Closed -= OnContentClosed;
        content.Closing += OnContentClosing;
        content.Closed += OnContentClosed;
    }

    private static bool _closingApplication;

    private static void OnContentClosing(object sender, CancelEventArgs e)
    {
        // the application asked every panel already (ConfirmCloseAll): closing the windows on exit doesn't ask again
        if (!_closingApplication && sender is LayoutContent { Content: IDockPanel panel } && !panel.OnPanelClosing())
            e.Cancel = true;
    }

    /// <summary>
    /// Asks every open panel whether it may close (unsaved edits...) before the application exits; false when one says no.
    /// </summary>
    public static bool ConfirmCloseAll()
    {
        foreach (var content in AllContents().Where(c => c is not LayoutAnchorable { IsHidden: true }).ToArray())
        {
            if (content.Content is IDockPanel panel && !panel.OnPanelClosing())
                return false;
        }

        _closingApplication = true;
        return true;
    }

    private static void OnContentClosed(object sender, EventArgs e)
    {
        if (sender is not LayoutContent content)
            return;

        content.Closing -= OnContentClosing;
        content.Closed -= OnContentClosed;
        Pinned.Remove(content.ContentId);
        Independent.Remove(content.ContentId);

        if (content.Content is IDockPanel panel)
            panel.OnPanelClosed();
    }

    #endregion

    #region Layout persistence

    public static void SaveLayout()
    {
        if (Manager == null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath, SerializeLayout());
            File.WriteAllText(FlagsPath, System.Text.Json.JsonSerializer.Serialize(new DockFlags
            {
                Pinned = [.. Pinned], Independent = [.. Independent],
                PopupSizes = PopupSizes.ToDictionary(o => o.Key, o => new[] { o.Value.Width, o.Value.Height }),
            }));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to save the dock layout");
        }
    }

    public static void RestoreLayout()
    {
        if (!File.Exists(LayoutPath))
            return;

        try
        {
            DeserializeLayout(File.ReadAllText(LayoutPath));
            LoadFlags();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to restore the dock layout, falling back to the default one");
            ResetLayout();
        }
    }

    public static void ResetLayout()
    {
        // panels opened at runtime aren't part of the default layout, close them properly first
        foreach (var content in AllContents().Where(c => !IsStatic(c)).ToArray())
            content.Close();

        Pinned.Clear();
        Independent.Clear();
        DeserializeLayout(_defaultLayout);
    }

    private sealed class DockFlags
    {
        public List<string> Pinned { get; set; } = [];
        public List<string> Independent { get; set; } = [];
        public Dictionary<string, double[]> PopupSizes { get; set; } = [];
    }

    /// <summary>Applies the remembered window options (always on top, independent window) to the panels the layout brought back.</summary>
    private static void LoadFlags()
    {
        Pinned.Clear();
        Independent.Clear();
        try
        {
            if (File.Exists(FlagsPath) && System.Text.Json.JsonSerializer.Deserialize<DockFlags>(File.ReadAllText(FlagsPath)) is { } flags)
            {
                foreach (var id in flags.Pinned.Where(o => AllContents().Any(c => c.ContentId == o)))
                    Pinned.Add(id);
                foreach (var id in flags.Independent.Where(o => AllContents().Any(c => c.ContentId == o)))
                    Independent.Add(id);
                foreach (var (id, size) in flags.PopupSizes.Where(o => o.Value is { Length: 2 }))
                    PopupSizes[id] = (size[0], size[1]);
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to read the dock window flags");
        }

        foreach (var window in Manager.FloatingWindows.ToArray())
            ApplyWindowFlagsLater(window);
    }

    private static string SerializeLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(Manager).Serialize(writer);
        return writer.ToString();
    }

    private static void DeserializeLayout(string xml)
    {
        var serializer = new XmlLayoutSerializer(Manager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Content != null)
                return; // panels declared in MainWindow.xaml keep their content

            if (e.Model.ContentId != null && Restorable.TryGetValue(e.Model.ContentId, out var factory))
            {
                var content = factory();
                PrepareContent(content);
                e.Content = content;
                return;
            }

            e.Cancel = true; // needs arguments we don't have anymore (blueprint viewer, properties...)
        };

        using (var reader = new StringReader(xml))
            serializer.Deserialize(reader);

        foreach (var content in AllContents())
        {
            Hook(content);

            // the saved title is a snapshot, keep following the UI language
            if (content.ContentId != null && StaticContents.TryGetValue(content.ContentId, out var titleKey))
                BindingOperations.SetBinding(content, LayoutContent.TitleProperty, LocBinding(titleKey));
        }
    }

    #endregion
}
