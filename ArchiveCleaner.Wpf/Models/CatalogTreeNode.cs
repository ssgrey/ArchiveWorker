using System.Collections.ObjectModel;
using ArchiveCleaner.Wpf.ViewModels;

namespace ArchiveCleaner.Wpf.Models;

public abstract class CatalogTreeNode : ObservableObject
{
    private bool? _isChecked = true;

    protected CatalogTreeNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }
    public FolderTreeNode? Parent { get; private set; }
    public virtual bool? IsChecked
    {
        get => _isChecked;
        set => SetCheckedState(value, updateChildren: true);
    }

    public event EventHandler? CheckedStateChanged;

    internal void AttachTo(FolderTreeNode parent) => Parent = parent;

    protected bool SetCheckedState(bool? value, bool updateChildren)
    {
        if (!SetProperty(ref _isChecked, value, nameof(IsChecked))) return false;
        OnCheckedStateChanged(updateChildren);
        return true;
    }

    protected virtual void OnCheckedStateChanged(bool updateChildren)
    {
        Parent?.RefreshCheckedStateFromChildren();
        CheckedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal virtual void RefreshCheckedStateFromChildren() { }
}

public sealed class FolderTreeNode : CatalogTreeNode
{
    private bool _isExpanded = true;
    private bool _isUpdatingChildren;

    public FolderTreeNode(string name, string fullPath, bool isVirtual = false) : base(name, fullPath) => IsVirtual = isVirtual;

    public ObservableCollection<CatalogTreeNode> Children { get; } = [];
    public bool IsVirtual { get; }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public int ImageCount => Children.Sum(child => child is ImageTreeNode ? 1 : ((FolderTreeNode)child).ImageCount);
    public string Summary => $"{ImageCount} 张";

    protected override void OnCheckedStateChanged(bool updateChildren)
    {
        if (updateChildren && IsChecked is bool isChecked)
        {
            _isUpdatingChildren = true;
            try
            {
                foreach (var child in Children) child.IsChecked = isChecked;
            }
            finally { _isUpdatingChildren = false; }
        }
        base.OnCheckedStateChanged(updateChildren);
    }

    internal override void RefreshCheckedStateFromChildren()
    {
        if (_isUpdatingChildren || Children.Count == 0) return;
        var checkedCount = Children.Count(child => child.IsChecked == true);
        var uncheckedCount = Children.Count(child => child.IsChecked == false);
        bool? state = checkedCount == Children.Count ? true : uncheckedCount == Children.Count ? false : null;
        if (!SetCheckedState(state, updateChildren: false)) base.OnCheckedStateChanged(updateChildren: false);
    }

    public void AddChild(CatalogTreeNode child)
    {
        child.AttachTo(this);
        Children.Add(child);
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(Summary));
        RefreshCheckedStateFromChildren();
    }

    internal void RaiseContentSummaryChanged()
    {
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(Summary));
        Parent?.RaiseContentSummaryChanged();
    }
}

public sealed class ImageTreeNode(ArchiveImageItem image) : CatalogTreeNode(image.FileName, image.FilePath)
{
    public ArchiveImageItem Image { get; } = image;

    public override bool? IsChecked
    {
        get => base.IsChecked;
        set => base.IsChecked = value != false;
    }
}
