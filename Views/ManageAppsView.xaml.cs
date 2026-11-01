using System.Collections.ObjectModel;
using System.Diagnostics;
using AppManager.Models;
using AppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AppManager.Views;

/// <summary>应用管理页：添加 exe / 快捷方式、搜索、调整分类和移除应用。</summary>
public sealed partial class ManageAppsView : UserControl
{
    private readonly AppRepository _repository;
    private readonly IconService _iconService;
    private readonly IntPtr _ownerHwnd;
    private readonly Action<bool> _setExternalDialogOpen;

    private AppItem? _selectedApp;
    private bool _refreshing;
    private bool _syncingDetail;
    private bool _pickerOpen;
    private bool _isLoaded;

    public ManageAppsView(
        AppRepository repository,
        IconService iconService,
        IntPtr ownerHwnd,
        Action<bool> setExternalDialogOpen)
    {
        _repository = repository;
        _iconService = iconService;
        _ownerHwnd = ownerHwnd;
        _setExternalDialogOpen = setExternalDialogOpen;

        InitializeComponent();
        AppsList.ItemsSource = Array.Empty<AppItem>();

        Loaded += ManageAppsView_Loaded;
        Unloaded += ManageAppsView_Unloaded;
        RefreshList();
    }

    private void ManageAppsView_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _repository.Changed -= Repository_Changed;
        _repository.Changed += Repository_Changed;
        RefreshList();
    }

    private void ManageAppsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _repository.Changed -= Repository_Changed;
    }

    private void Repository_Changed(object? sender, EventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        // 数据变更可能发生在列表选择事件中，延后一帧刷新，避免重入。
        DispatcherQueue.TryEnqueue(RefreshList);
    }

    private void RefreshList()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var selectedId = _selectedApp?.Id;
            var query = AppSearchBox.Text.Trim();

            var filtered = _repository.Apps
                .Where(app =>
                    string.IsNullOrWhiteSpace(query) ||
                    app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    app.LaunchTarget.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // 整表替换 ItemsSource，避免 ObservableCollection 逐项添加导致列表反复重绘。
            AppsList.ItemsSource = filtered;

            ListEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (filtered.Count == 0)
            {
                var hasAnyApps = _repository.Apps.Count > 0;
                ListEmptyTitle.Text = hasAnyApps ? "没有匹配的应用" : "还没有收纳应用";
                ListEmptyHint.Text = hasAnyApps
                    ? "换一个关键词，或清空搜索框。"
                    : "点击右上角「添加应用」选择 exe 或快捷方式。";
            }

            ListCountText.Text = string.IsNullOrWhiteSpace(query)
                ? $"共 {filtered.Count} 个应用"
                : $"{filtered.Count} 个匹配";

            var selected = string.IsNullOrWhiteSpace(selectedId)
                ? null
                : filtered.FirstOrDefault(app => app.Id == selectedId);
            _selectedApp = selected;
            AppsList.SelectedItem = selected;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateDetail();
    }

    private void UpdateDetail()
    {
        var app = _selectedApp;
        if (app is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoSelectionState.Visibility = Visibility.Visible;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        NoSelectionState.Visibility = Visibility.Collapsed;

        DetailNameText.Text = app.Name;
        DetailSourceText.Text = app.Source switch
        {
            "UWP" => "Windows 商店应用",
            "Manual" => "手动添加的桌面应用",
            _ => "桌面应用"
        };
        DetailPathText.Text = string.Equals(app.Source, "UWP", StringComparison.OrdinalIgnoreCase)
            ? "AUMID · " + app.LaunchTarget
            : app.LaunchTarget;
        DetailIconImage.Source = app.IconSource;
        FolderDetailButton.Visibility = string.Equals(app.Source, "UWP", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _syncingDetail = true;
        try
        {
            CategoryCheckList.Children.Clear();
            var categories = _repository.GetAssignableCategories();
            if (categories.Count == 0)
            {
                CategoryCheckList.Children.Add(new TextBlock
                {
                    Text = "暂无分类",
                    FontSize = 12,
                    Opacity = 0.7
                });
            }
            else
            {
                foreach (var category in categories)
                {
                    var checkBox = new CheckBox
                    {
                        Content = category.Name,
                        Tag = category.Id,
                        IsChecked = app.IsInCategory(category.Id),
                        MinWidth = 0,
                        MinHeight = 26,
                        FontSize = 12
                    };
                    checkBox.Checked += CategoryCheckBox_Changed;
                    checkBox.Unchecked += CategoryCheckBox_Changed;
                    CategoryCheckList.Children.Add(checkBox);
                }
            }
        }
        finally
        {
            _syncingDetail = false;
        }
    }

    private void AppSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        _selectedApp = AppsList.SelectedItem as AppItem;
        UpdateDetail();
    }

    private void CategoryCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingDetail || _selectedApp is null ||
            sender is not CheckBox { Tag: string categoryId } checkBox)
        {
            return;
        }

        var isMember = checkBox.IsChecked == true;
        if (!isMember && _selectedApp.CategoryIds.Count <= 1)
        {
            _syncingDetail = true;
            try
            {
                checkBox.IsChecked = true;
            }
            finally
            {
                _syncingDetail = false;
            }

            ShowInfo(
                "至少保留一个分类",
                "应用需要至少属于一个分类。请先勾选其他分类，再取消当前分类。",
                InfoBarSeverity.Informational);
            return;
        }

        _repository.SetAppCategoryMembership(_selectedApp.Id, categoryId, isMember);
    }

    private async void AddAppsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerOpen)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".lnk");
        picker.FileTypeFilter.Add(".com");
        picker.FileTypeFilter.Add(".appref-ms");

        IReadOnlyList<StorageFile> files;
        _pickerOpen = true;
        AddAppsButton.IsEnabled = false;
        _setExternalDialogOpen(true);
        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
            files = await picker.PickMultipleFilesAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("选择应用文件失败。", ex);
            ShowInfo("无法选择应用", ex.Message, InfoBarSeverity.Error);
            return;
        }
        finally
        {
            _setExternalDialogOpen(false);
            _pickerOpen = false;
            AddAppsButton.IsEnabled = true;
        }

        if (files.Count == 0)
        {
            return;
        }

        var candidates = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .Select(file =>
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                return AppItemFactory.CreateDesktopApp(
                    name,
                    file.Path,
                    file.Path,
                    null,
                    file.Path,
                    "Manual");
            })
            .ToList();

        var added = _repository.AddApps(candidates);
        if (added == 0)
        {
            ShowInfo("没有新增应用", "所选应用都已经在收纳器中。", InfoBarSeverity.Informational);
            return;
        }

        AddAppsButton.IsEnabled = false;
        try
        {
            await _iconService.LoadAllAsync(_repository.Apps);
            _repository.PersistQuietly();
        }
        catch (Exception ex)
        {
            LogService.Error("加载新添加应用的图标失败。", ex);
        }
        finally
        {
            AddAppsButton.IsEnabled = true;
        }

        RefreshList();
        ShowInfo("已添加应用", $"成功添加 {added} 个应用，可在右侧调整所属分类。", InfoBarSeverity.Success);
    }

    private void OpenDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is null)
        {
            return;
        }

        try
        {
            AppLauncher.Launch(_selectedApp);
            _repository.MarkLaunched(_selectedApp.Id);
        }
        catch (Exception ex)
        {
            LogService.Error($"从管理页启动应用失败：{_selectedApp.Name}", ex);
            ShowInfo("无法启动应用", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void FolderDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is null)
        {
            return;
        }

        try
        {
            AppLauncher.OpenContainingFolder(_selectedApp);
        }
        catch (Exception ex)
        {
            ShowInfo("无法定位文件", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void RemoveDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is null)
        {
            return;
        }

        var app = _selectedApp;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "从收纳器中移除？",
            Content = new TextBlock
            {
                Text = $"「{app.Name}」会从收纳器中移除，但不会卸载或删除应用文件。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _repository.RemoveApp(app.Id);
        _selectedApp = null;
        RefreshList();
        ShowInfo("已移除应用", $"「{app.Name}」已离开收纳器。", InfoBarSeverity.Informational);
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        ManageInfoBar.Title = title;
        ManageInfoBar.Message = message;
        ManageInfoBar.Severity = severity;
        ManageInfoBar.IsOpen = true;
    }
}
