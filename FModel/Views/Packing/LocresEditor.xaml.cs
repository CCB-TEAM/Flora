using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using FModel.Framework;
using FModel.Framework.Packing;
using FModel.Framework.Packing.Locres;
using FModel.Localization;
using FModel.ViewModels;
using Serilog;

namespace FModel.Views.Packing;

/// <summary>Edits the strings of a .locres of the pak; every change is written to the workspace copy right away.</summary>
public partial class LocresEditor : IPackEditor
{
    private static string L(string key) => LocalizationManager.Instance.Get(key);

    private sealed class Row : ViewModel
    {
        private string _text;

        public Row(LocresEntryDto entry)
        {
            Entry = entry;
            Original = entry.Text;
            _text = entry.Text;
        }

        public LocresEntryDto Entry { get; }
        public string Namespace => Entry.Namespace;
        public string Key => Entry.Key;
        public string Original { get; }
        public string Text { get => _text; set => SetProperty(ref _text, value ?? ""); }
        public bool Changed => Text != Original;
    }

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private PackWorkspaceViewModel _viewModel = null!;
    private PackItem _item = null!;
    private LocresPreviewDto _locres = null!;
    private Row[] _rows = [];
    private ICollectionView? _view;

    public LocresEditor()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        Unloaded += (_, _) =>
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                Save();
            }
        };
    }

    public void Load(PackItem item, PackWorkspaceViewModel viewModel)
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }

        _item = item;
        _viewModel = viewModel;
        try
        {
            _locres = LocresResourceCodec.Read(File.ReadAllBytes(item.DiskPath));
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to read locres {Path}", item.PakPath);
            InfoText.Text = string.Format(L("Pack_StatusFailed"), e.Message);
            Grid.ItemsSource = null;
            return;
        }

        _rows = _locres.Entries.Select(o => new Row(o)).ToArray();
        foreach (var row in _rows)
            row.PropertyChanged += (_, _) =>
            {
                _saveTimer.Stop();
                _saveTimer.Start();
                UpdateInfo();
            };
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Match;
        Grid.ItemsSource = _view;
        FilterBox.Text = "";
        OnlyChanged.IsChecked = false;
        UpdateInfo();
    }

    private bool Match(object o)
    {
        if (o is not Row row) return false;
        if (OnlyChanged.IsChecked == true && !row.Changed) return false;
        var filter = FilterBox.Text.Trim();
        return filter.Length == 0 || row.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.Namespace.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Original.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void UpdateInfo() => InfoText.Text = string.Format(L("Pack_LocInfo"), _locres.Version, _rows.Length, _rows.Count(o => o.Changed));

    private void Save()
    {
        try
        {
            var entries = _rows.Select(o => o.Entry with { Text = o.Text }).ToArray();
            var data = LocresResourceCodec.Write(_locres with { Entries = entries });
            _viewModel.ReplaceContent(_item, data, _item.Origin);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to write locres {Path}", _item.PakPath);
            InfoText.Text = string.Format(L("Pack_StatusFailed"), e.Message);
        }
    }
}
