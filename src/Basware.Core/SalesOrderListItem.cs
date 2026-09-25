using System;
using System.ComponentModel;
using System.Linq;
using WpfAppBaswareLogin.Services;

namespace WpfAppBaswareLogin.ViewModels;

public sealed class SalesOrderListItem(EdiOrderSummary summary) : INotifyPropertyChanged
{
    public EdiOrderSummary Summary { get; } = summary;
    public string? OrderNumber => Summary.OrderNumber;
    public string? DeliveryPointShortCode => Summary.DeliveryPointShortCode;
    public string LaboratoryDisplay => Summary.LaboratoryDisplay;
    public DateTime? DocumentDate => Summary.DocumentDate;
    public DateTime? RequestedDeliveryDate => Summary.RequestedDeliveryDate;
    private bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class SalesOrderFilter
{
    public static bool Matches(EdiOrderSummary order, DateTime? from, DateTime? to, string? laboratory, string? location)
    {
        if (from?.Date > to?.Date) return false;
        if (from.HasValue && (!order.RequestedDeliveryDate.HasValue || order.RequestedDeliveryDate.Value.Date < from.Value.Date)) return false;
        if (to.HasValue && (!order.RequestedDeliveryDate.HasValue || order.RequestedDeliveryDate.Value.Date > to.Value.Date)) return false;
        var lab = laboratory?.Trim() ?? "";
        var place = location?.Trim() ?? "";
        return (lab.Length == 0 || order.LaboratoryNames.Any(n => Contains(n, lab)))
            && (place.Length == 0 || order.DeliveryPointSearchNames.Any(n => Contains(n, place))
                || Contains(order.DeliveryPointShortCode, place) || Contains(order.DeliveryPointId, place));
    }

    private static bool Contains(string? value, string filter) => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
}
