using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using FModel.Framework;
using FModel.Services;
using Newtonsoft.Json.Linq;
using UAssetAPI;

namespace FModel.ViewModels;

public class ImportManagerViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;

    public FullyObservableCollection<ImportItem> Imports { get; private set; }
    public ICollectionView ImportsView { get; private set; }

    private bool _hasImports;
    public bool HasImports
    {
        get => _hasImports;
        private set => SetProperty(ref _hasImports, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            SetProperty(ref _searchText, value);
            ImportsView.Refresh();
        }
    }

    private ImportItem _selectedImport;
    public ImportItem SelectedImport
    {
        get => _selectedImport;
        set => SetProperty(ref _selectedImport, value);
    }

    private bool _needsRefresh;
    public bool NeedsRefresh
    {
        get => _needsRefresh;
        private set => SetProperty(ref _needsRefresh, value);
    }

    private TabItem _sourceTab;

    public ImportManagerViewModel()
    {
        // Pre-initialize collections so bindings are ready before async load completes
        Imports = new FullyObservableCollection<ImportItem>();
        ImportsView = new ListCollectionView(Imports)
        {
            Filter = FilterImports,
            SortDescriptions = { new SortDescription("Index", ListSortDirection.Ascending) }
        };
        HasImports = false;
    }

    public void SetSourceTab(TabItem tab)
    {
        _sourceTab = tab;
    }

    public async Task RefreshFromSourceAsync()
    {
        if (_sourceTab == null)
            return;

        var json = _sourceTab.Document?.Text;
        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            var root = JObject.Parse(json);
            var importsArray = root["Imports"] as JArray;
            if (importsArray == null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Imports.Clear();
                    HasImports = false;
                    NeedsRefresh = false;
                });
                return;
            }

            var items = await Task.Run(() =>
            {
                var result = new List<ImportItem>();
                for (int i = 0; i < importsArray.Count; i++)
                {
                    var imp = importsArray[i];
                    result.Add(new ImportItem
                    {
                        Index = i,
                        ClassPackage = imp["ClassPackage"]?.ToString() ?? string.Empty,
                        ClassName = imp["ClassName"]?.ToString() ?? string.Empty,
                        ObjectName = imp["ObjectName"]?.ToString() ?? string.Empty,
                        OuterIndex = (int)(imp["OuterIndex"]?["Index"]?.Value<long>() ?? 0)
                    });
                }
                return result;
            });

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Imports.Clear();
                foreach (var item in items)
                {
                    Imports.Add(item);
                }
                HasImports = Imports.Count > 0;
                NeedsRefresh = false;
            });
        }
        catch
        {
            // JSON parse error - ignore
        }
    }

    public void MarkNeedsRefresh()
    {
        NeedsRefresh = true;
    }

    public async Task LoadImportsAsync(List<UAssetAPI.Import> imports)
    {
        // Process imports on background thread
        var items = await Task.Run(() =>
        {
            return imports?.Select((imp, index) => new ImportItem
            {
                Index = index,
                ClassPackage = imp.ClassPackage?.ToString() ?? string.Empty,
                ClassName = imp.ClassName?.ToString() ?? string.Empty,
                ObjectName = imp.ObjectName?.ToString() ?? string.Empty,
                OuterIndex = imp.OuterIndex.Index
            }).ToList() ?? new List<ImportItem>();
        });

        // Update collection on UI thread
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Imports.Clear();
            foreach (var item in items)
            {
                Imports.Add(item);
            }
            HasImports = Imports.Count > 0;
        });
    }

    internal bool FilterImports(object obj)
    {
        if (string.IsNullOrWhiteSpace(_searchText))
            return true;

        if (obj is ImportItem item)
        {
            var searchLower = _searchText.ToLower();
            return item.ClassPackage.ToLower().Contains(searchLower) ||
                   item.ClassName.ToLower().Contains(searchLower) ||
                   item.ObjectName.ToLower().Contains(searchLower);
        }
        return false;
    }

    public void RefreshFilter()
    {
        ImportsView?.Refresh();
    }
}

public class ImportItem : ViewModel
{
    public int Index { get; set; }
    public string ClassPackage { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public int OuterIndex { get; set; }

    public string FullPath => string.IsNullOrEmpty(ClassPackage) ? ObjectName : $"{ClassPackage}.{ClassName}.{ObjectName}";
    public string DisplayName => string.IsNullOrEmpty(ObjectName) ? ClassName : ObjectName;
}
