using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
/// separate windows, and lets any panel float as its own window, stay on top of everything (sticky note) or
/// detach from the main window so it gets its own taskbar entry.
/// </summary>
public static class DockWorkspace
{
    private static readonly string LayoutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "DockLayout.xml");

    // panels declared in MainWindow.xaml (id -> title key), they are never closed (only hidden)
    private static readonly Dictionary<string, string> StaticContents = new()
    {
        ["Archives"] = "Main_Tab_Archives",
        ["Folders"] = "Main_Tab_Folders",
        ["Packages"] = "Main_Tab_Packages",
        ["Workspace"] = "Main_Tab_Workspace",
        ["Output"] = "Main_Tab_Output"
    };

    private static readonly HashSet<string> Pinned = [];
    private static readonly HashSet<string> Detached = [];
    private static readonly Dictionary<string, Func<FrameworkElement>> Restorable = [];
    private static string _defaultLayout;

    public static DockingManager Manager { get; private set; }

    private static bool IsStatic(LayoutContent content) => content.ContentId != null && StaticContents.ContainsKey(content.ContentId);

    public static void Attach(DockingManager manager)
    {
        Manager = manager;
        manager.AnchorableContextMenu = BuildContextMenu(true);
        manager.DocumentContextMenu = BuildContextMenu(false);
        manager.LayoutFloatingWindowControlCreated += (_, e) => ApplyWindowFlagsLater(e.LayoutFloatingWindowControl, true);

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

        // a floating panel can be hidden behind other applications
        if (FindFloatingWindow(content) is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    public static void Activate(string contentId)
    {
        if (FindContent(contentId) is { } content)
            Activate(content);
    }

    public static void Close(string contentId) => OnUiThread(() => Close(FindContent(contentId)));

    public static void Close(FrameworkElement content) => OnUiThread(() => Close(FindContent(content)));

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
        if (FindContent(content) is { } layoutContent)
            layoutContent.Title = title;
    }

    #region Floating windows

    public static bool IsPinned(LayoutContent content) => Pinned.Contains(content.ContentId);

    public static bool IsDetached(LayoutContent content) => Detached.Contains(content.ContentId);

    /// <summary>
    /// Floats the panel and keeps its window above every other window, like a sticky note.
    /// </summary>
    public static void SetPinned(LayoutContent content, bool pinned)
    {
        Toggle(Pinned, content.ContentId, pinned);
        FloatAndApply(content, pinned);
    }

    /// <summary>
    /// Floats the panel as an independent window: not owned by the main window (it can go behind it and isn't
    /// minimized with it) and it gets its own taskbar entry.
    /// </summary>
    public static void SetDetached(LayoutContent content, bool detached)
    {
        Toggle(Detached, content.ContentId, detached);
        FloatAndApply(content, detached);
    }

    private static void Toggle(HashSet<string> set, string id, bool on)
    {
        if (on) set.Add(id);
        else set.Remove(id);
    }

    private static void FloatAndApply(LayoutContent content, bool needsWindow)
    {
        if (needsWindow && !content.IsFloating)
        {
            if (!content.CanFloat)
                return;

            if (content is LayoutAnchorable { IsAutoHidden: true } anchorable)
                anchorable.ToggleAutoHide();

            var command = Manager.GetLayoutItemFromModel(content)?.FloatCommand;
            if (command?.CanExecute(null) == true)
                command.Execute(null);
            else
                content.Float();
        }

        if (FindFloatingWindow(content) is { } window)
            ApplyWindowFlagsLater(window);
    }

    private static LayoutFloatingWindowControl FindFloatingWindow(LayoutContent content)
    {
        return Manager?.FloatingWindows.FirstOrDefault(w =>
            w.Model is ILayoutElement model && model.Descendents().OfType<LayoutContent>().Contains(content));
    }

    private static void ApplyWindowFlagsLater(LayoutFloatingWindowControl window, bool created = false)
    {
        // the model's children are only attached once the window finished its initialization
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

        var pinned = contents.Any(c => Pinned.Contains(c.ContentId));
        var detached = contents.Any(c => Detached.Contains(c.ContentId));

        window.Topmost = pinned;
        window.ShowInTaskbar = detached;
        window.Title = contents.FirstOrDefault(c => c.IsSelected)?.Title ?? contents[0].Title;

        var mainWindow = Window.GetWindow(Manager);
        var owner = detached ? null : mainWindow;
        if (!ReferenceEquals(window.Owner, owner) && !ReferenceEquals(window, owner))
            window.Owner = owner;
    }

    #endregion

    #region Context menus

    private static ContextMenu BuildContextMenu(bool anchorable)
    {
        var menu = new ContextMenu();

        menu.Items.Add(CommandItem("Dock_Float", "FloatCommand"));
        if (anchorable)
        {
            menu.Items.Add(CommandItem("Dock_Dock", "DockCommand"));
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

        var detach = new MenuItem { IsCheckable = true };
        detach.SetBinding(HeaderedItemsControl.HeaderProperty, LocBinding("Dock_Detach"));
        detach.Click += (_, _) =>
        {
            if (ContentOf(menu, detach) is { } content)
                SetDetached(content, detach.IsChecked);
        };
        menu.Items.Add(detach);

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
            detach.IsChecked = content != null && IsDetached(content);
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

    private static void OnContentClosing(object sender, CancelEventArgs e)
    {
        if (sender is LayoutContent { Content: IDockPanel panel } && !panel.OnPanelClosing())
            e.Cancel = true;
    }

    private static void OnContentClosed(object sender, EventArgs e)
    {
        if (sender is not LayoutContent content)
            return;

        content.Closing -= OnContentClosing;
        content.Closed -= OnContentClosed;
        Pinned.Remove(content.ContentId);
        Detached.Remove(content.ContentId);

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
        Detached.Clear();
        DeserializeLayout(_defaultLayout);
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
