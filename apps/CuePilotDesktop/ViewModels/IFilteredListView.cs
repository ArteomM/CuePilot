namespace CuePilotDesktop.ViewModels;

/// <summary>
/// A filtered, grouped view over a collection, provided by the UI platform
/// (WPF: ICollectionView from CollectionViewSource). <see cref="View"/> is what the list binds to.
/// </summary>
public interface IFilteredListView
{
    object View { get; }

    IEnumerable<object> VisibleItems { get; }

    void Refresh();
}
